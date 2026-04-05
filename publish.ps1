$ErrorActionPreference = "Stop"

$publishDir = Join-Path $PSScriptRoot "Publish"

# Clean previous publish
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "Publishing Atelier..." -ForegroundColor Cyan

dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Published to: $publishDir" -ForegroundColor Green
Write-Host "Files:" -ForegroundColor Yellow
Get-ChildItem $publishDir | Format-Table Name, @{N="Size (MB)";E={[math]::Round($_.Length / 1MB, 2)}} -AutoSize
