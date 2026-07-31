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

**L5. Layer split.** Geoscape rides the diff rail: host runs native logic -> Serialize(current) in memory -> Diff(previous, current) -> Delta -> clients apply via Apply() -> UI via path-prefix subscriptions. Tactical is NOT quarantine -- quarantine RETIRED by developer decision 2026-07-31 (see §9). Tactical = SHARED BATTLE on the same rail: all peers fight the same mission; there is NO ownership model at all -- any peer may select and command ANY soldier, two peers may hold the same soldier, and whoever CLICKS commands it at that moment (first-to-act-wins, the same rule as the geoscape). During the player team's turn ALL peers act SIMULTANEOUSLY -- 6 soldiers means 6 players each moving a different soldier at the same time; genuine parallel commanding, NOT turn-passing. The engine carries the real substance: aiming, shots, statuses, inventory, animations. Purely local/cosmetic presentation stays local and is NEVER relayed: idle animation, hugging cover on arrival, camera, selection highlight, hover/preview aiming, per-frame pose. Shared-seed determinism remains a separate future project after RNG recon (`docs/research/02-rng-analysis.md`).

- *Parallelism is settled, not a bet:* `TacticalActorBase.cs:54` `public readonly List<TacticalAbility> ExecutingAbilities` is per-actor with no global lock; `TacticalLevelController.CheckForFallAbilitiesToActivate:1917-1936` already activates N abilities on N different actors in a loop and awaits them all concurrently; `TacticalView.IsWaitingForActiveAbilitiesAndMapUpdate:864` scans a SET of actors, not a slot. Runtime confirmation: 5 soldiers observed pathfinding across a map SIMULTANEOUSLY in v1 (a deployment bug made them walk to their start points instead of teleporting).
- *Constraints (real, not fatal):* reaction-class abilities hold level-global single slots (`TacticalLevelController.cs:257-261` `_panicUpdateable`/`_aiEvaluationUpdateable`/`_hurtReactionUpdateable`); reactions self-serialize behind all executing abilities (`TacticalHurtReactionAbility.cs:68` -> `WaitWhileExecutingAbilitiesCrt(this, ignoreReactions:true)`, `:1903-1910`), so simultaneous movers DELAY reaction fire but never corrupt it; `TacticalView._viewUnlockTime`/`WaitForAbilities` (`TacticalView.cs:833-844`) is a per-peer VIEW lock, not a model lock. Known gap: overwatch resolves in host-arrival order, not truly simultaneously.
- *Authority:* host decides rolls, damage, death, status, TU/AP spend, reaction fire, objectives, mission end. Seam = `TacticalActorBase.ApplyDamage(DamageResult)` (`TacticalActorBase.cs:950`), a funnel taking a PRE-COMPUTED result; peers apply the host's `DamageResult` verbatim and never re-roll. The acting peer runs the native `Activate` for PRESENTATION ONLY, damage-neutered. Needs no new principle: `IntentRail.ShouldRunNative()` (`src/Rail/IntentRail.cs:78-100`) already blocks model mutation on the client while presentation-only work may proceed.
- *ONE generic op, not v1's sprawl:* `TacticalAbility.Activate(object parameter = null)` (`TacticalAbility.cs:1078`) with `TacticalAbilityTarget` (`TacticalAbilityTarget.cs:10-50`) is the single funnel for move/shoot/grenade/heal/overwatch/melee/jetjump. v1 died of 36 bespoke tactical surfaces (22 sync + 38 Harmony files; overwatch alone cost 2 surfaces + 2 codecs).
- *Conflict rule -- no ownership table, ever:* a PURE validator in the shape of `EventSync.Validate` (`src/Rail/EventSync.cs:63-72`) -- same soldier, same instant -> host executes in arrival order, the first spends TU/AP, the second re-validates against post-first state and fails. THE TU/AP CHECK ITSELF IS THE ARBITER. Loser gets `IntentRail.Reject` + nudge; since his local play was view-only, rollback is just the authoritative state delta -- NO REWIND ENGINE. v1's in-memory arbiter died on reload and must not be rebuilt; `PacketType 0x40/0x41 PermissionUpdate/SoldierAssignment` stay dead tombstones forever.
- *Surface band:* tactical owns 0x80-0x9F (`src/Rail/SurfaceIds.cs:5`); tactical ids must NEVER fall in 0xA0-0xBF (geoscape, occupied through 0xB7). Enforced by law L62. v1 RCA 3ff508d: tactical ids at 0xA0-0xA3 silently ate every geoscape envelope for days.
- *Entry/exit:* entry rides the native save-transfer path (L6's native save loader) -- NO deploy-snapshot surface; A1 needed zero surfaces. Exit: host's native `GameOver` -> all peers run the native teardown -> one authoritative outcome; geoscape consequences are ordinary save-graph state on the existing value rail, needing no tactical message.
- *Status 2026-07-31:* A1 + A2 + A3a landed (surfaces 0x80 TacTurn, 0x81 TacTurnIntent, 0x82 TacCommand, 0x83 TacCommandIntent). A3a = THE generic per-soldier command seam (one op: `activate(actorKey, abilityDefGuid, TacticalAbilityTarget)`), movement as its first and only rider; arbitration is the PURE `TacticalCommandSync.Validate` (no ownership table, no in-memory ledger) and the host's `settle` closer carries the one thing no peer can reproduce -- move's AP, charged once at end of traversal against an interrupt-dependent distance. A3b landed (surface 0x84 TacResult, law L66): the host's resolved `DamageResult` per receiver, no peer recomputes damage. A4 landed (law L67, NO new surface -- ops 3/4 on 0x84): actor lifecycle -- mid-battle spawns get a HOST-ASSIGNED key shipped with the spawn event (a derived key cannot name an actor that was in no shared snapshot), the client's spawn RNG is gated at `TacParticipantSpawn.DeployForTurn` and contained at `ActorComponent.DoEnterPlay`, death is forced through the game's own `Health.Set(0)` trigger by both the damage applier and the resnapshot, the corpse's contents are the host's pre-rolled manifest riding the killing hit, and evacuation is an ordinary A3a rider so every peer runs the native HIDE -- never a destroy. A5 landed (law L68, NO new surface and no new op): ENEMY / AI ACTION REPLICATION is the SAME 0x82 activate mirror, because an enemy action is the same `TacticalAbility.Activate` an order is -- all twelve `PhoenixPoint.Tactical.AI.Actions` classes reach `TacticalAbility.ExecuteAndWait`:1168 which is three lines over `Activate`:1078, AI movement is the same `MoveAbility` a player click uses, and there is NO bypass (nothing under `Tactical.AI.Actions` touches Navigate/SetTransform/ApplyDamage/SpawnActor directly). So the host mirrors EVERY faction rather than only the player's; the AI itself stays host-only because its DECISION consumes the global generator before any ability activates (`AIFaction.SelectTarget`:395) so a re-deriving peer picks a different TARGET, not merely a different roll. Two consequences: the rider whitelist became a declared DROP list (the AI executes data-configured ability defs, which no whitelist can enumerate), and an AUTONOMOUS activation -- `AttackType != Regular`, i.e. the engine's own Overwatch/ReturnFire/ZoneControl/Synced set -- never crosses in EITHER direction, since every peer raises its own overwatch off the same replicated board and a mirrored copy would fire it twice. A5 also closed A4's two ceilings: an unrebuildable spawn (runtime-generated `ComponentSetDef`) is now a NAMED refusal registered against its key instead of a misleading "a spawn record never arrived", and the resnapshot carries the host's archived corpse manifest. **A6 landed** (law L69, NO new surface -- ops 5/6 on 0x84 + op 3 on the 0x83 intent family; 0x85 still free): INVENTORY/LOOT and DESTRUCTIBLES. Inventory commits as a WHOLE BATCH because the game's own commit is one -- every drag only stages an `InventoryQuery`, and the single model commit is `InventoryQuery.SyncItems`:44-67, whose only caller in the assembly is `UIStateInventory.ApplyInventoryActions`:898-903. So there is no per-slot funnel to prefer, and the architect's whole-list prescription is confirmed by the engine rather than by analogy with `GeoEquipIntent setItems=8`. AP is charged EXACTLY ONCE at the native point (`ExitState`:436-444 -> `ApplyCostsCommand` -> `InventoryAbility.ApplyCosts`) and NEVER eagerly -- v1's per-gesture deduction (`6617846`) tripped the native `ActionPointRequirementSatisfied` gate and froze the screen -- so this arc only POSTFIX-OBSERVES the charge, and the one place it charges is the host applying a client's already-closed session. Loot stays A4's host roll and is never re-rolled (TFTV's `TFTVEconomyExploitsFixes.cs:130` override of `ShouldDestroyItem` runs host-only under that gate). v1's `TacCrateOpen` and `TacItemDestroy` are FOLDED into existing seams, not ported. `InventoryAbility` became a DECLARED LOCAL -- a real bug A5's drop-list inversion had introduced: its `Activate`:11-15 ends in `ToInventoryViewState()`, so the mirrored order was yanking every peer's screen into an inventory nobody there opened. Destructibles keep the game's own save key `DestructableBase.GuidInScene` but resolve through an index built the way `TacLevelSavegame`:49 enumerates them (`Map.NavigableRoot` + `GetComponentsInChildrenStable`); v1's `SceneObjectIdsComponent.GetForScene` lookup -- dead MISSION-WIDE (`fc661b7`) because it needs an ACTIVE tagged GameObject in exactly the scene asked for while `MapPlot`:230-243 reparents, merges and DESTROYS those per-scene registries -- is now mechanically BANNED from the arc. A hit is addressed by its receiver's AIM POINT (tile centre, round-trip proved numerically against the engine's own grid arithmetic) because one explosion damages many tiles sharing a single `ImpactHit.Point`. Falls stay DERIVED per peer under A5's autonomy rule. Known ceiling: dropping onto BARE ground spawns an `ItemContainer` A4 does not replicate, so such a batch ships marked PARTIAL. Detail in `../ARCHITECTURE.md` §Migration order.

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
- Tactical ordering gaps: overwatch resolves in host-arrival order, not truly simultaneously (L5 amended 2026-07-31 -- tactical is no longer quarantined)
- Presentation and live-panel wrangling
- Reflection fragility on game updates (narrowed to serialization boundary + three seams)
- Client freeze is a known non-free zone (clock overwrite moves `Timing.Now` -> scheduler raises its own events) -- sim-gating seam remains manual work

## 9. Amendments (post-receipt, dated)

- **2026-07-31 -- L5 rewritten** (developer decision, authoritative): tactical quarantine RETIRED. Tactical = shared battle on the rail, no ownership model, all peers command simultaneously, host-authoritative outcomes via `TacticalActorBase.ApplyDamage(DamageResult)`, one generic op `TacticalAbility.Activate`, TU/AP validator as conflict arbiter, surfaces 0x80-0x9F. §8 "NOT eliminated" bullet updated to match. Left as historical record (NOT amended): §3 fork-outcome skeleton wording `Tactical(quarantine)`, §5 night-agent rule "`Tactical/` out of scope".
