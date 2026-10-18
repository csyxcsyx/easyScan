param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskProject = Join-Path $taskRoot 'src/EasyScan.Desktop/EasyScan.Desktop.csproj'
$taskProjectXml = [xml](Get-Content -LiteralPath $taskProject -Raw)
$taskVersion = $taskProjectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part release version in the Desktop project.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $taskRoot "dist/easyScan-$taskVersion-win-x64" }
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
& dotnet publish $taskProject -c Release -r win-x64 --self-contained true -o $taskOutput -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($taskModel in (Get-Content -LiteralPath $taskModelManifest -Raw | ConvertFrom-Json)) {
  $taskPublishedModel = Join-Path $taskOutput "models/ocr/$($taskModel.file)"
  if (!(Test-Path -LiteralPath $taskPublishedModel) -or (Get-FileHash -LiteralPath $taskPublishedModel -Algorithm SHA256).Hash -ne $taskModel.sha256) {
    throw "Published OCR asset is missing or corrupt: $($taskModel.file)"
  }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'CHANGELOG.md') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput -Force
$taskDocs = Join-Path $taskOutput 'docs'
New-Item -ItemType Directory -Path $taskDocs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'docs') -Filter '*.md' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskDocs -Force }
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
    $taskPackageVersion = $taskDependency.Value.resolved
    if (!$taskPackageVersion -or $taskSeen.ContainsKey("$taskName/$taskPackageVersion")) { continue }
    $taskSeen["$taskName/$taskPackageVersion"] = $true
    $taskPackagePath = Join-Path $taskPackages "$taskName/$taskPackageVersion"
    if (!(Test-Path -LiteralPath $taskPackagePath)) { continue }
    Get-ChildItem -LiteralPath $taskPackagePath -Filter '*.nuspec' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskLicenses -Force }
    $taskDocuments = Get-ChildItem -LiteralPath $taskPackagePath -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING|THIRD-PARTY)' -and $_.Extension -in @('.txt','.md','') }
    foreach ($taskDocument in $taskDocuments) {
      Copy-Item -LiteralPath $taskDocument.FullName -Destination (Join-Path $taskLicenses "$taskName-$taskPackageVersion-$($taskDocument.Name)") -Force
    }
  }
}
$taskSourceCommit = (& git -C $taskRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $taskSourceCommit = $null }
$taskSourceDirty = if ($taskSourceCommit) { [bool](& git -C $taskRoot status --porcelain --untracked-files=normal) } else { $null }
@{
  Version = $taskVersion
  SourceCommit = $taskSourceCommit
  SourceDirty = $taskSourceDirty
  RuntimeIdentifier = 'win-x64'
  SelfContained = $true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'build-info.json') -Encoding utf8
$taskArchive = Join-Path (Split-Path $taskOutput -Parent) ((Split-Path $taskOutput -Leaf) + '.zip')
Compress-Archive -LiteralPath $taskOutput -DestinationPath $taskArchive -Force
$taskArchiveHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
"$taskArchiveHash  $([System.IO.Path]::GetFileName($taskArchive))" | Set-Content -LiteralPath ($taskArchive + '.sha256') -Encoding ascii
Write-Output "Portable package: $taskArchive"
