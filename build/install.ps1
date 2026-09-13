#!/usr/bin/env pwsh
<#+
.SYNOPSIS
为当前 Windows 用户安装 scrap。Installs scrap for the current Windows user.

.EXAMPLE
./install.ps1
安装或原地升级，并在需要时更新用户 PATH。Installs or upgrades in place and updates user PATH when needed.
#>
[CmdletBinding()]
param([switch] $NoPath)

$ErrorActionPreference = "Stop"
$scrapRoot = Join-Path ([Environment]::GetFolderPath("UserProfile")) ".scrap"
$binDirectory = Join-Path $scrapRoot "bin"
$stateDirectory = Join-Path $scrapRoot "install"
$pathMarker = Join-Path $stateDirectory "windows-path-added"
$payloadDirectory = Join-Path $PSScriptRoot "payload"
$programs = @("scrap.exe", "scrapd.exe", "scrap-gui.exe")

# 替换前尽力停止已安装 daemon。Stops the installed daemon on a best-effort basis before replacing binaries.
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

# 精确添加一次安装路径并记录归属。Adds the exact installation directory once and records ownership of the PATH entry.
function Add-UserPath {
    $current = [Environment]::GetEnvironmentVariable("Path", "User") ?? ""
    $entries = @($current.Split(';', [StringSplitOptions]::RemoveEmptyEntries))
    if ($entries -contains $binDirectory) { return }

    $updated = if ($current.Length -eq 0) { $binDirectory } else { "$current;$binDirectory" }
    [Environment]::SetEnvironmentVariable("Path", $updated, "User")
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    Set-Content -LiteralPath $pathMarker -Value $binDirectory -Encoding utf8NoBOM
}

foreach ($program in $programs) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadDirectory $program) -PathType Leaf)) {
        throw "安装包不完整，缺少 $program。Package is incomplete: $program is missing."
    }
}

Stop-InstalledDaemon
New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
$transaction = Join-Path $scrapRoot (".install-" + [Guid]::NewGuid().ToString("N"))
$staged = Join-Path $transaction "new"
$backup = Join-Path $transaction "old"
New-Item -ItemType Directory -Path $staged, $backup -Force | Out-Null
$installedPrograms = [Collections.Generic.List[string]]::new()

try {
    foreach ($program in $programs) {
        Copy-Item -LiteralPath (Join-Path $payloadDirectory $program) -Destination (Join-Path $staged $program)
    }
    foreach ($program in $programs) {
        $destination = Join-Path $binDirectory $program
        if (Test-Path -LiteralPath $destination) {
            Move-Item -LiteralPath $destination -Destination (Join-Path $backup $program)
        }
    }
    foreach ($program in $programs) {
        Move-Item -LiteralPath (Join-Path $staged $program) -Destination (Join-Path $binDirectory $program)
        $installedPrograms.Add($program)
    }
}
catch {
    foreach ($program in $installedPrograms) {
        Remove-Item -LiteralPath (Join-Path $binDirectory $program) -Force -ErrorAction SilentlyContinue
    }
    foreach ($program in $programs) {
        $oldProgram = Join-Path $backup $program
        if (Test-Path -LiteralPath $oldProgram) {
            Remove-Item -LiteralPath (Join-Path $binDirectory $program) -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $oldProgram -Destination (Join-Path $binDirectory $program) -Force
        }
    }
    throw
}
finally {
    Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $NoPath) { Add-UserPath }
Write-Host "scrap 已安装到 $binDirectory。请打开新终端以使用更新后的 PATH。"
Write-Host "scrap was installed to $binDirectory. Open a new terminal to use the updated PATH."
