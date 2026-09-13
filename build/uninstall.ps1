#!/usr/bin/env pwsh
<#+
.SYNOPSIS
卸载当前 Windows 用户的 scrap。Uninstalls scrap for the current Windows user.

.EXAMPLE
./uninstall.ps1 -Purge -Yes
删除程序与本地数据；外部密钥存储可能保留历史副本。Removes programs and local data; external key stores may retain historical copies.
#>
[CmdletBinding()]
param(
    [switch] $Purge,
    [switch] $Yes,
    [switch] $NoPath
)

$ErrorActionPreference = "Stop"
$scrapRoot = Join-Path ([Environment]::GetFolderPath("UserProfile")) ".scrap"
$binDirectory = Join-Path $scrapRoot "bin"
$stateDirectory = Join-Path $scrapRoot "install"
$pathMarker = Join-Path $stateDirectory "windows-path-added"
$programs = @("scrap.exe", "scrapd.exe", "scrap-gui.exe")

# 停止 daemon，但不把已停止状态视为卸载错误。Stops the daemon without making an already-stopped daemon an error.
function Stop-InstalledDaemon {
    $cli = Join-Path $binDirectory "scrap.exe"
    if (Test-Path -LiteralPath $cli -PathType Leaf) {
        try {
            $process = Start-Process -FilePath $cli -ArgumentList @("daemon", "shutdown") -PassThru -WindowStyle Hidden
            if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            $process.Dispose()
        }
        catch { }
        Start-Sleep -Milliseconds 300
    }
}

# 仅当本安装器曾添加 PATH 时才移除。Removes PATH only when this installer previously added it.
function Remove-OwnedUserPath {
    if (-not (Test-Path -LiteralPath $pathMarker -PathType Leaf)) { return }
    $ownedEntry = (Get-Content -LiteralPath $pathMarker -Raw).Trim()
    $current = [Environment]::GetEnvironmentVariable("Path", "User") ?? ""
    $entries = @($current.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | Where-Object {
        -not [string]::Equals($_, $ownedEntry, [StringComparison]::OrdinalIgnoreCase)
    })
    [Environment]::SetEnvironmentVariable("Path", ($entries -join ';'), "User")
    Remove-Item -LiteralPath $pathMarker -Force -ErrorAction SilentlyContinue
}

if ($Purge -and -not $Yes) {
    $answer = Read-Host "将永久删除 $scrapRoot 中的数据。输入 PURGE 继续 / Data will be deleted permanently. Type PURGE to continue"
    if ($answer -cne "PURGE") {
        Write-Host "已取消。Cancelled."
        exit 0
    }
}

Stop-InstalledDaemon
if (-not $NoPath) { Remove-OwnedUserPath }

if ($Purge) {
    Remove-Item -LiteralPath $scrapRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "scrap 程序与本地数据已删除。OS 密钥存储或备份可能仍保留历史副本。"
    Write-Host "scrap programs and local data were removed. OS key stores or backups may retain historical copies."
    exit 0
}

foreach ($program in $programs) {
    Remove-Item -LiteralPath (Join-Path $binDirectory $program) -Force -ErrorAction SilentlyContinue
}
if ((Test-Path -LiteralPath $binDirectory) -and -not (Get-ChildItem -LiteralPath $binDirectory -Force)) {
    Remove-Item -LiteralPath $binDirectory -Force
}
if ((Test-Path -LiteralPath $stateDirectory) -and -not (Get-ChildItem -LiteralPath $stateDirectory -Force)) {
    Remove-Item -LiteralPath $stateDirectory -Force
}

Write-Host "scrap 程序已卸载；data 与配置已保留在 $scrapRoot。"
Write-Host "scrap programs were removed; data and configuration remain in $scrapRoot."
