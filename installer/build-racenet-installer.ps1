<#
    Builds a self-contained win-x64 publish of RaceNetScraper.App and compiles it
    into a single Inno Setup installer (RaceNetScraperSetup-<version>.exe).

    Usage:
        powershell installer/build-racenet-installer.ps1                  (reads version from VERSION at repo root)
        powershell installer/build-racenet-installer.ps1 -Version 1.2.0   (overrides it)

    S3AccessKey/S3SecretKey are baked into the installer (it seeds a default
    %LOCALAPPDATA%\RaceNetScraper\settings.json on first install — see the [Code] section in
    RaceNetScraper.iss) so a fresh install doesn't need the bucket/keys typed in by hand. They are
    NEVER hardcoded in this script or the .iss source (both are public) — pass them explicitly or
    set RACENET_S3_ACCESS_KEY / RACENET_S3_SECRET_KEY as environment variables on the machine that
    builds the installer. Leaving both unset just ships an installer with blank defaults, same as
    before this existed.
#>
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$S3AccessKey = $env:RACENET_S3_ACCESS_KEY,
    [string]$S3SecretKey = $env:RACENET_S3_SECRET_KEY
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $repoRoot "installer"

if (-not $Version) {
    $versionFile = Join-Path $repoRoot "VERSION"
    if (Test-Path $versionFile) {
        $Version = (Get-Content $versionFile -Raw).Trim()
    } else {
        throw "No -Version given and no VERSION file found at $versionFile."
    }
}
$publishDir = Join-Path $installerDir "publish-racenet"
$outputDir = Join-Path $installerDir "output"
$appProject = Join-Path $repoRoot "src\RaceNetScraper.App\RaceNetScraper.App.csproj"
$issScript = Join-Path $installerDir "RaceNetScraper.iss"

$isccPath = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue).Path
if (-not $isccPath) {
    $defaultIscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $defaultIscc) {
        $isccPath = $defaultIscc
    } else {
        throw "ISCC.exe (Inno Setup Compiler) not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php or add it to PATH."
    }
}

Write-Host "==> Publishing $appProject (self-contained win-x64, v$Version)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "==> Compiling installer with Inno Setup" -ForegroundColor Cyan
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir | Out-Null }

& $isccPath "/DAppVersion=$Version" "/DS3AccessKey=$S3AccessKey" "/DS3SecretKey=$S3SecretKey" "/O$outputDir" $issScript
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

Write-Host "==> Done. Installer written to $outputDir" -ForegroundColor Green
Get-ChildItem $outputDir -Filter "RaceNetScraperSetup-*.exe" | ForEach-Object { Write-Host "    $($_.FullName)" }
