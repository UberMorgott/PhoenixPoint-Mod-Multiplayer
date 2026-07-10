@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Phoenix Point - UNIFIED N-INSTANCE CO-OP LAUNCHER
REM  Self-contained. Master lives at Multiplayer\tools\launch-instance.bat;
REM  drop a copy into ANY test-copy root (D:\PP-InstanceN) and run.
REM
REM  Per run it:
REM    1. Roots itself at its own folder (works in any copy, any name).
REM    2. REFUSES to run inside the real Steam install (copies only).
REM    3. Auto-activates the Goldberg steam_api64.dll (idempotent, keeps .orig).
REM    4. Syncs Mods\ as NTFS junctions from the Steam install + workshop,
REM       self-healing empty real dirs left by folder-copying an instance.
REM    5. Ensures steam_appid.txt + sets SteamAppId/SteamGameId.
REM    6. Launches PhoenixPointWin64.exe -mods  (offline / DirectIP testing).
REM
REM  Usage: launch-instance.bat [sync]
REM         "sync" = do steps 1-5 only (fix links + dll), do NOT launch.
REM  NOTE: instance 1 = the real Steam install; launch that normally via Steam.
REM ============================================================================

cd /d "%~dp0"

set "EXE=PhoenixPointWin64.exe"
set "MAIN_INSTALL=D:\Steam\steamapps\common\Phoenix Point"
set "STEAM_GAME_DIR=D:\Steam\steamapps\common\Phoenix Point"
set "WORKSHOP_DIR=D:\Steam\steamapps\workshop\content\839770"
set "PLUG=PhoenixPointWin64_Data\Plugins\x86_64\steam_api64.dll"
set "GB_SIZE=11423144"
set "GB_SRC=E:\DEV\PhoenixPoint\Multiplayer\tools\goldberg\steam_api64.dll"

REM --- step 2: refuse to run inside the real Steam install (copies only) --------
if /I "%CD%"=="%MAIN_INSTALL%" (
    echo REFUSED: this is the real Steam install. Launch instance 1 via Steam.
    pause
    exit /b 1
)
if not exist "%EXE%" (
    echo ERROR: %EXE% not found in "%CD%".
    pause
    exit /b 1
)

REM --- step 3: Goldberg activation (idempotent) --------------------------------
set "CURSZ=0"
if exist "%PLUG%" for %%A in ("%PLUG%") do set "CURSZ=%%~zA"
if "!CURSZ!"=="%GB_SIZE%" (
    echo Goldberg already active ^(%GB_SIZE% bytes^).
) else (
    if not exist "%PLUG%.orig" (
        copy /Y "%PLUG%" "%PLUG%.orig" >nul && echo Backed up Valve dll -^> steam_api64.dll.orig
    )
    set "SRC=%GB_SRC%"
    if exist "%PLUG%.gse" for %%A in ("%PLUG%.gse") do if "%%~zA"=="%GB_SIZE%" set "SRC=%PLUG%.gse"
    copy /Y "!SRC!" "%PLUG%" >nul && echo Goldberg activated from "!SRC!".
)

REM --- step 5a: ensure steam_appid.txt (self-heal) -----------------------------
if not exist "steam_appid.txt" (
    >"steam_appid.txt" echo 839770
    echo Created steam_appid.txt ^(839770^).
)

REM --- step 4: Mods must be a REAL folder (drop legacy whole-folder junction) ---
dir /AL /B . 2>nul | findstr /I /X "Mods" >nul && rmdir "Mods"
if not exist "Mods" mkdir "Mods"

REM --- clean empty real dirs AND dead junctions (rmdir handles both) -----------
REM     folder-copying an instance turns junctions into EMPTY real dirs -> relink.
REM     NB: `if exist "dir\*"` is TRUE for empty real dirs, so enumerate entries
REM     instead. `if defined` reads live state (no delayed expansion needed).
for /f "delims=" %%D in ('dir /AD /B "Mods" 2^>nul') do (
    set "HAS="
    for /f "delims=" %%x in ('dir /b /a "Mods\%%D" 2^>nul') do set "HAS=1"
    if not defined HAS rmdir "Mods\%%D" 2>nul && echo Cleaned empty/dead: Mods\%%D
)

REM --- link every Steam local mod not already present -------------------------
if exist "%STEAM_GAME_DIR%\Mods" (
    for /d %%D in ("%STEAM_GAME_DIR%\Mods\*") do (
        if not exist "Mods\%%~nxD" mklink /J "Mods\%%~nxD" "%%~fD" >nul && echo Linked local mod: %%~nxD
    )
) else (
    echo WARNING: Steam Mods folder not found: "%STEAM_GAME_DIR%\Mods"
)

REM --- link every subscribed workshop mod not already present -----------------
if exist "%WORKSHOP_DIR%" (
    for /d %%D in ("%WORKSHOP_DIR%\*") do (
        if not exist "Mods\%%~nxD" mklink /J "Mods\%%~nxD" "%%~fD" >nul && echo Linked workshop mod: %%~nxD
    )
) else (
    echo WARNING: Workshop folder not found: "%WORKSHOP_DIR%"
)

REM --- step 5b: launch env -----------------------------------------------------
set "SteamClientLaunch="
set "SteamAppId=839770"
set "SteamGameId=839770"

if /I "%~1"=="sync" (
    echo Sync done ^(no launch^).
    exit /b 0
)

REM --- step 6: launch ("-mods" enables ModManager + the MODS menu entry) -------
echo Launching Phoenix Point (test instance, Goldberg, mods synced)...
start "PhoenixPoint COOP" "%EXE%" -mods

endlocal
