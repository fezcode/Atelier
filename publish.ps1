$ErrorActionPreference = "Stop"

$publishDir = Join-Path $PSScriptRoot "Publish"

# Clean previous publish
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "Publishing Atelier..." -ForegroundColor Cyan

# NOT single-file: PublishSingleFile bundles ~37 MB of native libraries that the
# runtime must unpack into %TEMP% before the first launch, which cost ~4s on a cold
# start. The installer ships a folder, so there is nothing to gain from bundling.
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit 1
}

$files = Get-ChildItem $publishDir -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Published to: $publishDir" -ForegroundColor Green
Write-Host ("{0} files, {1} MB" -f $files.Count, $totalMb) -ForegroundColor Yellow
