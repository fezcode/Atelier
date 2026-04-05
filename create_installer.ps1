$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
$workDir = Split-Path $scriptDir -Parent

$builder = Join-Path $workDir "DeployPaladin\release\builder\DeployPaladin.Builder.exe"
$base = Join-Path $workDir "DeployPaladin\release\installer\DeployPaladin.exe"
$payload = $scriptDir
$output = Join-Path $scriptDir "Atelier_Installer_0.2.78.exe"

Write-Host "Building installer..." -ForegroundColor Cyan

& $builder --payload $payload --base $base --output $output

if ($LASTEXITCODE -ne 0) {
    Write-Host "Installer build failed." -ForegroundColor Red
    exit 1
}

Write-Host "Installer created: $output" -ForegroundColor Green
