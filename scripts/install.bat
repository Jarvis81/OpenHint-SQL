@echo off
setlocal EnableDelayedExpansion

REM ============================================================
REM  OpenHintSQL - Install to all installed SSMS versions
REM  Run as Administrator
REM
REM  Supported: SSMS 18.12.1 / 19.3 / 20.2.1 / 21.x / 22.x
REM
REM  Usage:  scripts\install.bat         - installs to every detected version
REM          scripts\install.bat 20      - installs to SSMS 20 only
REM ============================================================

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "BUILD_DIR=%REPO_ROOT%\src\OpenHintSQL\bin\Release\net48"
set "TARGET_VERSION=%~1"
set "BUILD_LOG=%TEMP%\OpenHintSQL-build.log"

echo.
echo  OpenHintSQL Install Script
echo  ================================
echo.

REM ── Check admin rights ─────────────────────────────────────
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo  ERROR: Please run this script as Administrator!
    pause
    exit /b 1
)

tasklist 2>nul | find /I "Ssms.exe" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo  ERROR: SSMS is currently running.
    echo  Please save your work, close all SSMS windows, then run scripts\install.bat again.
    pause
    exit /b 1
)

REM ── Locate MSBuild ─────────────────────────────────────────
REM VSCT compilation requires the full .NET Framework MSBuild that ships with
REM Visual Studio / Build Tools — the .NET Core "dotnet build" CLI cannot run
REM the VSSDK task that embeds Menus.ctmenu.
REM
REM Detection order:
REM   1. MSBuild.exe already on PATH
REM   2. vswhere — finds any VS edition or Build Tools without requiring specific components
REM   3. Known install paths for VS 2022 (64-bit) and VS 2019 (32-bit)
set "MSBUILD_EXE="

REM 1. Check PATH
for /f "delims=" %%I in ('where MSBuild.exe 2^>nul') do (
    if not defined MSBUILD_EXE set "MSBUILD_EXE=%%I"
)

REM 2. vswhere — no -requires so it matches every edition and Build Tools install
if not defined MSBUILD_EXE (
    set "_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if exist "!_VSWHERE!" (
        for /f "usebackq delims=" %%I in (`"!_VSWHERE!" -latest -find "MSBuild\**\Bin\MSBuild.exe" 2^>nul`) do (
            if not defined MSBUILD_EXE set "MSBUILD_EXE=%%I"
        )
    )
)

REM 3. Hardcoded fallback paths — VS 2022 (Program Files 64-bit) then VS 2019 (Program Files x86)
REM    NOTE: paths MUST be quoted with "%%~P" in the if-exist check, otherwise CMD
REM    splits at the space in "Program Files" and tries to run "C:\Program" as a command.
if not defined MSBUILD_EXE (
    for %%P in (
        "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    ) do if exist "%%~P" if not defined MSBUILD_EXE set "MSBUILD_EXE=%%~P"
)

if not defined MSBUILD_EXE (
    echo  ERROR: Could not find MSBuild.
    echo  To fix, choose one of:
    echo    1. Install Visual Studio 2019 or 2022 ^(any edition^)
    echo    2. Install "Build Tools for Visual Studio" and select the
    echo       "Visual Studio extension development" workload
    echo    3. Add MSBuild.exe to your PATH manually
    pause
    exit /b 1
)

REM ── Auto-build project ─────────────────────────────────────
REM -t:Rebuild (not incremental) ensures Menus.ctmenu is re-embedded correctly.
echo  Building OpenHintSQL (Release, full rebuild)...
"%MSBUILD_EXE%" "%REPO_ROOT%\src\OpenHintSQL\OpenHintSQL.csproj" -t:Restore -p:Configuration=Release -nologo -verbosity:minimal > "%BUILD_LOG%" 2>&1
if %ERRORLEVEL% equ 0 (
    "%MSBUILD_EXE%" "%REPO_ROOT%\src\OpenHintSQL\OpenHintSQL.csproj" -t:Rebuild -p:Configuration=Release -nologo -verbosity:minimal >> "%BUILD_LOG%" 2>&1
)
if %ERRORLEVEL% neq 0 (
    echo  ERROR: Build failed! Check the errors above.
    type "%BUILD_LOG%"
    pause
    exit /b 1
)
echo  Build completed.

REM ── Double-check build output ──────────────────────────────
if not exist "%BUILD_DIR%\OpenHintSQL.dll" (
    echo  ERROR: Build output not found in %BUILD_DIR%.
    pause
    exit /b 1
)

REM ── SSMS version table ─────────────────────────────────────
REM Format: VERSION  PF_DIR  ISOSHELL_SUFFIX
REM SSMS 18-20 install to Program Files (x86)  [32-bit]
REM SSMS 21-22 install to Program Files        [64-bit]

set INSTALLED=0

for %%V in (18 19 20 21 22) do (
    if "!TARGET_VERSION!"=="" (
        call :install_version %%V
    ) else if "!TARGET_VERSION!"=="%%V" (
        call :install_version %%V
    )
)

if %INSTALLED%==0 (
    echo  No matching SSMS installation found.
    if not "%TARGET_VERSION%"=="" (
        echo  SSMS %TARGET_VERSION% does not appear to be installed.
    ) else (
        echo  None of SSMS 18/19/20/21/22 were found on this machine.
    )
    pause
    exit /b 1
)

echo.
echo  ================================================
echo   Install complete!  %INSTALLED% SSMS version(s) updated.
echo   Open SSMS and check View ^> Output ^> OpenHint SQL.
echo  ================================================
echo.
pause
exit /b 0


REM ============================================================
:install_version
REM  Args: %1 = major version number (18/19/20/21/22)
REM ============================================================
set V=%1

REM SSMS 21+ are 64-bit — check both Program Files paths (prefer x86 first
REM to match Microsoft's historical convention; fall back to 64-bit)
set SSMS_DIR=
for %%P in (
    "%ProgramFiles(x86)%\Microsoft SQL Server Management Studio %V%\Common7\IDE"
    "%ProgramFiles%\Microsoft SQL Server Management Studio %V%\Common7\IDE"
    "%ProgramFiles(x86)%\Microsoft SQL Server Management Studio %V%\Release\Common7\IDE"
    "%ProgramFiles%\Microsoft SQL Server Management Studio %V%\Release\Common7\IDE"
) do (
    if "!SSMS_DIR!"=="" if exist "%%~P\Ssms.exe" set SSMS_DIR=%%~P
)

if "!SSMS_DIR!"=="" (
    echo  SSMS %V% : not installed, skipping.
    goto :eof
)

echo  SSMS %V% : found at !SSMS_DIR!

set EXT_DIR=!SSMS_DIR!\Extensions\OpenHintSQL

REM ── Copy files ────────────────────────────────────────────
echo             Copying files to !EXT_DIR!...
if exist "!EXT_DIR!" rmdir /S /Q "!EXT_DIR!"
mkdir "!EXT_DIR!"

copy /Y "%BUILD_DIR%\*.dll"                    "!EXT_DIR!\" >nul
copy /Y "%BUILD_DIR%\OpenHintSQL.pkgdef"       "!EXT_DIR!\" >nul
copy /Y "%REPO_ROOT%\src\OpenHintSQL\source.extension.vsixmanifest" "!EXT_DIR!\extension.vsixmanifest" >nul

if not exist "!EXT_DIR!\Config" mkdir "!EXT_DIR!\Config"
xcopy /Y /E "%BUILD_DIR%\Config" "!EXT_DIR!\Config\" >nul

REM ── Clear SSMS %V% caches ─────────────────────────────────
echo             Clearing caches...

set LEGACY_CACHE=%LOCALAPPDATA%\Microsoft\SQL Server Management Studio\%V%.0_IsoShell
call :clear_cache_root "!LEGACY_CACHE!"

REM SSMS 21+ moved local cache/private registry to Microsoft\SSMS\major.0_instanceId.
for /D %%C in ("%LOCALAPPDATA%\Microsoft\SSMS\%V%.0_*") do (
    call :clear_cache_root "%%~C"
)

echo  SSMS %V% : installed successfully.
set /A INSTALLED+=1
goto :eof


REM ============================================================
:clear_cache_root
REM  Args: %1 = SSMS local hive/cache root
REM ============================================================
set CACHE_ROOT=%~1
if "!CACHE_ROOT!"=="" goto :eof
if not exist "!CACHE_ROOT!" goto :eof

if exist "!CACHE_ROOT!\ComponentModelCache" (
    rmdir /S /Q "!CACHE_ROOT!\ComponentModelCache"
)

if exist "!CACHE_ROOT!\Extensions" (
    del /F /Q "!CACHE_ROOT!\Extensions\extensions.*.cache" >nul 2>&1
    del /F /Q "!CACHE_ROOT!\Extensions\ExtensionMetadata*.mpack" >nul 2>&1
    type nul > "!CACHE_ROOT!\Extensions\extensions.configurationchanged" 2>nul
)

REM VS 2022/SSMS 21+ cache pkgdef registration in privateregistry.bin.
if exist "!CACHE_ROOT!\privateregistry.bin" (
    del /F /Q "!CACHE_ROOT!\privateregistry.bin" >nul 2>&1
    del /F /Q "!CACHE_ROOT!\privateregistry.bin.LOG*" >nul 2>&1
)
goto :eof
