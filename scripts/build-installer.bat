@echo off
setlocal EnableDelayedExpansion

REM ============================================================
REM  OpenHintSQL - Build + package installer EXE
REM
REM  Usage:  scripts\build-installer.bat
REM          scripts\build-installer.bat --no-pause
REM  Output: dist\OpenHintSQLSetup-{version}.exe
REM
REM  What this script does:
REM    1. Build the Release DLL via dotnet build
REM    2. Verify all required files exist
REM    3. Verify Inno Setup 6 is installed
REM    4. Compute and display SHA-256 hashes of the output files
REM       (so you can verify the build before distribution)
REM    5. Compile the installer using ISCC
REM
REM  The installer supports SSMS 18.12.1 / 19.3 / 20.2.1 / 21.x / 22.x.
REM  It auto-detects which versions are installed and installs into all of them.
REM
REM  The installer itself performs its own safety checks:
REM    - Lists detected SSMS versions and asks for confirmation
REM    - Closes SSMS if running (with user confirmation)
REM    - Only writes to Extensions\OpenHintSQL, never SSMS core dirs
REM    - Clears per-version MEF + extension caches after install
REM    - On uninstall: removes only our files from every installed version
REM ============================================================

set "VERSION=1.1.0"
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "BUILD_DIR=%REPO_ROOT%\src\OpenHintSQL\bin\Release\net48"
set "INSTALLER_SCRIPT=%REPO_ROOT%\installer\OpenHintSQL.iss"
set "DIST_DIR=%REPO_ROOT%\dist"
set "ISCC_DEFAULT_X86=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
set "ISCC_DEFAULT_X64=%ProgramFiles%\Inno Setup 6\ISCC.exe"
set "NO_PAUSE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
if /I "%~1"=="/no-pause" set "NO_PAUSE=1"

echo.
echo  OpenHint SQL  ^|  Build ^& Installer
echo  ========================================
echo  Version : %VERSION%
echo.

REM Step 1: Build Release (full MSBuild — VSCT compile needs .NET Framework MSBuild)
echo  [1/5] Building Release...
set "MSBUILD_EXE="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"`) do (
        if not defined MSBUILD_EXE set "MSBUILD_EXE=%%I"
    )
)
if not defined MSBUILD_EXE (
    for %%P in (
        "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    ) do (
        if exist %%P set "MSBUILD_EXE=%%~P"
    )
)
if not defined MSBUILD_EXE (
    echo  ERROR: Could not find MSBuild from Visual Studio 2022.
    goto :fail
)
"%MSBUILD_EXE%" "%REPO_ROOT%\src\OpenHintSQL\OpenHintSQL.csproj" -t:Restore -p:Configuration=Release -nologo -v:quiet
if %ERRORLEVEL% neq 0 goto :fail
"%MSBUILD_EXE%" "%REPO_ROOT%\src\OpenHintSQL\OpenHintSQL.csproj" -p:Configuration=Release -nologo -v:quiet
if %ERRORLEVEL% neq 0 (
    echo  ERROR: Build failed. Fix compilation errors before packaging.
    goto :fail
)
echo       Build succeeded.

REM Step 2: Verify required output files exist
echo.
echo  [2/5] Verifying build output...

set MISSING=0
for %%F in (
    "OpenHintSQL.dll"
    "OpenHintSQL.pkgdef"
    "System.Data.SqlClient.dll"
    "Newtonsoft.Json.dll"
    "Config\snippets.json"
) do (
    if not exist "%BUILD_DIR%\%%~F" (
        echo  MISSING: %BUILD_DIR%\%%~F
        set MISSING=1
    )
)
if not exist "%REPO_ROOT%\src\OpenHintSQL\source.extension.vsixmanifest" (
    echo  MISSING: src\OpenHintSQL\source.extension.vsixmanifest
    set MISSING=1
)
if %MISSING%==1 (
    echo  ERROR: One or more required files are missing. Ensure the build succeeded.
    goto :fail
)
echo       All required files present.

REM Step 3: Ensure Inno Setup is installed
echo.
echo  [3/5] Checking for Inno Setup 6...
set "ISCC="

if exist "%ISCC_DEFAULT_X86%" set "ISCC=%ISCC_DEFAULT_X86%"
if "!ISCC!"=="" if exist "%ISCC_DEFAULT_X64%" set "ISCC=%ISCC_DEFAULT_X64%"
if "!ISCC!"=="" (
    for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do if "!ISCC!"=="" set "ISCC=%%~I"
)

if "!ISCC!"=="" (
    echo       Inno Setup not found at the default paths or on PATH.
    echo  ERROR: Inno Setup 6 is required to build the installer.
    echo  Please install Inno Setup 6, then re-run this script.
    goto :fail
)

if not exist "!ISCC!" (
    echo  ERROR: ISCC.exe was found but is not accessible:
    echo         !ISCC!
    goto :fail
)

echo       Found: !ISCC!

REM Step 4: SHA-256 integrity hashes
echo.
echo  [4/5] Computing SHA-256 file hashes...
echo  (Record these hashes - they let you verify the installer contains the exact build)
echo.

for %%F in (
    "OpenHintSQL.dll"
    "OpenHintSQL.pkgdef"
    "System.Data.SqlClient.dll"
    "Newtonsoft.Json.dll"
    "Config\snippets.json"
) do (
    for /f "tokens=1" %%H in (
        'certutil -hashfile "%BUILD_DIR%\%%~F" SHA256 ^| find /v "hash" ^| find /v "certutil"'
    ) do (
        echo      SHA256  %%H  %%~F
    )
)
for /f "tokens=1" %%H in (
    'certutil -hashfile "%REPO_ROOT%\src\OpenHintSQL\source.extension.vsixmanifest" SHA256 ^| find /v "hash" ^| find /v "certutil"'
) do (
    echo      SHA256  %%H  source.extension.vsixmanifest
)
echo.

REM Write hashes to a sidecar file so they can be shipped alongside the installer
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
set HASH_FILE=%DIST_DIR%\OpenHintSQLSetup-%VERSION%.sha256
echo OpenHint SQL %VERSION% - SHA-256 hashes > "%HASH_FILE%"
echo Build date: %DATE% %TIME% >> "%HASH_FILE%"
echo. >> "%HASH_FILE%"
for %%F in (
    "OpenHintSQL.dll"
    "OpenHintSQL.pkgdef"
    "System.Data.SqlClient.dll"
    "Newtonsoft.Json.dll"
    "Config\snippets.json"
) do (
    for /f "tokens=1" %%H in (
        'certutil -hashfile "%BUILD_DIR%\%%~F" SHA256 ^| find /v "hash" ^| find /v "certutil"'
    ) do (
        echo %%H  %%~F >> "%HASH_FILE%"
    )
)
echo  Hash file written: %HASH_FILE%

REM Step 5: Compile the installer
echo.
echo  [5/5] Compiling installer...
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
"!ISCC!" /Q /O"%DIST_DIR%" /F"OpenHintSQLSetup-%VERSION%" "%INSTALLER_SCRIPT%"
if %ERRORLEVEL% neq 0 (
    echo  ERROR: Inno Setup compilation failed.
    echo  Re-run without /Q to see details: "!ISCC!" "%INSTALLER_SCRIPT%"
    goto :fail
)

if not exist "%DIST_DIR%\OpenHintSQLSetup-%VERSION%.exe" (
    echo  ERROR: Installer compile finished but no EXE was found at:
    echo         %DIST_DIR%\OpenHintSQLSetup-%VERSION%.exe
    goto :fail
)

echo.
echo  ============================================================
echo   Installer built successfully.
echo.
echo   Installer : %DIST_DIR%\OpenHintSQLSetup-%VERSION%.exe
echo   Hashes    : %HASH_FILE%
echo.
echo   SAFETY CHECKLIST before distributing:
echo     [1] Scan the EXE with Windows Defender / VirusTotal
echo     [2] Confirm the EXE is not blocked by SmartScreen on a test machine
echo     [3] Verify hashes match the .sha256 file
echo     [4] Test install + uninstall on a clean machine
echo  ============================================================
echo.
goto :end

:fail
echo.
echo  Build-installer failed. See errors above.
echo.
if not "%NO_PAUSE%"=="1" pause
exit /b 1

:end
if not "%NO_PAUSE%"=="1" pause
exit /b 0
