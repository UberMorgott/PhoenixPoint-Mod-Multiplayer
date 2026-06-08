@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Install the Goldberg steam_api64.dll into the 2nd COPY only (idempotent).
REM  Backs up the copy's original steam_api64.dll -> steam_api64.dll.orig, then
REM  drops Goldberg's dll in its place.
REM
REM  SAFETY: only ever writes inside the DEST path you supply. It REFUSES to run
REM  against the real install. It NEVER downloads anything - you must already have
REM  Goldberg's 64-bit steam_api64.dll on disk (unblocked) and pass its path.
REM
REM  Usage:
REM    install-goldberg-dll.bat  "D:\PP-Instance2"  "C:\path\to\goldberg\steam_api64.dll"
REM  If args are omitted you will be prompted.
REM ============================================================================

set "REAL=D:\Steam\steamapps\common\Phoenix Point"

set "DEST=%~1"
if not defined DEST set /p "DEST=2nd-copy folder (e.g. D:\PP-Instance2): "
if not defined DEST ( echo Aborted - no destination. & pause & exit /b 1 )

set "GBSRC=%~2"
if not defined GBSRC set /p "GBSRC=Path to Goldberg's steam_api64.dll: "
if not defined GBSRC ( echo Aborted - no Goldberg dll path. & pause & exit /b 1 )

REM --- refuse to touch the real install ---------------------------------------
REM Normalize both paths for comparison.
for %%I in ("%DEST%")  do set "DEST_FULL=%%~fI"
for %%I in ("%REAL%")  do set "REAL_FULL=%%~fI"
if /i "%DEST_FULL%"=="%REAL_FULL%" (
    echo ERROR: DEST is the REAL install. This script refuses to modify it.
    pause & exit /b 1
)

set "PLUG=%DEST%\PhoenixPointWin64_Data\Plugins\x86_64"
set "DLL=%PLUG%\steam_api64.dll"
set "BAK=%PLUG%\steam_api64.dll.orig"

if not exist "%PLUG%\" (
    echo ERROR: "%PLUG%" not found. Run make-second-copy.bat first.
    pause & exit /b 1
)
if not exist "%GBSRC%" (
    echo ERROR: Goldberg dll not found: "%GBSRC%"
    pause & exit /b 1
)

REM --- back up the original (only once - idempotent) --------------------------
if exist "%BAK%" (
    echo Backup already exists: "%BAK%"  ^(leaving it as the pristine original^).
) else (
    if not exist "%DLL%" (
        echo ERROR: no steam_api64.dll in the copy to back up: "%DLL%"
        pause & exit /b 1
    )
    copy /Y "%DLL%" "%BAK%" >nul
    if errorlevel 1 ( echo ERROR: backup failed. & pause & exit /b 1 )
    echo Backed up original -^> "%BAK%"
)

REM --- install Goldberg's dll --------------------------------------------------
copy /Y "%GBSRC%" "%DLL%" >nul
if errorlevel 1 ( echo ERROR: copy of Goldberg dll failed. & pause & exit /b 1 )
echo Installed Goldberg steam_api64.dll into the 2nd copy.

echo(
echo  Done. To revert: copy "%BAK%" back over "%DLL%".
echo  Next: start instance #1, then run launch-second-copy.bat.
endlocal
pause
