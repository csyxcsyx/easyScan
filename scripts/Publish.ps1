param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $taskRoot 'dist/easyScan-1.3-win-x64' }
$taskOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$taskModelDirectory = Join-Path $taskRoot 'models/ocr'
$taskModelManifest = Join-Path $taskModelDirectory 'manifest.json'
if (!(Test-Path -LiteralPath $taskModelManifest)) { throw 'Missing OCR assets. Run python scripts/FetchOcrModels.py before packaging.' }
foreach ($taskModel in (Get-Content -LiteralPath $taskModelManifest -Raw | ConvertFrom-Json)) {
  $taskModelPath = Join-Path $taskModelDirectory $taskModel.file
  if (!(Test-Path -LiteralPath $taskModelPath) -or (Get-FileHash -LiteralPath $taskModelPath -Algorithm SHA256).Hash -ne $taskModel.sha256) {
    throw "OCR asset is missing or corrupt: $($taskModel.file). Run python scripts/FetchOcrModels.py."
  }
}
& dotnet publish (Join-Path $taskRoot 'src/EasyScan.Desktop/EasyScan.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $taskOutput -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput '使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput -Force
if (Test-Path -LiteralPath (Join-Path $taskRoot 'VERIFICATION.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot 'VERIFICATION.md') -Destination $taskOutput -Force }
if (Test-Path -LiteralPath (Join-Path $taskRoot 'RESEARCH_ALGORITHMS.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot 'RESEARCH_ALGORITHMS.md') -Destination $taskOutput -Force }
$taskLicenses = Join-Path $taskOutput 'licenses'
New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'licenses') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskLicenses -Force }
foreach ($taskUnusedName in @('opencv_videoio_ffmpeg4130_64.dll', 'libSkiaSharp.pdb')) {
  $taskUnusedPath = Join-Path $taskOutput $taskUnusedName
  if (Test-Path -LiteralPath $taskUnusedPath) { Remove-Item -LiteralPath $taskUnusedPath -Force }
}
$taskPackages = Join-Path $env:USERPROFILE '.nuget/packages'
$taskLock = Get-Content -LiteralPath (Join-Path $taskRoot 'src/EasyScan.Desktop/packages.lock.json') -Raw | ConvertFrom-Json
$taskSeen = @{}
foreach ($taskFramework in $taskLock.dependencies.PSObject.Properties) {
  foreach ($taskDependency in $taskFramework.Value.PSObject.Properties) {
    $taskName = $taskDependency.Name.ToLowerInvariant()
    $taskVersion = $taskDependency.Value.resolved
    if (!$taskVersion -or $taskSeen.ContainsKey("$taskName/$taskVersion")) { continue }
    $taskSeen["$taskName/$taskVersion"] = $true
    $taskPackagePath = Join-Path $taskPackages "$taskName/$taskVersion"
    if (!(Test-Path -LiteralPath $taskPackagePath)) { continue }
    Get-ChildItem -LiteralPath $taskPackagePath -Filter '*.nuspec' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskLicenses -Force }
    $taskDocuments = Get-ChildItem -LiteralPath $taskPackagePath -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING|THIRD-PARTY)' -and $_.Extension -in @('.txt','.md','') }
    foreach ($taskDocument in $taskDocuments) {
      Copy-Item -LiteralPath $taskDocument.FullName -Destination (Join-Path $taskLicenses "$taskName-$taskVersion-$($taskDocument.Name)") -Force
    }
  }
}
$taskArchive = Join-Path (Split-Path $taskOutput -Parent) ((Split-Path $taskOutput -Leaf) + '.zip')
Compress-Archive -LiteralPath $taskOutput -DestinationPath $taskArchive -Force
Write-Output "Portable package: $taskArchive"
