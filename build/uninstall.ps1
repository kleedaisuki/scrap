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
$removalAttempts = 10

# 停止 daemon，但不把已停止状态视为卸载错误。Stops the daemon without making an already-stopped daemon an error.
function Stop-InstalledDaemon {
    $cli = Join-Path $binDirectory "scrap.exe"
    if (Test-Path -LiteralPath $cli -PathType Leaf) {
        try {
            $process = Start-Process -FilePath $cli -ArgumentList @("daemon", "shutdown", "--if-running") -PassThru -WindowStyle Hidden
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
    $current = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($null -eq $current) { $current = "" }
    $entries = [Collections.Generic.List[string]]::new()
    foreach ($entry in $current.Split(';')) { $entries.Add($entry) }
    for ($index = $entries.Count - 1; $index -ge 0; $index--) {
        if ([string]::Equals($entries[$index], $ownedEntry, [StringComparison]::OrdinalIgnoreCase)) {
            $entries.RemoveAt($index)
            break
        }
    }
    [Environment]::SetEnvironmentVariable("Path", ($entries -join ';'), "User")
    Remove-Item -LiteralPath $pathMarker -Force -ErrorAction SilentlyContinue
}

# 有限重试删除一个 owned 文件，并以最终存在性决定结果。Retries removal of one owned file and decides by final existence.
function Remove-OwnedProgram {
    param([Parameter(Mandatory)] [string] $Path)

    for ($attempt = 1; $attempt -le $removalAttempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) { return $true }
        try { Remove-Item -LiteralPath $Path -Force -ErrorAction Stop } catch { }
        if (Test-Path -LiteralPath $Path) { Start-Sleep -Milliseconds 200 }
    }
    return -not (Test-Path -LiteralPath $Path)
}

# 有限重试 purge 根目录；调用方必须先确认 owned 程序均已删除。Retries purge after owned programs are gone.
function Remove-ScrapRoot {
    for ($attempt = 1; $attempt -le $removalAttempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $scrapRoot)) { return $true }
        try { Remove-Item -LiteralPath $scrapRoot -Recurse -Force -ErrorAction Stop } catch { }
        if (Test-Path -LiteralPath $scrapRoot) { Start-Sleep -Milliseconds 200 }
    }
    return -not (Test-Path -LiteralPath $scrapRoot)
}

if ($Purge -and -not $Yes) {
    $answer = Read-Host "将永久删除 $scrapRoot 中的数据。输入 PURGE 继续 / Data will be deleted permanently. Type PURGE to continue"
    if ($answer -cne "PURGE") {
        Write-Host "已取消。Cancelled."
        exit 0
    }
}

Stop-InstalledDaemon

$removalFailed = $false
foreach ($program in $programs) {
    $programPath = Join-Path $binDirectory $program
    if (-not (Remove-OwnedProgram -Path $programPath)) {
        [Console]::Error.WriteLine("无法删除仍被占用的 $programPath。Close the running program and retry; could not remove $programPath.")
        $removalFailed = $true
    }
}
if ($removalFailed) {
    [Console]::Error.WriteLine("卸载未完成，数据保持不变。Uninstall did not complete; data was left intact.")
    exit 1
}

if (-not $NoPath) { Remove-OwnedUserPath }

if ($Purge) {
    if (-not (Remove-ScrapRoot)) {
        [Console]::Error.WriteLine("无法完整删除 $scrapRoot。Purge could not completely remove $scrapRoot.")
        exit 1
    }
    Write-Host "scrap 程序与本地数据已删除。OS 密钥存储或备份可能仍保留历史副本。"
    Write-Host "scrap programs and local data were removed. OS key stores or backups may retain historical copies."
    exit 0
}
if ((Test-Path -LiteralPath $binDirectory) -and -not (Get-ChildItem -LiteralPath $binDirectory -Force)) {
    Remove-Item -LiteralPath $binDirectory -Force
}
if ((Test-Path -LiteralPath $stateDirectory) -and -not (Get-ChildItem -LiteralPath $stateDirectory -Force)) {
    Remove-Item -LiteralPath $stateDirectory -Force
}

Write-Host "scrap 程序已卸载；data 与配置已保留在 $scrapRoot。"
Write-Host "scrap programs were removed; data and configuration remain in $scrapRoot."
