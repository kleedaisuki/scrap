#!/usr/bin/env pwsh
<#
.SYNOPSIS
对指定 Store 候选运行 WACK，并把可审阅报告保存在仓库 .temp。Runs WACK for an exact Store candidate and preserves an inspectable report under repository .temp.

.DESCRIPTION
本脚本不会安装证书、修改信任存储或提升权限。It never installs certificates, changes trust stores, or elevates privileges.
WACK 需要交互式用户会话；失败、缺失报告或非 PASS 结果都会明确失败，而不会被包装成成功。
WACK requires an interactive user session; tool errors, missing reports, and non-PASS results all fail explicitly.

.EXAMPLE
./build/run-store-certification.ps1 -Candidate ./artifacts/store/scrap-store-v1.0.2.0-win-x64.msix

.EXAMPLE
./build/run-store-certification.ps1 -Candidate ./candidate.msix -AllowOptionalWarnings

仅当报告总体为 WARNING、所有必需测试通过且所有其他发现均标记为 optional 时显式接受；输出不会称其为 PASS。
Explicitly accepts only an overall WARNING whose mandatory tests all pass and whose other findings are optional; output never calls it a PASS.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $Candidate,
    [string] $AppCertPath,
    [ValidateRange(1, 120)] [int] $TimeoutMinutes = 30,
    [switch] $AllowOptionalWarnings
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$candidatePath = [IO.Path]::GetFullPath($Candidate)
if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
    throw "Store candidate was not found: $candidatePath"
}
if ([IO.Path]::GetExtension($candidatePath) -notin @(".msix", ".appx", ".msixbundle", ".appxbundle")) {
    throw "WACK candidate must be an MSIX/AppX package or bundle."
}

if ([string]::IsNullOrWhiteSpace($AppCertPath)) {
    $AppCertPath = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/App Certification Kit/appcert.exe"
}
$appCert = [IO.Path]::GetFullPath($AppCertPath)
if (-not (Test-Path -LiteralPath $appCert -PathType Leaf)) {
    throw "Windows App Certification Kit was not found. Install the Windows SDK or pass -AppCertPath."
}

$candidateHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
$safeName = [IO.Path]::GetFileNameWithoutExtension($candidatePath) -replace '[^A-Za-z0-9._-]', '-'
$reportRoot = Join-Path $repoRoot ".temp/wack/$safeName-$($candidateHash.Substring(0, 12))"
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$reportPath = Join-Path $reportRoot "ValidationResult.xml"
Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
[IO.File]::WriteAllText((Join-Path $reportRoot "candidate.sha256"), "$candidateHash  $([IO.Path]::GetFileName($candidatePath))`n", (New-Object Text.UTF8Encoding($false)))

Write-Output "Candidate SHA-256: $candidateHash"
Write-Output "WACK report: $reportPath"
& $appCert reset
if ($LASTEXITCODE -ne 0) { throw "WACK reset failed with exit code $LASTEXITCODE." }

& $appCert test -appxpackagepath $candidatePath -reportoutputpath $reportPath
$wackExitCode = $LASTEXITCODE
if ($wackExitCode -eq 1) {
    # WACK code 1 is a successful verb that explicitly requires report finalization.
    # WACK 返回码 1 表示命令成功，但明确要求完成报告。
    & $appCert finalizereport -reportfilepath $reportPath
    $finalizeExitCode = $LASTEXITCODE
    if ($finalizeExitCode -ne 0) { throw "WACK report finalization failed with exit code $finalizeExitCode." }
}
elseif ($wackExitCode -ne 0) {
    throw "WACK test failed with infrastructure/tool exit code $wackExitCode."
}
# appcert.exe can return after handing work to its interactive process. The report, not launcher lifetime, is authoritative.
# appcert.exe 可能在把工作交给交互进程后立即返回；应以报告而非启动器生命周期为准。
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path -LiteralPath $reportPath -PathType Leaf) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
}
if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw "WACK launcher exited with code $wackExitCode but no XML report appeared within $TimeoutMinutes minute(s). Run from an uninterrupted interactive Windows user session."
}

[xml]$report = Get-Content -LiteralPath $reportPath -Raw
if ($null -eq $report.REPORT) { throw "WACK report has no REPORT root: $reportPath" }
$overallResult = [string]$report.REPORT.OVERALL_RESULT
Write-Output "WACK OVERALL_RESULT: $overallResult"
$partialRun = [string]$report.REPORT.PARTIAL_RUN
if ($partialRun -cne "FALSE") { throw "WACK report is partial or does not prove a complete run (PARTIAL_RUN='$partialRun')." }
$tests = @($report.SelectNodes("//TEST"))
if ($tests.Count -eq 0) { throw "WACK report contains no test results: $reportPath" }
$nonPassingTests = @($tests | Where-Object { $_.SelectSingleNode("RESULT").InnerText.Trim().ToUpperInvariant() -cne "PASS" })
$mandatoryNonPassingTests = @($nonPassingTests | Where-Object { $_.GetAttribute("OPTIONAL").Trim().ToUpperInvariant() -cne "TRUE" })
if ($mandatoryNonPassingTests.Count -ne 0) {
    $names = @($mandatoryNonPassingTests | ForEach-Object { "'$($_.GetAttribute('NAME'))'=$($_.SelectSingleNode('RESULT').InnerText.Trim())" }) -join ", "
    throw "WACK has non-passing mandatory tests: $names. Review $reportPath"
}
if ($overallResult -ceq "PASS") {
    if ($nonPassingTests.Count -ne 0) {
        $optionalFindings = @($nonPassingTests | ForEach-Object { "'$($_.GetAttribute('NAME'))'=$($_.SelectSingleNode('RESULT').InnerText.Trim())" }) -join ", "
        Write-Warning "WACK OVERALL_RESULT is PASS and every mandatory test passed, but the report contains optional findings requiring review: $optionalFindings"
    }
    Write-Output "WACK OVERALL_RESULT is PASS for the exact candidate. Preserve and review this report with the submission evidence."
    return
}
if ($AllowOptionalWarnings -and $overallResult -ceq "WARNING" -and $nonPassingTests.Count -ne 0) {
    $optionalFindings = @($nonPassingTests | ForEach-Object { "'$($_.GetAttribute('NAME'))'=$($_.SelectSingleNode('RESULT').InnerText.Trim())" }) -join ", "
    Write-Warning "WACK OVERALL_RESULT is WARNING, not PASS. All non-passing tests are optional and were explicitly acknowledged: $optionalFindings"
    Write-Output "WACK optional-warning gate accepted for review; this result MUST NOT be described as a WACK pass."
    return
}
throw "WACK did not pass (OVERALL_RESULT='$overallResult'). Review the preserved report: $reportPath. Use -AllowOptionalWarnings only after every mandatory test passes."
