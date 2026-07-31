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
   client Apply() → UI via subscriptions. **Tactical = SHARED BATTLE on the same rail** (quarantine
   RETIRED by developer decision 2026-07-31): all peers fight one mission; **NO ownership model** —
   any peer commands ANY soldier, two peers may hold the same one, whoever CLICKS commands it;
   during the player team's turn ALL peers act SIMULTANEOUSLY (6 soldiers = 6 parallel commanders,
   NOT turn-passing); first-to-act-wins, same rule as the geoscape. Parallelism is native-safe:
   `ExecutingAbilities` is per-actor with no global lock (`TacticalActorBase.cs:54`) and
   `TacticalLevelController.CheckForFallAbilitiesToActivate:1917-1936` already awaits N concurrent
   activations. Host decides rolls/damage/death/status/TU-AP/reactions/objectives/mission-end; peers
   apply the host's `DamageResult` verbatim through `TacticalActorBase.ApplyDamage`
   (`TacticalActorBase.cs:950`) and run native `Activate` for PRESENTATION ONLY, damage-neutered
   (same posture as `IntentRail.ShouldRunNative()`, `src/Rail/IntentRail.cs:78-100`). ONE generic op:
   `TacticalAbility.Activate(object parameter = null)` (`TacticalAbility.cs:1078`) +
   `TacticalAbilityTarget` for move/shoot/grenade/heal/overwatch/melee/jetjump — never v1's 36
   bespoke surfaces. Conflict = PURE validator shaped like `EventSync.Validate`
   (`src/Rail/EventSync.cs:63-72`): host executes in arrival order, first spends TU/AP, second
   re-validates against post-first state and fails → `IntentRail.Reject` + nudge. The TU/AP check IS
   the arbiter — NO ownership table, NO rewind engine (local play was view-only; rollback = the
   authoritative delta); `0x40/0x41 PermissionUpdate/SoldierAssignment` stay dead tombstones.
   Tactical surfaces live in 0x80-0x9F ONLY (`src/Rail/SurfaceIds.cs:5`), NEVER 0xA0-0xBF (law L62).
   Local-only, never relayed: idle animation, cover-hug on arrival, camera, selection highlight,
   hover/preview aiming, per-frame pose. Entry = native save-transfer (law 1, zero surfaces); exit =
   host's native `GameOver` → native teardown on all peers → one authoritative outcome. Shipped:
   A1 `7808c7f`, A2 `285411d`+`90dc585` (0x80 TacTurn / 0x81 TacTurnIntent, laws L63+L64); A3a
   (0x82 TacCommand / 0x83 TacCommandIntent, law L65) = the generic per-soldier COMMAND seam with
   MOVEMENT as its only rider: capture is a PREFIX on `TacticalAbility.Activate` (base method — every
   rider override calls through it), the payload is an EXPLICIT declared `TacticalAbilityTarget` field
   set (`TacAbilityTargetCodec`, never reflection — the type holds live refs), the actor key is
   `TacticalActorBase.GeoUnitId` (serialized in `TacActorBaseInstanceData`, so reload- AND
   save-transfer-stable), the acting peer plays its own click SPECULATIVELY and the host's `settle`
   (final pos + AP + WP, shipped at `ClearPlayingAction`) is the authority. **A3b** (law L66) = attacks and
   their outcomes: shoot/bash join the A3a rider set with the attack fields on the same codec (no new intent
   surface), and **0x84 TacResult** carries the host's resolved `DamageResult` per receiver — addressed
   `(actorKey, IDamageReceiver.GetSlotName())`, "" = the actor. NO PEER RECOMPUTES DAMAGE: the client neuter
   is at `DamageAccumulation.ApplyAddedDamage:550` (+ belts on `TacticalActorBase.ApplyDamage` /
   `ItemSlot.ApplyDamage` for DoT ticks), and the mirror re-runs the game's own `ApplyDamage` inside a
   `MirrorApplyScope` that stands every FOREIGN `ref DamageResult` patch down (enumerated from Harmony, not
   a TFTV class list; late-bound via `TftvLateBinder`). Post-hit hp/ap/wp are overwritten from the host's
   snapshot and any non-zero correction is logged as a double-apply. Aliens are keyed by a DERIVED battle key
   (negative ordinal over battle-start position, built once at the first turn edge) — there is NO serialized
   per-actor identity, and a synthetic `GeoUnitId` is refused (`GeoMission:788-795` errors on it). **A4** (law
   L67) = ACTOR LIFECYCLE, and it takes NO new surface: spawn(3) + death(4) join **0x84**, because a spawn and a
   death are the same discrete-event shape (so they inherit its gap check + resnapshot) and because one seq
   stream is the only thing that stops a hit from overtaking the spawn of the actor it names. A mid-battle
   spawn's key is **HOST-ASSIGNED** (`TacticalActorKey.AssignHostKey`, one counter shared with the battle-start
   ordinals) and adopted verbatim — a derived key cannot name an actor that was in no shared snapshot. The
   client's own spawn RNG is gated at `TacParticipantSpawn.DeployForTurn` (reinforcements; TFTV reaches it from
   a POSTFIX on `RequestEndTurn`, which runs even when our end-turn prefix skipped the original) and, as a
   universal backstop, at `ActorComponent.DoEnterPlay` — CONTAINED (never enters play), never destroyed. Death
   is forced through the game's own trigger (`Health.Set(0)` → `OnHealthChange:616-622` → `Die`) by both the
   damage applier and the resnapshot, replacing A3b's log-only split; the corpse manifest is pre-rolled on the
   host at the `Die` prefix and rides WITH the killing hit (the mirror's death runs inside that same
   `ApplyDamage`). **Evacuation is an ordinary A3a rider** — every peer runs the native
   `ExitMissionAbility.HideActorInExitZone` hide (EvacuatedStatus + UnapplyAll + MountedStatus); nothing in this
   arc destroys an actor, and RailCheck asserts that mechanically (v1's `d41b8f8` destroy = empty
   BattleSummary + per-frame NREs + dead evac button). **A5** (law L68) = ENEMY / AI ACTION REPLICATION,
   and it takes **no new surface and no new op**: an enemy action IS the `TacticalAbility.Activate` A3a
   already mirrors — all twelve `PhoenixPoint.Tactical.AI.Actions` classes reach
   `TacticalAbility.ExecuteAndWait:1168` (three lines over `Activate:1078`), AI movement is the same
   `MoveAbility` a player click uses, and nothing under `Tactical.AI.Actions` mutates the model directly —
   so the HOST simply mirrors EVERY faction instead of only the player's. The AI stays host-only because
   its DECISION draws from `UnityEngine.Random` before anything activates (`AIFaction.SelectTarget:395`),
   so a re-deriving peer picks a different TARGET, not merely a different roll; `ClientAiGate` still holds
   the client's AI turn and `RelayDecision` is the runtime detector for the day it stops. Two consequences:
   the rider WHITELIST became a declared DROP list (`TacticalCommandSync.LocalAbilities` — the AI executes
   data-configured ability defs, which no whitelist can enumerate; five of the seven entries are exactly
   the classes `TacticalLevelController.AbilityExecuted:1183` calls ambient), and an **autonomous**
   activation — `TacticalAbilityTarget.AttackType != Regular`, i.e. the engine's own
   Overwatch/ReturnFire/ZoneControl/Synced set — **never crosses in either direction**, because every peer
   raises its own off the same replicated board and a mirror would be a SECOND overwatch shot (the exact
   hazard that appears the moment enemies move on a client). A5 also closed A4's two ceilings: a spawn with
   a runtime-generated `ComponentSetDef` is a NAMED refusal registered against its key
   (`TacticalActorKey.Refuse`) instead of a misleading "a spawn record never arrived", and the 0x84
   resnapshot carries the host's archived corpse manifest. **A6** (law L69) = INVENTORY/LOOT + DESTRUCTIBLES,
   and it takes **no new surface**: ops 5/6 on **0x84** plus op 3 on the 0x83 intent family; 0x85 stays free.
   Inventory commits as a WHOLE BATCH because the game's does — every drag only stages an `InventoryQuery`
   (`InventoryQuery.AddItem`:26-33 edits a private list), and the one model commit is
   `InventoryQuery.SyncItems`:44-67, whose single caller in the assembly is
   `UIStateInventory.ApplyInventoryActions`:898-903. Captured in a PREFIX (the staged `Items` already hold
   what the native body will write, so it is a capture and not a result-ship, law 19) gated on the game's own
   `WillModifyInventory`. AP is charged ONCE at the native point and never eagerly (v1 `6617846` deducted per
   gesture, so `CanPayForTransfer`→`ActionPointRequirementSatisfied` then denied every further drag and the
   screen locked): this arc only POSTFIX-OBSERVES `InventoryAbility.ApplyCosts()`, and the sole place it
   charges is the host applying a client's already-closed session. Containers are addressed
   `(actorKey, Inventory|Equipments)` — `ItemContainer` IS a `TacticalActorBase`, so crates and corpses are
   keyed by the battle-start ordinal; items ride as ordered def guids and the host CHECKS the multiset is
   preserved, which is what stops an edited layout minting equipment. v1's `TacCrateOpen` and `TacItemDestroy`
   are FOLDED, not ported (`OpenCrateAbility` is an ordinary A3a rider; a consumed item simply leaves every
   container in the batch), and `InventoryAbility` became a DECLARED LOCAL — its `Activate`:11-15 ends in
   `ToInventoryViewState()`, so under A5's drop-list inversion it was yanking every peer's screen into an
   inventory nobody there opened. Destructibles are keyed by `DestructableBase.GuidInScene`, the game's OWN
   save key, but resolved through an index built the way `TacLevelSavegame`:49 enumerates them
   (`Map.NavigableRoot` + `GetComponentsInChildrenStable`) — NEVER `SceneObjectIdsComponent.GetForScene`,
   which is v1's mission-wide-dead lookup (`fc661b7`: it needs an ACTIVE tagged GameObject in exactly the
   scene asked for, and `MapPlot`:230-243 reparents, merges and DESTROYS those registries) and is now
   mechanically banned from the arc. A hit is addressed by its receiver's AIM POINT, which sits at the tile
   centre and round-trips through `GetDamageReceiverForHit`'s own inverse — proved numerically, not assumed —
   because one explosion damages many tiles that all share a single `ImpactHit.Point`. FALLS ARE DERIVED:
   `CheckForFallAbilitiesToActivate`:1916-1935 runs per-peer off each peer's `OnMapUpdate`, so
   `FallNoSupportAbility` stays a declared local by A5's autonomy rule. KNOWN A6 CEILING: dropping onto BARE
   ground spawns a fresh `ItemContainer` that A4 does not replicate, so such a batch ships marked PARTIAL —
   the rest crosses and the dropped item stays in that soldier's pack elsewhere, loudly.
   Shared-seed determinism = still a separate future project.
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
    non-vacuous (L9), element ORDER survives the wire — blob order round-trip, live-instance reuse,
    key-order in-place reorder (L10), no `LocalizedTextBind` field/element rides covered — static
    belt for the runtime DefOwnership law, which itself needs a live `DefRepository` and is
    harness-invisible (L11), intent dedup idempotence / peer+surface keying / bounded ring / rejoin
    reset + the four families' [nonce][op] envelope round-trip (L12), field-codec
    CRC(host)==CRC(client) — re-encode after a real apply reproduces the host's exact bytes, hashed
    with the save-transfer `Crc32` (L13). Honest gaps: `IntentRail`'s nonce allocator, dispatch and
    reject-reconverge + the family BODY codecs need a live `NetworkEngine` (in-game gate); the
    live-tree differential CRC needs a `GeoLevelController`; DefRef round-trip stays deliberately
    un-faked (encode = a single Guid write — tautology; decode needs a live `DefRepository` —
    ARCHITECTURE.md §Verification).
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
