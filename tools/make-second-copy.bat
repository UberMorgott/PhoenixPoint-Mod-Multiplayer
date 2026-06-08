@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Make a PATH-ISOLATED 2nd copy of Phoenix Point for a Goldberg-faked
REM  instance #2 (see SECOND-INSTANCE-SETUP.md). ADDITIVE: never touches the
REM  original install. Mirrors the install with robocopy (~35 GB) then
REM  pre-creates the steam_settings folder + text files in the copy.
REM  You still drop Goldberg's steam_api64.dll yourself (Step 2/3 in the doc).
REM ============================================================================

set "SRC=D:\Steam\steamapps\common\Phoenix Point"

REM  Workshop content for appid 839770 (subscribed mods, e.g. TFTV). Lives OUTSIDE
REM  the install, so /MIR does NOT copy it. Goldberg returns no subscribed UGC, so
REM  the SteamWorkshopModLoader (SteamUGC.GetSubscribedItemsInstallInfo) finds
REM  nothing in the copy. We therefore copy the enabled workshop mods into the
REM  copy's LOCAL Mods\ folder, which PPModLoader scans by FOLDER regardless of Steam.
set "WORKSHOP=D:\Steam\steamapps\workshop\content\839770"

REM  The original Steam identity. PP stores its per-user config (Options.jopt with
REM  MOD_ACTIVATED, and ModConfig.json) under
REM    %%USERPROFILE%%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\<steamID64>
REM  That path is per-WINDOWS-user (Unity persistentDataPath), NOT per-install, so it
REM  is SHARED by both copies. The subfolder is the steamID64. We force the COPY to
REM  the SAME steamID64 so it reads the SAME enabled-mods list + mod settings.
REM  (A different forced steamID -> empty config folder -> the game shows NO mods.)
set "ORIG_STEAMID=76561197996210591"

if not exist "%SRC%\PhoenixPointWin64.exe" (
    echo ERROR: source install not found at "%SRC%".
    pause & exit /b 1
)

echo(
echo  Source : %SRC%
echo  Size   : ~35 GB. The copy needs that much FREE space at the destination.
echo(
set "DEST="
set /p "DEST=Enter destination folder for the 2nd copy (e.g. D:\PP-Instance2): "
if not defined DEST ( echo Aborted - no destination given. & pause & exit /b 1 )

echo(
echo  About to MIRROR ~35 GB:
echo      "%SRC%"  -->  "%DEST%"
echo  The original install is NOT modified. This can take several minutes.
echo(
set "OK="
set /p "OK=Type  YES  to proceed: "
if /i not "%OK%"=="YES" ( echo Aborted. & pause & exit /b 1 )

robocopy "%SRC%" "%DEST%" /MIR /MT:16 /R:1 /W:1
REM robocopy exit codes 0-7 are success; 8+ are errors.
if %ERRORLEVEL% GEQ 8 (
    echo ERROR: robocopy failed (code %ERRORLEVEL%).
    pause & exit /b 1
)

REM --- pre-create Goldberg steam_settings in the COPY -------------------------
set "PLUG=%DEST%\PhoenixPointWin64_Data\Plugins\x86_64"
set "SS=%PLUG%\steam_settings"
if not exist "%PLUG%" (
    echo WARNING: "%PLUG%" not found in the copy - did the mirror complete?
    pause & exit /b 1
)
if not exist "%SS%" mkdir "%SS%"
>"%SS%\steam_appid.txt"        echo 839770
>"%SS%\offline.txt"            rem.
>"%SS%\disable_overlay.txt"    rem.
>"%SS%\force_account_name.txt" echo Client2
REM  MUST match the original steamID so the copy reads the SAME per-user config
REM  folder (Options.jopt MOD_ACTIVATED + ModConfig.json). A different id would
REM  point at an empty folder and the game would show NO mods enabled.
>"%SS%\force_steamid.txt"      echo %ORIG_STEAMID%

REM --- pre-generated steam_interfaces.txt (the #1 Goldberg failure point) -------
REM Extracted from the game's real steam_api64.dll; saves you running Goldberg's
REM generate_interfaces tool. Copied into the COPY's steam_settings.
set "IFACE_SRC=%~dp0goldberg\steam_interfaces.txt"
if exist "%IFACE_SRC%" (
    copy /Y "%IFACE_SRC%" "%SS%\steam_interfaces.txt" >nul
    echo  steam_interfaces.txt copied into steam_settings (pre-generated).
) else (
    echo  WARNING: "%IFACE_SRC%" missing - steam_interfaces.txt NOT copied.
    echo           Goldberg may fail to expose the right interfaces without it.
)

REM --- bring Steam Workshop mods into the copy's LOCAL Mods\ ------------------
REM  Under Goldberg the SteamWorkshopModLoader enumerates nothing (no real UGC),
REM  so subscribed mods like TFTV would be missing. PPModLoader scans the copy's
REM  own <install>\Mods\ folder by FOLDER (no Steam), so we mirror each workshop
REM  mod (a folder containing meta.json) into "%DEST%\Mods\" under its folder id.
REM  ADDITIVE: only writes into the COPY's Mods\; never touches the workshop source.
set "DEST_MODS=%DEST%\Mods"
if not exist "%DEST_MODS%" mkdir "%DEST_MODS%"
if exist "%WORKSHOP%" (
    echo  Copying Steam Workshop mods into the copy's local Mods\ ...
    for /d %%W in ("%WORKSHOP%\*") do (
        if exist "%%W\meta.json" (
            echo    workshop %%~nxW  -^>  Mods\%%~nxW
            robocopy "%%W" "%DEST_MODS%\%%~nxW" /MIR /MT:8 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
        )
    )
    echo  Workshop mods copied (incl. TFTV if subscribed). They now load via the
    echo  folder-scanning PPModLoader regardless of Goldberg/offline Steam.
) else (
    echo  NOTE: workshop folder "%WORKSHOP%" not found - no workshop mods to copy.
    echo        If you use workshop mods (e.g. TFTV), copy each folder that contains
    echo        a meta.json into "%DEST_MODS%\" by hand so the copy can discover them.
)

echo(
echo  Done. 2nd copy at: %DEST%
echo(
echo  steam_settings (incl. pre-generated steam_interfaces.txt) is ready.
echo  Workshop mods mirrored into Mods\ ; forced steamID matches the original so
echo  the copy reads the SAME enabled-mods list. The copy should show ALL your mods.
echo(
echo  NEXT (see SECOND-INSTANCE-SETUP.md):
echo    1. Download Goldberg's 64-bit steam_api64.dll and UNBLOCK it.
echo         (Goldberg: https://mr_goldberg.gitlab.io/goldberg_emulator/ )
echo    2. Install it into THIS copy (idempotent helper):
echo         install-goldberg-dll.bat "%DEST%" "C:\path\to\goldberg\steam_api64.dll"
echo       (or by hand: in "%PLUG%" rename steam_api64.dll -^> .orig, drop Goldberg in)
echo    3. Edit launch-second-copy.bat so DEST matches "%DEST%", then run it.
echo(
endlocal
pause
