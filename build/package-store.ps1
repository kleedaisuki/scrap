#!/usr/bin/env pwsh
<#
.SYNOPSIS
生成供 Partner Center 提交的未签名完整包 MSIX。Builds an unsigned full-package MSIX for Partner Center submission.

.EXAMPLE
./build/package-store.ps1 -IdentityName '12345MoeSegfault.Scrap' -Publisher 'CN=...' `
  -PublisherDisplayName 'MoeSegfault' -ProductDisplayName 'moeSegFault Scrap' -ReleaseVersion '1.0.0'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $IdentityName,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $Publisher,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $PublisherDisplayName,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $ProductDisplayName,
    [Parameter(Mandatory)]
    [ValidatePattern("^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    [string] $ReleaseVersion,
    [ValidatePattern("^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$")]
    [string] $PackageVersion,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/store")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot "Packaging.Common.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$storeRoot = Join-Path $repoRoot "installer/Scrap.Installer.Store"
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = (Get-Content -LiteralPath (Join-Path $storeRoot "StoreVersion.txt") -Raw).Trim()
}
$parts = @($PackageVersion.Split('.') | ForEach-Object { [uint32]$_ })
if ($parts.Count -ne 4 -or $parts[0] -eq 0 -or @($parts | Where-Object { $_ -gt 65535 }).Count -ne 0 -or $parts[3] -ne 0) {
    throw "PackageVersion must contain four 0..65535 integers, start above 0, and use revision 0 for Store submission."
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$workRoot = Join-Path $outputRoot (".store-" + [Guid]::NewGuid().ToString("N"))
$payloadRoot = Join-Path $workRoot "payload"
$inspectionRoot = Join-Path $workRoot "inspection"
$packageName = "scrap-store-v$PackageVersion-win-x64.msix"
$packagePath = Join-Path $outputRoot $packageName

try {
    # 打包是纯读取操作；只验证已提交的生成资产，不改写源树。
    # Packaging is read-only: validate committed generated assets without rewriting the source tree.
    & (Join-Path $PSScriptRoot "generate-store-assets.ps1") -ValidateOnly | Out-Null
    Publish-ScrapWindowsEntryPoints -RepositoryRoot $repoRoot -WorkRoot $workRoot -PayloadRoot $payloadRoot -ReleaseVersion $ReleaseVersion
    Assert-ScrapWindowsEntryPointMetadata -PayloadRoot $payloadRoot -ReleaseVersion $ReleaseVersion

    Copy-Item -LiteralPath (Join-Path $storeRoot "Assets") -Destination $payloadRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $payloadRoot "LICENSE.txt")
    Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.txt") -Destination $payloadRoot

    # DOM 注入会正确转义 DN 与显示名，同时保持 Partner Center 提供值的大小写。
    # DOM injection escapes DNs/display names correctly while preserving Partner Center casing.
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $storeRoot "AppxManifest.xml") -Raw
    $namespace = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
    $namespace.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $namespace.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespace)
    $properties = $manifest.SelectSingleNode("/f:Package/f:Properties", $namespace)
    $identity.SetAttribute("Name", $IdentityName)
    $identity.SetAttribute("Publisher", $Publisher)
    $identity.SetAttribute("Version", $PackageVersion)
    $properties.SelectSingleNode("f:PublisherDisplayName", $namespace).InnerText = $PublisherDisplayName
    $properties.SelectSingleNode("f:DisplayName", $namespace).InnerText = $ProductDisplayName
    $visualElements = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application/uap:VisualElements", $namespace)
    if ($null -eq $visualElements) { throw "Store manifest is missing uap:VisualElements." }
    $visualElements.SetAttribute("DisplayName", $ProductDisplayName)

    $manifestPath = Join-Path $payloadRoot "AppxManifest.xml"
    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = New-Object Text.UTF8Encoding($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($manifestPath, $settings)
    try { $manifest.Save($writer) } finally { $writer.Dispose() }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
    $makeAppx = Find-WindowsSdkTool -Name "makeappx.exe"
    & $makeAppx pack /v /o /h SHA256 /d $payloadRoot /p $packagePath
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx validation or packaging failed." }

    # 解包并检查最终容器，而不是只信任 staging 目录。
    # Inspect the final container after unpacking instead of trusting staging alone.
    & $makeAppx unpack /o /p $packagePath /d $inspectionRoot
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx could not unpack the generated package." }
    foreach ($required in @("AppxManifest.xml", "scrap.exe", "scrapd.exe", "scrap-gui.exe", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $inspectionRoot $required) -PathType Leaf)) {
            throw "Generated MSIX is missing flat-layout file '$required'."
        }
    }
    [xml]$packedManifest = Get-Content -LiteralPath (Join-Path $inspectionRoot "AppxManifest.xml") -Raw
    $packedNamespaces = New-Object Xml.XmlNamespaceManager($packedManifest.NameTable)
    $packedNamespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $packedNamespaces.AddNamespace("uap5", "http://schemas.microsoft.com/appx/manifest/uap/windows10/5")
    $packedNamespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $packedNamespaces.AddNamespace("desktop4", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4")
    $packedNamespaces.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")
    $packedIdentity = $packedManifest.SelectSingleNode("/f:Package/f:Identity", $packedNamespaces)
    if ($packedIdentity.GetAttribute("Name") -cne $IdentityName -or $packedIdentity.GetAttribute("Publisher") -cne $Publisher -or
        $packedIdentity.GetAttribute("Version") -cne $PackageVersion) {
        throw "Generated MSIX identity does not exactly match the requested Partner Center identity."
    }
    $packedDisplayName = $packedManifest.SelectSingleNode("/f:Package/f:Properties/f:PublisherDisplayName", $packedNamespaces)
    if ($null -eq $packedDisplayName -or $packedDisplayName.InnerText -cne $PublisherDisplayName) {
        throw "Generated MSIX PublisherDisplayName does not exactly match the requested Partner Center identity."
    }
    $packedProductDisplayName = $packedManifest.SelectSingleNode("/f:Package/f:Properties/f:DisplayName", $packedNamespaces)
    $packedVisualDisplayName = $packedManifest.SelectSingleNode("/f:Package/f:Applications/f:Application/uap:VisualElements", $packedNamespaces)
    if ($null -eq $packedProductDisplayName -or $packedProductDisplayName.InnerText -cne $ProductDisplayName -or
        $null -eq $packedVisualDisplayName -or $packedVisualDisplayName.GetAttribute("DisplayName") -cne $ProductDisplayName) {
        throw "Generated MSIX package and application DisplayName values must exactly match ProductDisplayName."
    }

    # 清单断言与运行时 smoke 互补：前者防止入口/能力悄然增加，后者证明 Windows 接受这些声明。
    # Manifest assertions prevent silent entry/capability growth; installed smoke proves Windows accepts the declarations.
    $capabilities = @($packedManifest.SelectNodes("/f:Package/f:Capabilities/*", $packedNamespaces))
    if ($capabilities.Count -ne 1 -or $capabilities[0].NamespaceURI -cne $packedNamespaces.LookupNamespace("rescap") -or
        $capabilities[0].LocalName -cne "Capability" -or $capabilities[0].GetAttribute("Name") -cne "runFullTrust") {
        throw "Generated MSIX must declare exactly one runFullTrust capability."
    }
    $applications = @($packedManifest.SelectNodes("/f:Package/f:Applications/f:Application", $packedNamespaces))
    if ($applications.Count -ne 1 -or $applications[0].GetAttribute("Executable") -cne "scrap-gui.exe" -or
        $applications[0].GetAttribute("EntryPoint") -cne "Windows.FullTrustApplication") {
        throw "Generated MSIX must declare exactly one full-trust scrap-gui application."
    }
    $aliases = @($packedManifest.SelectNodes("/f:Package/f:Applications/f:Application/f:Extensions/uap5:Extension[@Category='windows.appExecutionAlias']", $packedNamespaces))
    $aliasTargets = @($packedManifest.SelectNodes("/f:Package/f:Applications/f:Application/f:Extensions/uap5:Extension/uap5:AppExecutionAlias/uap5:ExecutionAlias", $packedNamespaces))
    if ($aliases.Count -ne 1 -or $aliases[0].GetAttribute("Executable") -cne "scrap.exe" -or
        $aliases[0].GetAttribute("EntryPoint") -cne "Windows.FullTrustApplication" -or $aliasTargets.Count -ne 1 -or
        $aliasTargets[0].GetAttribute("Alias") -cne "scrap.exe" -or $aliasTargets[0].ParentNode.GetAttribute("Subsystem", $packedNamespaces.LookupNamespace("desktop4")) -cne "console") {
        throw "Generated MSIX must declare exactly one console scrap.exe App Execution Alias."
    }
    $rootExecutables = @(Get-ChildItem -LiteralPath $inspectionRoot -Filter "*.exe" -File | ForEach-Object Name | Sort-Object)
    if (($rootExecutables -join "|") -cne "scrap-gui.exe|scrap.exe|scrapd.exe") {
        throw "Generated MSIX root must contain exactly scrap-gui.exe, scrap.exe, and scrapd.exe."
    }

    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$packagePath.sha256", "$hash  $packageName`n", [Text.UTF8Encoding]::new($false))
    Write-Output $packagePath
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
