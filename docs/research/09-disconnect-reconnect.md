# Disconnect, Orphan Takeover & Reconnect Resync

> What happens when a player drops, how their units stay controllable, and how a returning player is brought back in sync — by reusing the session-start barrier ([specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md)) instead of a custom live-state serializer.

## Disconnect → Orphan Takeover

- Transport fires `OnPeerDisconnect` (see [engine/02-transport-layer](../engine/02-transport-layer.md)) → host reacts immediately.
- Host **reassigns the dropped player's owned soldiers to itself** (`ASSIGN_OWNER → host`) so units never freeze as uncontrollable orphans. Tactical / campaign continues.
- Binding is by `playerGUID`; the original ownership is remembered for restore on return → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md).
- **Toast** broadcast to everyone: `NOTICE` → "Player X disconnected — host is controlling their soldiers."

## Reconnect Resync (reuses the start barrier)

- **Key idea:** no custom live in-memory serializer. Host snapshots the **live** state through the game's **own save system**, then re-runs the session-start barrier mid-session.
- **Why reload everyone, not just the joiner:** the host snapshot is the single truth → reloading all peers = global resync, zero drift. Doubles as the "full-state resync after divergence" use case in [04-serialization](04-serialization.md). Joiner-only would be lighter but risks drift; reload-all is the safe call.
- **Sequence:**
  1. Host detects reconnect — a `JOIN` carrying a **known `playerGUID`** → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md).
  2. Host **pauses** the session (freeze clock / block input globally → [08-geoscape-concurrency](08-geoscape-concurrency.md)).
  3. Host **auto-saves** current live state → save blob.
  4. Host broadcasts blob → **all** peers (gzip/chunked, same wire path as the start barrier).
  5. Everyone shows loading screen, loads blob, sends `LOADED` (barrier).
  6. Host has all `LOADED` → broadcasts `BEGIN` → all unblock simultaneously → continue.
- **On return:** the player's `playerGUID` re-matches → host hands their soldiers back (`ASSIGN_OWNER`) and restores permissions → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md).
- **Toast:** `NOTICE` → "Player X reconnecting — resyncing…" then "Player X is back." (The loading screen already covers the pause visually.)
- **Reconnect storm:** one resync at a time; queue additional reconnects.

## Co-op Loading Overlay — as-built two-phase barrier (`ab4b921`→`4976474`)

> The session-start / resync barrier above describes the *design*. This is the *as-built* loading screen, **CONFIRMED WORKING in-game over DirectIP** end-to-end: the barrier is **two** barriers, because the native level-load and the native geoscape curtain each release per-instance and must be re-synchronized twice. Pump lives in `src/Network/SaveTransferCoordinator.cs`; UI in `src/UI/LoadOverlayController.cs`.

- **Phase 1 — LOADED/BEGIN (save received → native load done).** Unchanged. Host gates on `_barrierOpen||_loadPhaseActive`, collects LOADED, broadcasts BEGIN; 60s timeout/kick. Set up in `Begin()`/`OpenBarrier`.
- **Phase 2 — RevealAll (native curtain auto-lift → synchronized world reveal). IMPLEMENTED.**
  - **Why a second barrier:** the native curtain auto-lifts on `Loaded→Playing` (`Base.Utils.LevelSwitchCurtainController.OnLevelStateChanged → LiftCurtain()`) **per instance, no cross-peer wait** — the faster machine would enter the geoscape first. **The native curtain IS held**: a Harmony PREFIX on `OnLevelStateChanged` (`CurtainShowPatch.Prefix` `:37-52`) returns `false` on `*→Playing` while `engine.IsActive && coord.SessionStarted && !coord.Revealed` → suppresses the native auto-`LiftCurtain` so every peer HOLDS at the **still-visible native loading screen** (NOT a black cover). No fullscreen cover Image exists anymore.
  - **Reveal flow:** the Postfix (`:58`) on `Loaded→Playing` during a co-op session (`coord.SessionStarted`) calls `coord.OnReachedPlaying()` (HOLD; idempotent `SendLoadComplete`) instead of `HideLoadOverlay`. Host collects all `LoadComplete`; on `AllDone(roster)` it broadcasts `RevealAll` (once-guarded) and lifts locally. Every peer's `OnRevealAll` → `PerformDeferredLift()` reflection-invokes native `LiftCurtain()` (animated alpha→0, restores input+sound via `GeoscapeView.OnCurtainLifted`) then `MultiplayerUI.Instance?.HideLoadOverlay()` (guarded `_revealed`) → simultaneous reveal.
  - **Native-bar clone rows (CONFIRMED in-game; native-font label in-game-pending).** Overlay rows are CLONES of the native `Base.Utils.ProgressBarController` (`Image ProgressFill` fillAmount + `Text ProgressText`), captured via `NativeWidgetFactory.CaptureLoadingBarTemplate()` (`FindObjectOfType` SceneFadeController → `ProgressBar` field → gameObject). One small bar per OTHER player TOP-RIGHT — **self-row hidden** via `SlotIndex == Session.LocalSlotIndex` skip (`LoadOverlayController.cs:272`). Name `Text` label shows the player NICKNAME in the **native PP loading font**, captured off the cloned bar's `ProgressText.font` (`LoadOverlayController.cs:134-142`; legacy `UnityEngine.UI.Text`), fallback chain native-font → captured menu font → builtin Arial. The cloned controller is DISABLED (so its Update ramp doesn't fight us); fill driven manually from the live native bar value (see Real-bar progress below). NULL-template fallback to the old from-code Image row preserved.
  - **Real-bar progress — live native fillAmount forwarded raw, NO easing (`4976474`).** `FillEase.cs` + its 5 tests DELETED; overlay renders the received `percent/100` 1:1. The phase-2 pump forwards the LIVE on-screen native bar value `ProgressBarController.ProgressFill.fillAmount` — the game's OWN bar (its `Update` eases fillAmount toward the coarse `LoadingProgress.Progress` at `MoveSpeed*dt`, monotonic) → smooth-BUT-REAL (actual shown value, NOT a mod fabrication). Captured via `NativeWidgetFactory.CaptureLiveProgressBar` = `SceneFadeController.ProgressBar` (`NativeWidgetFactory.cs:333`), cached `SaveTransferCoordinator._liveProgressBar` (`:114`; set in `SetLoadingLevel` `:165-166`, cleared on Playing/Loaded + `OpenBarrier` `:308`; never `FindObjectOfType` per frame). Pump reads `GetProgressFill(_liveProgressBar).fillAmount` → `ProgressByte` (`:756-766`), falls back to `_loadingLevel.LoadingProgress.Progress` if uncaptured. DONE-signal UNCHANGED: `LoadingProgress == null` (`:775`). Cadence `SnapshotIntervalMs` 200→50 (≈20 Hz host re-broadcast `:96`); per-peer report on-change (`pct != _lastReportedLoadPct` `:767`).

### InPhase2 pump decoupling (fixes the host-stall, bugs B+C)

- The phase-2 native-load read + `ReportLoadProgress` + `SendLoadComplete` previously lived inside `LoadOverlayController.Update()` behind `if (!_visible) return;` → a peer whose overlay was hidden never reported phase-2 nor fired done → **host aggregate stalled forever.**
- Now the pump runs in `SaveTransferCoordinator.Update()` **above the host-only early-return**, on every peer each frame (NetworkEngine.Update → SaveTransfer.Update, unconditional). Predicate `RosterProgressTracker.InPhase2(begun, loadCompleteSent) => begun && !loadCompleteSent` (Unity-free, test-linked) gates it. `LoadOverlayController.Update()` is now UI-refresh-only.
- **Progress-source root cause (corrects the prior `CurrentLevel()` claim).** The phase-2 bar was STATIC because `GameUtl.CurrentLevel()` returns NULL during a geoscape load — old level cleared at `Game.cs:191`, new one assigned only at `Game.cs:211` AFTER `LoadCrt`. The pump read null every frame → never reported real phase-1 progress (bar held phase-0 download=100%). FIX: pump reads the actual LOADING `Level`, captured from `CurtainShowPatch.Postfix(object level,object prevState,object newState)` → `SaveTransferCoordinator.SetLoadingLevel(level)` on "Loading" (cleared on "Playing"/"Loaded"), then `_loadingLevel.LoadingProgress.Progress`; `LoadingProgress==null` ⇒ `SendLoadComplete`. The native bar binds identically (`SetLoadingLevel` → `level.LoadingProgress`), so it shows real % while `CurrentLevel()` is null. (CONFIRMED in-game over DirectIP)
- **Client-show predicate (bug B).** Client never showed the overlay: `TransferActive` is false by the time its curtain hits "Loading" (`_rxTotalBytes`=0 after ResetRx; `IsBarrierPending`=false because `_begun` set in EnterLevel before FinishLevel). The `CurtainShowPatch` "Loading" gate is now `!(coord.TransferActive || coord.InPhase2)` so the client shows during phase-2.

### Wire format

- **`RosterProgress = 0x1D`** — host-aggregated per-slot snapshot (unreliable). **`LoadProgress = 0x1A`** — per-peer phase-2 byte. **`RevealAll = 0x1F`** (reliable; next free after `LoadComplete = 0x1E`, `0x20` begins the Tactical block) — second-barrier synchronized reveal. `MessageSerializer.SerializeRevealAll(long serverTicks)` / `DeserializeRevealAll(byte[])→long` mirror SessionBegin (BinaryWriter Int64). Routed `NetworkEngine.RouteMessage → SaveTransferCoordinator.OnRevealAll`. Tracker keyed by slotIndex (NEVER LocalSteamId — 2-instance same-machine rig).

### Deadlock fallbacks (both placed ABOVE the host-only return so clients reach them)

- **(a) Host forced reveal** after `_phase2DeadlineMs` (= `Begin()` time + `BarrierTimeoutMs` = 60s) if any peer never reports done.
- **(b) Per-peer self-reveal** after `BarrierTimeoutMs` from `_revealHoldStartedMs` if the host dies / RevealAll never arrives.
- All phase-2 fields (`_reachedPlaying`, `_revealHoldStartedMs`, `_phase2DeadlineMs`, `_revealed`, `_revealAllSent`, `_lastReportedLoadPct`) reset in `OpenBarrier`. Same-machine 2-instance test rig safe: done-set keyed by slotIndex, barrier by `msg.SenderSteamId`, nothing on LocalSteamId.
- Status: co-op load **CONFIRMED WORKING in-game over DirectIP** end-to-end across `ab4b921`(barrier decouple + reveal) → `602a51b`(loading-Level + native-bar rows) → `1e9f122`(self-hide + names + simultaneous reveal) → `b1dbd00`(reverted easing) → `4976474`(real native-bar fillAmount, easing removed, 20Hz). Only the **native-font nickname label** (native-font label commit, in-game-pending) and **STUN/UDP save transfer** (best-effort, no ACK/retransmit → use DirectIP; deferred) remain unconfirmed. **MultiplayerLog** falls back to `multiplayer-2.log`..`-5.log` on IOException so the 2nd same-machine instance gets its own log instead of Player.log. Instrumentation: `[Multiplayer]` lines at the show predicate, phase-2 pump tick (pct), RosterProgress SEND/RECV, OnReachedPlaying/PerformDeferredLift/AllDone→RevealAll. Exact file:line → [00-current-state](00-current-state.md) §Co-op loading screen.

## Mid-Battle Save Caveat — RESOLVED

- Reconnect mid-tactical-mission needs a **save while in battle**.
- ~~The game supports mid-battle save — **but possibly via an experimental mod, not vanilla.**~~
  - **RESOLVED = vanilla.** `TacticalView.QuickSaveGame():1133` → `SaveGameCrt():1141` exists in vanilla decompile; `PPSavegameMetaData.isTacticalSave` bool (`Base.Serialization\PPSavegameMetaData.cs:50`); no tactical-specific SaveType. Mid-battle save is vanilla — no mod dependency needed.
- ~~v1 fallback~~ no longer needed; tactical reconnect path is viable.

## Host Disconnect (no migration in v1)

- Host = the single authoritative node. If the **host** drops, there is **nothing to fail over to**.
- **v1:** session ends. No host migration.
- Clients' transport detects host loss → client-side **toast**: "Host lost — session ended" → return to menu.
- Host migration / re-host = **deferred** → [open questions](../specs/03-open-questions-sdk.md).

## Toasts / Notices (system events)

- New message `NOTICE{ code, args }` (see [engine/02-transport-layer](../engine/02-transport-layer.md)) — system/session events, distinct from in-game `EVENT` ([08-geoscape-concurrency](08-geoscape-concurrency.md)).
- Triggers: peer disconnect, takeover, reconnect start/finish, host loss (client-detected), kick/abort.
- Cosmetic only — never gates game state; the barrier handles actual synchronization.

## Related

- Session-start barrier reused here → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md).
- Full-state resync rationale → [04-serialization](04-serialization.md).
- Ownership / permission rebind by `playerGUID` → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md).
- Global pause used during resync → [08-geoscape-concurrency](08-geoscape-concurrency.md).
