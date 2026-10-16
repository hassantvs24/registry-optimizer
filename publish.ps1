# publish.ps1 — one-command build for Registry Optimizer
# Produces:
#   dist\RegistryOptimizer.exe            (portable single-file, self-contained, no .NET needed)
#   dist\RegistryOptimizer-Setup.exe      (Inno Setup installer, if Inno is installed)
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "RegistryOptimizer\RegistryOptimizer.csproj"
$dist = Join-Path $root "dist"

Write-Host "==> Publishing RegistryOptimizer ($Configuration, $Runtime, self-contained single file)..." -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r $Runtime --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -o (Join-Path $dist "publish")

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $dist "publish\RegistryOptimizer.exe") (Join-Path $dist "RegistryOptimizer.exe") -Force
Write-Host "==> Portable exe: $dist\RegistryOptimizer.exe" -ForegroundColor Green

$iss = Join-Path $root "installer\setup.iss"
$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    # Not on PATH — check the standard Inno Setup 6 install locations.
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
        "$env:ProgramFiles\Inno Setup 6\iscc.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\iscc.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($iscc) {
    Write-Host "==> Building installer with Inno Setup..." -ForegroundColor Cyan
    & $iscc $iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
    Write-Host "==> Installer: $dist\RegistryOptimizer-Setup.exe" -ForegroundColor Green
} else {
    Write-Host "!! Inno Setup (iscc.exe) not found on PATH - skipped installer." -ForegroundColor Yellow
    Write-Host "!! Install Inno Setup 6 from https://jrsoftware.org/isdl.php to enable it." -ForegroundColor Yellow
}

Write-Host "==> Done." -ForegroundColor Green
