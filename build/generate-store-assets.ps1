#!/usr/bin/env pwsh
<#
.SYNOPSIS
从单一品牌源图可复现地生成并验证 MSIX 与 Store 图标。Generates and validates MSIX and Store icons reproducibly from one brand source.

.DESCRIPTION
输出透明 PNG，且不重新绘制或改变品牌标记。Every output is a transparent PNG resize of the checked-in source; the script does not redraw the visual mark.

.EXAMPLE
./build/generate-store-assets.ps1

.EXAMPLE
./build/generate-store-assets.ps1 -ValidateOnly

验证已提交资产但不改写源树。Validates committed assets without modifying the source tree.
#>
[CmdletBinding()]
param(
    [string] $SourcePath,
    [string] $PackageAssetsDirectory,
    [string] $ListingAssetsDirectory,
    [switch] $ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.Drawing
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath = Join-Path $scriptRoot "../assets/branding/scrap-icon-source.png" }
if ([string]::IsNullOrWhiteSpace($PackageAssetsDirectory)) { $PackageAssetsDirectory = Join-Path $scriptRoot "../installer/Scrap.Installer.Store/Assets" }
if ([string]::IsNullOrWhiteSpace($ListingAssetsDirectory)) { $ListingAssetsDirectory = Join-Path $scriptRoot "../store-listing/assets" }

function Write-ScaledPng {
    <# 以高质量重采样写入确定尺寸的透明 PNG。Writes a high-quality transparent PNG at an exact size. #>
    param(
        [Parameter(Mandatory)] [Drawing.Image] $Source,
        [Parameter(Mandatory)] [int] $Size,
        [Parameter(Mandatory)] [string] $Path
    )

    $bitmap = New-Object Drawing.Bitmap($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage($Source, [Drawing.Rectangle]::new(0, 0, $Size, $Size))
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Assert-TransparentSquarePng {
    <# 验证尺寸、PNG 格式及真实 alpha 通道。Validates dimensions, PNG encoding, and a meaningful alpha channel. #>
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [int] $Size
    )

    $image = [Drawing.Bitmap]::FromFile($Path)
    try {
        if ($image.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid -or
            $image.Width -ne $Size -or $image.Height -ne $Size) {
            throw "Store asset '$Path' must be a ${Size}x${Size} PNG."
        }
        $hasTransparentPixel = $false
        $hasVisiblePixel = $false
        for ($y = 0; $y -lt $Size -and (-not $hasTransparentPixel -or -not $hasVisiblePixel); $y++) {
            for ($x = 0; $x -lt $Size -and (-not $hasTransparentPixel -or -not $hasVisiblePixel); $x++) {
                $alpha = $image.GetPixel($x, $y).A
                if ($alpha -lt 255) { $hasTransparentPixel = $true }
                if ($alpha -gt 0) { $hasVisiblePixel = $true }
            }
        }
        if (-not $hasTransparentPixel -or -not $hasVisiblePixel) {
            throw "Store asset '$Path' must contain both transparent and visible pixels."
        }
    }
    finally {
        $image.Dispose()
    }
}

$sourceFullPath = [IO.Path]::GetFullPath($SourcePath)
if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    throw "Brand source was not found: $sourceFullPath"
}
$packageRoot = [IO.Path]::GetFullPath($PackageAssetsDirectory)
$listingRoot = [IO.Path]::GetFullPath($ListingAssetsDirectory)
if ($ValidateOnly) {
    foreach ($directory in @($packageRoot, $listingRoot)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Generated Store asset directory is missing: $directory" }
    }
}
else {
    New-Item -ItemType Directory -Path $packageRoot, $listingRoot -Force | Out-Null
}

$assets = [ordered]@{
    "Square44x44Logo.png" = 44
    "Square44x44Logo.scale-200.png" = 88
    "Square44x44Logo.scale-400.png" = 176
    "Square150x150Logo.png" = 150
    "Square150x150Logo.scale-200.png" = 300
    "Square150x150Logo.scale-400.png" = 600
    "StoreLogo.png" = 50
    "StoreLogo.scale-125.png" = 63
    "StoreLogo.scale-150.png" = 75
    "StoreLogo.scale-200.png" = 100
    "StoreLogo.scale-250.png" = 125
    "StoreLogo.scale-300.png" = 150
    "StoreLogo.scale-400.png" = 200
}
foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)) {
    $assets["Square44x44Logo.targetsize-$size.png"] = $size
    $assets["Square44x44Logo.targetsize-${size}_altform-unplated.png"] = $size
    $assets["Square44x44Logo.targetsize-${size}_altform-lightunplated.png"] = $size
}

$source = [Drawing.Image]::FromFile($sourceFullPath)
try {
    if ($source.Width -ne $source.Height -or $source.Width -lt 600) {
        throw "Brand source must be square and at least 600x600 pixels."
    }
    foreach ($entry in $assets.GetEnumerator()) {
        $path = Join-Path $packageRoot $entry.Key
        if (-not $ValidateOnly) { Write-ScaledPng -Source $source -Size $entry.Value -Path $path }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Generated Store asset is missing: $path" }
        Assert-TransparentSquarePng -Path $path -Size $entry.Value
    }
    $listingIcon = Join-Path $listingRoot "AppTileIcon-300x300.png"
    if (-not $ValidateOnly) { Write-ScaledPng -Source $source -Size 300 -Path $listingIcon }
    if (-not (Test-Path -LiteralPath $listingIcon -PathType Leaf)) { throw "Generated Store listing icon is missing: $listingIcon" }
    Assert-TransparentSquarePng -Path $listingIcon -Size 300
}
finally {
    $source.Dispose()
}

# 遗留或误命名变体会产生不可预测的资源选择，因此生成器也约束完整文件集。
# Stale or misnamed variants make resource selection unpredictable, so constrain the complete generated set.
$expectedPackageFiles = @($assets.Keys | Sort-Object)
$actualPackageFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter "*.png" -File | ForEach-Object Name | Sort-Object)
if (($expectedPackageFiles -join "|") -cne ($actualPackageFiles -join "|")) {
    throw "Package asset set differs from the generated contract. Remove stale PNG files and rerun."
}
$verb = if ($ValidateOnly) { "Validated" } else { "Generated and validated" }
Write-Output "$verb $($assets.Count) package assets and one 300x300 Store listing icon."
