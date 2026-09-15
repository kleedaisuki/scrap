param(
    [string]$OutputDirectory
)

<#
.SYNOPSIS
Captures deterministic Store-ready screenshots from the real Avalonia MainWindow.

.DESCRIPTION
使用无头 Avalonia 后端和内存展示 client 为中英文各输出四张 1366x768 PNG；不读写用户的
~/.scrap、LocalAppData 偏好或系统剪贴板。
Uses Avalonia's headless backend and an in-memory showcase client to emit four 1366x768 PNG files per language.
It never reads or writes the user's ~/.scrap, LocalAppData preferences, or system clipboard.

.EXAMPLE
pwsh ./build/capture-screenshots.ps1
#>

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot '.temp/gui-screenshots'
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$relativeImages = @(
    'zh-CN/01-all-scopes-masked.png',
    'zh-CN/02-multi-scope-subset.png',
    'zh-CN/03-create-record.png',
    'zh-CN/04-temporary-reveal.png',
    'en-US/01-all-scopes-masked.png',
    'en-US/02-multi-scope-subset.png',
    'en-US/03-create-record.png',
    'en-US/04-temporary-reveal.png'
)

# 只删除本工具拥有的精确文件；显式外部目录中的其他内容不可触碰。
# Delete only exact files owned by this tool; unrelated content in explicit external directories is untouchable.
foreach ($relativeImage in $relativeImages) {
    $ownedImage = Join-Path $OutputDirectory $relativeImage
    if (Test-Path -LiteralPath $ownedImage -PathType Leaf) {
        Remove-Item -LiteralPath $ownedImage -Force
    }
}

$buildRoot = Join-Path $repositoryRoot '.temp/gui-screenshot-build'
$screenshotProject = Join-Path $repositoryRoot 'tools/Scrap.Screenshot/Scrap.Screenshot.csproj'
dotnet restore $screenshotProject --locked-mode --artifacts-path $buildRoot
if ($LASTEXITCODE -ne 0) {
    throw "Screenshot renderer restore failed with exit code $LASTEXITCODE."
}

dotnet run `
    --project $screenshotProject `
    --configuration Release `
    --artifacts-path $buildRoot `
    --no-restore `
    -- $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Screenshot renderer failed with exit code $LASTEXITCODE."
}

foreach ($relativeImage in $relativeImages) {
    $ownedImage = Join-Path $OutputDirectory $relativeImage
    if (-not (Test-Path -LiteralPath $ownedImage -PathType Leaf)) {
        throw "Expected screenshot was not generated: $relativeImage"
    }
}

Write-Host "Captured $($relativeImages.Count) screenshots in $OutputDirectory"
