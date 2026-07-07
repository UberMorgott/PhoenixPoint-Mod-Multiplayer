# Multiplayer — local 2-instance co-op testing

Run host + client Phoenix Point on ONE machine, windowed, side-by-side, both
running the Multiplayer mod — to watch host UI and client UI at the same time.

## Prerequisites
- Steam client running and logged in (the script does NOT start Steam).
- Game installed at `D:\Steam\steamapps\common\Phoenix Point`.
- Multiplayer mod deployed to the game `Mods\Multiplayer` dir (run `deploy.ps1`).
- PowerShell 7+ (pwsh).

## The identity seam (why a 2nd instance needs a distinct identity)
- Both instances share `persistentDataPath` =
  `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point`, so a single
  `Multiplayer\identity.json` would give BOTH the same PlayerGuid. That is fatal: the host
  seeds slot 0 with its own guid, so a client presenting the same guid is assigned slot 0 and
  per-player ownership/permissions (both keyed by the guid) collapse — and the co-op loading
  screen shows no peer-progress bars (the sole roster row is the host's own, hidden by skip-self).
- **Automatic isolation (default, no setup):** `ClientIdentity` now keys the file off the
  authoritative same-machine index `MultiplayerLog.InstanceIndex` — instance 1 uses
  `identity.json`, the 2nd same-machine instance uses `identity-2.json` (`-3`, … for more),
  the SAME `-N` suffix scheme as `multiplayer-N.log`. Each instance therefore gets its own
  persistent guid with zero manual steps. A real cross-machine peer is always instance 1 →
  `identity.json` on its own machine — unchanged.
- **Manual override (optional):** `ClientIdentity.Load()` still checks env var
  `MULTIPLAYER_IDENTITY` FIRST; a non-empty GUID there is used process-scoped (never persisted),
  overriding the file. Useful to pin a specific identity; unnecessary now that isolation is automatic.
- **Loud failure if it ever collides:** the host REFUSES a JOIN whose guid equals its own with a
  `ConnectionRejected` + an ERROR log (`REJECTING JOIN … EQUALS the host's own identity`), so a
  shared-identity misconfig is never silent.
- Startup logs which identity file/source was used (`ClientIdentity: loaded … from identity-2.json (instance 2)`).

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
