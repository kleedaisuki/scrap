# Validates that fixture-creating modes reject occupied or external paths without touching prior data.
# 验证创建夹具的模式拒绝非空或外部路径，且不会改动既有数据。
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

function Assert-Rejected {
    # Exercise the executable contract, not an internal helper. / 测试可执行程序契约，而非内部辅助函数。
    param([string]$Mode, [string]$Root, [string]$Output)

    & dotnet run --no-build -c Release --project tools/Scrap.Perf -- $Mode $Root $Output
    if ($LASTEXITCODE -ne 2) {
        throw "Expected exit code 2 for $Mode with root '$Root' and output '$Output', got $LASTEXITCODE."
    }
}

Push-Location $repository
try {
    & dotnet build -c Release tools/Scrap.Perf/Scrap.Perf.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark tool build failed.' }

    foreach ($mode in @('baseline', 'startup', 'resources')) {
        Assert-Rejected $mode $runRoot $output
    }

    $outsideRoot = Join-Path $repository 'perf-root-outside-guard'
    Assert-Rejected 'baseline' $outsideRoot $output
    if (Test-Path -LiteralPath $outsideRoot) { throw 'The rejected external root was created.' }

    $outsideOutput = Join-Path $repository 'perf-output-outside-guard.json'
    $emptyRoot = Join-Path $repository ".temp/perf-empty-guard-$([guid]::NewGuid().ToString('N'))"
    Assert-Rejected 'baseline' $emptyRoot $outsideOutput
    if (Test-Path -LiteralPath $outsideOutput) { throw 'The rejected external output was created.' }
}
finally {
    Pop-Location
}

$after = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
if ($before -ne $after) {
    throw 'The existing sentinel was modified.'
}
if (Test-Path -LiteralPath $output) {
    throw 'The rejected run created its output file.'
}

Write-Host "Run-root guard passed for all fixture modes and external paths; preserved $sentinel"
