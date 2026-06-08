# Multipleer — local 2-instance co-op testing

Run host + client Phoenix Point on ONE machine, windowed, side-by-side, both
running the Multipleer mod — to watch host UI and client UI at the same time.

## Prerequisites
- Steam client running and logged in (the script does NOT start Steam).
- Game installed at `D:\Steam\steamapps\common\Phoenix Point`.
- Multipleer mod deployed to the game `Mods\Multipleer` dir (run `deploy.ps1`).
- PowerShell 7+ (pwsh).

## The identity seam (why a 2nd instance needs it)
- Both instances share `persistentDataPath` =
  `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point`, so both read the
  SAME `Multipleer\identity.json` -> identical PlayerGuid -> breaks per-player ownership.
- `ClientIdentity.Load()` first checks env var `MULTIPLEER_IDENTITY`: if it parses to a
  non-empty GUID, that GUID is used and the file is NOT read/written (process-scoped,
  never persisted). If unset/invalid, normal identity.json logic runs unchanged.
- The launch script sets `MULTIPLEER_IDENTITY` for instance #2 (CLIENT) only; instance
  #1 (HOST) launches WITHOUT it and uses the real identity.json.

## Steps
- Deploy the mod:
  - `pwsh -NoProfile -File .\deploy.ps1`  (from `E:\DEV\PhoenixPoint\Multipleer`)
- Launch both windows:
  - `pwsh -NoProfile -File .\tools\launch-coop-test.ps1`
  - Optional sizing: `-Width 960 -Height 1080` (defaults; half a 1920-wide screen).
  - Script ensures `steam_appid.txt` (839770) exists in the game folder (created only if
    missing) so the exe inits Steamworks without the Steam relauncher.
- In-game:
  - HOST (#1): create or load a campaign -> it starts hosting on `:14242`.
  - CLIENT (#2): Multiplayer -> Direct Connect -> `127.0.0.1:14242`.
- Layout: tile the two windows side-by-side (Win+Left / Win+Right).

## Known-uncertain
- Steam must be running. If a 2nd instance still won't start, confirm Steam is open
  AND `D:\Steam\steamapps\common\Phoenix Point\steam_appid.txt` exists (contains `839770`).
