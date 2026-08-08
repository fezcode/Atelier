$ErrorActionPreference = "Stop"

# Publishes Atelier and stamps a self-contained installer with Forge.
#   -> dist/Atelier-Setup-<version>.exe
#
# Forge reads forge.toml in this folder. Keep its [app] version in step with
# <Version> in Atelier.csproj.

$scriptDir = $PSScriptRoot
$workDir   = Split-Path $scriptDir -Parent
$forge     = Join-Path $workDir "Forge\build\forge.exe"

if (-not (Test-Path $forge)) {
    Write-Host "forge.exe not found at: $forge" -ForegroundColor Red
    Write-Host "Build it first: run 'gobake build' in the Forge project." -ForegroundColor Yellow
    exit 1
}

$forgeVersion = (& $forge --version | Out-String).Trim()
Write-Host "Using $forge -- $forgeVersion" -ForegroundColor DarkGray

# 1. Publish the app (self-contained, ReadyToRun, not single-file).
& (Join-Path $scriptDir "publish.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit 1
}

# 2. Validate the manifest before spending time bundling ~140 MB of payload.
Push-Location $scriptDir
try {
    & $forge validate
    if ($LASTEXITCODE -ne 0) {
        Write-Host "forge.toml is invalid." -ForegroundColor Red
        exit 1
    }

    Write-Host ""
    Write-Host "Building installer..." -ForegroundColor Cyan
    & $forge build
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Installer build failed." -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}

Write-Host ""
Get-ChildItem (Join-Path $scriptDir "dist") -Filter *.exe |
    ForEach-Object {
        Write-Host ("Installer: {0} ({1} MB)" -f $_.FullName, [math]::Round($_.Length / 1MB, 1)) -ForegroundColor Green
    }
