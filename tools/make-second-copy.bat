@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Make a PATH-ISOLATED 2nd copy of Phoenix Point for a Goldberg-faked
REM  instance #2 (see SECOND-INSTANCE-SETUP.md). ADDITIVE: never touches the
REM  original install. Mirrors the install with robocopy (Mods\ EXCLUDED, so
REM  noticeably smaller than the full ~35 GB) then pre-creates the
REM  steam_settings folder + text files in the copy.
REM  Mods are SHARED, not copied: the copy's Mods\ is filled with DIRECTORY
REM  JUNCTIONS (mklink /J) to the original install's local mods AND to the
REM  subscribed Workshop mods. Update/deploy a mod ONCE (to the original
REM  install Mods\, or via Steam for a workshop mod) and BOTH instances see it.
REM  You still drop Goldberg's steam_api64.dll yourself (Step 2/3 in the doc).
REM ============================================================================

set "SRC=D:\Steam\steamapps\common\Phoenix Point"

REM  Workshop content for appid 839770 (subscribed mods, e.g. TFTV). Lives OUTSIDE
REM  the install, so /MIR does NOT copy it. Goldberg returns no subscribed UGC, so
REM  the SteamWorkshopModLoader (SteamUGC.GetSubscribedItemsInstallInfo) finds
REM  nothing in the copy. We therefore JUNCTION each enabled workshop mod into the
REM  copy's LOCAL Mods\ folder, which PPModLoader scans by FOLDER regardless of Steam.
REM  A junction is seen by Directory.GetDirectories as a normal directory, so the
REM  mod loads fine; updating the workshop folder (Steam) is seen by BOTH instances.
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
echo  Size   : ~35 GB MINUS the Mods\ folder (Mods are SHARED via junctions, not
echo           copied), so the copy is somewhat smaller. Still needs most of that
echo           much FREE space at the destination.
echo(
set "DEST="
set /p "DEST=Enter destination folder for the 2nd copy (e.g. D:\PP-Instance2): "
if not defined DEST ( echo Aborted - no destination given. & pause & exit /b 1 )

echo(
echo  About to MIRROR the install (Mods\ EXCLUDED, ~35 GB minus mods):
echo      "%SRC%"  -->  "%DEST%"
echo  Mods will be SHARED via directory junctions afterwards (not copied).
echo  The original install is NOT modified. This can take several minutes.
echo(
set "OK="
set /p "OK=Type  YES  to proceed: "
if /i not "%OK%"=="YES" ( echo Aborted. & pause & exit /b 1 )

REM  /XD excludes the install's Mods\ from the mirror: real mod files are NOT
REM  duplicated into the copy. They are shared via junctions below instead.
robocopy "%SRC%" "%DEST%" /MIR /XD "%SRC%\Mods" /MT:16 /R:1 /W:1
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

REM --- SHARE mods via directory junctions into the copy's LOCAL Mods\ ---------
REM  Mods are NOT copied. Instead the copy's Mods\ holds a JUNCTION (mklink /J)
REM  to each live source, so updating/deploying a mod ONCE is seen by BOTH
REM  instances (no double-deploy, no stale copies). A junction is reported by
REM  Directory.GetDirectories as a normal directory, so PPModLoader's FOLDER scan
REM  (PPModLoader.DiscoverMods) loads a junctioned mod exactly like a real one.
REM  ADDITIVE/SAFE: we only ever write under "%DEST%". Removing a stale link uses
REM  a PLAIN rmdir (NO /s) which deletes only the JUNCTION, never the target's
REM  contents. We NEVER rmdir /s anything, and NEVER touch "%SRC%" / the workshop.
REM  mklink /J needs no admin. Junctions to another local NTFS volume work too;
REM  here SRC + workshop are on D:, so a DEST on D: is fully local-to-local.
set "DEST_MODS=%DEST%\Mods"
if not exist "%DEST_MODS%" mkdir "%DEST_MODS%"

REM  (a) Local install mods: one junction per subfolder of the original Mods\.
echo  Junctioning the original install's local mods into the copy's Mods\ ...
for /d %%M in ("%SRC%\Mods\*") do (
    if exist "%DEST_MODS%\%%~nxM" rmdir "%DEST_MODS%\%%~nxM"
    echo    local %%~nxM  -^>  Mods\%%~nxM
    mklink /J "%DEST_MODS%\%%~nxM" "%%M" >nul
)

REM  (b) Workshop mods: one junction per workshop folder that contains a meta.json
REM  (skip any without one, e.g. non-mod UGC). Skip if the name collides with a
REM  local-mod junction already created above (local mods win).
if exist "%WORKSHOP%" (
    echo  Junctioning subscribed Workshop mods into the copy's Mods\ ...
    for /d %%W in ("%WORKSHOP%\*") do (
        if exist "%%W\meta.json" (
            if exist "%DEST_MODS%\%%~nxW" (
                echo    skip workshop %%~nxW  ^(name already present in Mods\^)
            ) else (
                echo    workshop %%~nxW  -^>  Mods\%%~nxW
                mklink /J "%DEST_MODS%\%%~nxW" "%%W" >nul
            )
        )
    )
    echo  Workshop mods junctioned (incl. TFTV if subscribed). They load via the
    echo  folder-scanning PPModLoader regardless of Goldberg/offline Steam, and
    echo  Steam updates to the workshop folder are seen by BOTH instances.
) else (
    echo  NOTE: workshop folder "%WORKSHOP%" not found - no workshop mods to link.
    echo        If you use workshop mods (e.g. TFTV), junction each folder that
    echo        contains a meta.json:  mklink /J "%DEST_MODS%\<id>" "<workshop folder>"
)

echo(
echo  Done. 2nd copy at: %DEST%
echo(
echo  steam_settings (incl. pre-generated steam_interfaces.txt) is ready.
echo  Mods are SHARED via junctions in Mods\ ; forced steamID matches the original
echo  so the copy reads the SAME enabled-mods list. The copy should show ALL your
echo  mods, and updating a mod once is seen by BOTH instances.
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
