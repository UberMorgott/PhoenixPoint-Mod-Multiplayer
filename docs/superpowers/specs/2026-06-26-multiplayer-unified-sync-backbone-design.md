# Multiplayer — Unified Co-op Sync Backbone (design spec)

Status: DRAFT (brainstorming output, 2026-06-26) — pending user spec-review, then → writing-plans.
Author: rework brainstorming session 2026-06-26.
Strategy chosen: **A — write this one unified backbone design first, then converge BOTH layers
onto it incrementally, starting with the rail.** (Alternative B "finish tactical as reference
then converge geoscape" was rejected: leaves geoscape divergent longer and gives no single
documented target to converge to.)

## 0. Why this doc exists

The mod grew TWO co-op sync layers — **tactical battle** and **geoscape (global map)** — that solve
the same conceptual problems DIFFERENTLY ("вермишель"). The documented vision (2026-06-17 decision +
the 2026-06-19/20/25 specs) is ONE backbone for both: host-authoritative, client = pure mirror with a
frozen sim, snapshot-at-join, one per-entity state-delta stream, one generic intent surface. No single
document reconciles both layers into that one backbone — this spec IS that document. Every future
increment and bug-fix references this target, so fixes become class-level instead of per-surface.

Non-goals: host migration (OUT, decided). No big-bang rewrite — every convergence step is additive
first, in-game-gated, retiring exactly one legacy surface per commit (`dont-replace-working-architecture`).

## 1. Principles (the invariants both layers obey)

1. **Host is the sole authority. Client is a pure mirror.** The client never decides gameplay outcomes.
2. **Client sim is FROZEN.** The client does not run its own simulation producers (geoscape sim
   producers, tactical AI/turn/vision/damage-roll). It only renders state the host sends.
3. **Client never RE-EXECUTES a command.** Host→client is ALWAYS absolute state, never "replay this
   action locally." (This kills the geoscape command-replay model.)
4. **Snapshot heavy, stream light.** Full native-serializer snapshot only on join/reconnect. During
   play, only per-entity absolute state-DELTAS (changed fields), ~2–4 Hz heartbeat + flush-on-change,
   idle = 0 bytes.
5. **Intents go up, authorized once.** Client→host intents pass ONE authorize stage
   (dedup → permission → validate) before the host applies + replicates.
6. **Presentation events are cosmetic-only and exist ONLY for travelling animation** (a mover/projectile
   whose motion must look concurrent). Everything instantaneous rides the state-delta. Cosmetic
   mispredict is always corrected by the next delta.
7. **One mechanism per concept.** Exactly one rail, one router, one seq/dedup, one intent surface, one
   state-delta contract, one snapshot/reconnect contract, one freeze declaration — shared by both layers.

## 2. Target architecture — the shared backbone

### 2.1 Wire / rail (ONE)
- One `PacketType` envelope (the existing tactical `SyncEnvelope` 0x67 model) carrying
  `{surfaceId:u8, kind:SyncKind, seq, payload}`. ALL co-op traffic (tactical + geoscape, intents +
  deltas + presentation + snapshot chunks) rides this single enveloped rail.
- One inbound chokepoint: `SurfaceRouter` dispatches by `surfaceId` to a registered handler. Geoscape's
  raw per-purpose packets (`0x60`–`0x66`) are migrated onto enveloped surfaces and retired.
- Surface-id space partitioned by domain (tactical `0x80–0x9F`, geoscape `0xA0–0xBF`, shared/core
  `0x00–0x0F`) but the ROUTER, codec envelope, and seq/dedup are shared.

### 2.2 Sequencing + dedup (ONE)
- One per-surface monotonic `Seq` (last-writer-wins on the client) — generalize the tactical
  `TacticalLiveSeq` into a shared `SurfaceSeq`.
- One intent `Dedup` (nonce ring per peer) — generalize `TacticalIntentDedup` + the geoscape
  `RequestDedup` into one. The global geoscape `SequenceTracker` is replaced by per-surface seq.

### 2.3 Intent surface (ONE) + authorize stage (ONE)
- One generic `intent` surface client→host: `{actorOrEntityId, opGuid/abilityGuid, target, nonce}`
  (the tactical reserved `tac.intent` 0x8E + geoscape `ISyncedAction` collapse into this shape).
- One authorize chokepoint on the host for EVERY intent: `Dedup → PermissionGate.CheckFor →
  Validate → apply → replicate (delta)`. Tactical intents (today ungated) gain the same gate.

### 2.4 State-delta stream (ONE contract)
- One contract `IStateDeltaSource` per domain: produce a per-entity, field-masked, ABSOLUTE-value
  delta of changed fields since last broadcast; client applies idempotently under one re-entrancy
  guard (`_applyingRemote`).
- Tactical = `tac.actorstate` 0x8F (grow it: facing, equip, overwatch bits already reserved).
  Geoscape = a per-entity `GeoFaction.InstanceData`/entity diff (revive the dormant
  `GeoStateDiffCodec`/`GeoVehicleStateDiffer`/`GeoEntityOp` machinery PROPERLY).
- Broadcast cadence: signature-skip heartbeat (~2–4 Hz) + immediate flush on an applied intent. Idle =
  0 bytes.

### 2.5 Snapshot-at-join / reconnect (ONE contract)
- One contract `ISnapshotDomain`: `Snapshot()` → bytes (native serializer with the game's configured
  Serializer + Timing pump, see `pp-serializer-context-and-pump`), `Apply(bytes)` on the joiner.
- Same chunked transport (the `SaveTransferCoordinator` chunk/done + 2-phase barrier) for both the
  tactical deploy snapshot and the geoscape save snapshot. Triggered on join AND reconnect.
- Divergence backstop: rolling `CRC32` over each domain's serialized state; on mismatch or
  reconnect, re-snapshot the one affected peer (geoscape Inc5 = tactical Inc5, shared code).

### 2.6 Freeze model (ONE declaration)
- Each domain declares a "frozen producer set" — the simulation entry points the client must NOT run
  (geoscape: the 13 `GeoSimProducerTable` producers + travel emitters; tactical: AI/NextTurn/
  PlayTurnCrt body/damage-roll/move-cost/vision-recompute).
- Replace the current PILE of per-symptom tactical mirror guards (8+ gate files) and geoscape
  per-domain event-suppression band-aids with this single declarative freeze + the "client never
  re-executes" rule.

### 2.7 Presentation events (kept, minimal)
- Only for travelling animation: tactical `move.start` / `fire.start` / `melee.start` + the `damage`
  event; geoscape slaved-clock travel (`StartTravel{path,startTime}` driving native `NavigateRoutine`).
- These are FX triggers, not authority. The state-delta is the source of truth; a presentation event
  that mispredicts is corrected by the next delta. (The 2026-06-26 client-predicted fire anim +
  `RelayedHostShotRegistry` instant-shot fit here and stay.)

## 3. Components (modules + boundaries)

Shared core (new/generalized, domain-agnostic):
- `SyncEnvelope` codec + `SurfaceRouter` (chokepoint) + `SurfaceSeq` + `IntentDedup` + `AuthorizeStage`
  (Dedup→Permission→Validate).
- `IStateDeltaSource` / `ISnapshotDomain` / `IFrozenProducerSet` contracts + a `Crc32` divergence
  monitor + the chunked snapshot transport.

Per-domain adapters (implement the contracts; hold all domain knowledge):
- Tactical adapter: `tac.actorstate` delta source, `tac.deploy` snapshot domain, tactical frozen set,
  tactical presentation events.
- Geoscape adapter: `GeoFaction`/entity delta source, save-blob snapshot domain, geoscape frozen set
  (producer table), geoscape travel presentation.

Each unit answers: what it does / how to use it / what it depends on. Domain adapters depend on the
shared core's interfaces only — not on each other.

## 4. Data flow

- **Join/reconnect:** host `Snapshot()` per domain → chunked transfer → joiner `Apply()` → CRC seed.
- **Live, client acts:** client emits one `intent` (suppress local authority) → host `AuthorizeStage`
  (dedup→permission→validate) → host applies natively (authoritative) → host's `IStateDeltaSource`
  picks up the changed fields on the next heartbeat/flush → broadcast absolute delta → all clients
  apply idempotently. Cosmetic `*.start` event may fire concurrently for travelling animation.
- **Live, host acts:** host applies natively → delta broadcast → clients apply. (No intent needed.)
- **Divergence:** CRC mismatch → re-snapshot that peer.

## 5. Current → target mapping (what each layer keeps / retires)

- **Rail:** keep tactical `0x67` envelope + `SurfaceRouter`; MIGRATE geoscape `0x60–0x66` onto
  enveloped surfaces; retire the raw packets + the dead geoscape router arms (do it PROPERLY now).
- **Seq/dedup:** keep one `SurfaceSeq` + one `IntentDedup`; retire geoscape `SequenceTracker` +
  per-channel version + separate `RequestDedup`.
- **Propagation:** geoscape STOPS client-side `action.Apply` replay → host-only apply + delta. Tactical
  already delta-applies; grow the delta to subsume per-action outcomes.
- **Intent:** collapse tactical's N typed intent surfaces (0x82/84/87/8A/8C) + geoscape `ISyncedAction`
  into one generic intent surface + the shared authorize stage.
- **Freeze:** collapse tactical's 8+ per-symptom guards + geoscape per-domain suppression into the
  declarative frozen-producer-set on both.
- **Surfaces to retire (one per commit, in-game-gated):** tactical move-END `0x83`, equip `0x8B`,
  overwatch.state `0x8D`, vision `0x89` (Inc6); geoscape research/wallet/inventory/diplomacy/unlock
  channels → generic `GeoFaction` diff (Inc4).

## 6. Convergence increments (the sequence; each additive-first, in-game-gated)

1. **Unify the rail.** Route geoscape actions/channels/events through the shared `SyncEnvelope` +
   `SurfaceRouter`; converge seq/dedup onto `SurfaceSeq` + `IntentDedup`. (Tactical already here →
   low risk. Revive the deleted geoscape arms properly.)
2. **Finish each domain's state-delta.** Tactical Inc2–3 (facing, then combat fields) + geoscape Inc2–3
   (entity-op/travel/InstanceData diff) — absolute per-entity deltas under one re-entrancy scope.
3. **Flip discrete actions to intent+delta.** Geoscape stops `action.Apply` replay (host-only apply +
   delta); tactical adds the generic intent surface + authorize gate. Both go through `AuthorizeStage`.
4. **Collapse the freeze.** Replace tactical per-symptom guards + geoscape per-domain suppression with
   the single frozen-producer-set + "client never re-executes."
5. **Retire redundant surfaces.** Geoscape Inc4 (drop the per-domain channels → generic diff) +
   tactical Inc6 (drop the outcome surfaces), one per commit, each in-game-gated.
6. **Shared reconnect + CRC.** Wire the `Crc32` divergence backstop + re-snapshot on the now-identical
   snapshot contract (geoscape Inc5 = tactical Inc5).

Each increment ships behind the existing in-game gate discipline; the user verifies in-game before the
next increment and before any code-default flip / surface retirement.

## 7. Error handling

- **Dropped/duplicate intents:** `IntentDedup` nonce ring (idempotent; re-sends are no-ops).
- **Out-of-order deltas:** per-surface `SurfaceSeq` last-writer-wins; absolute values are idempotent.
- **Snapshot failure:** the native Serializer needs the game's configured instance + Timing pump
  (`pp-serializer-context-and-pump`); on failure, abort the join, do not partially apply.
- **State divergence:** `CRC32` mismatch → re-snapshot the affected peer (no global reload).
- **Permission/validate reject:** host drops the intent + (optionally) sends a reject; client is
  already only a mirror, so it self-heals on the next delta.

## 8. Testing

- **Pure unit tests** (the bulk): envelope codec, `SurfaceSeq`, `IntentDedup`, each `IStateDeltaSource`
  diff (field-mask, absolute, idempotent apply), `Crc32`, authorize-stage decision. These are
  engine-free and must stay green every commit.
- **In-game gates** (the user): each increment has a concrete 2-instance DirectIP checklist; no
  code-default flip or surface retirement until the increment is in-game-confirmed.
- Baseline at spec time: 801 tests green.

## 9. Risks / open items

- Reviving the dormant geoscape full-state machinery (`GeoStateDiffCodec` etc.) must be re-validated
  against the post-2026-06-24 decompile (signatures may have shifted).
- Geoscape client sim is NOT yet fully frozen (travel emitters out of scope historically,
  `multiplayer-client-sim-not-frozen`); Increment 4 must close that.
- The generic intent + authorize collapse must preserve every today-working tactical action
  (move/shoot/melee/equip/overwatch) — covered by keeping them working additively before retiring the
  typed surfaces.
- DIAG `[Multiplayer][tac]` logging must be stripped before any publish.

## 10. Decision log

- 2026-06-26: strategy A chosen (unified spec first, converge incrementally from the rail) — user
  delegated the call.
- Re-affirms the 2026-06-17 full-state-replication decision and the 2026-06-20 convergence handoff;
  this spec is their reconciliation into one backbone.
