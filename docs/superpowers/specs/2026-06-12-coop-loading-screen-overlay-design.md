# Co-op Loading Screen Overlay — Design

**Status:** Approved (2026-06-12); SHIPPED + in-game-confirmed over DirectIP (`4976474`). **Mod:** Multiplayer (Phoenix Point co-op). **Topic:** shared loading screen showing every player's two-phase load progress.

> **AS-BUILT DIVERGENCE (authoritative = [00-current-state](../../research/00-current-state.md) §Co-op loading screen + [09-disconnect-reconnect](../../research/09-disconnect-reconnect.md) §Co-op Loading Overlay).** This design doc records the original plan; the shipped form differs on three points: (1) overlay shows ONLY OTHER players (self-row hidden), not "ALL including host"; (2) simultaneous reveal is via **native-curtain-HOLD** (Harmony prefix suppresses the auto-`LiftCurtain`, peers hold at the still-visible native screen) — NOT a from-code fullscreen black cover (that "Cover" was added then removed); (3) phase-2 bar value = the live native `ProgressFill.fillAmount` forwarded RAW (no mod-side easing; `FillEase` deleted). Engine facts below remain accurate.

## Goal

After all players ready and host presses Play, every player sees the vanilla loading screen. A top-right overlay (~quarter screen) lists ALL players (including host), each with ONE progress bar that runs two sequential phases:
- **Phase 1** = savegame DOWNLOAD % (host streams the save to clients).
- **Phase 2** = native world-load % (the real engine load into geoscape/tactical).

Everyone sees everyone's live progress. Both players enter gameplay together at the barrier.

## Engine facts (decompiled, authoritative)

All paths under `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src\`.

- Native loading SCREEN = `SceneFadeController` (`Base.Utils\SceneFadeController.cs:13`): full-screen `Curtain`, `LoadingArt`, tips, and progress bar `ProgressBarController ProgressBar` (`:47`). Show seam `DropCurtainInstant` (`:70`).
- Driver = `LevelSwitchCurtainController` (`Base.Utils\LevelSwitchCurtainController.cs:11`, `[DefaultExecutionOrder(-100)]`). `OnLevelStateChanged` (`:46`): `→Loading` drops curtain (`:48-50`), `Loaded→Playing` lifts (`:60-62`). Event `OnCurtainLifted` (`:29`, fired `:114`). Owned by PhoenixGame (`PhoenixGame.cs:129` `GetComponent<LevelSwitchCurtainController>()`).
- Native progress value = `Level.LoadingProgress` (`Base.Levels\Level.cs:56`), type implements `ILoadingProgress` with `float Progress {get;}` 0..1 + `event Action<float> ProgressChanged` (`Base.Core\ILoadingProgress.cs:5-9`). The native bar binds it: `ProgressBarController.SetLoadingLevel` (`Base.Utils\ProgressBarController.cs:46-59`), `Update` reads `Progress`→`fillAmount`/% text (`:62-99`). Reading `GameUtl.CurrentLevel()?.LoadingProgress?.Progress` each frame = the exact value the player sees.
- Granularity caveat: progress is STEP-quantized — `SceneLoader.LoadScenesCrt` calls `SetStep` per whole scene (`Base.Core\SceneLoader.cs:30-49`); `AsyncOperation.progress` is NOT fed in. Value jumps in `1/MaxStep` increments, and can sit <1.0 even when effectively done; goes null at load end (`Level.cs:148-149`).
- Persistence: the host `Game` GameObject is `DontDestroyOnLoad` (`Base.Core\Game.cs:94`), so a mod canvas under a DontDestroyOnLoad root survives the scene transition and can render over the curtain.

## Mod facts (existing wiring)

Under `E:\DEV\PhoenixPoint\Multiplayer\src\`.

- `SaveTransferCoordinator.cs`: host chunks save (32KB `SaveChunk`+`SaveDone` crc32), client reassembles (`OnSaveDone`), barrier LOADED/BEGIN (`OpenBarrier :214`, `SendLoaded :361`, `OnClientLoaded :366`, `TryReleaseBarrier :391`, `Begin :404`, `EnterLevel :423`→`game.FinishLevel(_pendingResult)`). Download % already broadcast: `ReportDownloadProgress :449-457`, host `OnLoadProgress :460-477` stores `_peerDownloadPct[steamId]` + rebroadcasts; accessors `LocalDownloadPercent :103`, `TryGetPeerDownloadPercent :114`. Barrier timeout 60s in `Update :483-503`.
- `SaveLoadPatches.cs` `FinishLevelBarrierPatch`: Prefix on `PhoenixGame.FinishLevel`, `return false` while barrier pending (real gate, not stub).
- `NetworkEngine.cs RouteMessage`: cases SaveChunk `:451`, SaveDone `:455`, LoadProgress `:459`, ClientLoaded `:463`, SessionBegin `:467`.
- `MessageSerializer.SerializeLoadProgress(ulong peerSteamId, byte phase, byte percent)` (`src\Network\MessageLayer\MessageSerializer.cs:361-382`), `// phase 0=download,1=load`; `PacketType.LoadProgress = 0x1A`.
- `SessionManager`: host-authoritative chat/relay; `AddClient` call order = roster order (arrival); PEER_LIST broadcast.
- UI: `MultiplayerMain.cs:33` builds on `ModGO`; mod root overlay canvases pattern at sortingOrder 4000/5000/6000 with `overrideSorting` (`LobbyPanel.cs:127-137`, `MultiplayerUI.cs:100-107`, `SavePickerPanel.cs:64-80`). `LobbyPanel.Refresh :792-796` calls `Hide()` on `SaveTransfer.SessionStarted`. Roster progress is text-only today (`RefreshRoster :934-989`, `ProgressFor :994-1013`). `NativeWidgetFactory.HideMenuChrome` disables native root canvases. Play seam `MultiplayerUI.OnLobbyPlay :191-207`.

## Architecture

### Identity — stable slotIndex
Host assigns a `slotIndex` (byte) per player at join in arrival order (host = slot 0). Mapping echoed in PEER_LIST (`slotIndex → displayName + steamId|0`). ALL progress data is keyed by `slotIndex`, never by transport peer-id — this removes the DirectIP synthetic-peer-id mapping gap. Reconnect reuses the slot (match by steamId on Steam, by host-issued session token on DirectIP).

### Progress propagation — host-aggregated snapshot (selective, not firehose)
- Each client sends its OWN progress (phase-1 download AND phase-2 load) to host via existing `LoadProgress` (0x1A): on-change, whole-percent, ~150ms throttle.
- Host does NOT relay individual deltas. Host aggregates into a compact `RosterProgress` snapshot (N × {slotIndex, phase, percent}, ~3 bytes/row) and broadcasts on-change at ≤5 Hz. New PacketType for the snapshot.
- Snapshot is idempotent/self-healing: any single received snapshot fully reconstructs the UI; a lost packet only delays one tick.
- Channel: snapshots go UNRELIABLE/unordered, separate from the reliable save-chunk stream (else they head-of-line-block behind megabytes of save).
- Receiver merge: monotonic-max per (slot, phase) — phase only advances, percent within a phase only increases. UDP reorder becomes invisible; matches step-quantized phase-2 (no time-smoothing needed for correctness; optional cosmetic lerp).

### Done — event-driven, never threshold
Each client sends a RELIABLE `LoadComplete` when its load truly finishes (the mod already has the LOADED/BEGIN barrier — reuse/extend it). Host keeps broadcasting snapshots until ALL slots are done; sends the final snapshot reliably (or 3× redundant on pure UDP). Never infer done from percent==100 (engine `Progress` can sit <1.0 when finished).

### Screen + overlay
- On Play: drop the native curtain EARLY (`SceneFadeController.DropCurtainInstant` via `LevelSwitchCurtainController`) so phase-1 already looks like one seamless vanilla load. RISK: manually driving the curtain controller (an `ILevelStateListener`) may desync its state — must be verified in-game; keep a fallback (our own loading-style backdrop) if the early force misbehaves.
- Overlay = its OWN `ScreenSpaceOverlay` Canvas (`overrideSorting=true`, `sortingOrder≈7000`, `CanvasScaler` 1920×1080, no raycaster — display only) parented under a DontDestroyOnLoad root (NOT the menu canvas, which gets disabled by HideMenuChrome). Anchored top-right (1,1), ~quarter screen. Created once, toggled.
- Player row = display name + ONE progress bar (`Image.fillAmount`) with a phase label ("Downloading" / "Loading") that resets to 0 between phases + % text. All players shown, including host.

### Hooks
- SHOW: Postfix `LevelSwitchCurtainController.OnLevelStateChanged` on `newState==Level.State.Loading`; plus the early force-drop at Play for phase-1.
- DRIVE phase-2: overlay's own `Update()` reads `GameUtl.CurrentLevel()?.LoadingProgress?.Progress` (null-guard), converts 0..1→byte, throttles, broadcasts via `SerializeLoadProgress(slotKey, phase:1, percent)`. Phase-1 stays from `SaveTransferCoordinator` bytes-received.
- HIDE: subscribe `LevelSwitchCurtainController.OnCurtainLifted` (or `Loaded→Playing`), but keep overlay up until the mod BEGIN barrier fires (all enter together).

## Testing
- Pure-logic TDD (linked into `Multiplayer.Tests`, no UnityEngine dep): slotIndex assignment + reconnect reuse; `RosterProgress` snapshot serialize/deserialize; monotonic-max merge; done-tracking barrier (all-slots-done gate).
- Engine/UI seams (curtain drop, overlay render, `LoadingProgress` read) = manual 2-instance in-game run.

## Open items — close only in-game (cannot source from decompile)
1. Native curtain Canvas `sortingOrder` (prefab-authored) — confirm our 7000 renders above it.
2. Safety of early `DropCurtainInstant` at Play (no state desync); fallback backdrop if it misbehaves.

## Out of scope
- Disconnect/reconnect UX beyond slot reuse. Per-player permission enforcement. Smooth (non-stepped) phase-2 interpolation beyond optional cosmetic lerp. Tactical/geoscape concurrency.
