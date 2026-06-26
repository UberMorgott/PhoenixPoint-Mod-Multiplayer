# Geoscape EVENT-WINDOW mirror — system map (research, 2026-06-26)

Grounding for redesigning host→client geoscape EVENT-window mirroring (Multipleer co-op).
Read-only map; cites `Class.Method file:line`. Decompile root `decompiled\AssemblyCSharp`;
mod src `Multipleer\src`. NOTE: two distinct mirror RAILS exist — (1) the **GeoscapeEvent
dialog** rail (POI/encounter popups, packets 0x65/0x66 + EventAdvanceResult — SHIPPED, partly
gated) and (2) the newer **report-window** rail (mission/research/base outcome modals, packet
0x69 — Phase-A, fully gated OFF). The "тяп-ляп client jumps to final window" bug lives on rail (1).

---

## A. NATIVE PP geoscape event/encounter system

**A1. Entry / raise.**
- Single funnel for ALL geoscape event raises: `GeoscapeEventSystem.OnGeoscapeEvent(BaseEventData,BaseEventContext)`
  (private, GeoscapeEventSystem.cs:610) — both the eventus-handler path (RegisterHandler :546) and the
  direct `TriggerGeoscapeEvent` path (:328) pass through it.
- Build + (maybe) auto-complete + raise: `GeoscapeEventSystem.OnEventTriggered(GeoscapeEventData,GeoscapeEventContext)`
  (GeoscapeEventSystem.cs:638). Builds `new GeoscapeEvent(data,ctx)` (:641), then **:651 `if (@event.HasSingleChoice && !IsEventTheMarketplace)` → :655 `geoscapeEvent.CompleteEvent(Choices.FirstOrDefault(), ViewerFaction)`** — single-choice events are GRANTED at trigger — and **only then :657 `GeoscapeEventRaised?.Invoke(geoscapeEvent)`** pops the modal. (This ordering = the wire-order bug, §B7.)
- Modal raise hook the host patches: `GeoscapeView.OnGeoscapeEventRaised(GeoscapeEvent)` (GeoscapeView.cs:2109) → pushes `UIStateGeoscapeEvent` (:2131).
- Event identity: `GeoscapeEvent.EventID` (public string field, GeoscapeEvent.cs:18), `.EventData` (GeoscapeEventData), `.Context` (GeoscapeEventContext → Site/Vehicle/Faction), `.ChoiceReward` (GeoFactionReward), `.SelectedChoice`, `.IsCompleted` (:30-36). No native per-occurrence id (the mod synthesizes one, §B5).

**A2. Stages of the event window UI (`UIModuleSiteEncounters`).**
- `ShowEncounter` (:194→:247) is the render entry; `_pagingEvent = Description.Count > 1` (:238).
- **Stage 0 prompt:** `SetEncounter(GeoscapeEvent,bool,string)` (:265) draws flavour + choice buttons.
- Single-choice auto-path: `IsSingleChoiceEncounter()` (:258) true → `SetSingleChoiceEncounter()` (:249) auto-runs `SelectChoice` + `SetClosingEncounter`. **Crucially `IsSingleChoiceEncounter()` returns FALSE when the lone choice has non-empty Outcome text** → such an event stays on the window-1 PROMPT (`SetEncounter` :245) even though already completed at trigger.
- Choice click handler: `OnChoiceSelected(GeoEventChoice)` (:548); pages while `_pagingEvent` (:550), else → `SelectChoice`.
- `SelectChoice(GeoscapeEvent,GeoEventChoice)` (:600) runs `CompleteEvent` (:604) **only if not already completed** (single-choice auto-completed at trigger runs NO CompleteEvent here, :600-603) then derefs `ChoiceReward.ApplyResult.StartMission` (:606).
- **Stage 1 result/outcome:** `SetClosingEncounter(GeoscapeEvent,GeoEventChoice,bool)` (:324) renders the window-2 result + reward lines. Reaches `ChoiceReward.ApplyResult` (:359).
- Local close: `FinishEncounter()` (:620) → `GeoscapeView.FinishQueriedState()`. Esc/back: `UIStateGeoscapeEvent.OnCancel()` (:68).

**A3. WHERE REWARDS ARE GRANTED (double-grant risk site).**
- THE site: `GeoscapeEvent.CompleteEvent(GeoEventChoice,GeoFaction)` (GeoscapeEvent.cs:86), at **:101 `ChoiceReward = choice.Outcome.GenerateFactionReward(faction,Context,EventID)`** then **:102 `ChoiceReward.Apply(faction, Context.Site, Context.Vehicle)`** — the wallet/inventory/research/diplomacy mutation. (Marketplace variant: `CompleteMarketplaceEvent` :74→:79.)
- Granting is **MODEL-level, NOT coupled to the UI advance**: it runs inside `CompleteEvent` (called at trigger :655 for single-choice, or from `SelectChoice` :604 / a host click for multi-choice). The window stages (`SetEncounter`/`SetClosingEncounter`) only DISPLAY; the close (`FinishEncounter`) does not grant. So a faithful display mirror that never calls `CompleteEvent` cannot double-grant.

---

## B. EXISTING Multipleer event-mirror implementation

**B4. Gates (both shipped `false`).**
- `EventMirrorFixGate.Enabled` (Network\Sync\EventMirrorFixGate.cs:22) — gates the single-choice
  stage-lockstep fix: (a) `SingleChoiceAdvancePatch` emits the prompt→result advance only when ON
  (SingleChoiceAdvancePatch.cs:57); (b) `EventCorrelator.Raised` mirrors the prompt vs jumps to the
  result page (SyncEngine.cs:358 `mirrorSingleChoice = Enabled && singleChoice`); (c) the synthetic
  result page is pushed WITH its occId so a different occurrence's dismiss can't evict it
  (EventDisplay.cs:150); (d) `DropBufferedReward` is skipped so a reward isn't thrown out from under a
  page the client will show (SyncEngine.cs:377). OFF = byte-for-byte legacy.
- `ReportMirrorGate.Enabled` (Network\Sync\ReportMirrorGate.cs:14) — gates the SEPARATE report-window
  rail (§B/rail-2): host Postfix broadcasts nothing on 0x69 and client Prefix suppresses nothing.

**B5. Event-mirror machinery (rail 1, GeoscapeEvent dialog).**
- Host broadcasts (all host-only + `!SyncApplyScope.IsApplying`):
  - `EventRaisedDisplayPatch.Postfix` on `GeoscapeView.OnGeoscapeEventRaised` (EventDisplayPatches.cs:72) → `SyncEngine.BroadcastEventRaised(occId,eventId,siteId,vehicleId,identity,singleChoice)` (SyncEngine.cs:546, PacketType.EventRaised 0x65). Carries `singleChoice = choiceCount∈{0,1}` (EventDisplayPatches.cs:110-115) + an absent-site `GeoSiteState` identity.
  - `CompleteEventDismissPatch.Postfix` on `GeoscapeEvent.CompleteEvent` (EventDisplayPatches.cs:144) → `BroadcastEventDismiss(occId,eventId,choiceIndex,rewardBlob,siteId)` (SyncEngine.cs:562, 0x66). `rewardBlob` = `RewardDisplaySnapshot.Encode(RewardDisplayReflection.BuildFromReward(reward))` — text-only delta lines, NO re-apply.
  - `SingleChoiceAdvancePatch.Postfix` on `UIModuleSiteEncounters.SetClosingEncounter` (SingleChoiceAdvancePatch.cs:52, gated ON) → `BroadcastEventAdvanceResult(occId,...)` (SyncEngine.cs:578, PacketType.EventAdvanceResult) — the single-choice prompt→result advance (no native CompleteEvent fires on that click, so this is the only advance signal).
  - `FinishEncounterHostDismissPatch.Postfix` (EventDialogClientLockPatches.cs:403) — fallback bare close-only dismiss, skipped when `EventOccurrenceIds.WasDismissed` (no double-dismiss).
- Per-occurrence id: `EventOccurrenceIds.GetOrAssign(geoEvent)` — order-independent; raise & dismiss of the same live instance share it (EventDisplayPatches.cs:91, :175).
- Packet dispatch: `NetworkEngine.cs` switch → `Sync?.OnEventRaised/OnEventDismiss/OnReportModalShow` (cited NetworkEngine.cs:547-555 in plan).
- Client receive → correlate → display:
  - `SyncEngine.OnEventRaised` (SyncEngine.cs:348) → `EventCorrelator.Raised(occId,eventId,mirrorSingleChoice)` → switch: `ShowDialog` builds + shows the prompt (`EventReflection.BuildEvent` + `EventDisplay.Show`, :391-393); `ShowResultPage` jumps to the result page (`ResolveToResultPage`, :399); `DropNoop` (:401).
  - `SyncEngine.OnEventDismiss` (:446) → `EventCorrelator.Dismissed` → `ShowResultInPlace`→`ResolveToResultPage` (:468) / `CloseDialog`→`EventDisplay.Dismiss` (:471) / `BufferDismiss`→stash reward (:475).
  - `SyncEngine.OnEventAdvanceResult` (:492) → `EventCorrelator.Advanced` → `ShowResultPage`→`ResolveToResultPage` (:508).
  - `ResolveToResultPage` (:520) → `EventReflection.BuildResultEvent` (synthetic, EventID="") → arm `RewardDisplayReflection.SetPending` → `EventDisplay.ShowResult`.
- `EventCorrelator` (Network\Sync\State\EventCorrelator.cs) — PURE occId-keyed state machine: `_open`, `_pending` (out-of-order dismiss), `_promptMirror` (single-choice awaiting advance), `_pendingAdvance`. `Raised` :129, `Advanced` :177, `Dismissed` :194.
- `EventDisplay` (Network\Sync\State\EventDisplay.cs) — reflection bridge: `Show` :96 (push `UIStateGeoscapeEvent` via `GeoscapeViewSwitchQuery.QueryStateSwitch`, PauseGame=false), `ShowResult` :129 (Dismiss+Show result page), `Dismiss` :164 (occId-guarded `FinishQueriedState`). Tracks `_openOccurrenceId`.
- Client LOCAL suppression (so the client only ever shows host events): `ClientEventRaiseChokepointPatch.Prefix` on `GeoscapeEventSystem.OnGeoscapeEvent` (EventRaiseChokepointPatch.cs:74, returns false on client) + `EventRaisedDisplayPatch.Prefix` (EventDisplayPatches.cs:58) + `EventSuppressClientGeoscapePatch` (`SuppressEvents=true`).

**Rail 2 (report-window mirror, Phase-A, gated `ReportMirrorGate`):**
- Host Postfix + client Prefix on `GeoscapeView.OpenModalPersistent(ModalType,object,int)` (GeoscapeView.cs:849) and `OpenModal(...)` (:868) — `ReportModalMirrorPatches`. Host → `ReportModalClassifier.TryBuild` → `SyncEngine.BroadcastReportModal` (SyncEngine.cs:592, PacketType.ReportModalShow 0x69).
- Client `SyncEngine.OnReportModalShow` (:605) → reconstruct `modalData` by `ReportModalVariant` (NullData/SiteOnly/Research/Diplomacy via `ReportModalReflection`; **MissionOutcome NOT wired — returns at default :630**) → `GeoModalDisplay.Show` under `SyncApplyScope.Enter()` (:637).

**B6. Plan doc** (`docs\superpowers\plans\2026-06-26-multipleer-event-window-mirror.md`):
mirror the proven EventRaised chokepoint shape for the REPORT/outcome modals — host Postfix on
`OpenModalPersistent`/`OpenModal` broadcasts a `ReportModalShow` (0x69) packet; client reconstructs
modalData by id and replays the native modal under `SyncApplyScope` (read-only, host-authoritative);
default-OFF `ReportMirrorGate`. Phase-A = NullData/SiteOnly/Research/Diplomacy variants; Phase-B =
MissionOutcome (siteId+missionDefId+rewardDisplayBlob). Open Q1 mission-reconstruct depth, Q2
research-modal close-callback safety (`ResearchCompleteModalHandler` can switch state on a client),
Q3 converge onto the unified `SyncEnvelope` (0x67) rail later.

**B7. ROOT CAUSE of "client shows final window as if OK already clicked / skips stages".**
Specific to **single-choice-with-outcome** POI/encounter events. Mechanism (chain):
1. Host trigger order: `OnEventTriggered` runs `CompleteEvent` (grant) at **GeoscapeEventSystem.cs:655 BEFORE** the raise at **:657**.
2. So on the host the result-bearing `EventDismiss` (occId, choiceIndex≥0, rewardBlob) is broadcast (via `CompleteEventDismissPatch`) **before** the `EventRaised`. The host UI meanwhile sits on the window-1 PROMPT (`IsSingleChoiceEncounter()` false because the lone choice has outcome text → `SetEncounter` :245).
3. Client receives dismiss first → `EventCorrelator.Dismissed` → no open dialog → `BufferDismiss` (reward stashed). Then the raise lands → `EventCorrelator.Raised(occId,eventId, singleChoice=**false because gate OFF**)`. With a buffered dismiss whose `ChoiceIndex≥0` and `singleChoice=false`, `Raised` returns **`ShowResultPage` unconditionally** (EventCorrelator.cs:153-156) → `SyncEngine.OnEventRaised` calls `ResolveToResultPage` (SyncEngine.cs:399) → client jumps STRAIGHT to the window-2 result page. **= the bug** (host on prompt, client on result).
4. The FIX already exists but is **gated OFF**: with `EventMirrorFixGate.Enabled=true`, `mirrorSingleChoice=true` → `Raised` returns `ShowDialog` and tracks `_promptMirror` (EventCorrelator.cs:137-150) → client mirrors the PROMPT and waits; the host click then fires `SingleChoiceAdvancePatch` → `EventAdvanceResult` → `Advanced` → `ShowResultPage` (lockstep). Multi-choice events were never broken (in-order raise → `ShowDialog` prompt, dismiss → `ShowResultInPlace`).
- Contributing factor it is NOT: the client does NOT re-raise its own event here (the chokepoint blocks that) and `SingleChoiceAdvancePatch` does NOT fire on the client (host-only). The skip is purely the off-gate `ShowResultPage` legacy branch.

**B8. Double-grant — PROTECTED (layered host-only design).**
Reward grant only ever runs at native `CompleteEvent` :101-102, and the client never reaches it:
- `ClientEventRaiseChokepointPatch` blocks the client's local raise (no local event → no CompleteEvent).
- `EncounterSelectChoiceClientPatch.Prefix` short-circuits `SelectChoice`→false (EventDialogClientLockPatches.cs:324).
- `EncounterChoiceClientPatch.Prefix` swallows a client multi-choice click (return false, no native body) and relays an `AnswerEventAction` instead (EventDialogClientLockPatches.cs:212-229).
- `CompleteEventPatch.Prefix` non-host branch returns false — blocks any stray client CompleteEvent (EventSyncPatches.cs:103).
- Client-displayed pages are SYNTHETIC (`EventReflection.BuildResultEvent`, EventID="") — display-only; reward LINES are text via `RewardDisplayReflection` (no apply).
- The actual reward STATE reconverges on the client through channels (wallet echo / InventoryChannel / ResearchChannel / DiplomacyChannel).
- Host applies exactly once: `AnswerEventAction` is `IHostOnlyApply` + `IResolvesOutsideScope` → host runs `EventReflection.CompleteEventByOccurrence` on the LIVE instance; `CompleteEventPatch.Prefix` first-click-wins `Arbiter.Claim(occId)` (EventSyncPatches.cs:90) guarantees one roll/dismiss even if host-click and client-answer race.
- **KNOWN GAP** (AnswerEventAction.cs:71-72 TODO): non-channelled outcomes — site reveal / mission spawn / faction-diplomacy flag / direct research unlock — are NOT yet synced to the client; the window mirrors but that underlying state can diverge.

---

## C. Verdict — SALVAGEABLE (flip + validate + converge), not a REDO

The rail-1 GeoscapeEvent-dialog mirror is already the clean host-authoritative shape and the
stage-skip is a *disabled* fix, not a missing one. Recommended shape (= what the code already
implements when armed): **host broadcasts per-stage display-state keyed by occId — raise→PROMPT
(0x65), single-choice prompt→result advance (EventAdvanceResult), multi-choice answer→result
dismiss (0x66) — and the client opens the native window READ-ONLY at stage N (synthetic EventID=""
pages, locked choice buttons, blocked Esc) and never grants; a client OK/choice routes as an
`AnswerEventAction` intent to the host, whose native `CompleteEvent` advances + grants + broadcasts
the next stage.** Double-grant is already structurally prevented. So the work is: (1) flip
`EventMirrorFixGate` ON and validate single-choice lockstep in-game; (2) close the non-channelled-
outcome gap; (3) finish/validate the parallel rail-2 report-window mirror (Phase-B MissionOutcome)
and decide whether to converge both rails onto the unified `SyncEnvelope` (0x67) rail.

**Native seams (already used; keep):**
- Suppress on client: `GeoscapeEventSystem.OnGeoscapeEvent` (raise funnel), `GeoscapeEvent.CompleteEvent` (grant), `UIModuleSiteEncounters.SelectChoice` / `IsSingleChoiceEncounter` / `OnChoiceSelected`, `UIStateGeoscapeEvent.OnCancel`.
- Drive the client window: `GeoscapeViewSwitchQuery.QueryStateSwitch` + `UIStateGeoscapeEvent(GeoscapeEvent)` ctor (via `EventDisplay`); `GeoscapeView.FinishQueriedState` to close.
- Host broadcast taps: `GeoscapeView.OnGeoscapeEventRaised` (raise), `GeoscapeEvent.CompleteEvent` (dismiss+reward blob), `UIModuleSiteEncounters.SetClosingEncounter` (single-choice advance).
- Report rail seams: `GeoscapeView.OpenModalPersistent` / `OpenModal` (host Postfix + client Prefix), `GeoModalDisplay.Show` under `SyncApplyScope`.

---

## D. POST-FLIP in-game RCA (EventMirrorFixGate ON — stage lockstep CONFIRMED working)

Single-choice event "МЕСТНОСТЬ РАЗВЕДКИ"/"Добро пожаловать в ад": host shows narrative+reward
lines (МАТЕРИАЛЫ +92 / ПРОВИАНТ +92 / выносливость 10)+OK in ONE window; client shows same
narrative+OK but NO reward lines; client top-bar = host top-bar +92 on both resources.

**D1. Reward-text gap — root = 1-window vs forced-2-window mismatch (NOT a missing blob).**
- The reward DOES ride the wire: trigger-time 0x66 `EventDismiss` carries `rewardBlob` =
  `RewardDisplayReflection.BuildFromReward(ChoiceReward)` (EventDisplayPatches.cs:159-183); client
  stashes it (`StashBufferedReward`, SyncEngine.cs:475) and reuses it at advance
  (`OnEventAdvanceResult` → `TakeBufferedReward` → `ResolveToResultPage` → `SetPending` →
  `RewardRenderPatch.Render`, SyncEngine.cs:508 / RewardRenderPatch.cs:59-63). The ADVANCE packet
  carries NO reward blob by design (SyncEngine.cs:582). `Render` only fires on the synthetic RESULT
  page — never on a prompt.
- THIS event is the **1-window** kind: `IsSingleChoiceEncounter()` returns TRUE iff the lone choice's
  `Outcome.OutcomeText` key is EMPTY (UIModuleSiteEncounters.cs:256-262) → host shows
  `SetSingleChoiceEncounter` (:249) → `SetClosingEncounter` (:253) directly = ONE window WITH reward.
- But the client patch `EncounterSingleChoiceClientPatch` FORCES `IsSingleChoiceEncounter()`=false
  (EventDialogClientLockPatches.cs:87-93, to dodge the null-`ChoiceReward` NRE), so the client takes
  the `SetEncounter` PROMPT path (:245) — a reward-LESS window-1 the host never showed. The client
  only reaches the reward via the deferred `SetClosingEncounter`-driven advance. The screenshot is the
  client sitting on that phantom prompt (window-1 has no reward by design) while the host is on its
  single combined window.
- **Minimal fix:** stamp the host's `IsSingleChoiceEncounter()` ("direct single window") bit onto the
  `EventRaised` wire (next to the existing `singleChoice` flag, EventDisplayPatches.cs:110-115). When
  true, the client SKIPS the prompt-mirror and resolves STRAIGHT to the result page (reuse the stashed
  reward) — i.e., `EventCorrelator.Raised` returns `ShowResultPage` for the direct-window variant —
  matching the host's one window. Keep the prompt-mirror+advance lockstep ONLY for the genuine
  2-window (non-empty outcome-text) case. (Disambiguating logs already emitted: "CLIENT
  ResolveToResultPage … rewardEmpty=", "RewardDisplayRender drew N", "reward-type resolve … drops",
  "HOST BroadcastEventDismiss … rewardBytes=N" — if N>0 and the client never logs ResolveToResultPage,
  the client is stuck on the phantom prompt = this RCA.)

**D2. Count discrepancy = host BAR stale, NOT double-grant (user's #1 fear is unfounded).**
- Grant happens ONCE on host: `GeoscapeEvent.CompleteEvent` :101-102 at trigger
  (GeoscapeEventSystem.cs:655). Client NEVER grants (no `CompleteEvent`; §B8).
- Client wallet is set by `WalletApplier.Apply` = CONVERGENT diff to the host's ABSOLUTE snapshot
  (`diff = target − cur`, WalletApplier.cs:30-41) — idempotent, CANNOT double/overshoot. So client
  bar ≡ host's authoritative model value = base+92. The client also EXPLICITLY repaints via
  `GeoUiRefresh.RefreshPersistentBars` after the wallet apply (SyncEngine.cs:259).
- The host top bar (`UIModuleInfoBar`) repaints only off `_context.View.FactionResourcesChanged` →
  `OnResourcesChanged` (UIModuleInfoBar.cs:148/361) and lags while the event modal is open; the host
  has NO equivalent re-drive (`RefreshPersistentBars` is called ONLY on the client's `OnWalletSync`,
  SyncEngine.cs:259-260 — host path has none). So the host MODEL is base+92 but the host BAR shows
  the stale pre-grant base; the client correctly shows base+92. Authoritative-correct side = CLIENT.
- **Minimal fix (cosmetic):** give the host the same kick — call
  `GeoUiRefresh.RefreshPersistentBars(GeoRuntime.Instance)` on the host in the `MarkWalletDirty`
  callback (SyncEngine.cs:246) or after the host event completes, so the host bar repaints its own
  grant without waiting for modal-close/next native repaint. No model change (both sides already agree).

---

## KEY DESIGN DECISIONS TO RESOLVE

1. **Replay-event vs stream-display-state.** The shipped design STREAMS display-state (raise/advance/dismiss + synthetic pages), it does NOT replay the native event end-to-end on the client. Confirm this stays the model (vs the "freeze client sim" Inc4 endgame where the client follows mirrored host state with no local openers to suppress). The two rails (event-dialog vs report-window) should agree.
2. **Single-choice auto-advance handling = flip `EventMirrorFixGate` ON?** Validate the `SingleChoiceAdvancePatch`/`_promptMirror`/`EventAdvanceResult` lockstep in-game (host on prompt → client on prompt → host clicks → both on result), then flip on. This is the direct fix for the reported bug.
3. **How a client choice routes.** Keep `AnswerEventAction` (intent → host `CompleteEvent`, first-click-wins arbiter, `IHostOnlyApply`)? Confirm permission policy (currently NONE — last-write-wins, PermissionGate dormant).
4. **Which native methods to suppress on client — keep the 4-layer belt** (`OnGeoscapeEvent` chokepoint + `OnGeoscapeEventRaised` prefix + `SelectChoice`/`OnChoiceSelected`/`IsSingleChoiceEncounter` client patches + `SuppressEvents`)? Or collapse once the client sim is frozen (Inc4) and these arms retire one-by-one.
5. **Close the non-channelled-outcome gap** (site reveal / mission spawn / diplomacy flag / direct research unlock — AnswerEventAction TODO). Decide: extend channels, or accept display-only mirror for v1.
6. **Rail convergence + report-window Phase-B.** Finish MissionOutcome (siteId+missionDefId+rewardDisplayBlob, plan Q1), resolve the Research-modal client-close callback safety (plan Q2 — `ResearchCompleteModalHandler` can switch state on a client), and decide dedicated 0x69 vs unified `SyncEnvelope` 0x67 rail (plan Q3).
