# Multiplayer — local 2-instance co-op testing

Run host + client Phoenix Point on ONE machine, windowed, side-by-side, both
running the Multiplayer mod — to watch host UI and client UI at the same time.

## Prerequisites
- Steam client running and logged in (the script does NOT start Steam).
- Game installed at `D:\Steam\steamapps\common\Phoenix Point`.
- Multiplayer mod deployed to the game `Mods\Multiplayer` dir (run `deploy.ps1`).
- PowerShell 7+ (pwsh).

## The identity seam (why a 2nd instance needs it)
- Both instances share `persistentDataPath` =
  `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point`, so both read the
  SAME `Multiplayer\identity.json` -> identical PlayerGuid -> breaks per-player ownership.
- `ClientIdentity.Load()` first checks env var `MULTIPLAYER_IDENTITY`: if it parses to a
  non-empty GUID, that GUID is used and the file is NOT read/written (process-scoped,
  never persisted). If unset/invalid, normal identity.json logic runs unchanged.
- For a second local instance, set `MULTIPLAYER_IDENTITY` for the CLIENT process only; the
  HOST process launches WITHOUT it and uses the real identity.json.

## Steps
- Deploy the mod:
  - `pwsh -NoProfile -File .\deploy.ps1`  (from `E:\DEV\PhoenixPoint\Multiplayer`)
- Launch both windows:
  - Local two-instance testing requires the developer's own second Steam-API session; no
    emulator tool is provided or endorsed by this repo.
- In-game:
  - HOST (#1): create or load a campaign -> it starts hosting on `:14242`.
  - CLIENT (#2): Multiplayer -> Direct Connect -> `127.0.0.1:14242`.
- Layout: tile the two windows side-by-side (Win+Left / Win+Right).

## Known-uncertain
- Steam must be running. If a 2nd instance still won't start, confirm Steam is open
  AND `D:\Steam\steamapps\common\Phoenix Point\steam_appid.txt` exists (contains `839770`).
