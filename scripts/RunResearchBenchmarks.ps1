param(
    [string]$BaselineCore = "artifacts/research-baseline/EasyScan.Core.dll",
    [string]$Output = "artifacts/research-bench",
    [string]$Data = "artifacts/research-data",
    [string]$Fixtures = "artifacts/research-final"
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
if (!(Test-Path -LiteralPath $BaselineCore)) { throw 'Provide the Core DLL saved before applying changes.' }
$benchmarkDll = 'scripts/EasyScan.Benchmarks/bin/Release/net10.0-windows/EasyScan.Benchmarks.dll'
$currentCore = 'src/EasyScan.Core/bin/Release/net10.0-windows/EasyScan.Core.dll'
$clean = Join-Path $Fixtures '扫描优化/中英文-原图.png'
$long = Join-Path $Fixtures 'research-algorithms/long-line.png'
$real = Join-Path $Data 'real-photo.jpg'
foreach ($required in @($clean, $long, $real, (Join-Path $Data 'dataset/1.bmp'))) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing $required. Run the full regression and FetchResearchSamples.py first." }
}
& dotnet build scripts/EasyScan.Benchmarks -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
$jobs = @(
    @{ Name='binary'; Mode='binary'; Iterations=15; Extra='' },
    @{ Name='enhance'; Mode='enhance'; Iterations=15; Extra='' },
    @{ Name='quality'; Mode='quality'; Iterations=1; Extra='' },
    @{ Name='holdout'; Mode='holdout'; Iterations=1; Extra='' },
    @{ Name='batch10'; Mode='batch'; Iterations=10; Extra='' },
    @{ Name='batch30'; Mode='batch'; Iterations=30; Extra='' },
    @{ Name='dibco'; Mode='dibco'; Iterations=1; Extra=$Data },
    @{ Name='ocr'; Mode='ocr'; Iterations=30; Extra=$clean },
    @{ Name='ocr-long'; Mode='ocr-long'; Iterations=1; Extra=$long },
    @{ Name='real'; Mode='real'; Iterations=1; Extra=$real },
    @{ Name='numeric'; Mode='numeric'; Iterations=1; Extra='' }
)
foreach ($job in $jobs) {
    foreach ($tag in @('baseline', 'current')) {
        $core = if ($tag -eq 'baseline') { $BaselineCore } else { $currentCore }
        $report = Join-Path $Output ($tag + '-' + $job.Name + '.json')
        & dotnet $benchmarkDll $core $report $job.Mode $job.Iterations $job.Extra
        if ($LASTEXITCODE -ne 0) { throw "Failed: $tag / $($job.Name)" }
    }
}
& python scripts/VerifyResearchBenchmarks.py $Output
if ($LASTEXITCODE -ne 0) { throw 'Research acceptance gates failed; inspect individual reports.' }
