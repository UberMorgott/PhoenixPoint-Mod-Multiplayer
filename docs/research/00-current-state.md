# Multipleer — Current State (status note)

> Single-glance "where are we now" for the co-op mod. Updated as the as-built state moves.
> Companion to the design index (`docs/superpowers/specs/2026-06-12-geoscape-command-sync-design.md`)
> and the staging plans (`docs/superpowers/plans/`). Engine as-built detail: `docs/engine/`.

## CURRENT STATE — 2026-06-13

- **Branch:** `feat/geoscape-command-sync`. HEAD = `c5e2b2a` *feat(geo-sync): CommandRelay + StartTravel intercept end-to-end (first vertical proof)*.
- **Uncommitted working-tree changes** (4 src files + 2 new docs + goldberg tooling) — see "Undocumented-but-shipped" below; NOT yet committed.

### Done + in repo (geoscape command sync — Stage 1)

- **CommandSync layer fully present** `src/Network/CommandSync/` (8 files): `CommandCodec`, `PermissionGate`, `InterceptRegistry` (all pure, test-linked) + `HostArbiter`, `ClientApplier`, `CommandRelay`, `CommandExecutor`, `GeoBridge` (engine seams, build+manual).
  - `CommandRelay.Wire(NetworkEngine.Instance)` called from `src/UI/MultiplayerUI.cs:189` (host start) and `:310` (client connect). Idempotent (detaches prior wiring).
  - Wires THREE engine events: `OnCampaignActionRequest → HostArbiter.HandleRequest`, `OnHostCampaignActionResult (0x31) → ClientApplier.HandleResult`, `OnHostCampaignActionRejected (0x32) → ClientApplier.HandleRejected` (log-only, NO apply). The rejected channel was split out (`e665c8c`) so the originator never re-applies a host-refused action. **(Stage-1 plan text predates this split — it described one channel applying every result; the shipped code is the corrected form.)**
  - `CommandRelay.ApplyResult` `CommandRelay.cs:65-83` sets `[ThreadStatic] _applying` and calls `CommandExecutor.Execute`; `IsApplying` guard lets the intercept prefix pass a re-entrant apply through.
  - `CommandExecutor.Execute` switch has **only `StartTravel`** today (`CommandExecutor.cs:17`); `default → LogWarning`. Other curated rows (C1–C7) are registry-dormant or pending.
  - `NetworkEngine.BroadcastCampaignActionResult` `NetworkEngine.cs:288` fans an approved action to all peers via `0x31`.
- **First vertical proof = `GeoVehicle.StartTravel(List<GeoSite>)`** — `src/Harmony/StartTravelInterceptPatch.cs`. Client → encode + relay + block; host → execute + postfix broadcast.
- **`CampaignActionType` enum** `MessageSerializer.cs:533-549` ends at `StartTravel = 13`. No `SetTimeState` yet.
- **Permissions:** real per-GUID gate `PermissionGate` + `PermissionManager` (`ControlTime = 1<<7`, `FullCommander = 1<<9` override). See FullCommander interim below.

### Stubs / NOT implemented

- **`0x33 CampaignActionResult` / `0x34 CampaignStateUpdate` receive-cases are TODO stubs** — `NetworkEngine.cs:530-536`. `BroadcastCampaignState(byte[])` `:295` exists (generic framing) but no peer applies it.
- **Time / clock sync — ZERO code in `src/`.** Only the `ControlTime` permission BIT is defined (`PermissionManager.cs:18`); no Timing patch, no time action, no clock broadcaster exists. → the new time-sync increment-1 plan is **greenfield, not duplicating anything**.
- **Per-player permission MENU** — not built; deferred (vision in `03-campaign-layer.md` §Roadmap + `specs/02` §4). Interim = FullCommander-for-all.

### Undocumented-but-shipped (uncommitted working tree, 2026-06-13)

- **FullCommander default grant** (interim "allow everything", no perm menu yet):
  - `SessionManager.cs:218` — joining client granted `FullCommander` keyed by `PlayerGuid` after JOIN (PlayerGuid only bound post-`AddClient`); mirrors mask onto `ClientInfo.Permissions` for the roster.
  - `SessionManager.cs:356` — host self-entry granted `FullCommander`; host-row `Permissions` now reflects the real mask (was hardcoded `0`).
  - **Fixes:** clients previously had no permission entry → `HasCampaignPermission` returned `false` → host rejected ALL client `StartTravel`. (Documented in `03-campaign-layer.md` §Interim.)
- **Lobby exit guards** `src/Harmony/MainMenuPatches.cs`:
  - `LobbyEscapeLeavePatch` **renamed → `LobbyEscapeSuppressPatch`**: Escape inside the open lobby now SWALLOWED (no Options, no leave/teardown). Previously Escape ran `OnLobbyLeave` (accidental Escape broke the session).
  - **NEW `LobbyCancelSuppressPatch`**: prefixes `HomeScreenViewState.OnInputEventInternal` to swallow the "Cancel" (RMB/back) gesture inside the lobby so back-navigation can't tear down the menu under the overlay. Gated by `IsLobbyOpen` → inert everywhere else. **Leaving the lobby is ONLY via the LEAVE button.**
- **DirectTransport connect-fail diagnostic** `src/Transport/DirectTransport.cs`: the previously-silent `catch` on DirectIP connect now logs the real failure (`SocketException` → includes `SocketErrorCode`) via a reflection-based `LogError` (file stays Unity-free for the test link; no-op/Console fallback under tests).

### Co-op loading screen — CONFIRMED WORKING in-game over DirectIP (end-to-end)

**The full co-op load flow works in-game over DirectIP**, lobby → world: lobby Ready/AllReady → host presses **Play** (save-required warning if no save chosen; the native Play button repaints immediately via `PhoenixGeneralButton.SetInteractable` reflection in `LobbyPanel.RefreshPlayButtonVisual` `:975`, driven on every ready-state change `:962`) → host serializes + chunks the save, clients reassemble (`SaveTransferCoordinator` SaveChunk/SaveDone) → **phase-1 LOADED/BEGIN barrier** (`OpenBarrier`/`SendLoaded`/`OnClientLoaded`/`TryReleaseBarrier`/`Begin`/`EnterLevel→FinishLevel`, host gate `_barrierOpen||_loadPhaseActive`, 60s timeout/kick) → native loading screen with a **TOP-RIGHT overlay** → **phase-2 RevealAll barrier** → simultaneous geoscape reveal. Design detail → [09-disconnect-reconnect](09-disconnect-reconnect.md) §Co-op Loading Overlay.

**Commit lineage (all on `main`, deployed + hash-verified, NOT pushed):** `ab4b921`(decouple phase-2 pump + add reveal barrier) → `602a51b`(read loading-Level for progress + show client overlay + native-bar rows) → `1e9f122`(hide self + name bars + simultaneous reveal) → `b1dbd00`(smooth bar — reverted) → `4976474`(forward real native-bar fillAmount, easing removed, 20Hz) → (native-font label commit, in-game-pending).

**Overlay** (`src/UI/LoadOverlayController.cs`): clones the native `Base.Utils.ProgressBarController` (one bar per **OTHER** player TOP-RIGHT — self-row hidden via `SlotIndex == Session.LocalSlotIndex` skip `:272`), `Image ProgressFill` fillAmount + `Text ProgressText`, captured via `NativeWidgetFactory.CaptureLoadingBarTemplate()` (`FindObjectOfType` SceneFadeController → `ProgressBar` field → gameObject). The cloned controller is DISABLED so its own Update ramp doesn't fight us. Nickname label (from `PeerListEntry.Nickname` via `GetLobbyRoster()`; fallback `"Player {slot}"`) renders in the **native PP loading font**, captured off the cloned bar's `ProgressText.font` (`:134-142`; legacy `UnityEngine.UI.Text`), fallback chain native-font → captured menu font → builtin Arial. NULL-template fallback to the old from-code Image row preserved.

**Progress source — live native fillAmount, forwarded RAW (no easing).** `src/UI/FillEase.cs` + its 5 tests DELETED; the overlay renders received `percent/100` 1:1 (no client-side easing/prediction). The phase-2 pump forwards the LIVE on-screen native bar value `Base.Utils.ProgressBarController.ProgressFill.fillAmount` — the game's OWN bar, whose `Update` eases fillAmount toward the coarse `LoadingProgress.Progress` at `MoveSpeed*dt` (monotonic, snaps ~1.0 near done) → smooth-BUT-REAL (the actual shown value, NOT a mod fabrication). Captured via `NativeWidgetFactory.CaptureLiveProgressBar` = `SceneFadeController.ProgressBar` field (`NativeWidgetFactory.cs:333`), cached `SaveTransferCoordinator._liveProgressBar` (`:114`; set in `SetLoadingLevel` `:165-166`, cleared on Playing/Loaded + `OpenBarrier` `:308`; never `FindObjectOfType` per frame). Pump reads `GetProgressFill(_liveProgressBar).fillAmount` → `ProgressByte` → `ReportLoadProgress` (`:756-772`), falls back to `_loadingLevel.LoadingProgress.Progress` if uncaptured. **Done-signal UNCHANGED: `LoadingProgress == null`** (`:775`; fillAmount holds ~1.0 near end so MUST NOT gate done). slotIndex-keyed aggregation, broadcast as `RosterProgress 0x1D` at ≈20 Hz (`SnapshotIntervalMs=50` `:96`); per-peer report on-change (`pct != _lastReportedLoadPct` `:767`). Other opcodes: `LoadProgress 0x1A` (per-peer phase-2 byte), `RevealAll 0x1F` (reliable second-barrier). slotIndex-keyed tracker (NEVER LocalSteamId — 2-instance same-machine rig).

**Phase-2 pump placement (fixed the host-stall).** The pump runs at the TOP of `SaveTransferCoordinator.Update()`, ABOVE the host-only early-return (`if (!_engine.IsHost || (!_barrierOpen && !_loadPhaseActive)) return;`), on every peer each frame (NetworkEngine.Update → SaveTransfer.Update, unconditional). Predicate `RosterProgressTracker.InPhase2(begun, loadCompleteSent) => begun && !loadCompleteSent` (`RosterProgressTracker.cs:64`, Unity-free) + coordinator prop `InPhase2` (`:135`). `LoadOverlayController.Update()` is UI-refresh-only. Loading `Level` captured from `CurtainShowPatch.Postfix(object level,…)` → `SetLoadingLevel` on "Loading" (cleared on Playing/Loaded) — `GameUtl.CurrentLevel()` is NULL during a geoscape load (old level cleared `Game.cs:191`, new assigned only `Game.cs:211` after `LoadCrt`), so the pump must read the captured loading Level (the native bar binds identically, which is why IT shows real %). Client-show gate is `!(coord.TransferActive || coord.InPhase2)` so the client overlay shows during phase-2.

**Simultaneous reveal — second barrier via native-curtain-hold.** The native curtain auto-lifts per-instance on `Loaded→Playing` (`Base.Utils.LevelSwitchCurtainController.OnLevelStateChanged → LiftCurtain()`). A Harmony PREFIX (`src/Harmony/CurtainShowPatch.cs:37-52`) returns `false` on `*→Playing` while `engine.IsActive && coord.SessionStarted && !coord.Revealed` → SUPPRESSES the auto-lift so every peer HOLDS at the **still-visible native loading screen** (no fullscreen cover Image — the old "Cover" was removed). The Postfix (`:58`) fires `coord.OnReachedPlaying()` (hold + idempotent `SendLoadComplete`, opcode `LoadComplete 0x1E`; records `_revealHoldStartedMs`). Host collects every `LoadComplete` (`MarkDone`); on `_tracker.AllDone(GetRosterSlots())` broadcasts `RevealAll 0x1F` (reliable, once-guarded `_revealAllSent`; `MessageSerializer.SerializeRevealAll(long)`/`Deserialize…`→long `:495,505`, BinaryWriter Int64; routed `NetworkEngine.RouteMessage → OnRevealAll` `:500`) + lifts locally; every peer's `OnRevealAll → PerformDeferredLift` reflection-invokes native `LiftCurtain()` (animated alpha→0, unpauses rendering, fires `OnCurtainLifted → GeoscapeView` restores input+sound) then `HideLoadOverlay`, behind the `_revealed` once-guard. `SaveTransferCoordinator.Revealed` getter gates the prefix.

**DEADLOCK-SAFE** (both release paths ABOVE the host-only return so clients reach them): (a) host forced-reveal at `_phase2DeadlineMs` (= `Begin()` NowMs()+`BarrierTimeoutMs` = 60s) if a peer never reports done; (b) per-peer self-reveal after `BarrierTimeoutMs` from `_revealHoldStartedMs` if the host dies / RevealAll never arrives. Suppression gated on `!Revealed`; every `_revealed` path lifts the native curtain → no reachable stuck-suppressed-curtain state. REVIEWER ship-cleared. Phase-2 fields (`_reachedPlaying`,`_revealHoldStartedMs`,`_phase2DeadlineMs`,`_revealed`,`_revealAllSent`,`_lastReportedLoadPct`) reset in `OpenBarrier`. **Same-machine 2-instance rig safe:** done-set keyed by slotIndex, barrier by `msg.SenderSteamId`, NOTHING on LocalSteamId.

**KNOWN LIMITATIONS:**
- **(a) Coarse engine source.** `LoadingProgress.Progress` is step-quantized; the native bar's own `MoveSpeed` ease smooths the forwarded `fillAmount` (real, not fabricated).
- **(b) STUN/UDP save transfer is BEST-EFFORT — use DirectIP.** 32KB chunks fragment over UDP with NO ACK/retransmit; one lost fragment fails the transfer → client never completes the save → never sends `LOADED` → phase-1 barrier sticks at `loadedPeers=1/expected=2`. Diagnosed via phase-1 instrumentation (`61582b4`). FIX (deferred): ACK/retransmit layer on `StunTransport`.

**NOT done yet:** time-sync flight A (host-authoritative clock) — plan `docs/superpowers/plans/2026-06-13-time-sync-stage2-increment1.md`; time-sync increments 2-3; per-player permission menu.

**MultipleerLog:** client (2nd same-machine instance) falls back to `multipleer-2.log`..`-5.log` on IOException → its own dedicated log instead of Player.log. Instrumentation: `[Multipleer]` lines at the show predicate, phase-2 pump tick (pct), RosterProgress SEND/RECV, OnReachedPlaying/PerformDeferredLift/AllDone→RevealAll.

**Tests: 85/85 green** (`InPhase2_*` in `RosterProgressTrackerTests.cs`, `RevealAll_RoundTrips` in `RosterProgressSerializerTests.cs`). Only the **native-font nickname label** is in-game-pending (the font only); the rest of the flow is in-game-CONFIRMED over DirectIP.

### Active next step

- **Time Sync — Stage 2 Increment 1 (host-authoritative time)** — plan `docs/superpowers/plans/2026-06-13-time-sync-stage2-increment1.md`, grounded in `docs/research/12-time-flow-and-sync-seams.md`. Adds `SetTimeState = 14`, client pause/speed intercepts, client hourly-sim suppression, and a continuous `0x34` clock mirror (`RecordInstanceData`/`ProcessInstanceData`). Verified to NOT overlap any existing code.

### Deferred / out of scope (now)

- Time-sync **Increments 2 (world-state delta broadcast) + 3 (route base/research/manufacture/squad inputs)**.
- **Per-player permission menu** (granular per-resource rights, aircraft/soldier ownership) — `03-campaign-layer.md` §Roadmap, `specs/02` §4.
- Broaden the curated intercept registry beyond `StartTravel` (Stage-1 plan Task 7 — most rows are `SignatureConfirmed = false` dormant or absent in this build).
- Tactical sync; reconnect/divergence-resync beyond the existing save-transfer path.

### In-game verification status (2-instance)

- **Pending / to re-test** (per `multipleer-second-instance-setup`):
  - host→client `StartTravel` mirror ✓ (first proof committed); client→host `StartTravel` after FullCommander grant — **re-test pending** (the grant is uncommitted).
  - Lobby `Escape` / RMB-`Cancel` guard — in-game pending.
  - DirectTransport connect-fail logging — surfaces the real socket error in `Player.log`, pending live confirm.
  - Co-op load over **DirectIP — CONFIRMED WORKING in-game ✓** end-to-end: Play → save transfer → phase-1 LOADED/BEGIN barrier → phase-2 native-bar overlay (self-hidden, named, real native fillAmount) → phase-2 RevealAll → simultaneous geoscape reveal via native-curtain-hold (prefix-suppress `LevelSwitchCurtainController.OnLevelStateChanged` + deferred `LiftCurtain()` on `RevealAll 0x1F`, deadlock-safe). STUN/UDP transfer **BEST-EFFORT — known limitation (deferred)**, needs ACK/retransmit on `StunTransport` (diagnosed via `61582b4`).
  - **Native-font nickname label** (native-font label commit, in-game-pending) — the overlay nickname now renders in the native PP loading font captured off the cloned bar's `ProgressText.font`; only this font swap is unverified in-game (the rest of the load flow is confirmed).
