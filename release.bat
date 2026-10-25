@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul

set "PROJ=%~dp0"
cd /d "%PROJ%"

set "VERSION=%~1"
set "TITLE=%~2"
if "%TITLE%"=="" set "TITLE=%VERSION%"

if "%VERSION%"=="" goto :usage

powershell -NoProfile -Command "if ('%VERSION%' -notmatch '^\d+\.\d+\.\d+$') { exit 1 }"
if errorlevel 1 (
    echo [ERROR] Version must look like 1.2.3
    goto :fail
)

where gh >nul 2>nul
if errorlevel 1 (
    echo [ERROR] GitHub CLI ^(gh^) not found in PATH
    goto :fail
)

set "DRYRUN=%DRYRUN%"
for /f "delims=" %%I in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY echo [!] Uncommitted changes will be included in the commit.

echo.
echo [1/5] Setting version %VERSION% in DsoHelper.csproj ...

rem Read/write as UTF-8 explicitly: Get-Content in PowerShell 5.1 falls back to the ANSI
rem code page for BOM-less files and mangles non-ASCII comments in the csproj.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p='DsoHelper.csproj'; $c=[IO.File]::ReadAllText($p);" ^
  "$c=$c -replace '<Version>[^<]+</Version>', '<Version>%VERSION%</Version>';" ^
  "$c=$c -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>%VERSION%</AssemblyVersion>';" ^
  "$c=$c -replace '<FileVersion>[^<]+</FileVersion>', '<FileVersion>%VERSION%</FileVersion>';" ^
  "[IO.File]::WriteAllText($p, $c, (New-Object Text.UTF8Encoding $false))"

if errorlevel 1 goto :fail

if not "%DRYRUN%"=="1" (
    echo [2/5] Commit, push and tag v%VERSION% ...
    git add -A
    rem Re-running for an existing version may have nothing new to commit - that is fine.
    git diff --cached --quiet
    if errorlevel 1 (
        git commit -m "Bump version to %VERSION%; %TITLE%"
        if errorlevel 1 (
            echo [ERROR] git commit failed - see messages above
            goto :fail
        )
    ) else (
        echo        Nothing new to commit - v%VERSION% is already in the tree.
    )
    git push origin main
    if errorlevel 1 goto :fail
    git tag -f v%VERSION%
    if errorlevel 1 goto :fail
    rem --force: a re-run moves the tag to the new commit instead of being rejected.
    git push --force origin v%VERSION%
    if errorlevel 1 goto :fail
) else (
    echo [2/5] DRY RUN - skipping commit/push/tag
)

echo [3/5] Building (dotnet publish, single-file, trimmed) - takes a couple of minutes ...

dotnet publish -c Release -r win-x64 /p:PublishProfile=win-x64
if errorlevel 1 (
    echo [ERROR] Build failed
    goto :fail
)

set "EXE=dist\win-x64\DsoHelper.exe"
if not exist "%EXE%" (
    echo [ERROR] %EXE% was not produced
    goto :fail
)
for %%F in ("%EXE%") do set "SIZE=%%~zF"
echo        Built: %EXE% (!SIZE! bytes)

rem Optional Authenticode signing: set SIGN_THUMBPRINT (and SIGNTOOL, if signtool is not in PATH).
if not defined SIGN_THUMBPRINT goto :hash
if defined SIGNTOOL (set "ST=%SIGNTOOL%") else (set "ST=signtool")
echo        Signing with certificate %SIGN_THUMBPRINT% ...
"%ST%" sign /sha1 "%SIGN_THUMBPRINT%" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "%EXE%"
if errorlevel 1 (
    echo [WARN] Signing failed - the exe stays unsigned.
) else (
    echo        Signed: %EXE%
)

:hash
rem SHA-256 of the artifact goes into the release notes, so users can verify the download.
set "HASH="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%EXE%" SHA256') do if not defined HASH set "HASH=%%H"
set "HASH=%HASH: =%"
set "SHA256=%HASH%"
if defined SHA256 (echo        SHA-256: %SHA256%) else (echo [WARN] Could not compute SHA-256.)

echo [4/5] Writing release notes ...

set "POINTS="
shift
shift
:points_loop
if "%~1"=="" goto :points_done
if defined POINTS (set "POINTS=!POINTS!@@@%~1") else (set "POINTS=%~1")
shift
goto :points_loop
:points_done
if not defined POINTS set "POINTS=Fixes and improvements."

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$items = $env:POINTS -split '@@@' | Where-Object { $_.Trim() -ne '' };" ^
  "if (-not $items) { $items = @('Fixes and improvements.') };" ^
  "$lines = @('## What''s new') + ($items | ForEach-Object { '- ' + $_.Trim() });" ^
  "if ($env:SHA256) { $lines += ''; $lines += 'SHA-256: ' + $env:SHA256 };" ^
  "[IO.File]::WriteAllLines('release-notes.md', $lines, (New-Object Text.UTF8Encoding $false))"

if not exist "release-notes.md" goto :fail

if "%DRYRUN%"=="1" (
    echo.
    echo ---- release-notes.md ----
    type release-notes.md
    echo ------------------------
    echo.
    echo DRY RUN - nothing was pushed or published.
    del /q "release-notes.md" >nul 2>nul
    goto :eof
)

echo [5/5] Publishing release v%VERSION% on GitHub ...

gh release create v%VERSION% "%EXE%" --latest --title "v%VERSION% - %TITLE%" --notes-file "release-notes.md"
if errorlevel 1 (
    echo.
    echo [!] Could not create the release - v%VERSION% may already exist.
    echo     Replacing the exe in the existing release instead ...
    gh release upload v%VERSION% "%EXE%" --clobber
    if errorlevel 1 goto :fail
    gh release edit v%VERSION% --latest --title "v%VERSION% - %TITLE%" --notes-file "release-notes.md"
    if errorlevel 1 goto :fail
)

del /q "release-notes.md" >nul 2>nul

echo.
echo ============================================================
echo  DONE: v%VERSION% published and marked as Latest.
echo  Check: gh release view v%VERSION% --web
echo ============================================================
goto :eof

:usage
echo.
echo ============================================================
echo  Usage:
echo    release.bat ^<version^> "^<title^>" "^<point 1^>" "^<point 2^>" ...
echo.
echo  Example:
echo    release.bat 1.4.4 "common quality removed" "Removed the Common checkbox" "Fixed texts"
echo.
echo  Dry run (no push, no publish):
echo    set DRYRUN=1 ^&^& release.bat 1.4.4 "test"
echo.
echo  Optional code signature (signtool must be in PATH or set SIGNTOOL):
echo    set SIGN_THUMBPRINT=^<certificate thumbprint^>
echo.
echo  The SHA-256 of the built exe is written into the release notes.
echo ============================================================
pause
exit /b 1

:fail
echo.
echo [FAILED] See messages above.
echo If the commit and tag were pushed but the release was not created,
echo run this script again with the same version - it replaces the exe.
pause
exit /b 1
