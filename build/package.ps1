#!/usr/bin/env pwsh
<#+
.SYNOPSIS
为一个运行时生成可发布的 scrap 归档。Builds a distributable scrap archive for one runtime.

.DESCRIPTION
三个入口分别发布到隔离目录，再汇总为只包含单文件入口与安装脚本的归档。
The three entry points are published in isolation, then assembled into an archive that contains
only the single-file executables and installer scripts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "linux-x64", "osx-x64", "osx-arm64")]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory)]
    [ValidatePattern("^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$")]
    [string] $Version,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot "../artifacts")
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$workRoot = Join-Path $outputRoot (".package-" + [Guid]::NewGuid().ToString("N"))
$packageName = "scrap-v$Version-$RuntimeIdentifier"
$packageRoot = Join-Path $workRoot $packageName
$payloadRoot = Join-Path $packageRoot "payload"
$isWindowsTarget = $RuntimeIdentifier.StartsWith("win-", [StringComparison]::Ordinal)
$suffix = if ($isWindowsTarget) { ".exe" } else { "" }

# 使用稳定的 userspace 名称发布单个入口，且不修改项目文件。
# Publishes one executable with its public userspace name without changing project files.
function Publish-EntryPoint {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $AssemblyName
    )

    $publishDirectory = Join-Path $workRoot $AssemblyName
    dotnet publish (Join-Path $repoRoot $Project) `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $publishDirectory `
        -p:AssemblyName=$AssemblyName `
        -p:Version=$Version `
        -p:InformationalVersion=$Version `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project."
    }

    $entryPoint = Join-Path $publishDirectory "$AssemblyName$suffix"
    if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) {
        throw "Expected single-file entry point was not produced: $entryPoint"
    }

    Copy-Item -LiteralPath $entryPoint -Destination (Join-Path $payloadRoot "$AssemblyName$suffix")
}

try {
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

    Publish-EntryPoint -Project "src/Scrap.Cli/Scrap.Cli.csproj" -AssemblyName "scrap"
    Publish-EntryPoint -Project "src/Scrap.Daemon/Scrap.Daemon.csproj" -AssemblyName "scrapd"
    Publish-EntryPoint -Project "src/Scrap.Gui/Scrap.Gui.csproj" -AssemblyName "scrap-gui"

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageRoot
    if ($isWindowsTarget) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination $packageRoot
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall.ps1") -Destination $packageRoot
    }
    else {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.sh") -Destination $packageRoot
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall.sh") -Destination $packageRoot
        & chmod 700 (Get-ChildItem -LiteralPath $payloadRoot -File).FullName
        & chmod 700 (Join-Path $packageRoot "install.sh") (Join-Path $packageRoot "uninstall.sh")
        if ($LASTEXITCODE -ne 0) { throw "chmod failed." }
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    if ($isWindowsTarget) {
        $archive = Join-Path $outputRoot "$packageName.zip"
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        Compress-Archive -LiteralPath $packageRoot -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        $archive = Join-Path $outputRoot "$packageName.tar.gz"
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        & tar -C $workRoot -czf $archive $packageName
        if ($LASTEXITCODE -ne 0) { throw "tar failed." }
    }

    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding utf8NoBOM
    Write-Output $archive
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
