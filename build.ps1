#!/usr/bin/env pwsh
# BIBIM Build Pipeline
# Usage: .\build.ps1
# Usage: .\build.ps1 -RevitConfig R2026
# Usage: .\build.ps1 -SkipFrontend -SkipTests

param(
    [string]$RevitConfig = "Release",
    [string]$RevitSdkPath = "",
    [ValidateSet("ko","en","all")]
    [string]$Lang = "all",
    [switch]$SkipFrontend,
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

function Remove-IfExists {
    param([string]$PathToRemove)

    if (Test-Path $PathToRemove) {
        Remove-Item $PathToRemove -Recurse -Force
    }
}

function Get-GitShortHash {
    param([string]$RepoRoot)

    try {
        $gitCmd = Get-Command git -ErrorAction SilentlyContinue
        if (-not $gitCmd) { return "" }

        $hash = & $gitCmd.Source -C $RepoRoot rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($hash)) {
            return $hash.Trim()
        }
    }
    catch {
    }

    return ""
}

# --- Auto-elevate to admin ---
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = "-ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($RevitConfig -ne "Release") { $argList += " -RevitConfig $RevitConfig" }
    if ($RevitSdkPath) { $argList += " -RevitSdkPath `"$RevitSdkPath`"" }
    if ($Lang -ne "all") { $argList += " -Lang $Lang" }
    if ($SkipFrontend) { $argList += " -SkipFrontend" }
    if ($SkipTests) { $argList += " -SkipTests" }
    if ($SkipInstaller) { $argList += " -SkipInstaller" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = "Stop"
$root = Split-Path $PSCommandPath -Parent
$buildStamp = Get-Date -Format "yyyyMMdd_HHmmss"
$gitHash = Get-GitShortHash -RepoRoot $root
$buildId = if ($gitHash) { "${buildStamp}_${gitHash}" } else { $buildStamp }

[xml]$csproj = Get-Content "$root\Bibim.Core\Bibim.Core.csproj"
$appVersion = $csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1 -ExpandProperty Version
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    $appVersion = "0.0.0"
}
$informationalVersion = "$appVersion+$buildId"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  BIBIM AI - Build Pipeline" -ForegroundColor Cyan
Write-Host "  Config: $RevitConfig  Lang: $Lang" -ForegroundColor Gray
Write-Host "  Version: $appVersion  BuildId: $buildId" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if ($RevitSdkPath) {
    $env:REVIT_SDK_PATH = $RevitSdkPath
    Write-Host "[INFO] REVIT_SDK_PATH = $RevitSdkPath" -ForegroundColor Gray
}

# -- Step 0: Reset stale build artifacts (keep Output installers) --
Write-Host "[0/5] Resetting stale build artifacts..." -ForegroundColor Yellow

$artifactDirs = @(
    "$root\Bibim.Core\bin\Release",
    "$root\Bibim.Core\bin\Release_EN",
    "$root\Bibim.Core\obj",
    "$root\Bibim.Core.Tests\bin",
    "$root\Bibim.Core.Tests\obj"
)

foreach ($artifactDir in $artifactDirs) {
    Remove-IfExists -PathToRemove $artifactDir
}

Write-Host "[0/5] Stale build artifacts cleared (Output preserved)" -ForegroundColor Green
Write-Host ""

# -- Step 1: Frontend build (React -> wwwroot) --
if (-not $SkipFrontend) {
    Write-Host "[1/5] Building frontend..." -ForegroundColor Yellow
    Push-Location "$root\Bibim.Core\frontend"
    npm install --silent 2>&1 | Out-Null
    npm run build
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Frontend build failed" }
    Pop-Location
    Write-Host "[1/5] Frontend build done" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "[1/5] Frontend build skipped" -ForegroundColor DarkGray
    Write-Host ""
}

# -- Step 2: C# build (dual target, KO/EN) --
Write-Host "[2/5] Building C# (net48 / net8.0 / net10.0) config=$RevitConfig lang=$Lang ..." -ForegroundColor Yellow

$langsToBuild = @()
if ($Lang -eq "all") { $langsToBuild = @("ko", "en") }
else { $langsToBuild = @($Lang) }

foreach ($buildLang in $langsToBuild) {
    $langSuffix = if ($buildLang -eq "en") { "_EN" } else { "" }
    $outDir = "$root\Bibim.Core\bin\Release$langSuffix"
    $extraArgs = @()
    if ($buildLang -eq "en") {
        $extraArgs += "/p:AppLang=en"
    }

    Write-Host "  Building [$buildLang] ..." -ForegroundColor Gray

    $buildTargets = @()
    if ($RevitConfig -eq "Release") {
        # R2027: net10.0-windows — requires .NET 10 SDK; skipped if not installed
        $buildTargets = @(
            @{ Config = "R2027"; Framework = "net10.0-windows"; Folder = "2027" },
            @{ Config = "R2026"; Framework = "net8.0-windows";  Folder = "2026" },
            @{ Config = "R2025"; Framework = "net8.0-windows";  Folder = "2025" },
            @{ Config = "R2024"; Framework = "net48";           Folder = "2024" },
            @{ Config = "R2023"; Framework = "net48";           Folder = "2023" },
            @{ Config = "R2022"; Framework = "net48";           Folder = "2022" }
        )
    } else {
        $targetFramework = switch ($RevitConfig) {
            "R2027" { "net10.0-windows" }
            "R2026" { "net8.0-windows" }
            "R2025" { "net8.0-windows" }
            default { "net48" }
        }
        $targetFolder = $RevitConfig -replace "^R", ""
        $buildTargets = @(
            @{ Config = $RevitConfig; Framework = $targetFramework; Folder = $targetFolder }
        )
    }

    foreach ($target in $buildTargets) {
        $targetConfig = $target.Config
        $targetFramework = $target.Framework
        $targetFolder = $target.Folder
        $targetOutput = "$outDir\$targetFolder"
        $sdkYear = $targetFolder
        $sdkPath = if ($RevitSdkPath) { Join-Path $RevitSdkPath '' } else { "C:\Program Files\Autodesk\Revit $sdkYear" }

        if (-not (Test-Path $sdkPath)) {
            Write-Host "    -> skipping $targetConfig ($targetFramework): Revit not found at $sdkPath" -ForegroundColor DarkYellow
            continue
        }

        # net10.0-windows requires .NET 10 SDK — skip gracefully if not installed
        if ($targetFramework -eq "net10.0-windows") {
            $dotnet10 = dotnet --list-sdks 2>$null | Where-Object { $_ -match "^10\." }
            if (-not $dotnet10) {
                Write-Host "    -> skipping ${targetConfig}: .NET 10 SDK not installed (download from https://aka.ms/dotnet/download)" -ForegroundColor DarkYellow
                continue
            }
        }

        Write-Host "    -> $targetConfig / $targetFramework" -ForegroundColor DarkGray
        dotnet build "$root\Bibim.Core\Bibim.Core.csproj" -c $targetConfig @extraArgs -o $targetOutput /p:TargetFramework=$targetFramework /p:BuildId=$buildId /p:InformationalVersion=$informationalVersion
        if ($LASTEXITCODE -ne 0) {
            throw "C# build failed ($buildLang $targetConfig $targetFramework)"
        }
    }

    Write-Host "  [$buildLang] done -> $outDir" -ForegroundColor Gray
}

Write-Host "[2/5] C# build done" -ForegroundColor Green
Write-Host ""

# -- Step 3: Tests --
if (-not $SkipTests) {
    Write-Host "[3/5] Running tests..." -ForegroundColor Yellow
    dotnet test "$root\Bibim.Core.Tests\Bibim.Core.Tests.csproj" --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
    Write-Host "[3/5] Tests passed" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "[3/5] Tests skipped" -ForegroundColor DarkGray
    Write-Host ""
}

# -- Step 4: Installer (Inno Setup) --
if (-not $SkipInstaller) {
    Write-Host "[4/5] Building installer..." -ForegroundColor Yellow

    $isccExe = $null
    $isccCmd = Get-Command "iscc" -ErrorAction SilentlyContinue
    if ($isccCmd) {
        $isccExe = $isccCmd.Source
    } else {
        $defaultPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        if (Test-Path $defaultPath) {
            $isccExe = $defaultPath
        }
    }

    if ($isccExe) {
        # Build KO installer
        if ($Lang -eq "all" -or $Lang -eq "ko") {
            Write-Host "  Building KO installer..." -ForegroundColor Gray
            & $isccExe "/DMyBuildId=$buildId" "/DMyAppVersion=$appVersion" "$root\Bibim.Core\BibimInstaller.iss"
            if ($LASTEXITCODE -ne 0) { throw "KO installer build failed" }
        }
        # Build EN installer
        if ($Lang -eq "all" -or $Lang -eq "en") {
            Write-Host "  Building EN installer..." -ForegroundColor Gray
            & $isccExe "/DMyBuildId=$buildId" "/DMyAppVersion=$appVersion" "$root\Bibim.Core\BibimInstaller_EN.iss"
            if ($LASTEXITCODE -ne 0) { throw "EN installer build failed" }
        }
        Write-Host "[4/5] Installer done" -ForegroundColor Green

        $setupExes = Get-ChildItem "$root\Bibim.Core\Output\*.exe" -ErrorAction SilentlyContinue
        foreach ($exe in $setupExes) {
            Write-Host "  Setup: $($exe.FullName)" -ForegroundColor Gray
        }
    } else {
        Write-Host "[4/5] Inno Setup not found - installer skipped" -ForegroundColor DarkYellow
    }
    Write-Host ""
} else {
    Write-Host "[4/5] Installer skipped" -ForegroundColor DarkGray
    Write-Host ""
}

# -- Step 5: Code Signing (DigiCert EV Certificate) --
Write-Host "[5/5] Code Signing..." -ForegroundColor Yellow

$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

if (-not (Test-Path $signtool)) {
    $sdkBase = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $sdkBase) {
        $sdkVer = Get-ChildItem $sdkBase -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($sdkVer) {
            $altPath = Join-Path $sdkVer.FullName "x64\signtool.exe"
            if (Test-Path $altPath) { $signtool = $altPath }
        }
    }
}

if (Test-Path $signtool) {
    $signArgs = @("sign", "/a", "/tr", "http://timestamp.digicert.com", "/td", "sha256", "/fd", "sha256")

    # Sign the installer EXEs
    $setupExes = Get-ChildItem "$root\Bibim.Core\Output\*.exe" -ErrorAction SilentlyContinue
    foreach ($setupExe in $setupExes) {
        Write-Host "  Signing: $($setupExe.Name)" -ForegroundColor Gray
        & $signtool @signArgs /v $setupExe.FullName
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  WARNING: Installer signing failed (USB token connected?)" -ForegroundColor Red
        }
    }

    # Sign the main DLLs (both KO and EN)
    $dllsToSign = @(
        "$root\Bibim.Core\bin\Release\2026\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release\2025\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release\2024\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release\2023\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release\2022\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release_EN\2026\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release_EN\2025\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release_EN\2024\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release_EN\2023\Bibim.Core.dll",
        "$root\Bibim.Core\bin\Release_EN\2022\Bibim.Core.dll"
    )
    foreach ($dll in $dllsToSign) {
        if (Test-Path $dll) {
            $tfName = Split-Path (Split-Path $dll) -Leaf
            Write-Host "  Signing: Bibim.Core.dll ($tfName)" -ForegroundColor Gray
            & $signtool @signArgs $dll
            if ($LASTEXITCODE -ne 0) {
                Write-Host "  WARNING: DLL signing failed for $tfName" -ForegroundColor DarkYellow
            }
        }
    }
    Write-Host "[5/5] Code signing done" -ForegroundColor Green
} else {
    Write-Host "[5/5] signtool.exe not found - code signing skipped" -ForegroundColor DarkYellow
}
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build complete!" -ForegroundColor Cyan
Write-Host "  Output: Bibim.Core\Output\" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
pause
