> ## PRIME DIRECTIVE — read before any work (universal-first)
> This rewrite exists to replace ALL per-subsystem hand-sync with ONE universal mechanism that mirrors every game-state value host→client by default; we then OPT OUT (exclude) what we don't want. When something looks broken in co-op (manufacturing, inventory, salvage, sites, vehicles…), the fix is almost always a CROSS-CUTTING GENERIC gap — stable-keying for un-keyed collection elements, structural create/destroy, or wiring one of the small generic seams — which unlocks many subsystems at once. NEVER propose a per-subsystem hand-sync: that is the exact pattern that killed the old repo (MANDATE S5, "additive-first, no retirement"). The "migration order" (Research → Wallet → Manufacturing → …) is NOT re-implementing sync per subsystem; it is the order in which to WIRE the small fixed catalog of generic seams (intents / order-channel / structural appliers / sim-gates / UI-repaint) and DELETE the legacy code for that area. Default lens for every task: "which generic gap is this?" — not "which subsystem do I patch?".

# Multiplayer2 — PhoenixPoint co-op mod (rail rewrite)

Ground-up rewrite of the co-op campaign mod. Mod name stays **Multiplayer** (same meta.json id,
same DLL name, same target mod folder — deploying this mod REPLACES the old one in the game).
Old repo `E:\DEV\PhoenixPoint\Multiplayer` = **quarry**: reference for seam knowledge (what to
hook, NRE workarounds, game behavior). Read it, never copy legacy sync layers from it
(SyncEngine, channels/codecs, src/Harmony/Sync, Multiplayer.Core/Sync are OFF-LIMITS for porting).

Binding sources: `docs/MANDATE-v2.md` (developer mandate, verbatim) reconciled with recon facts
in `ARCHITECTURE.md` (read before any code). Paradigm: replicate STATE (generalized diff), not
BEHAVIOR (per-subsystem mirroring).

## Laws (every agent, every change)

1. **Wire = Intent + Delta, nothing else.** Intent: client → host ("I want X"). Delta: host → all
   ("state changed so"). Join/reconnect = native save-loader transfer (proven), NOT a third
   message kind and NOT a full snapshot pushed through the delta path.
2. **Hybrid addressing — deliberate, don't collapse it.** Mutation target = stable GAME id:
   Delta = (entityId, field, value); entityId = `GeoSite.SiteId` / `GeoTacUnitId` /
   `GeoVehicle.VehicleID` / def GUIDs. NEVER serializer ObjectIDs (session-local), NEVER list
   indices. Stable-keyed path prefixes (`geo/research/byId/<GUID>/progress`) = the scheme for UI
   subscriptions and CRC subtrees. IDs decide the mutation; path prefixes decide subscription.
3. **Client = projector + emitter.** Client never executes game logic, never mutates authoritative
   state outside the single delta `Apply()`. Apply = universal value-appliers + explicit small set
   of structural appliers. Identity boundary: value-mutations never create identity; structural
   ones may (site/soldier/vehicle create-destroy — live Unity views, referential integrity).
   Structural layer exists from day one — never "generalize later". Never replace a
   MonoBehaviour-bound instance (GeoSite, GeoVehicle) — patch fields only.
4. **Harmony patches live on 3 seams only:** (a) intent-capture, (b) sim-gating, (c) presentation.
   A patch that transfers or mirrors state is forbidden — the rail does that.
5. **Layer split.** Geoscape rides the rail: host runs native logic → canonical diff → Delta →
   client Apply() → UI via subscriptions. Tactical = quarantine (`src/Tactical/`): current hybrid
   stays (presentation local to actor, outcomes host-computed); shared-seed determinism = separate
   future project. The rail does not fix tactical bugs — expectations fixed.
6. **Canonical diff.** Same state → byte-identical Delta: traversal sorted by stable IDs, fixed
   field order, no nondeterministic dictionary walks. IMPLEMENTATION (recon-bound): diff walks the
   LIVE game graph guided by the save serializer's type metadata (its field discovery = our
   enumerability; TFTV fields ride free). NEVER diff serializer blobs — ObjectIDs are
   session-local, blob format nondeterministic (`SerializationWriter._object2ID`). Serializer
   blobs are used ONLY as payloads for structural creates.
7. **Delivery contract.** Single writer (host), one ordered seq stream. Apply is idempotent
   (redelivery = same result); out-of-order safe (late seq after newer one breaks nothing);
   resync-on-gap (gap → request subtree snapshot). CRC per path-subtree as drift backstop —
   divergence resends only the diverged subtree. Commutativity NOT required (single-writer; no
   CRDT). SurfaceSeq/IntentDedup quarried as-is.
8. **One-way flow, both echo loops closed.** Cycle: Intent → Host → Diff → Delta → Apply. Apply
   never emits Diff/Delta (direct loop). Applying a delta may fire native game events that an
   intent-capture seam could catch and echo to host (indirect loop) — intent capture is suppressed
   inside Apply scope (`SyncApplyScope`), IntentDedup = second line. (Law binds from the first
   intent seam onward; no intent seams exist yet.)
9. **Journal = observational, never authoritative.** Journal(Intent, Delta, CRC) written AFTER the
   diff pipeline as derived artifact: no patches, no part in state computation, may be
   debug-build-only. FORBIDDEN to implement journaling as mutation interception
   (Native→Hook→Journal→Network) — that is the 250-patch architecture returning.
10. **Mod parity is blocking.** Save-graph shape must match on all peers (same mods + configs).
    ParityManifest BLOCKS join on mismatch, not logs. Host executes logic; clients only need the
    identical container shape. Shipping mod code over the wire is excluded.
11. **Reactivity is law #1 (user mandate).** A delta arriving while ANY UI is open repaints that UI
    instantly — fire the native event the view already listens to (GeoscapeView is push-model).
    Never lazy-refresh on next open. Multi-client: everyone sees each other's effects immediately.
12. **Minimal code.** No speculative abstraction, no config for constants, no defensive checks
    without a known failure they guard. Old repo died of boilerplate — don't rebuild it.

## Verification — two stages, both mandatory (supersedes old "no test suites" mandate for THIS repo)

- **Stage 1 — `cd tools/RailCheck && dotnet run -c Debug`** (fast gate, EVERY commit that touches
  `src/Rail/`). Exit 0 = green, 1 = red. Seconds, headless, no game and no save needed.
  Red harness → revert, do not proceed. "It compiles" is NOT the gate.
  - Asserts the rail's own laws on the real game metadata: every list-classed field has an
    `ApplyList` strategy (L1), no unmatched custom-create param (L2), no Unity object in the blob
    codec (L3), codec round-trip (L4), no abstract element type riding unclassified (L5), every real
    blob-reconstructed element type survives encode→decode (L6), the dict tombstone stays undecodable
    as a value (L7), `SurfaceSeq` honours the law-7 delivery contract (L8), `GeoItemDict` coverage is
    non-vacuous (L9).
  - `docs/rail-baseline.txt` is the committed classifier snapshot (table + per-type blob husk lists
    + today's known violations). **Any drift in it is RED** — that is the whole point: a field moving
    Excluded↔covered must be a reviewable diff, never a silent side effect. Change is intended →
    `dotnet run -c Debug -- --update` and commit the baseline IN THE SAME COMMIT as the rail change,
    so review sees the coverage delta next to the code that caused it.
  - It is NOT the differential SIM harness the mandate originally described: no CRC(host)==CRC(client),
    no seeded command sequences, no live `GeoLevelController`. Read `ARCHITECTURE.md` §Verification
    for the full uncovered list before treating green as safe.
- **Stage 2 — in-game gate** (slow gate, every subsystem): migrated subsystem verified in live
  2-instance game BEFORE the next one starts.
- **"Done" for a subsystem** = harness green + in-game gate passed + legacy counterpart NOT ported
  (stays in the quarry).

## Process

- **WIP limit = 1**: one subsystem in flight; next starts only when previous is done (above).
- **Dependency gate**: referencing legacy namespaces/types (SyncEngine, *Channel, *Codec, *Mirror,
  *Reflection sync helpers) = red build. Enforced by build script check, not convention.
- **Fire isolation**: shipped-mod bugfixes live in the OLD repo; they never pull work into legacy
  channels here.
- Commit-on-green to inner `main`, conventional commits, NO feature branches, NO push during dev.
  Deploy via `deploy.ps1` after every green commit. Stage explicit files only — NEVER `git add -A`.
- Migration order (ascending structural complexity): Research → Wallet/Resources → Manufacturing →
  Diplomacy → Personnel → Aircraft → GeoSites → Mission generation.
- Serializer facts: `GameUtl.GameComponent<SerializationComponent>().Serializer` (never
  `new Serializer(null)`) + `Timing.RunUntilComplete` pump, else silently empty graph. Network
  callbacks have no `Timing.Current` — defer applies onto the game loop.
- Docs = English, compressed bullets, exact names/values.
