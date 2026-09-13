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
    [ValidatePattern("^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
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

# 将项目的单文件 apphost 重命名为稳定 userspace 名称；apphost 本身不依赖文件名。
# Renames the project's single-file apphost to its stable userspace name; apphosts do not depend on their filename.
# 不把 AssemblyName 作为全局 MSBuild 属性传入，否则它还会污染 ProjectReference 输出。
# AssemblyName is deliberately not passed globally because that would also contaminate ProjectReference outputs.
function Publish-EntryPoint {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $BuildName,
        [Parameter(Mandatory)] [string] $PublicName
    )

    $publishDirectory = Join-Path $workRoot $PublicName
    dotnet publish (Join-Path $repoRoot $Project) `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $publishDirectory `
        -p:Version=$Version `
        -p:InformationalVersion=$Version `
        -p:PublishSingleFile=true `
        -p:PublishAot=false `
        -p:PublishTrimmed=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project."
    }

    $entryPoint = @(
        (Join-Path $publishDirectory "$BuildName$suffix"),
        (Join-Path $publishDirectory "$PublicName$suffix")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $entryPoint) {
        throw "Expected single-file entry point was not produced for $Project."
    }

    Copy-Item -LiteralPath $entryPoint -Destination (Join-Path $payloadRoot "$PublicName$suffix")
}

try {
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

    Publish-EntryPoint -Project "src/Scrap.Cli/Scrap.Cli.csproj" -BuildName "Scrap.Cli" -PublicName "scrap"
    Publish-EntryPoint -Project "src/Scrap.Daemon/Scrap.Daemon.csproj" -BuildName "Scrap.Daemon" -PublicName "scrapd"
    Publish-EntryPoint -Project "src/Scrap.Gui/Scrap.Gui.csproj" -BuildName "Scrap.Gui" -PublicName "scrap-gui"

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.txt") -Destination $packageRoot
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
    $checksum = "$hash  $([IO.Path]::GetFileName($archive))`n"
    [IO.File]::WriteAllText("$archive.sha256", $checksum, [Text.UTF8Encoding]::new($false))
    Write-Output $archive
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
