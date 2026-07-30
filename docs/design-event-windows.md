# Event-window engine — v2 design

> **SUPERSEDED 2026-07-30 — kept as the record of why the record-derived model was tried and how it
> failed. Do NOT implement from it.** The "queued history derived from the mirrored records" premise
> below is structurally impossible: `GeoscapeEventRecord` persists only
> id/timestamps/state/`_selectedChoice`/`_triggerCount`, so it can never rebuild the
> `GeoscapeEventContext` that every `[HavenName]`/`[AircraftName]` replacer dereferences unguarded —
> measured 54 of 94 replayed raises had `site=null` and rendered raw tokens over baked dev
> placeholder text, and the persisted per-peer cursor replayed each joiner's whole campaign history
> (97 windows). Replaced by the v1 model expressed on the v2 rail: the HOST ships a **presentation
> payload** per raise (surface `GeoEventRaise` 0xB6 — eventId + site/vehicle root refs + host-resolved
> title/narrative) captured at `GeoscapeView.OnGeoscapeEventRaised`, the client rebuilds a REAL
> context from it and pushes the native `UIStateGeoscapeEvent`, and there is **no history and no
> backlog**: a peer that was not in the session when an event fired never sees that window. Live
> source of truth = `src/Rail/EventPopup.cs` + RailCheck **L39**.

> Read-only design pass, 2026-07-29, Multiplayer2 @ ~`fc8bc04`. Every `file:line` verified
> unless marked `UNVERIFIED`. Grounding: v2 `src/`, decompile
> `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src\`, v1 quarry
> `E:\DEV\PhoenixPoint\Multiplayer\src` (behaviour only).
>
> Deployment status: nothing since `fc8bc04` deployed yet — deployed DLL is 477184 B
> @ 2026-07-29 13:12:13. Entire chain awaits one in-game retest once geoscape + event windows land.

Scope: geoscape event windows as a **queued history** with **first-choice-wins**, under the v2 rail.

---

## 0. What is actually broken today (the silence inventory)

The v2 mirror already exists (`src/Rail/EventPopup.cs`) and the state already rides. Three silent swallows kill it:

- **Silent re-seed.** `GenericApplier.cs:51` calls `EventPopup.Reset()` on every reload / save-transfer boundary
  (`_seeded=false`, no log). The next `Sync` copies every record into the latch and `return;`s with **no log line**
  (`EventPopup.cs:63-69`). A JOINING client's first apply is exactly that path → its entire backlog is consumed
  invisibly. This is the reported "host popup does not show on the client".
- **Single-choice windows can never raise, by construction.** The host auto-completes them at trigger
  (`GeoscapeEventSystem.cs:651-656`, `HasSingleChoice && !IsEventTheMarketplace` → `CompleteEvent` BEFORE
  `GeoscapeEventRaised` at `:657`), so the record reaches the client already `Completed`. `EventPopup.Sync`'s raise
  test requires `rec.State == Triggered` (`EventPopup.cs:74-76`) → never true; and the `<2 choices` gate
  (`EventPopup.cs:93-94`) would drop it anyway. **This is the whole quest/narrative/reward-popup class**, incl. the
  event-granted-soldier window (`GeoEventChoiceOutcome:296/305`). No log fires for it at all — `Raise` is never called.
- **No client-local raise gate.** `ClientSimGate.cs` has 5 seams; none touches `GeoscapeEventSystem`. `SupressEvents`
  is a *rail-mirrored leaf* (`docs/rail-baseline.txt:254`) so it carries the HOST's `false` — it cannot serve as a
  client-local switch. `GeoscapeEventSystem.Update()` (`:550` → `CompleteTimer:568`) and the arrival/visit handlers
  (`:412`, `:421`) run on a client whose sim is live, so the client can mint its OWN record for a different random
  encounter (v1's in-game RCA, `EventRaiseChokepointPatch.cs:19-25`).
- **L21 is blind to the worst one.** `UIStateGeoscapeEvent.ExitState` locally completes a still-`Triggered` event
  (`UIStateGeoscapeEvent.cs:61-65` → `CompleteEvent(Choices.Last(), ViewerFaction)`), i.e. a client that gets its
  event dialog re-entered by the universal repaint (`OpenUiRepaint.cs:189-206` fallback Exit+Enter) **applies the
  reward locally and resolves the record**. L21 only reports **void** instance commands
  (`tools/RailCheck/Program.cs:1409-1414`, `:1441`); `CompleteEvent` returns `GeoFactionReward` → law stays green forever.

---

## 1. v1 behaviour harvested

KEEP (behaviour, not code):

- **Client-local raise chokepoint at the ONE funnel** — `GeoscapeEventSystem.OnGeoscapeEvent(BaseEventData, BaseEventContext)`
  (private; v2 decompile `GeoscapeEventSystem.cs:606`). v1: `Multiplayer\src\Harmony\Sync\EventRaiseChokepointPatch.cs:11-25`.
  Verified it really is the single funnel: the eventus handler path and the direct `TriggerGeoscapeEvent` both land there
  (`GeoscapeEventSystem.cs:319-329` calls `OnGeoscapeEvent` at `:328`).
- **Reuse the native switch-query as the queue** — v1 `EventDisplay.cs:9-30` (`GeoscapeViewSwitchQuery.QueryStateSwitch`,
  `FinishQueriedState`, `PauseGame=false` because pause is host-authoritative). v2 already does this
  (`EventPopup.cs:99-102`). Keep.
- **Show the OUTCOME as a synthetic single-choice window with `EventID == ""`** — v1 `EventDisplay.ShowResult`
  (`EventDisplay.cs:163-172`). It matches what the game itself does: `UIModuleSiteEncounters.SetClosingEncounter`
  builds `new GeoscapeEvent(new GeoscapeEventData { EventID = string.Empty, … })` (`:326-331`). Empty EventID is also
  what v2's own locks treat as "not a mirrored host event" (`EventPopup.cs:159`, `:177`) → locally dismissible. Keep.
- **"Replay mode" instead of a forced close** — v1 `EventReplayReflection.cs:9-23`: re-render the SAME native dialog
  with the winning choice highlighted and every other button greyed/non-interactable. Verified handles still exist:
  `UIModuleSiteEncounters.ChoicesButtonController` (public field, `UIModuleSiteEncounters.cs:45`),
  `SiteBaseChoicesController.Choices` (protected `List<SiteBaseChoiceButton>`, `SiteBaseChoicesController.cs:23`,
  index i ↔ `EventData.Choices[i]` per `SetChoices:25-45`), `SiteBaseChoiceButton.Button`
  (`SiteBaseChoiceButton.cs:19`), `PhoenixGeneralButton.SetInteractable(bool)` (`:283`), `IsSelected` (`:35`),
  `IsNonInteractableWhenSelected` (`:37`, default true → must be forced false to keep the winner clickable),
  `ResetButtonAnimations()` (`:166`). This is the "see the outcome, cannot re-pick" presentation, native-UI-first.
- **First-choice-wins arbitrated at the native `CompleteEvent` funnel** — v1 `SyncEngine.cs:122-131` + `:397-401`
  (`ChoiceArbiter.Claim(occId)` from `CompleteEventPatch.Prefix`), because a host-local click AND a relayed client
  answer both converge there. The funnel choice is right; the key is wrong (see AVOID).

AVOID (v1's buggy parts):

- **Occurrence ids / display sequencer / correlator** — `EventOccurrenceIds`, `displaySeq`, `EventCorrelator`,
  `PendingHostAdvance`, reward stashes (`SyncEngine.cs:854-958`, `:1067-1081`, `:1587-1662`). Whole machinery exists
  only because v1 shipped *raise events* instead of *state*. v2 has the record; `EventId` is the key.
- **A third message kind** — `PacketType.EventRaised` / `EventDismiss` / `AnswerEventAction` (v1
  `SyncEngine.BroadcastEventRaised:1721`). v2 law 1: Intent + Delta only.
- **Text over the wire** — `wireTitle`/`wireNarrative` in the raise payload (`SyncEngine.cs:881`, `:1721`). v2 has
  ParityManifest; both peers hold the same defs and render the text themselves.
- **Belt-and-suspenders suppression stack** — `SuppressEvents=true` + a prefix on `GeoscapeView.OnGeoscapeEventRaised`
  + the funnel chokepoint (`EventRaiseChokepointPatch.cs:43-46`). One gate at the funnel; nothing else.
- **`Arbiter` keyed by occurrence + reset-on-reload** (`SyncEngine.cs:217-219`): an in-memory claim table that a reload
  invalidates. v2's claim is a **fact in replicated state** (`GeoscapeEventRecord.State`), so nothing to reset.

---

## 2. Native surface (what we reuse, no custom UI)

- **Window state**: `UIStateGeoscapeEvent(GeoscapeEvent)` public ctor (`UIStateGeoscapeEvent.cs:42`), a
  `UIStateBaseGeoscapeEvent<UIModuleSiteEncounters>`; module = `_geoscapeModules.SiteEncountersModule` (`:40`).
  Marketplace variant `UIStateMarketplaceGeoscapeEvent` (`GeoscapeView.cs:2046-2052`) — still out of scope, logged.
- **The queue IS native**: `GeoscapeViewSwitchQuery._viewStateSwitchRequests` (`GeoscapeViewSwitchQuery.cs:15`),
  priority-ordered insert (`QueryStateSwitch:75-84`), popped one at a time and only when nothing is current
  (`ProcessQueriedStateSwitch:58-73`), closed by `FinishCurrentStateSwitch:116` (=`GeoscapeView.FinishQueriedState:2164`).
  **It also persists across save/load**: `GetRestorableData:25-37` / `RestoreData:39-56`, wired at
  `GeoscapeView.cs:1300` and `:349`, regenerating each state through `UIStateGeoscapeEvent.RestoreContext.RegenerateState`
  (`UIStateGeoscapeEvent.cs:29-37`). "Click through them afterwards" is native behaviour we inherit, not build.
- **Normal open path**: `GeoscapeEventSystem.OnEventTriggered:638` → record create/bump (`:642-650`) →
  `GeoscapeEventRaised:657` → `GeoscapeView.OnGeoscapeEventRaised:2034-2066` → `QueryStateSwitch(…, PauseGame=true)`.
  Priority: 10 if `Context.TriggeredByEvent`, 15 when an uncompleted request is superseded by a completed one (`:2044`, `:2057-2060`).
- **Choice applied natively**: `UIModuleSiteEncounters.OnChoiceSelected:546-596` → `Wallet.Take(choice.Requirments.Resources,
  OperationReason.Gift)` **in the UI layer** (`:573`) → `SelectChoice:598-616` → `GeoscapeEvent.CompleteEvent:86-118`
  (`Record.SelectChoice(index)` `:97`, `GenerateFactionReward` + `ChoiceReward.Apply` `:101-102`,
  `Context.Site.DestroySite()` `:108-111`, `Record.Complete` `:112-116`) → `StartMission` → `Context.View.LaunchMission`
  (`:604-613`; access modifier of `LaunchMission` `UNVERIFIED`).
- **Authoritative vs derived**: authoritative = `GeoscapeEventRecord` (`EventId`, `FirstTriggerAt`, `_lastTriggerAt`,
  `_state`, `_selectedChoice`, `_triggerCount`, `_completedAt` — `GeoscapeEventRecord.cs:10-36`). Derived/session-local =
  the `GeoscapeEvent` instance, its `Context` (site/vehicle), `ChoiceReward`, `IsCompleted` (`GeoscapeEvent.cs:32-36`).
  **`GeoscapeEvent.IsCompleted` is per-INSTANCE**: a freshly built instance over an already-resolved record will happily
  re-run `CompleteEvent` and re-grant the reward. The record is the only claim ledger.
- **Outcome page**: `SetClosingEncounter` (private, `:324-359`) ends in `ShowReward(geoEvent, geoEvent.ChoiceReward.ApplyResult)`
  (`:357-358`) → on a client `ChoiceReward` is null → NRE. `GeoFactionReward` and `GeoFactionRewardApplyResult` are plain
  classes with collection fields initialised inline (`GeoFactionReward.cs:20-22`, `:88`; `GeoFactionRewardApplyResult.cs:11-69`),
  so a stub `new GeoFactionReward { ApplyResult = new GeoFactionRewardApplyResult() }` makes `HasRewards()` false
  (`GeoFactionRewardApplyResult.cs:69`) and the native page renders outcome TEXT only. That single stub also feeds the
  arbiter's skip path (`SelectChoice:604` dereferences `ChoiceReward.ApplyResult`).

---

## 3. v2 design

### 3.1 The queue is already replicated — **no new root**

- Root `"ES"` = `GeoscapeEventSystem` (`IdentityResolver.cs:225-235` walk, `:342` client resolve).
- `EncounterRecords` rides via the game's own DTO twin (`RailMeta.cs:726` `EncounterRecords → _records`), licensed as a
  keyed `Dictionary→List` shape whose key is re-derivable from the element (`RailMeta.cs:818-828`, DictKeyMember = `EventId`).
- Coverage is **7/7 on the element** (`docs/rail-baseline.txt:240-247`: `EventId`, `FirstTriggerAt`, `_completedAt`,
  `_lastTriggerAt`, `_selectedChoice`, `_state`, `_triggerCount`) and 6/7 on the owner (`:248-255`; the one exclusion is
  the v<4 migration leftover `OldTriggeredEncounters`, `RailMeta.cs:603`).
- Per-record wire/subscription path form: `ES.EncounterRecords#<EventId>` — the generic keyed-element path
  (`DiffEngine.cs:694` `path + "." + f.Name + "#" + key`). It survives the walk because the key is a serialized member
  of the element, so the applier rebuilds entries keyed by it; element blob round-trip is asserted
  (`docs/rail-baseline.txt:394` `roundtrip=ok(7)`).
- Growth is bounded by construction: one record per event DEF (`_records` is keyed by `EventID`,
  `GeoscapeEventSystem.cs:92`, `:642-648`). Re-triggers bump `_triggerCount`, they do not append.
- **A second "queue" root would be wrong**, not just redundant: it duplicates the record (two sources of one fact) and
  it would make the per-peer "already clicked through" bit SHARED — one player's dismissal would erase another's window.
  That bit must stay local.

### 3.2 The backlog = a pure derivation, not a live latch

Replace the transition latch (`EventPopup._latch`, `_seeded`) with a **cursor**:

- Local, per-peer: `cursor` = the largest `_lastTriggerAt` (TimeUnit ticks) this player has acknowledged.
- `Backlog(records, cursor)` = records with `_lastTriggerAt > cursor`, ordered by (`_lastTriggerAt`, `EventId`) ascending
  (deterministic; law 6 style). Per entry the MODE is read off the record, not off a transition:
  `_state == Triggered` → **picker**; `SelectedChoice`/`Completed`/`MigratedCompleted` → **outcome**; `Reset` → skip.
- Why a cursor, not transitions: single-choice windows are already `Completed` when they reach the client (S0), and any
  gap in delta observation (tactical mission, reload, join, disconnect) loses transitions but never loses records. The
  derivation self-heals; the latch cannot.
- Pump sites: (a) the existing per-kind hook `UiEventMap.cs:122-127` (`case GeoscapeEventSystem es:`), and (b) once per
  second from the rail Tick — a joiner's records arrive with the SAVE, not with a delta, so a delta-only trigger can
  miss the entire backlog. Cost = a dict scan of <= |event defs| entries. `ponytail: 1 Hz scan, make it change-driven only if it profiles`.
- Raising: unchanged native push (`EventPopup.cs:95-102`) with `PauseGame=false`; the native switch query gives FIFO,
  one-at-a-time click-through and save/load persistence for free (S2). Before pushing, skip an `EventId` already
  present in `_viewStateSwitchRequests` or in `CurrentViewState` (a transferred save restores the host's pending
  windows, `GeoscapeView.cs:349`) — else the joiner gets each window twice.
- The cursor advances **only when the player closes that window** (`FinishEncounter`/`OnCancel` on a window whose
  `EventID` is a real record) — never at raise time, so a crash/quit re-shows it.

### 3.3 Intent family: "answer this event" (new surface `0xB4 GeoEventIntent`)

- `SurfaceIds`: `0xB4` is free (highest live id is `0xB3`, geoscape partition `0xA0-0xBF`, `SurfaceIds.cs:20-53`).
- Registration: `IntentRail.Register(SurfaceIds.GeoEventIntent, "event", ops)` in `SyncEngine`'s ctor, same shape as
  `TimeSync.cs:104`, `FacilitySync.cs:65`. Envelope `[nonce:u32][op:u8][body]` is the engine's (`IntentRail.cs:107-134`).
- One op: `answer = 1`, body `[eventId:string][choiceIndex:i32]` (`-1` = the native "no choice" path,
  `UIModuleSiteEncounters.cs:562-569`). Choice INDEX, not a def guid — indices are def-order and mod parity is blocking
  (law 10); `CompleteEvent` itself works in indices (`GeoscapeEvent.cs:97`).
- **Capture is block-first, at the presentation seam that already exists**: `EventChoiceClientLock.Prefix` on
  `UIModuleSiteEncounters.OnChoiceSelected` (`EventPopup.cs:148-163`). Today it swallows and returns false; it becomes
  `if (IntentRail.ShouldRunNative()) return true;` → `IntentRail.Send(0xB4, 1, "answer " + id, w => {…})` → return false.
  Paging clicks stay native (`_pagingEvent`, `EventPopup.cs:157` — they only advance text). Must stay a PREFIX (L19).
- Host handler (`OpHandler`, `IntentRail.cs:50`), all native, host state only:
  1. `rec = es.GetEventRecord(eventId)` (`GeoscapeEventSystem.cs:313`); **reject unless `rec.State == Triggered`** →
     first-choice-wins. Reject unless `-1 <= index < EventData.Choices.Count`.
  2. Resolve the LIVE `GeoscapeEvent` from the host's own view (`CurrentViewState` / `_viewStateSwitchRequests` entry
     whose `.Event.EventID` matches) so the real `Context` (site + vehicle) is used; else synthesise exactly as
     `EventPopup.cs:99-100` does (`new GeoscapeEvent(data, new GeoscapeEventContext(es.FindEventLocation(id), geo.ViewerFaction)) { Record = rec }`)
     — the same shape the game uses for its own re-entry (`GeoscapeView.ToMarketplace:735-738`).
  3. Pay the choice cost the way the UI layer does: `choice.Requirments != null` →
     `geo.ViewerFaction.Wallet.Take(choice.Requirments.Resources, OperationReason.Gift)` (mirrors `UIModuleSiteEncounters.cs:573`;
     the client must NOT pay locally — law 3).
  4. `ev.CompleteEvent(choice, geo.ViewerFaction)` (`GeoscapeEvent.cs:86`).
  5. `ApplyResult.StartMission != null` → host-side `geo.View.LaunchMission(startMission, ev.Context.Vehicle)`
     (mirrors `:604-613`). Which peer plays the mission is TACTICAL scope (law 5) — out of this engine.
- Outcome to everyone: the record's `_state/_selectedChoice/_completedAt` + every reward-touched root ride the normal
  0xAC value rail; `IntentRail.HandleInbound` already calls `DiffEngine.FlushNow()` + `OpenUiRepaint.MarkDirty()` in the
  same frame (`IntentRail.cs:165-182`).
- Reject: `IntentRail.Reject(SurfaceIds.GeoEventIntent, peer, "event '"+id+"' already resolved (choice "+rec.SelectedChoice+")", "ES.EncounterRecords#"+id)`
  → logs + scoped `DiffEngine.ForceReemit` (`IntentRail.cs:201-226`, `DiffEngine.cs:166`) + the client-side nudge repaint
  (`IntentRail.cs:144-153`). Never log-only.

### 3.4 First-choice-wins: one funnel, one ledger

- The **ledger is the record** (`_state`), replicated. No claim table, nothing to reset on reload.
- The **funnel is `GeoscapeEvent.CompleteEvent`** (`GeoscapeEvent.cs:86`) — both a host-local click
  (`OnChoiceSelected → SelectChoice:602`) and the relayed intent (S3.3 step 4) pass through it. Guard (host + client,
  sim-gating seam, law 4b): if `Record.State != Triggered` → skip the native body, stub
  `ChoiceReward = new GeoFactionReward { ApplyResult = new GeoFactionRewardApplyResult() }` (so `SelectChoice:604`
  cannot NRE), set `__result` to that stub, and LOG. This is the only thing standing between a stale host dialog and a
  double reward grant; the auto-complete at trigger passes it (record is `Triggered` at `GeoscapeEventSystem.cs:648-655`).
- Losers never even get to click: when a record resolves, the open window re-renders in **replay mode** (S3.5), whose
  buttons are non-interactable — so the `Wallet.Take` at `UIModuleSiteEncounters.cs:573` (which runs BEFORE any
  arbitration) is unreachable for a lost race. The `CompleteEvent` guard is the belt for the one-frame overlap.

### 3.5 Presentation of the outcome (native, no overlay) + repaint path

- `UiNativeRepaint.Table[typeof(UIStateGeoscapeEvent)]` — a **read-direction rebuild**, which is also the opt-out from
  the universal Exit+Enter (the `UIStateManufacturing` pattern, `UiEventMap.cs:39`): refresh the dialog's stale
  `Record` ref (blob apply rebuilds the record instance — `EventPopup.cs:113` already does this) then
  - picker mode → `Module.ShowEncounter(ev)` (PUBLIC, `UIModuleSiteEncounters.cs:192`);
  - outcome mode → replay arm: `ChoicesButtonController.Choices[i].Button.SetInteractable(false)` for every
    `i != rec.SelectedChoice`; winner gets `IsSelected = true`, `IsNonInteractableWhenSelected = false`,
    `ResetButtonAnimations()` (all S2 handles) — winner highlighted, still clickable to close.
  This makes the `ExitState` local-complete (`UIStateGeoscapeEvent.cs:61-65`) **structurally unreachable**: the fallback
  Exit+Enter (`OpenUiRepaint.cs:189-206`) never runs for this screen again.
- Closing a resolved window: the existing `EventCancelClientLock` (`EventPopup.cs:171-184`) already lets the native close
  through once the record is resolved. Keep; extend it to the host (a host holding a lost dialog is the same case).
- History replay (a backlog entry the player opens after the fact) uses the identical outcome mode — read-only by the
  same greying, no second code path.
- Mandatory log lines (the anti-silence contract):
  - `[MP][events] backlog n=<k> cursor=<ticks> next=<eventId> mode=picker|outcome`
  - `[MP][events] raised '<id>' state=<s> triggerCount=<n> mode=<m>` / `skipped '<id>' — <reason>` (no def / marketplace /
    already queued / Reset) — one line per id, never a bare `return`.
  - `[MP][events] cursor advanced to <ticks> after closing '<id>'`
  - `[MP][events] client-local raise of '<id>' BLOCKED — the host's record arrives via the rail` (log-once per id)
  - `[MP][events] stale CompleteEvent for '<id>' skipped (record=<state>, selected=<n>)`
  - reject: the `IntentRail.Reject` line (`IntentRail.cs:201-226`).

---

## 4. Batch plan (1-3 changes each, independently deployable + in-game testable)

Highest landed law: **L25** (`crc-backstop`). Event-window work claims **L26**.
Law numbers in later batches are tentative — assign at implementation time.

### B1 — make the windows appear at all (client presentation + sim gate) — IN PROGRESS

1. `EventPopup`: latch → cursor derivation (S3.2); delete the `_seeded` silent return and the `<2 choices` gate;
   raise picker/outcome by record state; skip ids already queued natively; the log lines above.
2. New client sim gate: prefix on `GeoscapeEventSystem.OnGeoscapeEvent(BaseEventData, BaseEventContext)` — client and
   not `SyncApplyScope.Active` → return false + log-once. (`AccessTools`/`HarmonyPatch` must name **exact** param types
   `(BaseEventData, BaseEventContext)`; a base-typed guess binds nothing, silently.)
3. `UiEventMap` ES case: call the pump; add the 1 Hz Tick pump.
- Law: **L23** (`Program.cs:1451-1530`) — every static reflection handle must resolve non-null and every
  attribute-declared Harmony target must resolve. Falsify: mistype the patched method name (`"OnGeoscapeEvents"`) or the
  `_viewStateSwitchRequests` field name → RED `L23 patch-target-unresolved: …GeoscapeEventSystem.OnGeoscapeEvents` /
  `L23 handle-unbound: EventPopup.RequestsField`.
- Law: **L26** (new, pure) — backlog derivation as a headless function over synthetic `(eventId, state, lastTriggerAt,
  triggerCount)` tuples: (a) cursor 0 + one `Triggered` + one `Completed` record => backlog of 2 (falsify: restore the
  silent seed => RED `L26 backlog-swallowed-on-seed`); (b) a `Completed` record maps to `outcome`, never `picker`
  (falsify: return picker => RED `L26 picker-for-resolved`); (c) re-running with the advanced cursor => empty (falsify:
  forget to advance => RED `L26 backlog-not-idempotent`); (d) ordering by (`lastTriggerAt`,`EventId`) is stable
  (falsify: iterate the dict => RED `L26 backlog-nondeterministic`).
- In-game gate: client sees the quest/narrative window (single choice) and a multi-choice window; joiner mid-campaign
  gets the pending window(s) once, not twice, not never.

### B2 — the client can answer (intent family + arbiter)

1. `SurfaceIds.GeoEventIntent = 0xB4` + `IntentRail.Register(…, "event", ops)` + the `answer` op handler (S3.3).
2. `EventChoiceClientLock.Prefix` → block-first send instead of a bare swallow.
3. Arbiter prefix on `GeoscapeEvent.CompleteEvent` (S3.4) with the reward stub.
- Law: **L12** — add `SurfaceIds.GeoEventIntent` to the envelope loop (`Program.cs:994-996`): `[nonce][op]` prefix +
  empty reject-nudge round-trip. Falsify: write the op before the nonce in `Send` => RED `L12 intent-prefix: 0xB4 …`.
- Law: **L19** (`Program.cs:1330-1376`) — IL walk: an intent emitted from a POSTFIX on a model patch is
  `L19 result-ship`. Falsify: move the capture to `Postfix` => RED.
- Law: new (number TBD) — the answer validator as a headless function `(recordState, choiceIndex, choiceCount) →
  accept | reason`: `Triggered`+valid => accept; `SelectedChoice`/`Completed` => reject with a NON-EMPTY reason
  (falsify: accept a resolved record => RED `double-answer-accepted`); index `>= count` or `< -1` => reject
  (falsify => RED `index-unchecked`); reject must never return an empty reason (falsify => RED `silent-reject`).
- In-game gate: two clients race a 2-choice window → exactly one grant, both see the same `SelectedChoice`, the loser's
  buttons are greyed with the winner highlighted, host wallet charged once.

### B3 — outcome/replay presentation + close the ExitState hole

1. `UiNativeRepaint.Table` entry for `UIStateGeoscapeEvent` (picker rebuild + replay arm, S3.5).
2. Extend `EventCancelClientLock` to the host; delete `EventPopup.Dismiss`'s forced close (replaced by re-render).
- Law: **L21 widened** (`Program.cs:1401-1449`) — `FirstModelCommand` currently accepts only VOID instance commands
  (`:1409-1414`), which is why `UIStateGeoscapeEvent.ExitState → CompleteEvent` (returns `GeoFactionReward`) is invisible.
  Widen the walk to value-returning instance commands on rail-covered types (preferred, generic) — blast radius unknown
  from a read-only pass, so run it once and reconcile `docs/rail-baseline.txt` in the same commit; if it floods, land the
  scoped arm instead: resolve the `ExitState → GeoscapeEvent.CompleteEvent` IL edge and require the screen to be in
  `UiNativeRepaint.Table`, reporting `L21 vacuous` if the edge ever disappears. Falsify either form: delete the table
  entry => RED `L21 exit-writeback-unrepainted: UIStateGeoscapeEvent.ExitState reaches GeoscapeEvent.CompleteEvent`.
- In-game gate: a delta arriving while the event dialog is open repaints it in place (no flicker, no local completion);
  a lost race flips picker → outcome with no reward text NRE.

### B4 — the cursor survives a reload

1. Persist the cursor via `UnityEngine.PlayerPrefs` (`SetString`/`GetString` of the tick count, one key).
   `ponytail: one key per machine — two parallel campaigns share it; key by campaign name if that ever bites.`
2. Drop `EventPopup.Reset()`'s meaning to "forget the in-flight raise set", never the cursor (`GenericApplier.cs:51`).
- Law: **L26 extension** (or successor) — cursor codec round-trip (`long → string → long`, incl. 0 and `long.MaxValue`) and
  "a restored cursor yields an empty backlog for the same records". Falsify: persist with `ToString()` under a
  culture-sensitive parse => RED `cursor-round-trip`.
- In-game gate: save+reload on the client → no popup storm, and a window left unanswered before the reload is still there.

---

## 5. Traps

- **Save/load, both directions.** The transferred save carries the host's pending queue
  (`GeoscapeView.cs:349` ← `GetRestorableData:1300`, `UIStateGeoscapeEvent.RestoreContext:16-38`), so on join some
  windows already exist natively → the pump MUST dedup against `_viewStateSwitchRequests`/`CurrentViewState` or every
  joiner sees doubles. Conversely `RestoreContext.RegenerateState` returns null for an empty `EventID`
  (`UIStateGeoscapeEvent.cs:31-34`) → our synthetic outcome pages silently vanish on reload and log
  `Could not restore view state switch…` (`GeoscapeViewSwitchQuery.cs:48`). Acceptable (the record still drives the
  backlog), but do not build the outcome page as a *synthetic* event if it must survive a reload.
- **A choice arriving for an already-resolved event** is the NORMAL case, not an error: two peers click within one RTT.
  Reject + reconverge, never throw — `CompleteEvent` itself throws `"Selected invaid choice"` when the *instance* is
  completed (`GeoscapeEvent.cs:88-91`), and `GeoscapeEvent.IsCompleted` is per-instance, so a synthetic instance would
  NOT throw and would re-grant. The record check is the only real guard.
- **The outcome mutates state other roots own.** `ChoiceReward.Apply` (`GeoscapeEvent.cs:102`) touches wallet, sites
  (`DestroySite:110`), diplomacy, and can CREATE soldiers (`GeoEventChoiceOutcome:296/305` → `_tacUnits[id]`, root `U#`)
  or start a mission. Those ride their own roots/structural appliers, in the same flush but not necessarily the same
  frame the popup is rendered → the outcome page must not read reward data from the mirror (we render TEXT only, and
  `HasRewards()` is false on the stub). `ReEneableEvent` (`:103-106`) resets the record to `Reset` — the backlog must
  skip `Reset` or a re-enabled event re-raises forever.
- **Unbounded history**: not a risk for records (one per event def, `GeoscapeEventSystem.cs:92`), and the cursor is a
  single long. The only unbounded set would be a per-id "seen" HashSet — do not build one.
- **A client picking while its mirror is stale.** Choice indices come from defs (parity-blocking, law 10) so the index
  is never ambiguous; staleness shows up as "record already resolved" → reject path. The dangerous half is the reverse:
  the client's UI paying locally (`UIModuleSiteEncounters.cs:573`) or completing locally
  (`UIStateGeoscapeEvent.cs:61-65`, `OnChoiceSelected:566`) — all three must be unreachable on a client (B1 gate + B2
  block-first + B3 table entry).
- **Token NRE on a historical window.** `GeoscapeEventContext`'s token table dereferences `context.Site`
  (`GeoscapeEventContext.cs:23`, `:27`, `:35`, `:39`) and a completed exploration event destroys its site
  (`GeoscapeEvent.cs:108-111`), so a backlog entry rendered later can have `Site == null`. Whether
  `ReplaceEventTokens` guards null is `UNVERIFIED` — wrap the raise in try/catch that LOGS the event id (never a silent
  skip) and keep going.
- **Marketplace events** (`UIStateMarketplaceGeoscapeEvent`, `GeoscapeEventSystem.IsEventTheMarketplace:407`) open a
  trade screen with its own model writes — still skipped, still logged once (`EventPopup.cs:91-92`). Out of scope here.
- **`SuppressEvents` is a mirrored leaf** (`docs/rail-baseline.txt:254`) — never write it locally to gate the client;
  a host change would silently overwrite the local intent. The gate is the Harmony prefix.
- **RailCheck green proves none of the convergence** (`tools/RailCheck/Program.cs:19` never builds a
  `GeoLevelController`): every batch above needs its in-game gate.
