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

# forge.exe is linked with -H windowsgui. PowerShell's call operator does NOT wait
# for a GUI-subsystem process: `& $forge build` returns immediately, so the script
# raced ahead to look for an installer that was still being written, and
# $LASTEXITCODE was never set -- meaning a genuine forge failure read as success.
# Start-Process -Wait gives us both the wait and a real exit code.
function Invoke-Forge {
    param([Parameter(Mandatory = $true)][string[]] $ForgeArgs)

    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $forge -ArgumentList $ForgeArgs `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile

        Get-Content $outFile -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
        Get-Content $errFile -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }

        return $proc.ExitCode
    }
    finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

$forgeVersion = (Invoke-Forge @("--version")) | Out-Null
Write-Host "Using $forge" -ForegroundColor DarkGray

# 1. Publish the app (self-contained, ReadyToRun, not single-file).
& (Join-Path $scriptDir "publish.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit 1
}

# 2. Validate the manifest before spending time bundling ~140 MB of payload.
Push-Location $scriptDir
try {
    if ((Invoke-Forge @("validate")) -ne 0) {
        Write-Host "forge.toml is invalid." -ForegroundColor Red
        exit 1
    }

    Write-Host ""
    Write-Host "Building installer..." -ForegroundColor Cyan
    if ((Invoke-Forge @("build")) -ne 0) {
        Write-Host "Installer build failed." -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}

Write-Host ""
$built = @(Get-ChildItem (Join-Path $scriptDir "dist") -Filter *.exe -ErrorAction SilentlyContinue)
if ($built.Count -eq 0) {
    Write-Host "forge build reported success but produced no installer." -ForegroundColor Red
    exit 1
}

$built | ForEach-Object {
    Write-Host ("Installer: {0} ({1} MB)" -f $_.FullName, [math]::Round($_.Length / 1MB, 1)) -ForegroundColor Green
}
