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

## Co-op Loading Overlay — as-built two-phase barrier (`ab4b921`)

> The session-start / resync barrier above describes the *design*. This is the *as-built* loading screen: the barrier is **two** barriers, because the native level-load and the native geoscape curtain each release per-instance and must be re-synchronized twice. Pump lives in `src/Network/SaveTransferCoordinator.cs`; UI in `src/UI/LoadOverlayController.cs`.

- **Phase 1 — LOADED/BEGIN (save received → native load done).** Unchanged. Host gates on `_barrierOpen||_loadPhaseActive`, collects LOADED, broadcasts BEGIN; 60s timeout/kick. Set up in `Begin()`/`OpenBarrier`.
- **Phase 2 — RevealAll (native curtain auto-lift → synchronized world reveal). NEW.**
  - **Why a second barrier:** the native curtain auto-lifts on `Loaded→Playing` (`Base.Utils.LevelSwitchCurtainController.OnLevelStateChanged → LiftCurtain()`) **per instance, no cross-peer wait** — the faster machine would enter the geoscape first. The native curtain can't be held, so our overlay becomes a **synchronized opaque cover** that every peer drops at the same instant.
  - **Reveal flow:** on `Loaded→Playing` during a co-op session (`coord.SessionStarted`), `CurtainShowPatch` calls `coord.OnReachedPlaying()` (HOLD; idempotent `SendLoadComplete`) instead of `HideLoadOverlay`. Host collects all `LoadComplete`; on `AllDone(roster)` it broadcasts `RevealAll` (once-guarded) and lifts locally. Every peer's `OnRevealAll` → `PerformDeferredLift()` = `MultiplayerUI.Instance?.HideLoadOverlay()` (guarded `_revealed`).
  - **Native-bar clone rows (latest co-op-load fix, in-game pending).** Overlay rows are NO LONGER custom from-code Images — they are CLONES of the native `Base.Utils.ProgressBarController` (`Image ProgressFill` fillAmount + `Text ProgressText`), captured via `NativeWidgetFactory.CaptureLoadingBarTemplate()` (`FindObjectOfType` SceneFadeController → `ProgressBar` field → gameObject). One small bar per player TOP-RIGHT, name `Text` label. The cloned controller is DISABLED (so its Update ramp doesn't fight us); driven manually `ProgressFill.fillAmount = Tracker.Get(slot).percent/100`. BOTH local + remote rows tracker-driven; ALL players shown INCLUDING self (self-row-skip removed). NULL-template fallback to the old from-code Image row preserved.
  - **Cover REMOVED (regression).** The prior FULLSCREEN opaque black "Cover" Image blacked out the host's own loading screen — removed. (D simultaneous-reveal via holding the native curtain is **DESIGNED but HELD / not yet implemented**, pending in-game B/C verification.)

### InPhase2 pump decoupling (fixes the host-stall, bugs B+C)

- The phase-2 native-load read + `ReportLoadProgress` + `SendLoadComplete` previously lived inside `LoadOverlayController.Update()` behind `if (!_visible) return;` → a peer whose overlay was hidden never reported phase-2 nor fired done → **host aggregate stalled forever.**
- Now the pump runs in `SaveTransferCoordinator.Update()` **above the host-only early-return**, on every peer each frame (NetworkEngine.Update → SaveTransfer.Update, unconditional). Predicate `RosterProgressTracker.InPhase2(begun, loadCompleteSent) => begun && !loadCompleteSent` (Unity-free, test-linked) gates it. `LoadOverlayController.Update()` is now UI-refresh-only.
- **Progress-source root cause (corrects the prior `CurrentLevel()` claim).** The phase-2 bar was STATIC because `GameUtl.CurrentLevel()` returns NULL during a geoscape load — old level cleared at `Game.cs:191`, new one assigned only at `Game.cs:211` AFTER `LoadCrt`. The pump read null every frame → never reported real phase-1 progress (bar held phase-0 download=100%). FIX: pump reads the actual LOADING `Level`, captured from `CurtainShowPatch.Postfix(object level,object prevState,object newState)` → `SaveTransferCoordinator.SetLoadingLevel(level)` on "Loading" (cleared on "Playing"/"Loaded"), then `_loadingLevel.LoadingProgress.Progress`; `LoadingProgress==null` ⇒ `SendLoadComplete`. The native bar binds identically (`SetLoadingLevel` → `level.LoadingProgress`), so it shows real % while `CurrentLevel()` is null. (latest co-op-load fix, in-game pending)
- **Client-show predicate (bug B).** Client never showed the overlay: `TransferActive` is false by the time its curtain hits "Loading" (`_rxTotalBytes`=0 after ResetRx; `IsBarrierPending`=false because `_begun` set in EnterLevel before FinishLevel). The `CurtainShowPatch` "Loading" gate is now `!(coord.TransferActive || coord.InPhase2)` so the client shows during phase-2.

### Wire format

- **`RosterProgress = 0x1D`** — host-aggregated per-slot snapshot (unreliable). **`LoadProgress = 0x1A`** — per-peer phase-2 byte. **`RevealAll = 0x1F`** (reliable; next free after `LoadComplete = 0x1E`, `0x20` begins the Tactical block) — second-barrier synchronized reveal. `MessageSerializer.SerializeRevealAll(long serverTicks)` / `DeserializeRevealAll(byte[])→long` mirror SessionBegin (BinaryWriter Int64). Routed `NetworkEngine.RouteMessage → SaveTransferCoordinator.OnRevealAll`. Tracker keyed by slotIndex (NEVER LocalSteamId — 2-instance same-machine rig).

### Deadlock fallbacks (both placed ABOVE the host-only return so clients reach them)

- **(a) Host forced reveal** after `_phase2DeadlineMs` (= `Begin()` time + `BarrierTimeoutMs` = 60s) if any peer never reports done.
- **(b) Per-peer self-reveal** after `BarrierTimeoutMs` from `_revealHoldStartedMs` if the host dies / RevealAll never arrives.
- All phase-2 fields (`_reachedPlaying`, `_revealHoldStartedMs`, `_phase2DeadlineMs`, `_revealed`, `_revealAllSent`, `_lastReportedLoadPct`) reset in `OpenBarrier`. Same-machine 2-instance test rig safe: done-set keyed by slotIndex, barrier by `msg.SenderSteamId`, nothing on LocalSteamId.
- Status: `ab4b921` deployed + hash-verified, **in-game pending** (UI/Harmony/curtain seams integration-only). Latest co-op-load fix (`CurrentLevel()`-null root cause, client-show predicate, native-bar clone rows, Cover removed) on `main`, deployed + hash-verified, NOT pushed, **in-game pending**. **MultipleerLog** falls back to `multipleer-2.log`..`-5.log` on IOException so the 2nd same-machine instance gets its own log instead of Player.log. Instrumentation: `[Multipleer]` lines at the show predicate, phase-2 pump tick (pct), RosterProgress SEND/RECV, OnReachedPlaying/PerformDeferredLift/AllDone→RevealAll. Exact file:line → [00-current-state](00-current-state.md) §Co-op loading screen.

## Mid-Battle Save Caveat

- Reconnect mid-tactical-mission needs a **save while in battle**.
- The game supports mid-battle save — **but possibly via an experimental mod, not vanilla.**
  - ⚠️ **VERIFY (SDK):** is mid-battle save **vanilla** or **mod-provided**? → [open questions](../specs/03-open-questions-sdk.md).
  - If mod-provided → either **depend on that mod** or implement our own tactical snapshot via [04-serialization](04-serialization.md).
- **v1 fallback if mid-battle save is unavailable/unreliable:** allow reconnect only on the **geoscape**. Drop in battle → host controls the player's soldiers until the mission ends → player rejoins at the next geoscape resync.

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
