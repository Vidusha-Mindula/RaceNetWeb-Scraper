@echo off
setlocal enabledelayedexpansion

:: Cuts a full release: builds the Inno Setup installer, then creates a matching GitHub Release
:: (tag + uploaded .exe + auto-generated notes) via the GitHub CLI. Every running copy of
:: RaceNetScraper.App checks GitHub's releases/latest API on startup (see
:: src/RaceNetScraper.App/Services/UpdateChecker.cs) and shows an "Update available" banner once
:: this is live, so this script is the whole "push an update" workflow in one step.
::
:: Usage:
::     installer\release.bat            (uses the version in the VERSION file at repo root)
::     installer\release.bat 3.0.0      (overrides it, and updates the VERSION file to match)
::
:: Requires the working tree to be clean (everything already committed AND pushed) so the release
:: always matches something actually in git history. First run also needs the GitHub CLI (gh) --
:: this script installs it via winget if missing, and signs you in via a browser if needed.

set "INSTALLER_DIR=%~dp0"
set "REPO_ROOT=%INSTALLER_DIR%.."
set "VERSION_FILE=%REPO_ROOT%\VERSION"

if "%~1"=="" (
    if not exist "%VERSION_FILE%" (
        echo Usage: release.bat VERSION
        echo Example: release.bat 3.0.0
        echo Or create a VERSION file at the repo root containing the version to release.
        exit /b 1
    )
    set /p VERSION=<"%VERSION_FILE%"
) else (
    set "VERSION=%~1"
)

set "OUTPUT_EXE=%INSTALLER_DIR%output\RaceNetScraperSetup-%VERSION%.exe"

echo ==============================================
echo  Releasing RaceNet Meetings Scraper v%VERSION%
echo ==============================================

pushd "%REPO_ROOT%" || exit /b 1

:: --- 1. Working tree must be clean and match what's already on GitHub ---
set "DIRTY="
for /f "delims=" %%L in ('git status --porcelain 2^>nul') do set "DIRTY=1"
if defined DIRTY (
    echo.
    echo ERROR: You have uncommitted changes. Commit and push them first, then re-run this script.
    git status --short
    popd
    exit /b 1
)

git fetch origin --quiet
for /f "delims=" %%L in ('git rev-parse HEAD') do set "LOCAL_HEAD=%%L"
for /f "delims=" %%L in ('git rev-parse origin/main 2^>nul') do set "REMOTE_HEAD=%%L"
if not "%LOCAL_HEAD%"=="%REMOTE_HEAD%" (
    echo.
    echo ERROR: Local main doesn't match origin/main - push your commits first, then re-run this script.
    popd
    exit /b 1
)

:: --- 2. GitHub CLI must be installed ---
where gh >nul 2>nul
if errorlevel 1 (
    echo gh CLI not found - installing via winget...
    winget install --id GitHub.cli -e --source winget
    if errorlevel 1 (
        echo ERROR: Failed to install gh CLI. Install it manually from https://cli.github.com and re-run.
        popd
        exit /b 1
    )
    echo.
    echo gh CLI installed. Close and reopen this terminal so PATH picks it up, then re-run this script.
    popd
    exit /b 0
)

:: --- 3. Must be signed in ---
gh auth status >nul 2>nul
if errorlevel 1 (
    echo Not signed in to GitHub CLI - opening browser sign-in...
    gh auth login --web -h github.com
    if errorlevel 1 (
        echo ERROR: gh auth login failed.
        popd
        exit /b 1
    )
)

:: --- 4. Build the installer ---
echo.
echo ==^> Building installer v%VERSION%...
powershell -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER_DIR%build-racenet-installer.ps1" -Version %VERSION%
if errorlevel 1 (
    echo ERROR: Installer build failed.
    popd
    exit /b 1
)

if not exist "%OUTPUT_EXE%" (
    echo ERROR: Expected installer not found at %OUTPUT_EXE%
    popd
    exit /b 1
)

:: --- 5. Create the GitHub Release (gh creates + pushes the vX.Y.Z tag against the current
::        commit automatically since it doesn't exist yet) ---
echo.
echo ==^> Creating GitHub Release v%VERSION%...
gh release create v%VERSION% "%OUTPUT_EXE%" --title "v%VERSION%" --generate-notes
if errorlevel 1 (
    echo ERROR: gh release create failed. If a v%VERSION% tag was left behind, remove it with:
    echo     git push --delete origin v%VERSION% ^&^& git tag -d v%VERSION%
    echo before retrying.
    popd
    exit /b 1
)

:: --- 6. Keep the VERSION file in sync with what was just released (only touches git if the
::        file actually changed, e.g. you passed an explicit version that differs from it) ---
echo %VERSION%> "%VERSION_FILE%"
git diff --quiet -- "%VERSION_FILE%"
if errorlevel 1 (
    git add "%VERSION_FILE%"
    git commit -m "Bump version to %VERSION%" --quiet
    git push origin main --quiet
    if errorlevel 1 (
        echo WARNING: Release succeeded, but committing/pushing the VERSION bump failed.
        echo Commit and push "%VERSION_FILE%" manually so it stays in sync.
    )
)

popd
echo.
echo ==============================================
echo  Done. v%VERSION% is live - every running copy of
echo  the app will show the update banner on next launch.
echo  VERSION file is now %VERSION% - bump it before the
echo  next release.
echo ==============================================
