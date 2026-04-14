# Quick deploy for Revit 2026 testing
# Auto-elevates to admin if needed

# --- Auto-elevate to admin ---
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$ErrorActionPreference = "Stop"
$root = Split-Path $PSCommandPath -Parent

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  BIBIM AI - Deploy (Revit 2026)" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# 1. Build
Write-Host "[1/3] Building for Revit 2026..." -ForegroundColor Yellow
dotnet build "$root\Bibim.Core\Bibim.Core.csproj" -c R2026
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "[1/3] Build done" -ForegroundColor Green
Write-Host ""

# 2. Copy DLLs
$installDir = "C:\Program Files\BIBIM AI\net8.0"
Write-Host "[2/3] Deploying to $installDir ..." -ForegroundColor Yellow
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}
$sourceDir = "$root\Bibim.Core\bin\R2026\net8.0-windows"
Copy-Item "$sourceDir\*" $installDir -Recurse -Force
Write-Host "[2/3] Deploy done" -ForegroundColor Green
Write-Host ""

# 3. Install .addin
$addinDir = "$env:ProgramData\Autodesk\Revit\Addins\2026"
Write-Host "[3/3] Installing .addin ..." -ForegroundColor Yellow
if (-not (Test-Path $addinDir)) {
    New-Item -ItemType Directory -Path $addinDir -Force | Out-Null
}
Copy-Item "$root\Bibim.Core\Bibim.Core.net8.addin" "$addinDir\Bibim.Core.addin" -Force
Write-Host "[3/3] Done" -ForegroundColor Green
Write-Host ""

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Deploy complete! Restart Revit 2026" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
pause
