# build.ps1 — Pedal Filter build script
param([string]$BuzzDir = "C:\Program Files\ReBuzz")
$ErrorActionPreference = "Stop"

Write-Host "=== Pedal Filter – ReBuzz managed effect ===" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: dotnet not found. Install .NET 10.0 SDK from https://dotnet.microsoft.com/en-us/download/dotnet/10.0" -ForegroundColor Red
    exit 1
}

$reBuzzDll = Join-Path $BuzzDir "ReBuzz.dll"
if (-not (Test-Path $reBuzzDll)) {
    Write-Host "ERROR: ReBuzz.dll not found at: $reBuzzDll" -ForegroundColor Red
    exit 1
}

Write-Host "ReBuzz: $BuzzDir" -ForegroundColor Green
dotnet build PedalFilter.csproj -c Release /p:BuzzDir="$BuzzDir"

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nBuild succeeded! Restart ReBuzz and look for 'Pedal Filter' in Effects." -ForegroundColor Green
} else {
    Write-Host "`nBuild FAILED." -ForegroundColor Red; exit 1
}
