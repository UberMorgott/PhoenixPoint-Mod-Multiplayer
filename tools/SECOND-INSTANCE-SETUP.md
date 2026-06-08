# Running TWO Phoenix Point instances on ONE PC (same session, side-by-side)

## Why the old approach failed (diagnosis)

`steam_appid.txt` (839770) **is present and correct** in the install
(`D:\Steam\steamapps\common\Phoenix Point\steam_appid.txt`, exactly `839770`,
no BOM/newline). It was NOT removed by Steam. But it only solves HALF the problem:

- `steam_appid.txt` defeats **the relauncher** — `SteamAPI_RestartAppIfNecessary`.
  With it present, launching the exe directly does NOT bounce back through Steam.
- It does **NOT** defeat **the Steam client's single-instance-per-appid lock**.
  When the 2nd `PhoenixPointWin64.exe` calls `SteamClient.Init(839770)`, the running
  Steam client already has appid 839770 marked "running" (instance #1), so the client
  **focuses the existing game window** instead of letting a 2nd game session register.
  That focus/un-minimize is exactly the symptom observed. The lock is keyed on
  **appid + the one live Steam client**, NOT on install path and NOT a game mutex
  (the game has no mutex — decompile-confirmed).

Consequence: a 2nd copy in a different folder that still uses the **real**
`steam_api64.dll` ALSO fails — it still talks to the same Steam client for appid 839770.

## Can Phoenix Point boot without Steam? NO (decompile-confirmed)

`PlatformComponent.CreatePlatform()` is hardcoded to `new PlatformSteam()`. Its `Init()`
calls Facepunch `SteamClient.Init(839770u, ...)`; if that throws (and the demo fallback
1973790 also throws), it shows "Steam Required" and calls `Abort()` — the game quits.
So you CANNOT run a Steam-less instance with the stock dll. The Steam API must either be
**real & reachable** OR **faked** by an emulator so `SteamClient.Init` succeeds.

Source paths (decompiled):
- `AssemblyCSharp\...\Base.Platforms\PlatformComponent.cs` (hardcoded PlatformSteam)
- `AssemblyCSharp\...\Base.Platforms.Steam\PlatformSteam.cs` Init() (abort on failure)

## Recommended method: Goldberg-faked 2nd copy

- **Instance #1 (HOST)** = launch normally from Steam (or the existing
  `launch-1st-instance.bat`). Uses the REAL Steam client + real identity.
- **Instance #2 (CLIENT)** = a **path-isolated 2nd copy** of the install whose
  `steam_api64.dll` is **replaced by the Goldberg emulator**. Goldberg makes
  `SteamClient.Init` succeed WITHOUT the real client, so there is no appid lock and no
  focus-existing. The mod's DirectIP transport (TCP 14242, 127.0.0.1) needs no Steam, so
  co-op still works.

This is the canonical LAN/splitscreen test method and the only reliable one here.

## How mods load in the 2nd copy (why it showed no mods, and the fix)

Decompile-confirmed mod model (`PhoenixPoint.Modding.ModManager` / loaders):

- **Two discovery roots.** `ModManager.Initialize` adds a `PPModLoader("Default", <install>\Mods)`
  where the install root is `GetRootDir()` =
  `Path.GetDirectoryName(Path.GetDirectoryName(Application.streamingAssetsPath))`,
  i.e. **install-relative** — each copy scans its OWN `Mods\` folder
  (`PPModLoader.DiscoverMods` → `Directory.GetDirectories`, a plain FOLDER scan, no Steam).
  Separately, `PlatformSteam.InitModSupport` adds a `SteamWorkshopModLoader` that enumerates
  **subscribed Workshop items via `SteamUGC.GetSubscribedItemsInstallInfo()`** — a Steam UGC
  query, not a folder scan.
- **Enabled state is per-Steam-user, shared across installs.** The active set is the
  `MOD_ACTIVATED` string array in the **options store**, persisted to `Options.jopt` under
  `Application.persistentDataPath\Steam\<steamID64>` (PlatformDataUserFile.GetFilePathRoot;
  per-mod settings sit next to it in `ModConfig.json`). `persistentDataPath` is
  `…\AppData\LocalLow\Snapshot Games Inc\Phoenix Point` — **per-Windows-user, NOT per-install**,
  so both copies share it; only the `<steamID64>` subfolder differs. At boot
  `ModManager.EnableModsFromStore` enables every id in `MOD_ACTIVATED`, and **returns false /
  aborts the whole enable pass on the first id it cannot find among discovered mods**.

**Two reasons the Goldberg copy showed no mods, and what the bat now does:**

1. **Wrong config folder (primary).** The old setup forced a *different* steamID, so the copy
   read `…\Steam\76561197960287931\` — which doesn't exist → empty `MOD_ACTIVATED` → nothing
   enabled. **Fix:** the bat now writes `force_steamid.txt = 76561197996210591` (the original
   id), so the copy reads the **same** `Options.jopt` + `ModConfig.json` → identical enabled
   set + settings.
2. **Workshop mods invisible under Goldberg.** Your enabled set includes `phoenixrising.tftv`
   (TFTV), which lives in `steamapps\workshop\content\839770\2872311902` and is normally found
   via Steam UGC. Goldberg (offline, fake Steam) returns **no** subscribed items, so
   `SteamWorkshopModLoader` finds nothing → TFTV is never discovered → and because
   `EnableModsFromStore` aborts on the first missing id, the rest may fail to enable too.
   **Fix:** the bat junctions every workshop folder containing a `meta.json` into the copy's
   **local `Mods\`**, where the folder-scanning `PPModLoader` discovers them with no Steam at all.

**Mods are SHARED via directory junctions — ONE source, both instances.** The `/MIR` now
**excludes** `<install>\Mods\` (`/XD`), so real mod files are **not** duplicated into the copy.
Instead the copy's `Mods\*` are **directory junctions** (`mklink /J`) pointing at the live
sources: one per subfolder of the original install `Mods\` (AutoAI, Multipleer, OfficerClass,
PerkOracle, TheTurned) and one per subscribed Workshop folder that contains a `meta.json`
(e.g. TFTV `2872311902`; folders without `meta.json`, like `3250097289`, are skipped). A
junction is reported by `Directory.GetDirectories` as a normal directory, so `PPModLoader`'s
folder scan loads a junctioned mod exactly like a real one. **Consequence: update/deploy a mod
once — to the original install `Mods\` for a local mod, or via Steam for a workshop mod — and
BOTH instances see it immediately. No double-deploy, no stale copies.**

Junction creation needs **no admin** (it is a junction, same/any local NTFS volume). Here the
install and workshop both live on `D:`, so a destination on `D:` is fully local-to-local; a
destination on a *different* local NTFS drive also works with `/J`. The bat is **idempotent**:
before each junction it removes any pre-existing entry with a **plain `rmdir`** (no `/s`), which
deletes only the link, never the target's contents. It only ever writes under the destination
and never touches the original install or the workshop folders.

> **Deploying a NEW local mod later** (a new folder under the original install `Mods\`): re-run
> `make-second-copy.bat` to add its junction (the mirror is incremental; the junction loops are
> idempotent), or just add it by hand:
> `mklink /J "D:\PP-Instance2\Mods\<NewMod>" "D:\Steam\steamapps\common\Phoenix Point\Mods\<NewMod>"`.
> **TFTV / workshop updates flow automatically** — the junction points at the live workshop
> folder, so a Steam update is seen by both instances with no action. Changing your *enabled
> set* still updates the shared `Options.jopt` (same steamID), so both stay in sync.

> **Shared-config caveat.** Matching the steamID means both instances read/write the
> **same** `…\Steam\76561197996210591\` folder (saves, `Options.jopt`, `ModConfig.json`).
> That is exactly what makes the enabled-mods list match. The Multipleer client only needs
> the mods loaded, so this is fine for co-op testing; just avoid having instance #2 overwrite
> options/saves you care about while both are running. If that ever becomes a problem, the
> clean alternative is to give the copy its own steamID and **manually create**
> `…\Steam\<thatID>\Options.jopt` + `ModConfig.json` (copy them from the original) so the new
> folder is non-empty — but the matched-id approach above needs no manual config copy.

### TL;DR — minimal user steps

1. **Run `make-second-copy.bat`** — one-time mirror (~35 GB **minus** `Mods\`, which
   is excluded from the copy). It auto-creates `steam_settings\` AND drops in a
   **pre-generated `steam_interfaces.txt`** (extracted from the game's real
   `steam_api64.dll`), so you do **NOT** need Goldberg's interface-generator tool. It
   **junctions your local mods AND your Steam Workshop mods (e.g. TFTV) into the
   copy's local `Mods\`** (one source, both instances — see below) and **forces the
   copy's steamID to match the original**, so the copy shows **all the same mods,
   enabled identically**.
2. **Download Goldberg's 64-bit `steam_api64.dll`** from the official source (URL
   below), then **unblock it** (right-click → Properties → Unblock, or
   `Unblock-File -Path .\steam_api64.dll` in PowerShell).
3. **Install it into the 2nd copy only**: run
   `install-goldberg-dll.bat "D:\PP-Instance2" "C:\path\to\goldberg\steam_api64.dll"`
   (backs up the copy's `steam_api64.dll` → `.orig` and drops Goldberg in —
   idempotent, refuses to touch the real install). Or do it by hand (Step 3 below).
4. **Start instance #1 normally** (host a campaign → listens on `127.0.0.1:14242`),
   then **run `launch-second-copy.bat`**, then in instance #2 **Direct Connect to
   `127.0.0.1:14242`**.

The detailed walkthrough follows.

> Goldberg is a 3rd-party DLL. Some antivirus engines flag Steam emulators. You must
> download it yourself from the official source and unblock it. Do this only if you
> accept that. It is dropped ONLY into the 2nd copy — your real install is untouched.

### Step 1 — make the 2nd copy (~35 GB, manual)

Run `make-second-copy.bat` in this folder (it asks for the destination path and warns
about the size before copying). Pick a path with free space, e.g. `D:\PP-Instance2`.
The bat uses `robocopy /MIR` with the install `Mods\` **excluded** (mods are shared via
junctions afterwards) and does NOT touch the original.

Or do it yourself — mirror with `Mods\` excluded, then junction the mods in:

```
robocopy "D:\Steam\steamapps\common\Phoenix Point" "D:\PP-Instance2" /MIR /XD "D:\Steam\steamapps\common\Phoenix Point\Mods" /MT:16 /R:1 /W:1
mkdir "D:\PP-Instance2\Mods"
REM local mods:
for /d %M in ("D:\Steam\steamapps\common\Phoenix Point\Mods\*") do mklink /J "D:\PP-Instance2\Mods\%~nxM" "%M"
REM workshop mods that have a meta.json (e.g. TFTV 2872311902):
mklink /J "D:\PP-Instance2\Mods\2872311902" "D:\Steam\steamapps\workshop\content\839770\2872311902"
```

### Step 2 — download Goldberg (official source)

- Latest builds: https://mr_goldberg.gitlab.io/goldberg_emulator/
- Repo: https://gitlab.com/Mr_Goldberg/goldberg_emulator
- (Maintained mirror/fork if the above is down: https://github.com/Detanup01/gbe_fork)

From the release archive take the **64-bit** `steam_api64.dll` (in the
`experimental` or `release` folder of the archive — the root/`experimental` 64-bit one).

### Step 3 — install Goldberg into the 2nd copy ONLY

**Easiest (recommended): run the helper bat.** After you have Goldberg's unblocked
64-bit `steam_api64.dll` on disk:

```
install-goldberg-dll.bat "D:\PP-Instance2" "C:\path\to\goldberg\steam_api64.dll"
```

It backs up the copy's `steam_api64.dll` → `steam_api64.dll.orig` (only once),
then drops Goldberg's dll in its place. It is **idempotent** (safe to re-run) and
**refuses to run against the real install**. To revert, copy `.orig` back.

**By hand** (equivalent), in the 2nd copy
`D:\PP-Instance2\PhoenixPointWin64_Data\Plugins\x86_64\`:

1. Rename the original `steam_api64.dll` to `steam_api64.dll.orig` (backup).
2. Copy Goldberg's `steam_api64.dll` in its place.

The `steam_settings` folder is **already created by `make-second-copy.bat`** in the
copy, containing:

- `steam_appid.txt` with `839770`
- **`steam_interfaces.txt`** — PRE-GENERATED from the game's real `steam_api64.dll`
  (the #1 Goldberg failure point). You do **NOT** need to run Goldberg's
  `generate_interfaces_file` / `steamclient_loader` interface tool.
- empty file `offline.txt`            (no Steam servers needed)
- empty file `disable_overlay.txt`    (avoid overlay weirdness)
- **networking left ENABLED** — no `disable_networking.txt`; the mod's DirectIP uses
  normal TCP and does not rely on Goldberg at all, so leaving it on is harmless.
- `force_account_name.txt` with a unique name (`Client2`)
- `force_steamid.txt` set to the **SAME** steamID as the original install
  (`76561197996210591`). This is deliberate (see "How mods load in the 2nd copy"):
  PP keys its per-user config folder by steamID, so matching it lets the copy read
  the **same enabled-mods list + mod settings**. A *different* id would point the
  copy at an empty config folder and the game would show **NO mods enabled** — that
  was the original symptom. The two instances are still distinguished by
  `force_account_name` and a throwaway `MULTIPLEER_IDENTITY` at launch.

So you only need to drop in the Goldberg `steam_api64.dll` + back up the original
(Step 3 above) — everything else in `steam_settings` is done for you.

### Step 4 — launch both

1. Start **instance #1** normally (Steam, or `launch-1st-instance.bat`). Host a campaign
   → it listens on `127.0.0.1:14242`.
2. Run `launch-second-copy.bat` (edit the `DEST` path inside if you used a different one)
   to start **instance #2** windowed. It also sets `MULTIPLEER_IDENTITY` (throwaway GUID)
   as a second safeguard so the two share no Multipleer identity.
3. In instance #2: Multiplayer → Direct Connect → `127.0.0.1:14242`.
4. Tile the windows (Win+Left / Win+Right).

## Fallback (no dll swap): Sandboxie-Plus

If you refuse the emulator dll, run instance #2 inside **Sandboxie-Plus**
(https://sandboxie-plus.com/). A sandboxed process gets isolated Steam IPC, so its
`SteamClient.Init` does not collide with the host instance's appid lock. Requirements:
Steam must be reachable/logged-in inside the sandbox (you may need a 2nd Steam account,
because one account cannot have the same game "running" twice on the real client either).
Heavier setup, no dll swap, both windows visible. Use only if Goldberg is unacceptable.

## Rejected methods

- **2nd copy with the REAL `steam_api64.dll`** → still hits the appid lock → fails.
- **More env/launch-order tweaks on the single install** → this is the current failing
  approach; the lock is in the live Steam client, env can't bypass it → fails.
- **Second Windows user session** → cannot show both windows on one screen at once →
  disqualified for "see both UIs side-by-side".
