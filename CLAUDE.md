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
   re-validates against post-first state and fails → `IntentRail.Reject` + nudge. BUT BUSY IS NOT A
   CONFLICT WHEN THE HOST IS BUSY WITH THAT SAME PEER'S OWN ORDER (`4293f53`): the acting peer plays its
   click SPECULATIVELY, so it finishes an order while the host is still ANIMATING it, and its follow-up
   for that soldier lands on a host legitimately busy ON THAT PEER'S BEHALF — melee always, because a
   melee order is the one that arrives during the move it followed (`MoveAbility
   .TryToExecuteFollowupAbility`:146-156 fires it the instant the walk ends). Such an order is HELD and
   RE-DISPATCHED through `HandleActivate` itself, oldest first, only onto a free soldier (so arrival
   order survives, law 7), with a 10 s ceiling after which it is refused OUT LOUD — never refused on
   arrival, and never activated immediately either (`BashAbility.Activate`:190 → `TacticalAbility
   .PlayAction`:998 passes `cancelCurrent:true`, which would cancel the host's in-flight move mid-walk
   and settle every peer onto a position the order never reached). Ownership of the running order is
   `TacticalCommandSync._cmdOwner[actorKey] = _replayOriginPeer`, written at the one point every
   accepted order passes (`OnAbilityActivated`'s host branch; 0 = the host's own click), and busy from a
   DIFFERENT peer still falls through to first-to-act-wins — `Validate` is unchanged and still pure
   (RailCheck L65 arbiter-double-commands green). The TU/AP check IS
   the arbiter — NO ownership table, NO rewind engine (local play was view-only; rollback = the
   authoritative delta); `0x40/0x41 PermissionUpdate/SoldierAssignment` stay dead tombstones.
   Tactical surfaces live in 0x80-0x9F ONLY (`src/Rail/SurfaceIds.cs:5`), NEVER 0xA0-0xBF (law L62).
   Local-only, never relayed: idle animation, cover-hug on arrival, camera, selection highlight,
   hover/preview aiming, per-frame pose — with ONE carve-out, **A8b** (law L97): the COMMITTED manual-aim
   POSE, and only the pose.
   - **What crosses:** the discrete pair `(actorKey, targetKey)` on its own surface pair **0x87 TacAimPose /
     0x88 TacAimPoseIntent** (`src/Tactical/TacticalAimPoseSync.cs`), NOT an op on 0x82 — 0x82 carries
     ORDERS (discrete, each played exactly once) while this is a STANDING VALUE whose whole correctness
     property is that the latest wins, so a re-assert on the order stream would read as a second order.
   - **Why it is not cosmetics:** entering manual aim leaves the soldier in the aim loop, which sets
     `TravelType.Aim` on the animator; `TacticalActor.CurrentlyAiming`:228-238 reads exactly those integers,
     and `TacticalLevelController.FireWeaponAtTargetCrt`:1645 SKIPS the whole aim-start block (`:1647-1678`,
     a checkpoint wait with a 5 s ceiling) when it is true — so the acting peer fires from a pose it already
     holds while every mirror first plays the entry-into-aim. A built-in desync on EVERY shot.
   - **REMOVED BY DEVELOPER DECISION 2026-08-04, and it stays removed:** the third-person aim camera, the
     aim UI, the auto-enter/auto-leave of a watcher's aim view state, and the TAB-target mirror. A watcher
     keeps its own free camera and its own screen; an arriving message yanks nobody. Surfaces **0x85 TacAim /
     0x86 TacAimIntent**, the `TacticalAimSync` family and **RailCheck L82** went with that half — permanent
     tombstones, never re-mint those two ids. L97 arm `camera-ui-half-returned` is what keeps it gone, and it
     scans STRING LITERALS as well as call edges, because the deleted code reached the view stack through
     `AccessTools.Method(…, "ActivateAttackAbilityState")` and a callee-only walk would see nothing.
   - **How the mirror poses:** it writes the animator integers `CurrentlyAiming` reads, through the game's
     OWN nav-free entry `PathProcessorUtils.SetAimParams`:81-84 / `SetNullNavParams`:91-94 →
     `SetParams`:67-74 (whose entire body is a loop of `animator.SetInteger`), after the clip bind
     `TacActorAnimActions.ActivateShootingClips` that `FireWeaponAtTargetCrt`:1566 does one line earlier.
     Facing rides as a LERP at the game's own 6f rate, never its endpoint (`facing-snaps` if `SetForward` is
     written instead). The POSE never crosses (law 5): cover geometry stays local on every peer.
   - **`IdleAbility` and every nav-reaching path are FORBIDDEN**, and the ban is MECHANICAL: L97 walks the
     transitive IL closure of every game method the mirror half calls — rooted on all THREE entries above,
     since a closure over fewer is a proof about the wrong set — and turns red on `IdleAbility` /
     `TacticalNavigationComponent` / `ExecutePoints` / `GetAimOrPeekPathPoints` /
     `NavigateAndWaitUntilFinished`. That ban is the first attempt's grave: `3071859` (ops 5/6 on 0x82/0x83,
     reverted by `0252247`, both ops dead tombstones) fed a `CoverPose` to `IdleAbility.ForceRefresh`, whose
     path ends in `TacticalNav.ExecutePoints` — a NAV TRAVERSAL, so a relayed aim MOVED mirrored soldiers
     instead of posing them (a jetpacked one re-flew, and while it ran the actor reported
     `HasExecutingAbility()` so its settle was held to the 10 s ceiling).
   - **Dedup is by SAMPLING, not edges:** a postfix reads the live top view state once per frame and emits
     only when it disagrees with the shared table, so a transition's intermediate values never exist as a
     sample. `3071859` captured edges and failed BOTH ways at once: 2-3 messages per repaint, because
     `UIStateShoot` genuinely alternates target→null→target across `ExitState`:1261 and `SetShootTarget`:277,
     AND swallowed changes a third peer then never learned.
   - **Arbitration = shared LAST-WRITER-WINS per soldier, no ownership table.** The host holds the one
     `actorKey→targetKey` table and is its only writer; a client announces on 0x88 and the host echoes to ALL
     peers on 0x87, "last writer" = last to reach the host (the rail's standing arrival-order rule, law 7).
     A peer re-asserts when the shared value was CLEARED under it, and never contests a different non-zero
     target another peer wrote — that suppression is what stops an unbounded ping-pong between two peers
     aiming one soldier at two enemies. The decider is the pure `TacticalAimPoseSync.Decide`, which L97
     EXECUTES case by case (`arbiter-*`). The table is reset per battle (`pose-leaks-battle`): keys are
     re-derived per mission, so a carried-over table would pose the NEXT battle's actors from a dead one.
   - **Still forbidden, unchanged:** per-frame pose STREAMING, hover/preview aiming (`UIStateFreeCam` is
     excluded by an exact type test, since its crosshair re-targets as it sweeps), and any path that can
     reach a nav traversal on a mirror.
   Entry = native save-transfer (law 1, zero surfaces); exit =
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
   and it takes **no new surface**: ops 5/6 on **0x84** plus op 3 on the 0x83 intent family (0x85 was free then; it is a retired tombstone now).
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
   `FallNoSupportAbility` stays a declared local by A5's autonomy rule. A6's GROUND-DROP CEILING IS CLOSED
   (law L80): a pile of dropped items gets a THIRD container kind, addressed by the GAME'S OWN container
   identity `(ComponentSetDef guid, Pos)` — `TacticalItem.GetOrCreateItemContainer`:668-690 finds one by
   `Utl.Equals(actor.Pos, pos) && actor.ActorDef == def` and spawns it with that exact recipe when there is
   none, so the mirror re-enters the game's own find-or-create. No actor key could ever have named such a
   pile: it is created LOCALLY by whichever peer's screen dropped into it
   (`UIStateInventory.CreateGroundInventory`:513-522, which runs on EVERY inventory open and destroys the
   empty one again on close) or by a death every peer mirrors separately (`DieAbility.DropItems`:181), so
   A4's host-assigned key had nothing to assign and the client's screen needs the pile before any host round
   trip. An untouched EMPTY pile is never named (it would litter every peer with the container the screen
   makes and unmakes); an emptied one destroys ITSELF through `ItemContainer`:93-96, so nothing in this arc
   calls `DestroyActor` (RailCheck L67 asserts that mechanically). PARTIAL now means only a container that is
   neither an actor's own two nor a pile on the ground.
   **A7** (law L76) = THE SECOND TACTICAL FUNNEL + the two item riders, and it takes **no new surface**:
   op 3 on **0x82** / op 4 on **0x83**. Switching a soldier's weapon is NOT an ability, so the A3a prefix on
   `TacticalAbility.Activate` could never see it — the model funnel is `EquipmentComponent.SetSelectedEquipment`:242
   (raising the game's own `EquipmentChangedEvent`:266), clicked straight out of `UIStateCharacterSelected`:748/751,
   `UIStateShoot`:854/862 and `UIStateAbilitySelected`:725/736. Un-relayed it was NOT cosmetic: the host validates
   every order with the game's `GetDisabledState()`, whose `EquipmentNotSelected` arm
   (`TacticalAbility.GetDisabledStateInternal`:435 → `isEquipmentOfSelectedGroup`:481-499) tests the HOST's
   selection — so a client that switched locally had its next grenade or reload REJECTED ("Предмет не выбран",
   2026-07-31). The seam is a NON-BLOCKING prefix, deliberately: the same method is the game's own repair path
   (`EquipmentComponent`:56/102/118/272, `RagdollDieAbility`:162), so block-first would leave a client holding
   nothing the first time a weapon broke; the posture is A3a's speculative one and the host's echo is the
   authority. A BELT rides with it — the host replays `Activate`:1087-1090's own auto-select BEFORE asking
   `GetDisabledState`, because an arbiter must not refuse an order on a state the next native line rewrites.
   Also: `TacticalAbilityTarget.Equipment`/`TacticalItem` moved from `Dropped` to `Rides` (bits 1<<10 / 1<<11,
   keyed `(actorKey, Inventory|Equipments, defGuid)` — A6's container address reused) because
   `ReloadAbility.ChooseEquipmentAndAmmo`:111-114 reads both and `DropItemAbility`:36 dereferences `TacticalItem`
   with no null test at all. A settle now carries `[forced:u8]`: one sent because the host REFUSED an order is
   applied immediately rather than held behind the refused peer's stuck speculative ability, and the ordinary
   hold gained a 10 s ceiling — a correction that waits forever is a swallowed correction.
   A FORCED SETTLE MUST CANCEL THE ACTOR'S LOCAL NAVIGATION BEFORE IT WRITES THE HOST'S POSITION
   (`d061b0a`): skipping the hold means the refused peer's speculative ability is still NAVIGATING, and
   navigation wins — `TacticalNavigationComponent.UpdateActorTransformFromPathSample`:679 →
   `SetPositionIfDelta`:521 rewrites the transform on the very next path sample with no log line, while AP
   and WP (plain stat writes nothing re-samples) survive, so the mirror shows the COST and not the MOVE
   (measured: a refused JetJump left one peer 4 MINUTES out of position, the same order later activating
   from two different places). The cancel is the game's OWN teardown
   `NavigationComponent.CancelNavigation`:156-160 — it cancels the navigation ACTION and zeroes the speed,
   never moves the actor, and ends the ability's `WaitUntilFinished`:172-176 so its `OnPlayingActionEnd`
   still runs. It lives in `ApplySettle`, the ONE place every settle applies (reject path and 10 s ceiling
   path both covered), and fires only while an ability is executing — an ordinary settle has nothing to
   cancel, and `HasExecutingAbility` ignores `IdleAbility` (`TacticalActorBase`:695-704) so the cover hug
   on arrival is never cancelled. Generic to EVERY forced settle; jetpack is only where it SHOWS (JetJump
   has a per-turn charge, so a second click is the one order the host reliably refuses mid-flight).
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
    EVERY MIRRORED MODEL CHANGE MARKS THE SCREEN DIRTY, not only an ability executing
    (`85fb79d`+`5091a10`): `TacticalUiRepaint.MarkDirty` is called at the FAMILY DISPATCH —
    `TacticalDamageSync.HandleInbound` after the 0x84 op dispatch (damage, resnapshot, spawn, death,
    inventory, destructible), the end of `TacticalInventorySync.ApplyLayout` (the single funnel both
    mirror paths pass, so drops/hand-offs/crates/corpses ride one line) and
    `TacticalCommandSync.ApplySelectEquipment` (A7's weapon switch). A mirrored batch executes NO
    ability, so the `AbilityExecuted` postfix (`TacticalUiRepaint.cs:175`) never fired for it and
    `UIModuleAbilities.SetAbilities` is baked once per `EnterState` — model fresh + view stale is
    indistinguishable from "the change never crossed", this repo's dominant bug shape. `MarkDirty` only
    sets a flag (`TacticalUiRepaint.cs:102`; the flush is that class's own `TacticalViewState.Update`
    postfix), so it is safe inside `SyncApplyScope`. AND A REPAINT MAY NOT Exit+Enter A SCREEN WHOSE
    `EnterState` TRANSITIONS (law L63, `7933bbe`): `UIStateShoot` is OUT of
    `TacticalUiRepaint.AbilityBarStates` — its `EnterState`:348 `EnterFpsCamera()` pushes
    `UIStateFreeCam` and :352 calls `SwitchToPreviousState()`, which pops and THEN runs Exit, so
    `ExitState`:1244 executes TWICE on one instance when the repaint already ran Exit one line earlier
    (`UIStateFreeCam : UIStateShoot` rides out with it — the allow list walks the base chain). RailCheck
    L63 resolves every allow-listed name in `PhoenixPoint.Tactical.View.ViewStates` and walks its
    `EnterState` for `SwitchToState` / `SwitchToPreviousState` / `EnterFpsCamera`; the siblings
    `UIStateCharacterSelected`:313, `UIStateAbilitySelected`:132 and
    `UIStateOverwatchAbilitySelected`:66 reach no stack move and stay. COST accepted, stated plainly: a
    peer sitting in aim mode keeps a stale ability bar and a dead target's crosshair until its own next
    transition (`UIStateShoot._selectedValidShoots` is a constructor snapshot, :193).
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
