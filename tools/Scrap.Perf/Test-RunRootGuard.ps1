# Validates that fixture-creating modes reject non-empty roots without deleting prior data.
# 验证创建夹具的模式会拒绝非空根目录，且不会删除既有数据。
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runRoot = Join-Path $repository ".temp/perf-root-guard-$([guid]::NewGuid().ToString('N'))"
$sentinel = Join-Path $runRoot 'sentinel.txt'
$output = Join-Path $runRoot 'results.json'
New-Item -ItemType Directory -Path $runRoot | Out-Null
Set-Content -LiteralPath $sentinel -Value 'must survive' -NoNewline
$before = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash

Push-Location $repository
try {
    & dotnet run -c Release --project tools/Scrap.Perf -- baseline $runRoot $output
    $toolExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

$after = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
if ($toolExitCode -ne 2) {
    throw "Expected exit code 2 for a non-empty run root, got $toolExitCode."
}
if ($before -ne $after) {
    throw 'The existing sentinel was modified.'
}
if (Test-Path -LiteralPath $output) {
    throw 'The rejected run created its output file.'
}

Write-Host "Run-root guard passed; preserved $sentinel"
