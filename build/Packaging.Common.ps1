#!/usr/bin/env pwsh

<#
.SYNOPSIS
共享 Windows 打包原语。Shared Windows packaging primitives.

.DESCRIPTION
此文件只包含 MSI 与 MSIX 都可以安全复用的无状态构建函数；导入它不会执行构建。
This file contains stateless build functions that MSI and MSIX can safely share; dot-sourcing it has no build side effects.
#>

Set-StrictMode -Version 2.0

function Find-WindowsSdkTool {
    <#
    .SYNOPSIS
    定位当前主机上最新的 x64 Windows SDK 工具。Locates the newest x64 Windows SDK tool on the host.
    #>
    param([Parameter(Mandatory)] [string] $Name)

    $overrideName = "SCRAP_" + ([IO.Path]::GetFileNameWithoutExtension($Name).ToUpperInvariant()) + "_PATH"
    $override = [Environment]::GetEnvironmentVariable($overrideName)
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if (-not (Test-Path -LiteralPath $override -PathType Leaf)) {
            throw "$overrideName does not point to a file: $override"
        }
        return [IO.Path]::GetFullPath($override)
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object { try { [Version]$_.Name } catch { [Version]"0.0" } } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "$Name was not found. Install the Windows SDK or configure $overrideName."
    }
    return $candidate
}

function Publish-ScrapWindowsEntryPoints {
    <#
    .SYNOPSIS
    发布平坦布局所需的三个 Windows 单文件入口。Publishes the three single-file Windows entry points for a flat payload.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $WorkRoot,
        [Parameter(Mandatory)] [string] $PayloadRoot,
        [Parameter(Mandatory)] [string] $ReleaseVersion
    )

    $entries = @(
        @{ Project = "src/Scrap.Cli/Scrap.Cli.csproj"; BuildName = "scrap.exe"; PublicName = "scrap.exe" },
        @{ Project = "src/Scrap.Daemon/Scrap.Daemon.csproj"; BuildName = "scrapd.exe"; PublicName = "scrapd.exe" },
        @{ Project = "src/Scrap.Gui/Scrap.Gui.csproj"; BuildName = "Scrap.Gui.exe"; PublicName = "scrap-gui.exe" }
    )

    New-Item -ItemType Directory -Path $PayloadRoot -Force | Out-Null
    foreach ($entry in $entries) {
        $publishRoot = Join-Path $WorkRoot ([IO.Path]::GetFileNameWithoutExtension($entry.PublicName))
        & dotnet publish (Join-Path $RepositoryRoot $entry.Project) `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --no-restore `
            --output $publishRoot `
            -p:Version=$ReleaseVersion `
            -p:InformationalVersion=$ReleaseVersion `
            -p:PublishSingleFile=true `
            -p:PublishAot=false `
            -p:PublishTrimmed=false `
            -p:DebugSymbols=false `
            -p:DebugType=None `
            -p:IncludeNativeLibrariesForSelfExtract=true
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($entry.Project)." }

        $source = Join-Path $publishRoot $entry.BuildName
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Expected single-file entry point was not produced: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $PayloadRoot $entry.PublicName)
    }
}

function Assert-ScrapWindowsEntryPointMetadata {
    <#
    .SYNOPSIS
    拒绝产品身份漂移的 Windows 载荷。Rejects Windows payloads whose product identity has drifted.
    #>
    param(
        [Parameter(Mandatory)] [string] $PayloadRoot,
        [Parameter(Mandatory)] [string] $ReleaseVersion
    )

    $productVersions = @()
    $fileVersions = @()
    foreach ($name in @("scrap.exe", "scrapd.exe", "scrap-gui.exe")) {
        $path = Join-Path $PayloadRoot $name
        $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        if ($metadata.ProductName -cne "Scrap" -or $metadata.FileDescription -cne "Scrap" -or $metadata.CompanyName -cne "MoeSegfault") {
            throw "$name has inconsistent Windows product metadata."
        }
        if (-not $metadata.ProductVersion.StartsWith($ReleaseVersion, [StringComparison]::Ordinal)) {
            throw "$name does not carry release version '$ReleaseVersion'."
        }
        $productVersions += $metadata.ProductVersion
        $fileVersions += $metadata.FileVersion
    }
    if (@($productVersions | Select-Object -Unique).Count -ne 1 -or @($fileVersions | Select-Object -Unique).Count -ne 1) {
        throw "Windows entry points do not share one product and file version."
    }
}
