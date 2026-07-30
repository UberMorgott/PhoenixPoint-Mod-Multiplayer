# Event-window engine — shipped design (presentation payload)

> Shipped 2026-07-30/31 (commits `2747cbe`..`3dfa73d`). Supersedes the record-derived backlog
> design that was tried and failed — see History below.

## 1. Architecture: v1 model on v2 rail

Event windows replicate via a **presentation payload** per raise, not via record-state derivation.
The host captures the fully-resolved text + context refs at raise time and ships them; clients
rebuild a real `GeoscapeEventContext` from the payload and push the native
`UIStateGeoscapeEvent`. There is **no history list and no backlog** — a peer absent when an event
fires never sees that window.

### Surface `GeoEventRaise` (0xB6)

- `SurfaceIds.GeoEventRaise = 0xB6`, host->all, `SyncKind.StateDelta`
- Payload: `[seq:u32][eventId:string][siteRef][vehicleRef][title:string][narrative:string][priority:int]`
- Capture: Postfix on `GeoscapeView.OnGeoscapeEventRaised`
- Client: rebuilds `GeoscapeEventContext` from site/vehicle refs, constructs
  `UIStateGeoscapeEvent`, pushes via native `GeoscapeViewSwitchQuery.QueryStateSwitch`
- Priority on the wire matches native queue priority (`GeoscapeView.cs:2044-2060`)

### Text resolution — WithWireTexts shallow copy (`13ad16a`)

- `WithWireTexts` returns a per-raise **shallow copy** of `GeoscapeEventData` with title/narrative
  stamped from the payload
- `Choices` stays the def's own `List` — native code keys by identity (`SiteBaseChoiceButton.Choice`,
  `EventData.Choices.IndexOf` in `CompleteEvent`)
- Never mutates the SHARED def graph (earlier self-poisoning: after first stamp the def is
  non-empty forever; re-raised runtime-narrative defs like TFTV `VoidOmen_{roll}` kept stale text)
- Window bound to its raise via `ConditionalWeakTable` — resolution from an older raise cannot
  dismiss the current one

### Single-choice events (`a64c502`)

- Game auto-completes single-choice events at trigger (`GeoscapeEventSystem.cs:651-655`), so
  record is `Completed#1` before window is queued
- Freeze uses a TRANSITION test via the game's own predicate (`Choices.Count <= 1 && !marketplace`),
  asked of the DEF (identical on every peer per law 10; covers save-restored windows)
- NOT a record-state check (that was the original bug — record already resolved, freeze thought
  another peer answered)

### First-choice-wins + replay visuals

- Winner: `IsSelected=true`, `IsNonInteractableWhenSelected=false` forced, stays clickable
  (click -> result page)
- Losers: `SetInteractable(false)` on every non-winning choice button
- Host that lost a race is NOT double-charged (`Wallet.Take` at
  `UIModuleSiteEncounters.cs:571-573` guarded)

### Answering peer auto-consume (`3dfa73d`)

- `EventPopup` memoises `(event, raise, choice)` at `EventChoiceClientLock.Prefix` and consumes
  it in `RepaintDialog` — no second click needed for the answering peer
- Observers keep replay visuals

## 2. Modal windows — UIStateGeoModal mirror (0xB7, `79e513a`)

- Surface `0xB7` mirrors `UIStateGeoModal` family host->client
- Payload: `[seq][modalType][shape][ref][n][key*n][num][priority]`
- Shape derived from RUNTIME type of `modalData`; NO text on wire (each peer renders own defs in
  own locale)
- Mirrored: `GeoResearchComplete`, `DiplomacyResearchBrief`, `GeoPhoenixBaseOutcome`
- Client copies: `dialogHandler: null`, never `Persistent`
- `GeoWindowCoverage.Declared` = total coverage table; undeclared kind = runtime error + RailCheck
  failure
- `UIStateGeoModal` now has a `UiNativeRepaint` entry (without it, delta during open modal fired
  Exit+Enter -> `ExitState:116` -> host's DialogCallback with `ModalResult.Close`)

**Declared Gap (18):** mission brief x10, mission outcome x11 (overlap), `PandoranRevealResult`,
interception x2, `AlienResearchBrief`, `FactionSoldierJoin`, 4 with no caller.
**Declared LocalOnly (12).**

## 3. Live source of truth

- `src/Rail/EventPopup.cs` — raise, freeze, replay, auto-consume
- `src/Rail/GeoWindowCoverage.cs` — coverage table
- RailCheck **L39** (event surface), **L40+** (modal surface, coverage exhaustiveness)

## 4. Law 1 amendment (pending)

0xB6/0xB7 are a THIRD category beyond Intent + Delta: **ephemeral presentation delta** —
host-resolved data not in the save graph and not derivable from it. Law 1 needs a bounded
amendment line.

---

## History — why the record-derived backlog failed

> The original design (this file pre-2026-07-30) proposed deriving a queued history from
> mirrored `GeoscapeEventRecord` state with a per-peer cursor. It was structurally impossible:

- `GeoscapeEventRecord` persists only `EventId`/timestamps/state/`_selectedChoice`/`_triggerCount`
- `GeoscapeEventContext` (resolves `[HavenName]` via `context.Site`) is built fresh per raise and
  NOT in the save graph
- 54 of 94 replayed raises had `site=null` -> rendered raw `[HavenName]` tokens
- Persisted per-peer cursor replayed each joiner's entire campaign history (97 windows)
- Single-choice events already `Completed` at trigger -> cursor derivation read them as
  "already answered by another peer"
- Commit `80ae362` (NRE swallow) made broken windows clickable, masking the problem

The replacement (presentation payload, this document) ships host-resolved text per raise and has
no history — matching v1's proven model.
