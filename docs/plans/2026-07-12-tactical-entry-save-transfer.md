# Tactical mission entry via mid-tactical save transfer — implementation plan (2026-07-12)

Replace the client's self-built tactical level (native launch + deploy-table reconcile + snap) with a
**host-authored mid-tactical save, transferred and loaded natively on the client** — byte-identical
placement / population / loot / quest state. Adds an explicit **host loading-screen barrier** (host does
not reveal tactical until every client finished loading).

All `file:line` are real source (`Multiplayer\src\...`, `Multiplayer\Multiplayer.Core\...`) or the
decompile (`decompiled\AssemblyCSharp\...`, marked *decompile*). Grounded via direct reads; Serena MCP
was unavailable this session, so the mod source (authoritative C#) + decompile were read directly.

---

## 0. The load-bearing insight (why this is small, not a rewrite)

The **save-transfer-into-tactical machinery already exists and works** — it is the F2 mid-tactical
reload path. Today, a host F2-load while in a tactical mission reroutes through
`HostStartSessionInGame` → `LaunchTransfer` → chunked transfer + LOADED/BEGIN barrier + reveal barrier;
the client loads the transferred save natively (`ClientLoadCrt → PrepareEntryFromBlobCrt → EnterLevel →
FinishLevel`) and the late `tac.deploy` hydrates the already-live level via the **HydrateExisting** path
(`TacticalDeploySync.OnDeployReceived`, `TacticalDeploySync.cs:536-556`; `ClientOnLevelReady(alreadyLoaded:true)`).

This arc = **trigger that same flow automatically at mission entry**, instead of on F2, and stop the
client self-launch. ~95% is reuse. The genuinely new code is:
1. A **tactical-safe save writer** (vanilla `AutosaveGame` NREs mid-tactical — see §1).
2. A **host tactical-entry transfer coroutine** that opens the barrier at launch (arms the reveal-hold),
   sends the blob at deploy-ready, and — unlike lobby/F2 — does NOT re-enter the level on the host (the
   host is already in the tactical level it built to capture the save).
3. **Suppressing the client legacy self-launch** (`ClientLaunchMission`) on this path.

Keep memory `multiplayer-reconcile-existing-first` + `dont-replace-working-architecture`: modify the
existing flow, feature-flag it, keep old rails bypassed (not deleted) until gates pass.

---

## 1. Chosen native save API (grounded) — vanilla `AutosaveGame` is UNUSABLE mid-tactical

- `PhoenixSaveManager.IsTactical => !_game.CurrentLevel.GetComponent<GeoLevelController>()`
  (*decompile* `PhoenixSaveManager.cs:101`) — true exactly when NOT on the geoscape.
- **`AutosaveGame()` NREs in tactical** (*decompile* `PhoenixSaveManager.cs:414`): it unconditionally
  does `GameUtl.CurrentLevel().GetComponent<GeoLevelController>()` then derefs `component.Timing.Now.DateTime`
  (`:421-422`). In tactical `GetComponent<GeoLevelController>()` is null → NRE. (This is why the P1
  on-demand-join path is geoscape-gated, and why `ClientAutosaveSkipPatch` exists.) **Do NOT call
  `AutosaveGame` for the tactical save.**
- **Tactical-safe native writers** (all funnel to the shared `SaveGame(PPSavegameMetaData, ext,
  ByRef<bool> written, showCurtain)`):
  - `QuickSave()` (*decompile* `:502`) — null-guards GeoLevelController, and when
    `CurrentLevel().LevelParams is TacticalGameParams tgp` sets `unityDateTime = tgp.GlobalTime` (`:520-524`),
    `IsTactical`-tagged; blocked by `IsIronmanMode` + writes name "quicksave" (would clobber the user's quicksave).
  - `SaveWithName(name, ext)` (*decompile* `:549`) — gated on `CurrentLevel.GetComponent<ISavegameProvider>()`
    (the tactical level implements it), `IsTactical`-aware, `SaveGame(..., showCurtain:true)`.
  - `IronmanSave()` (*decompile* `:460`) — the definitive tactical branch:
    `unityDateTime = (geoLevelController != null) ? geo.Timing.Now.DateTime : (CurrentLevel().LevelParams is
    TacticalGameParams tgp ? tgp.GlobalTime : new UnityDateTime())` (`:476`), then `SaveGame(...)`.
- **RECOMMENDED writer**: a small mod helper `HostWriteTacticalSaveCrt` that mirrors QuickSave's tactical
  branch — build a `PPSavegameMetaData` with `time = TacticalGameParams.GlobalTime`, `IsTactical=true`,
  a **dedicated unique name** (`EnsureUnique("coop_tac_xfer")` — NOT "autosave"/"quicksave", so it never
  clobbers a user save) — and call `SaveGame(meta, SerializationComponent.DefaultExtension, written,
  showCurtain:false)`. `showCurtain:false` = no save-curtain flash over the held host screen.
  - Then read it back to bytes exactly like the on-demand join does:
    `game.SaveManager.Serializer.ReadSavegameBinary(meta, ByRef<byte[]>)`
    (`SaveTransferCoordinator.cs:478`, `:600`), then `DeleteSaveGame(meta)` after read-back.
  - This respects memory `pp-serializer-context-and-pump`: we use the game's configured
    `SaveManager.Serializer` via native `SaveGame`/`ReadSavegameBinary` (no manual `new Serializer(null)`
    round-trip, no empty-graph trap).
  - Lazy alternative if the hand-built metadata proves fiddly in-game: call native `SaveWithName(
    "coop_tac_xfer", ext)` (showCurtain:true → a brief curtain, acceptable since the host is already
    behind the co-op hold), then `ReadSavegameBinary`. Pick whichever binds cleanly; both produce the
    same tactical save bytes.

## Client load path (grounded, reuse verbatim)

- `SaveTransferCoordinator.OnSaveChunk`/`OnSaveDone` → `ClientLoadCrt` (`:963`) →
  `PrepareEntryFromBlobCrt` (`:1000-1056`, **in-memory, no disk write on client** — reads meta + level
  params + scene binding from the received `byte[]`) → `EnterLevel` (`:1265`) →
  `game.FinishLevel(_pendingResult)` (`:1282`, *decompile* `PhoenixGame.cs:263`).
- On the client's tactical `Playing` transition, `TacticalLevelStateChangedPatch.Postfix`
  (`DeployLaunchPatches.cs:48-72`) calls `ClientOnLevelReady(__instance, alreadyLoaded:true)` →
  `ClientHydrateNow` **skips the snapshot `ProcessInstanceData`** (`TacticalDeploySync.cs:771-793`) and
  only rebuilds the NetId registry + reconcile + arms mirror. This is the existing HydrateExisting
  contract; it stays unchanged.

## Host-barrier hook points (grounded)

- **Reveal hold** already exists and is level-agnostic:
  `CurtainShowPatch.Prefix` suppresses the native `Loaded→Playing` auto-lift while
  `coord.SessionStarted && !coord.Revealed` (`CurtainShowPatch.cs:39-54`); `CurtainLiftGatePatch`
  parks *every* `LiftCurtainCrt` via `SaveTransferMath.HoldCurtain(engineActive, sessionStarted, revealed)`
  (`CurtainShowPatch.cs:146-190`). `_revealed` is reset to false by `OpenBarrier`
  (`SaveTransferCoordinator.cs:811`) and set true by `PerformDeferredLift` (`:1191`).
- **Release** = `RevealAll` at all-clients-`LoadComplete`: `Update()` fires `RevealAll` when
  `_tracker.AllDone(GetRosterSlots())` (`SaveTransferCoordinator.cs:1461-1477`); fallbacks =
  host-forced reveal at `RevealDeadlineMs` (180 s, `:1427-1435`) + per-peer self-reveal (`:1438-1441`).
  `RosterProgressTracker` tracks phase0=download / phase1=load / event-driven done
  (`RosterProgressTracker.cs`).
- **So the host barrier is mostly free**: if `OpenBarrier` runs *before* the host reaches tactical
  `Playing`, the host holds behind the curtain until the new `RevealAll`. The host is counted done by
  `OnReachedPlaying → SendLoadComplete` (`CurtainShowPatch.cs:91`; `SaveTransferCoordinator.cs:1155-1180`).

---

## 2. Sequence (target flow)

HOST:
1. `LaunchTacticalGame` (geoscape). `LaunchTacticalGameGatePatch.Prefix` (`DeployLaunchPatches.cs:101`)
   → `OnTacticalLaunch` stamps site id (`TacticalDeploySync.cs:251`). **[NEW]** host also
   `OpenBarrier()` here (arms reveal-hold: `_revealed=false`, tracker reset). Gate: host + active started session.
2. Tactical `Loading` → `CurtainShowPatch.Postfix` shows overlay. Tactical `Loaded→Playing` →
   Prefix **suppresses auto-lift** (SessionStarted && !Revealed) → host HELD behind curtain.
   Postfix → `OnReachedPlaying` → `SendLoadComplete` (host slot marked done) + "Waiting for players…".
3. Deploy-ready (`HasAnyTurnStarted`, via the existing deferred-capture gate) →
   `HostCaptureAndBroadcast` (`TacticalDeploySync.cs:422`): broadcast `tac.deploy` as today, **[NEW]**
   then write the mid-tactical save (§1), read to bytes, `SendBlob` into the already-open barrier,
   set `_hostLoaded=true`, `TryReleaseBarrier`. Host does **NOT** `PrepareEntryFromBlobCrt`/`EnterLevel`
   (it is already in this tactical level).
4. Clients ack `LOADED` → `BarrierReleased` → `Begin()` broadcasts `SessionBegin`; host `EnterLevel`
   no-ops (`_pendingResult==null`, `SaveTransferCoordinator.cs:1268`).
5. All clients report `LoadComplete` → `AllDone` → `RevealAll` → `PerformDeferredLift` on all peers →
   host + clients reveal tactical simultaneously.

CLIENT (self-launch removed):
1. On geoscape, gated from self-launch (`LaunchTacticalGameGatePatch` blocks spontaneous launch;
   `ClientLaunchInProgress` never set on this path). Shows the load indicator (TacticalLoadPhaseSync
   pre-transfer, then SaveTransfer download bar).
2. Receives `tac.deploy` early → stashed `_pendingClientDeploy` (no self-launch).
3. Receives `SaveChunk`/`SaveDone` → `ClientLoadCrt → PrepareEntryFromBlobCrt` (in-memory) →
   `SendLoaded(true)` → on `BEGIN`, `EnterLevel → FinishLevel` builds the tactical level from the
   host's exact bytes.
4. Tactical `Playing` → `ClientOnLevelReady(alreadyLoaded:true)` hydrates (registry + reconcile, skip
   ProcessInstanceData) → `OnReachedPlaying → LoadComplete`. Holds behind curtain until `RevealAll`.

Result: identical actor set / positions / loot / objectives / turn / vision on both sides; deploy-table
matching, position **snap**, and pandoran matching all become **no-ops** (state already byte-identical) —
kept-but-bypassed, deleted in the cleanup batch.

---

## 3. Staging (2 batches, each in-game gateable)

### Batch 1 — entry-via-save end-to-end (client loads host's mid-tactical save)

**Goal:** on a fresh geoscape→tactical entry in co-op, the client's tactical level is built from the
host's transferred mid-tactical save (not self-generated). Host barrier deferred to Batch 2 (host may
reveal on its own for now; correctness of client state is the Batch-1 gate).

Files / anchors:
- `Multiplayer\src\Sync\Tactical\TacticalDeploySync.cs`
  - `HostCaptureAndBroadcast` (`:422-498`): after the existing `tac.deploy` broadcast, **[NEW]** call
    `NetworkEngine.Instance.SaveTransfer.HostBeginTacticalEntryTransfer(tlc)` behind a feature flag
    `UseSaveTransferEntry` (default OFF).
  - `OnDeployReceived` (`:507-567`): **[CHANGE]** when `UseSaveTransferEntry`, the client NEVER takes the
    legacy `ClientLaunchMission` branch (`:564`). Keep only the HydrateExisting branch; when no live
    level yet, stash `_pendingClientDeploy` and wait for the save-transfer to build the level (do nothing else).
  - `ClientLaunchMission` (`:581-637`): dead on the new path — leave in place, guarded off by the flag
    (rollback safety). Delete in the cleanup batch.
- `Multiplayer\src\Network\SaveTransferCoordinator.cs`
  - **[NEW]** `HostBeginTacticalEntryTransfer(object tlc)` + `HostTacticalEntryTransferCrt`: mirror
    `HostSerializeAndSendCrt` (`:475-500`) but (a) use the tactical-safe writer (§1) instead of
    `ReadSavegameBinary(chosenMeta)`, (b) **skip** `PrepareEntryFromBlobCrt` + host `EnterLevel` (host
    already in level), (c) `SendBlob` + `_hostLoaded=true` + `TryReleaseBarrier`. Reuse `SendBlob`
    (`:503`), `OpenBarrier` (`:793`), the LOADED barrier + `Begin` (`:1106-1257`), and the client path
    verbatim.
  - **[NEW]** `HostWriteTacticalSaveCrt(saveManager, ByRef<byte[]> outBytes)` (§1 writer + read-back + delete).
- `Multiplayer\src\Harmony\Tactical\DeployLaunchPatches.cs`
  - `LaunchTacticalGameGatePatch.Prefix` (`:101`): already gates client self-launch — verify alignment
    (no code change expected; the client simply never sets `ClientLaunchInProgress` on the new path).
- Feature flag: a `public static bool UseSaveTransferEntry` on `TacticalDeploySync` (default `false`).

Test additions (`Multiplayer.Tests`, pure predicates only — pattern: `TacticalDeployArrivalGateTests`,
`SaveTransferBarrierTests`/`SaveTransferMath`):
- New pure gate `TacticalEntryTransferGate.ShouldSendTacticalSave(isHost, sessionStarted, isTactical,
  transferActive, flagOn)` → unit test all branches (host-only, tactical-only, not while a transfer is
  in flight, flag-gated). Mirror `InSessionHostLoadGateTests`.
- New pure predicate `HostEntryTransferSkipsSelfEnter()` = true (host already in level) → assert
  `EnterLevel` no-ops when `_pendingResult==null` (already covered by construction; add a Core-level
  test if a `HostAlreadyInLevel` flag is introduced).
- Reuse existing `BarrierReleased` / `RosterProgressTracker` tests unchanged.

Gate checklist (user, in-game, 2-instance / 2-Steam):
- [ ] Flag ON. Host launches a tactical mission in co-op. Client enters the SAME battle.
- [ ] Client actor set == host: soldier count, enemy (pandoran) count, turrets/eggs/loot containers.
- [ ] Client soldier + enemy POSITIONS match host exactly on turn 0 (no "unload at wrong cell").
- [ ] Client objectives panel matches host; NO duplicate "mission start" popup on client.
- [ ] Client is NOT shown a deployment/squad-placement screen (loads a post-deploy save).
- [ ] Live rails still work: move / shoot / end-turn / vision mirror as before.
- [ ] No `matched=0/N` + spawn NRE (the mirror-armed-mid-deploy failure); no ProcessInstanceData
      duplicate-key crash.
- [ ] Flag OFF → old self-launch path runs unchanged (regression guard).

Rollback (Batch 1): set `UseSaveTransferEntry=false` — the host stops sending the tactical save and the
client resumes the legacy self-launch + reconcile path. Zero other code paths touched (old rails intact).

### Batch 2 — host loading-screen barrier + cleanup

**Goal:** host's screen does NOT transition into tactical until every client reports load-complete;
robust timeout so a dead client never hangs the host; then remove the now-dead rails.

Files / anchors:
- `Multiplayer\src\Harmony\Tactical\DeployLaunchPatches.cs`
  - `LaunchTacticalGameGatePatch.Prefix` (`:101`): **[NEW]** on host + active started session + flag,
    call `SaveTransfer.OpenTacticalEntryBarrier()` (arms the reveal-hold BEFORE the host reaches tactical
    `Playing`, so `CurtainShowPatch.Prefix` suppresses the auto-lift). This is the single ordering-critical
    addition — `OpenBarrier` must run at launch, not at deploy-ready.
- `Multiplayer\src\Network\SaveTransferCoordinator.cs`
  - **[NEW]** `OpenTacticalEntryBarrier()` = `OpenBarrier()` split so the barrier opens at launch and
    `HostTacticalEntryTransferCrt` (Batch 1) only does the `SendBlob` half at deploy-ready. Reuse the
    existing reveal machinery (`RevealAll` at `AllDone`, `RevealDeadlineMs` host-forced + per-peer
    self-reveal fallbacks, `:1427-1477`) verbatim — they are already level-agnostic and time-bounded.
  - Verify the host is counted done via the existing `OnReachedPlaying → SendLoadComplete`
    (`CurtainShowPatch.cs:91`) — no new host-done wiring needed.
- `Multiplayer\src\Sync\Tactical\TacticalLoadPhaseSync.cs`
  - Reconcile the two overlays: `HostTick` already self-terminates when `coord.TransferActive`
    (`:90-96`), handing client feedback to the SaveTransfer download bar. Confirm the pre-transfer
    window (host building tactical + writing the save, before chunks flow) is still covered by the
    TacticalLoadPhaseSync ping so the client bar never freezes; then the SaveTransfer download bar owns
    the rest. Expected: no code change, just verification; tighten `Stage1SilenceSeconds` only if a gap
    shows in-game.
- Cleanup (same batch, after the gate passes): delete `ClientLaunchMission` + its helpers
  (`ResolveClientSquad`, `InvokePrepareTacticalGame`, the launch-stall watchdog `:642-648`), and the
  now-no-op position **snap** / deploy-table matching remnants that only served the self-build path.
  Keep the NetId registry + `tac.deploy` (still needed for the live rails).

Test additions:
- Pure gate `TacticalEntryBarrier.ShouldHoldHostReveal(sessionStarted, revealed, allClientsDone)` — unit
  test the hold/release truth table (mirror `SaveTransferMath.HoldCurtain` tests + `RosterProgressTracker.AllDone`).
- Assert the 180 s forced-reveal + per-peer self-reveal still release (reuse existing barrier tests; add a
  case where a client never reports done → host reveals after deadline).

Gate checklist (user, in-game, 3-instance ideal):
- [ ] Host stays on the loading screen ("Waiting for players…") after its own tactical level is built,
      until BOTH clients finish loading; then all three reveal the battle at the same instant.
- [ ] Kill one client mid-load → host reveals after ~60-90 s (the deadline), logs the straggler, never
      hangs forever.
- [ ] No double overlay / no client bar freeze between the pre-transfer ping and the download bar.
- [ ] Cleanup: self-launch code removed, everything above still green in-game.

Rollback (Batch 2): the barrier addition is one call (`OpenTacticalEntryBarrier`) gated behind
`UseSaveTransferEntry`; disabling the flag reverts to Batch-1 behaviour (host reveals on its own). The
cleanup deletions are the only non-flag-reversible part — do them in a **separate commit after** the
barrier gate passes, so a revert of that commit restores the dead rails if a regression surfaces.

### Batch 3 — tactical→geoscape RETURN via geoscape-save transfer (user-approved scope add)

**Goal:** on mission end, the client's post-mission geoscape is the host's transferred geoscape save
(byte-identical loot/storage/gear/injuries/XP), replacing the pile of post-mission delta-convergence
rails. Symmetric to entry but **easier**: it is a GEOSCAPE save, so `AutosaveGame` works (no tactical
NRE), and lobby/F2/on-demand already transfer a geoscape save — pure reuse.

Host wrap-up flow (grounded):
- `TacticalLevelController.GameOver()` (*decompile* `TacticalLevelController.cs:825`) fires
  `GameWrappingUpEvent` → `GameOverEvent`, builds `GetMissionResult()`. Mod already postfixes it:
  `TacticalLevelControllerGameOverPatch.Postfix` (`MissionEndPatches.cs:36`) →
  `TacticalMissionEndSync.HostOnGameOver` (0x95, closes the client's tactical scene).
- Host applies results back on geoscape in `GeoMission.ApplyMissionResults` (*decompile* `GeoMission.cs:551`)
  → `ManageGear` (`:856`: recovered gear→`Reward.Items`, `PostmissionReplenish`, `TryReloadItem`,
  `ManageFreeReloads`, `ManageAutosellItems`). The game then post-mission-autosaves on geoscape
  (*decompile* `GeoLevelController.cs:701/1236/1328`, native `AutosaveGame`).
- Reverse curtain already pinged: `TacticalLevelEndPatch.Postfix → TacticalLoadPhaseSync.HostBeginLoad`
  (`DeployLaunchPatches.cs:152`).

Client today (the pile Batch 3 retires): rides native `IsGameOver` back to geoscape
(`TacticalMissionEndSync.EndClientMission`, `TacticalMissionEndSync.cs:238`) and runs the native
mission-end LOCALLY, with the client's model kept correct by convergence rails:
`PostMissionGearClientSkipPatch` (skips `GeoMission.ManageGear`, `PostMissionGearClientSkipPatch.cs:51`)
+ `InventoryChannel.PollHostDrift` (#1 storage) + #9 personnel blob (soldier items/charges/injuries/XP)
+ wallet rail (reward resources) + 0x69 outcome modal / `RewardRenderPatch` (display).

Files / anchors:
- **[NEW] host trigger**: when the host reaches its post-mission geoscape frame after a co-op tactical
  mission, autosave + transfer. Lazy reuse: fire from the host's geoscape `Playing` seam
  (`CurtainShowPatch.cs:83` already calls `OnNewCampaignPlayableFrame` there — add a sibling
  `OnPostMissionGeoscapeFrame`) OR piggyback the native post-mission autosave completion, then
  `SaveTransferCoordinator.LaunchTransfer(meta)` — the SAME chunked transfer + LOADED/BEGIN + reveal
  barrier as F2 (`SaveTransferCoordinator.cs:433`, `HostStartSessionInGame:388`). Gate: host + active
  started session + just returned from a co-op tactical mission + no transfer in flight. Feature flag
  `UsePostMissionSaveTransfer` (default OFF).
- **Dead-once-green** (keep bypassed, delete in cleanup): `PostMissionGearClientSkipPatch` (whole file —
  the client no longer runs a local ManageGear; it loads the host's post-wrap geoscape), the #1
  post-mission storage-drift poll for this window, the #9 post-mission personnel blob, and the
  post-mission wallet reconcile. **KEEP:** 0x69 outcome modal + `RewardRenderPatch` (display, not state)
  and 0x95 `tac.missionend` (still closes the client's tactical scene in sync before the geoscape save
  arrives).

Known ceiling (`ponytail:`): the client rides `IsGameOver` back to a THROWAWAY native geoscape, then the
transfer's `FinishLevel` reloads the host's exact one — a double geoscape build. Acceptable first cut
(same full teardown+rebuild the F2 apply already does). Upgrade path (Batch 3b, optional): hold the
client in the tactical scene at 0x95 until the geoscape save arrives and load it directly, skipping the
throwaway.

Tests: pure gate `PostMissionTransferGate.ShouldTransfer(isHost, sessionStarted, returnedFromCoopMission,
transferActive, flagOn)` (mirror `InSessionHostLoadGateTests`). Reuse barrier/tracker tests unchanged.

Gate checklist (user, in-game): host finishes a co-op mission → client's post-mission geoscape matches
host exactly (storage counts incl. ammo, recovered gear, soldier injuries/XP/charges, wallet, roster);
NO ammo host-2/client-1 drift; outcome modal shows once (0x69); flag OFF → current rails path unchanged.

Rollback: `UsePostMissionSaveTransfer=false` reverts to the convergence-rails path; the dead-rail
deletions go in a separate post-gate commit (revert restores them). Batches 1-2 are independent and remain
the priority; Batch 3 ships after entry gates pass.

---

## 4. Top risks + mitigations

1. **Mid-tactical load at ENTRY is the least-proven path (highest risk).** Mission-start
   triggers/objectives could double-fire, deployment UI could reappear, or turn-0 scripts that run on the
   host *after* the save point could diverge on the client.
   - Mitigation: write the save at the **same** readiness point the deploy capture uses
     (`HasAnyTurnStarted` / turn-0 ready, `TacticalDeployReadinessGate`), so mission-start scripts have
     already run and are captured in the save; rely on the existing objective seed (`0x99`
     `TacticalObjectiveSync.HostSeedAfterDeploy`, `TacticalDeploySync.cs:488`) + `tac.turn` to converge
     post-load. Verify in-game per the Batch-1 checklist (objectives match, no duplicate popups, no deploy screen).
2. **Same-PC 2-instance shared-file sharing violation** (bit us in commit a2194ca — client autosave
   collided on shared `autosave.zsav`).
   - Mitigation: only the **host** writes to disk, under a **dedicated unique name** (`EnsureUnique(
     "coop_tac_xfer")`, never "autosave"/"quicksave"), deleted after read-back; the **client writes
     nothing** — it loads from in-memory `byte[]` via `PrepareEntryFromBlobCrt` (no temp file). Client
     autosave stays skipped (`ClientAutosaveSkipPatch`). Satisfies the "no shared-file writes on client"
     requirement.
3. **Host reveal-hold ordering** — if `OpenBarrier` runs at deploy-ready (after the host already reached
   tactical `Playing`), the host reveals the level before the barrier arms (curtain already lifted because
   `_revealed` was still true from the last barrier).
   - Mitigation: `OpenTacticalEntryBarrier()` fires at **`LaunchTacticalGame`** (Batch 2), before the
     tactical `Loading/Playing` transitions, so `_revealed=false` is set first and
     `CurtainShowPatch.Prefix`/`CurtainLiftGatePatch` hold the host. Split open-barrier (launch) from
     send-blob (deploy-ready).

Secondary: host loads its tactical level only once (it does NOT re-enter from the blob) — a divergence
between the host's live level and the bytes it wrote is impossible by construction (the client gets the
host's exact serialized state; the host keeps the live one it serialized). Live in-mission rails
(move/combat/equipment) are untouched.

## 5. Brief items that turned out different against real code

- The brief calls today's entry "client builds own level natively + reconcile + snap." True, but the
  **transferred-tactical-save load path already exists** (F2 mid-tactical reload → HydrateExisting).
  This arc reuses it, not builds it — smaller than framed.
- The brief's step-1 warns about the manual `Serializer` round-trip empty-graph trap. We **avoid it
  entirely** by using native `SaveGame`/`ReadSavegameBinary` (the configured serializer), same as the
  proven on-demand-join capture — no manual round-trip.
- The brief's "write a REAL mid-tactical save" via a native higher-level API: the obvious candidate
  `AutosaveGame` is **NRE-unsafe mid-tactical** (`PhoenixSaveManager.cs:421-422`). Use the tactical-safe
  `SaveGame` funnel (as `QuickSave`/`IronmanSave`/`SaveWithName` do), not `AutosaveGame`.
- The host barrier is **largely already implemented** (the reveal barrier is level-agnostic and
  time-bounded); Batch 2 is mostly one ordering-correct `OpenBarrier` call + verification, not new machinery.
