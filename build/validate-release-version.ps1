<#
.SYNOPSIS
验证仓库中所有用户可见版本基线，并可校验发布标签。
Validates every checked-in user-visible version baseline and, optionally, a release tag.

.DESCRIPTION
中央 .NET ProductVersion 是 SemVer 权威；网站版本必须精确一致。Store 使用独立的单调四段版本，
但受控计数器与模板 manifest 必须一致，避免候选包从陈旧状态开始。
The central .NET ProductVersion is the SemVer authority and the website must match it exactly.
The Store keeps an independent monotonic four-part version, while its tracked counter and manifest template
must agree so candidates never start from stale state.

.EXAMPLE
./build/validate-release-version.ps1 -Tag v0.3.0
#>
[CmdletBinding()]
param(
    [ValidatePattern('^v.+$')]
    [string] $Tag,

    [switch] $PassThru
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$semVerPattern = '^(?<core>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

[xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$productVersion = [string] $buildProperties.Project.PropertyGroup.Version
if ($productVersion -notmatch $semVerPattern -or $Matches.core -cne $productVersion) {
    throw "Directory.Build.props Version must be a stable SemVer core (X.Y.Z). Found '$productVersion'."
}
$productCore = $Matches.core

$websitePackage = Get-Content -LiteralPath (Join-Path $repositoryRoot "website/package.json") -Raw | ConvertFrom-Json
if ([string] $websitePackage.version -cne $productVersion) {
    throw "website/package.json version '$($websitePackage.version)' does not match product version '$productVersion'."
}

$storeRoot = Join-Path $repositoryRoot "installer/Scrap.Installer.Store"
$storeVersion = (Get-Content -LiteralPath (Join-Path $storeRoot "StoreVersion.txt") -Raw).Trim()
if ($storeVersion -notmatch '^(?<major>[1-9]\d{0,4})\.(?<minor>\d{1,5})\.(?<build>\d{1,5})\.0$') {
    throw "StoreVersion.txt must be a four-part Store version above 0 with revision 0. Found '$storeVersion'."
}
foreach ($part in @($Matches.major, $Matches.minor, $Matches.build)) {
    if ([uint32] $part -gt 65535) { throw "Store version component '$part' exceeds 65535." }
}

[xml] $storeManifest = Get-Content -LiteralPath (Join-Path $storeRoot "AppxManifest.xml") -Raw
$manifestVersion = [string] $storeManifest.Package.Identity.Version
if ($manifestVersion -cne $storeVersion) {
    throw "Store manifest template version '$manifestVersion' does not match StoreVersion.txt '$storeVersion'."
}

$isPrerelease = $false
if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $releaseVersion = $Tag.Substring(1)
    if ($releaseVersion -notmatch $semVerPattern) {
        throw "Release tag '$Tag' must contain a valid SemVer version."
    }
    if ($Matches.core -cne $productCore) {
        throw "Release tag core '$($Matches.core)' does not match the checked-in product version '$productCore'."
    }
    $isPrerelease = $Matches.ContainsKey("prerelease")
}

Write-Host "Validated product $productVersion, website $($websitePackage.version), and Store baseline $storeVersion."
if ($PassThru) {
    [pscustomobject] @{
        Version = if ([string]::IsNullOrWhiteSpace($Tag)) { $productVersion } else { $Tag.Substring(1) }
        IsPrerelease = $isPrerelease
    }
}
