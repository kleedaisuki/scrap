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

# 仅在明确提供证书时签名；未签名构建不会被伪装成可绕过 SmartScreen。
# Sign only when a certificate is explicitly supplied; unsigned builds are never presented as SmartScreen-safe.
function Invoke-CodeSigning {
    param([Parameter(Mandatory)] [string[]] $Files)

    $signTool = $env:SCRAP_SIGNTOOL_PATH
    $thumbprint = $env:SCRAP_SIGN_CERT_SHA1
    $pfxPath = $env:SCRAP_SIGN_PFX_PATH
    if ([string]::IsNullOrWhiteSpace($signTool)) {
        Write-Warning "SCRAP_SIGNTOOL_PATH is not configured; Windows artifacts will be unsigned and may trigger SmartScreen."
        return
    }
    if (-not (Test-Path -LiteralPath $signTool -PathType Leaf)) {
        throw "SCRAP_SIGNTOOL_PATH does not point to a file: $signTool"
    }

    $arguments = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", "http://timestamp.digicert.com")
    if (-not [string]::IsNullOrWhiteSpace($thumbprint)) {
        $arguments += @("/sha1", $thumbprint)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($pfxPath)) {
        if (-not (Test-Path -LiteralPath $pfxPath -PathType Leaf)) {
            throw "SCRAP_SIGN_PFX_PATH does not point to a file: $pfxPath"
        }
        $arguments += @("/f", $pfxPath)
        if (-not [string]::IsNullOrWhiteSpace($env:SCRAP_SIGN_PFX_PASSWORD)) {
            $arguments += @("/p", $env:SCRAP_SIGN_PFX_PASSWORD)
        }
    }
    else {
        throw "Configure SCRAP_SIGN_CERT_SHA1 or SCRAP_SIGN_PFX_PATH when SCRAP_SIGNTOOL_PATH is set."
    }

    & $signTool @arguments @Files
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed." }

    # 立即验证可避免发布“签名命令成功但产物签名不可用”的工件。
    # Verify immediately so a successful command cannot publish an artifact with an unusable signature.
    & $signTool verify /pa /all @Files
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signature verification failed." }
}

# Windows Installer ProductVersion 只接受数字三元组；发布文件名仍保留完整 SemVer。
# Windows Installer ProductVersion accepts a numeric triplet; artifact names retain the full SemVer.
function Get-MsiVersion {
    param([Parameter(Mandatory)] [string] $ReleaseVersion)

    $numeric = ($ReleaseVersion -split '[+-]', 2)[0]
    if ($numeric -notmatch '^\d+\.\d+\.\d+$') {
        throw "Cannot convert release version '$ReleaseVersion' to an MSI ProductVersion."
    }
    return $numeric
}

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
        --no-restore `
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

    if ($isWindowsTarget) {
        Invoke-CodeSigning -Files (Get-ChildItem -LiteralPath $payloadRoot -Filter "*.exe" -File).FullName
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.txt") -Destination $packageRoot
    if (-not $isWindowsTarget) {
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

        # MSI 是默认面向终端用户的安装体验；ZIP 继续作为便携/排障工件。
        # MSI is the default end-user installation experience; ZIP remains a portable/diagnostic artifact.
        $msiVersion = Get-MsiVersion -ReleaseVersion $Version
        $installerProject = Join-Path $repoRoot "installer/Scrap.Installer.Windows/Scrap.Installer.Windows.wixproj"
        dotnet build $installerProject `
            --configuration Release `
            --output $outputRoot `
            --no-incremental `
            -p:ProductVersion=$msiVersion `
            -p:ReleaseVersion=$Version `
            -p:RuntimeIdentifier=$RuntimeIdentifier `
            -p:PayloadDir=$payloadRoot
        if ($LASTEXITCODE -ne 0) { throw "WiX installer build failed." }

        $msi = Join-Path $outputRoot "$packageName.msi"
        if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) {
            throw "Expected MSI was not produced: $msi"
        }
        Remove-Item -LiteralPath (Join-Path $outputRoot "$packageName.wixpdb") -Force -ErrorAction SilentlyContinue
        Invoke-CodeSigning -Files @($msi)
        $msiHash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$msi.sha256", "$msiHash  $([IO.Path]::GetFileName($msi))`n", [Text.UTF8Encoding]::new($false))
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
    if ($isWindowsTarget) {
        Write-Output $msi
    }
    Write-Output $archive
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
