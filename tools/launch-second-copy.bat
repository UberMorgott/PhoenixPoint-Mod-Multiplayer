@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Launch INSTANCE #2 (CLIENT) from the path-isolated Goldberg copy, windowed.
REM  Prereq: make-second-copy.bat done AND Goldberg steam_api64.dll dropped into
REM  the copy's PhoenixPointWin64_Data\Plugins\x86_64 (see SECOND-INSTANCE-SETUP.md).
REM
REM  Goldberg fakes the Steam API so SteamClient.Init succeeds without the real
REM  client -> no appid single-instance lock -> a real 2nd process starts.
REM  Co-op uses the mod's DirectIP (127.0.0.1:14242), which needs no Steam.
REM ============================================================================

REM --- EDIT THIS to match the destination you gave make-second-copy.bat --------
set "DEST=D:\PP-Instance2"

set "EXE=%DEST%\PhoenixPointWin64.exe"
if not exist "%EXE%" (
    echo ERROR: 2nd-copy exe not found: "%EXE%"
    echo Run make-second-copy.bat first, or fix DEST above.
    pause & exit /b 1
)

set "GBDLL=%DEST%\PhoenixPointWin64_Data\Plugins\x86_64\steam_api64.dll"
set "GBBAK=%DEST%\PhoenixPointWin64_Data\Plugins\x86_64\steam_api64.dll.orig"
if not exist "%GBBAK%" (
    echo WARNING: no steam_api64.dll.orig backup found in the copy.
    echo This usually means Goldberg has NOT been installed yet. Without it,
    echo this instance will hit the same Steam single-instance lock and just
    echo focus instance #1. See SECOND-INSTANCE-SETUP.md, steps 2-3.
    echo(
    set "GO="
    set /p "GO=Launch anyway? (y/N): "
    if /i not "!GO!"=="y" ( echo Aborted. & exit /b 1 )
)

REM --- throwaway Multipleer identity for the client (process-scoped) ------------
set "MULTIPLEER_IDENTITY="
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "[guid]::NewGuid().ToString()"`) do set "MULTIPLEER_IDENTITY=%%i"
echo MULTIPLEER_IDENTITY=!MULTIPLEER_IDENTITY!

REM --- -mods is MANDATORY: PP gates ALL mod support on this launch arg ---------
REM  PhoenixGame.HandleCommandLineArg("mods") sets ModManager.CanUseMods=true
REM  (the ONLY place it is enabled). Without it, InitMods() bails immediately
REM  (yield break), DiscoverMods() never runs (0 mods), AND the main-menu MODS
REM  button is hidden (UIModuleMainMenuButtons sets it active = CanUseMods).
REM  Steam normally injects this via the game's Steam launch options; a
REM  standalone gbe_fork copy bypasses Steam, so we MUST pass it ourselves.
REM  (Arg parser strips all '-' then matches "mods", so "-mods" is correct.)
echo Launching INSTANCE #2 (CLIENT, Goldberg copy)...
start "PhoenixPoint #2 CLIENT" /D "%DEST%" "%EXE%" -mods -screen-fullscreen 0 -popupwindow -screen-width 960 -screen-height 1080

echo(
echo Done. Tile this window to the RIGHT half (Win+Right).
echo In this instance: Multiplayer -^> Direct Connect -^> 127.0.0.1:14242
endlocal
