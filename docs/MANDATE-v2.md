# Phoenix Point Co-op — Sync Rework Mandate (v2, final)

> Developer mandate v2 (final), received 2026-07-16, translated from Russian original
> (verbatim original preserved in git history at commit 4f5a5fb); reconciliation with recon
> facts lives in `../ARCHITECTURE.md` and is authoritative where they diverge.

Status: consensus of three independent reviewers + developer. Document = working mandate for
autonomous agent: architecture decisions follow it without developer clarification.
No remaining disagreements; disputed v1 points resolved and embedded in laws.

## 1. Context (brief)

- Co-op mod for Phoenix Point, host-authoritative, TFTV-compatible
- Single developer + AI agents
- Repo: ~118k LOC C#, 640 files; 246 `[HarmonyPatch]` in 96 files (intent-capture ~31, sim-gating ~28, presentation ~19, state-mirroring ~12)
- Infrastructure scaffolding (channel/codec/mirror/reflection) = 323 files (primary kill-set)
- Previous attempt with same architecture (Unified Sync Backbone, 2026-06-26, spec e837379) stalled on process: additive-first, legacy never retired, two worlds in parallel

Paradigm shift: from replicating BEHAVIOR (manual mirroring of each subsystem) to replicating STATE (generalized diff of the save graph).

## 2. Architecture Laws

**L1. Two primitives on the wire.** Intent (client -> host) and Delta (host -> all). No third message type exists. Reconnect/join is NOT a separate type (see L6).

**L2. Addressing.** Mutations addressed by stable IDs: Delta = (entityId, field, value); entityId = GeoSite ID / actor ID / def GUID -- never collection indices. Stable-keyed paths (`geo/research/byId/<GUID>/progress`) = scheme for UI subscriptions and CRC subtrees. Hybrid is intentional: ID resolves mutation target, path prefix resolves subscription. Do not collapse to one or the other.

**L3. Client = projector + emitter.** Client does not execute game logic, does not mutate authoritative state outside the single Apply() entry point. Apply = universal value-appliers + explicit small set of structural appliers. Identity boundary: value-mutations never create identity; structural mutations may (create/destroy of entities: site, soldier, vehicle -- live Unity views, referential integrity). Structural layer is designed from day one, not "generalize later".

**L4. Three legal Harmony seams.** Patches exist only for: (a) intent capture, (b) sim gating, (c) presentation. Any patch that transfers or mirrors state is removed; the rail does that work. Kill-set = 323 channel/codec/mirror/reflection infrastructure files.

**L5. Layer split.** Geoscape rides the diff rail: host runs native logic -> Serialize(current) in memory -> Diff(previous, current) -> Delta -> clients apply via Apply() -> UI via path-prefix subscriptions. Tactical = quarantine: current hybrid preserved (presentation local to actor, outcomes host-computed only); shared-seed determinism = separate future project after RNG recon (`docs/research/02-rng-analysis.md`). Rail does NOT fix tactical bugs -- expectations fixed.

**L6. Reconciliation and session join.** Periodic CRC per path subtrees; divergence -> resend snapshot of only the diverged subtree. Join/reconnect uses game's native save-loader (battle-tested), NOT a full snapshot pushed through the delta path.

**L7. Delivery contract.** Single writer, single order: one ordered stream with seq. Required: idempotent Apply (redelivery of same Delta = same result), out-of-order resilience (late seq after earlier one breaks nothing), resync-on-gap (gap -> request subtree snapshot). Commutativity of independent changes NOT required and NOT a law (single-writer topology; CRDT requirements do not apply). SurfaceSeq/IntentDedup quarried as-is.

**L8. Canonical diff.** For identical state, Diff must produce byte-identical Delta: fixed traversal order (sorted by stable IDs, fixed field order), no nondeterministic dictionary walks. Required for journal, replay, log comparison, and regression tests.

**L9. One-way flow, both echo loops closed.** Cycle: Intent -> Host -> Diff -> Delta -> Apply. Both reverse loops forbidden: (a) direct -- Apply never emits Diff/Delta; (b) indirect -- applying a delta on client fires game events that an intent-capture seam could catch and send echo-Intent to host; intent capture suppressed inside Apply scope (`SyncApplyScope` mechanism) + IntentDedup as second line.

**L10. Journal = observational, never authoritative.** Journal(Intent, Delta, CRC) written AFTER the diff pipeline as derived artifact: requires no patches, does not participate in state computation, cannot diverge from the network (journals already-sent Delta). Provides replay, debugging, and regression records for free. May exist only in debug builds. FORBIDDEN to implement journaling as mutation interception (Native -> Hook -> Journal -> Network) -- that is the 250-patch architecture returning.

**L11. Mod parity is blocking.** Save-graph shape must match on all peers: identical mod set and configs. ParityManifest BLOCKS join on mismatch, does not merely log. Host executes logic; clients need only the identical container shape. Shipping mod code over the wire is excluded.

## 3. Spike (mandatory spec, days, in the OLD repo)

- Core assumption of the entire plan: save serializer is viable as a live object-graph serializer, and applied state preserves runtime invariants
- Primary risk (consensus-identified): save graph and runtime graph overlap only partially -- caches, subscribers, scheduler, lazy lookups, Unity views, back-references are NOT serialized
- Most dangerous scenario: CRC matches but game is actually corrupted (false-green)
- Known serializer fact: `GameUtl.GameComponent<SerializationComponent>().Serializer` (NOT `new Serializer(null)`) + `Timing.RunUntilComplete` pump, else silently empty graph (verified 2026-06-18)

Spike must prove:

1. Value-apply to live `GeoscapeState` (resources, progress, timers)
2. Structural-apply (create/destroy entity with live Unity view)
3. Runtime invariants after Apply: events and subscribers fire; UI reacts; scheduler/timers alive; cached dictionaries and lookup tables consistent; Unity views bound; back-references intact
4. Idempotency: `Apply(delta); Apply(delta)` = same result
5. Out-of-order: late seq after earlier one does not corrupt state
6. No dangling references after object deletion (high-probability Unity issue)

Spike outcome = fork decision:
- Live-apply works broadly -> new repo skeleton `Rail/ Intents/ Seams/ Views/ Tactical(quarantine)/`; old repo = untouched shipped mod + knowledge quarry
- Works only for part of the graph -> strangler in old repo, rail scoped to viable portion, no new repo created

## 4. Two-stage verification (both mandatory)

**Stage 1 -- differential sim harness** (fast gate, every agent step):
- Host + client (`SimCluster`/`InMemoryTransport`) run randomized command sequences (research/build/cancel/move/produce/trade/pause/resume/save...)
- After EVERY applied step: `CRC(host) == CRC(client)` + trace (seed, step, intent, delta, entity, field)
- Mismatch -> first diverged step visible immediately, bug reproducible by seed+step
- Journal (L10) carries the trace
- Seconds to run, automatic, gates every night-agent commit

**Stage 2 -- in-game gate** (slow gate, every subsystem):
- Migrated subsystem verified in live game BEFORE starting the next one (lesson: `no-blind-mega-builds`)

**"Done" for a subsystem** = harness green + in-game gate passed + legacy files of that subsystem physically deleted.

## 5. Process and retirement enforcement

Root cause of first attempt's failure: additive-first with no retirement. Enforcement is mechanical, not declarative:

- **CI dependency gate (hard):** forbidden namespaces/types (`Legacy.`, `Mirror`, `ReflectionCodec`, old channels). New code depending on them = red CI. File-count can be gamed by renaming; dependency ban cannot.
- **Legacy file count** = progress metric toward milestone (not a gate)
- **WIP limit = 1:** one subsystem in flight; next does not start until previous subsystem's files are deleted
- **Fire isolation:** shipped-mod bugfixes live in old repo, never pull work into building out legacy channels (the funnel that killed the first attempt)
- **Night-agent rules:** one subsystem = one commit with green Stage 1; red harness -> revert, do not proceed; `Tactical/` out of scope; strictly strangler -- legacy deleted only after rail replaces it and harness confirms

## 6. Migration order (ascending structural complexity)

1. Research -- almost no identity, first end-to-end
2. Wallet/Resources -- pure value-only
3. Manufacturing -- queue, minimal identity
4. Diplomacy -- mostly values
5. Personnel -- structural begins: soldiers, inventory, references
6. Aircraft -- more complex
7. GeoSites -- appearance/disappearance, fog, Unity views
8. Mission generation -- last

## 7. Quarry transfers (as-is, no rewriting)

- `Transport/` (7 files: Steam, DirectIP, STUN, invite)
- Lobby
- SurfaceSeq/IntentDedup
- All NRE workarounds
- Hook-point knowledge (which methods to patch)
- `docs/research/` in its entirety
- ParityManifest (upgrade to blocking)
- Graphify already indexes old repo as quarry

## 8. Calibrated expectations

**Eliminated:**
- Coverage bugs ("forgot a field") -- rail enumerates state generically
- Coupling bugs ("fixed X, broke Y") -- single writer, single Apply, two-primitive vocabulary
- Geoscape infra: 323 files -> rail at single-digit thousands of lines; src realistically 67k -> 40-50k LOC
- LOC is a side effect; goal = minimize breakage surface on change

**NOT eliminated:**
- Tactical bugs (quarantine)
- Presentation and live-panel wrangling
- Reflection fragility on game updates (narrowed to serialization boundary + three seams)
- Client freeze is a known non-free zone (clock overwrite moves `Timing.Now` -> scheduler raises its own events) -- sim-gating seam remains manual work
