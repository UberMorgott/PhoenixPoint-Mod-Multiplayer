using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Tactical replication surface ids + wire codecs (spec §5). Increment 1 defines only the
    /// <c>tac.deploy</c> surface (host→all deploy snapshot). Later increments add tac.move / tac.damage /
    /// tac.turn / tac.ability on adjacent ids.
    ///
    /// RAIL (spec §3.6, with a grounded Inc-1 adaptation): tac.deploy is a host→ALL one-way push of a
    /// large, idempotent snapshot — NOT a request/apply action. It rides the SAME 0x67 SyncEnvelope
    /// inbound chokepoint the geoscape sync uses (<see cref="Multiplayer.Network.Sync.SurfaceRouter"/>),
    /// but via that router's tactical FAST-PATH hook (<c>SurfaceRouter.TacticalInbound</c>) which bypasses
    /// the action relay's shared <c>SequenceTracker</c>. Why not the action-apply path: that path gates on
    /// a SINGLE global monotonic seq shared with geoscape; a request-free host push has no correct fresh
    /// seq to assign from the tactical module, and forcing one would poison post-mission geoscape ordering.
    /// The hook is null unless tactical init arms it → inert for the geoscape/event sync. tac.deploy is NOT
    /// routed onto the dead legacy 0x20-0x24 tactical path.
    /// </summary>
    public static class TacticalSurfaceIds
    {
        // Tactical surface ids live in a HIGH, non-overlapping byte range (0x80+) so they never collide the
        // geoscape action surfaces (1-30) or the state-channel ids (1-5). The SurfaceRouter tactical hook
        // keys on this id to claim the envelope.
        public const ushort TacDeploy = 0x80;        // 128: host→all full deploy snapshot (single envelope, fits the cap)
        public const ushort TacDeployChunk = 0x81;   // 129: host→all deploy snapshot FRAGMENT (one of N chunks, over-cap path)

        // ─── Increment 2/4 LIVE outcome + intent surfaces (move + end-turn) ───────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path as tac.deploy, but these carry
        // a self-contained tactical seq (TacticalLiveSeq) instead of the deploy generation. Intents are
        // client→host (SendToHost); outcomes are host→all (BroadcastToAll). Kept in the 0x82+ tactical
        // byte range so they never collide the geoscape action/state surfaces.
        public const ushort TacIntentMove = 0x82;    // 130: client→host  "move actor netId to pos"  (intent, carries nonce)
        public const ushort TacMove = 0x83;          // 131: host→all     "actor netId landed at pos" (outcome, carries seq)
        public const ushort TacIntentEndTurn = 0x84; // 132: client→host  "end the current turn"      (intent, carries nonce)
        public const ushort TacTurn = 0x85;          // 133: host→all     "current faction advanced"  (outcome, carries seq)
        public const ushort TacMoveStart = 0x86;     // 134: host→all     "actor netId begins move to pos" (start, carries own seq) — client animates CONCURRENTLY with host; tac.move (0x83) END reconciles the exact final cell

        // ─── Increment 3a LIVE combat/damage surfaces (shoot intent + damage outcome) ───────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Inc3a host-authoritative
        // shot replication: a client sends a SHOOT intent, the host runs the real shot (roll → projectile
        // → ApplyDamage), then the host broadcasts the FINAL applied DamageResult so every peer's mirror
        // applies identical damage (the client's own roll chain is suppressed by FireWeaponPatch). All
        // damage funnels through TacticalActorBase.ApplyDamage, so this one surface covers shots, melee,
        // overwatch, AI, and the death cascade alike.
        public const ushort TacIntentAbility = 0x87; // 135: client→host  "actor netId shoots ability@guid at target" (intent, carries nonce)
        public const ushort TacDamage = 0x88;        // 136: host→all     "actor netId took this DamageResult" (outcome, carries seq)

        // ─── Inc Vision: host→client player-faction vision replication ──────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The client mirror suppresses local
        // perception, so the shared player faction's TacticalFactionVision.KnownActors stays empty → spotted-enemy
        // icons / RED-GREY target markers / the shoot target-gate all read empty. The host snapshots its player
        // faction's KnownActors on every FactionKnowledgeChangedEvent and pushes a full RECONCILE snapshot here;
        // the client sets/forgets to match. Outcome-style host→all push, carries its own TacticalLiveSeq.
        public const ushort TacVision = 0x89;        // 137: host→all     "player-faction vision snapshot" (reconcile, carries seq)

        // ─── Inc Equip: host-authoritative WEAPON/EQUIPMENT-SWAP replication ────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Mirrors the move/shoot model:
        // a CLIENT switching a soldier's active weapon/equipment is suppressed and sends a tac.intent.equip
        // (carries the equipment SLOT INDEX, stable across host/client via the shared save); the HOST re-invokes
        // the real EquipmentComponent.SetSelectedEquipment, then broadcasts tac.equip so every peer mirrors the
        // selected equipment (updating BOTH the visible weapon and the abilities the actor exposes). Selecting a
        // weapon is FREE (no AP/WP), so the outcome carries no AP-after. Self-contained tactical seq (last-writer-
        // wins) like tac.move / tac.turn / tac.vision.
        public const ushort TacIntentEquip = 0x8A;   // 138: client→host  "actor netId selects equipment@slot" (intent, carries nonce)
        public const ushort TacEquip = 0x8B;         // 139: host→all     "actor netId now has equipment@slot selected" (outcome, carries seq)

        // ─── Inc Overwatch: host-authoritative OVERWATCH-ARM replication ────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. In co-op a client putting a
        // soldier on overwatch ran ONLY locally — the HOST (the authority that runs enemy turns) never armed
        // that soldier, so the host never triggered the reaction fire, and the watch cone never showed on peers.
        // Mirrors the move/shoot/equip model: a CLIENT arming overwatch is suppressed and sends a
        // tac.intent.overwatch (carries the flattened watch CONE — built client-side, the host can't re-derive
        // it); the HOST rebuilds the cone, re-invokes OverwatchAbility.Activate so it is authoritatively armed
        // (→ it triggers reaction fire on enemy moves; the reaction DAMAGE already replicates via tac.damage),
        // then broadcasts tac.overwatch.state on every SetCone (arm AND clear/consume) so every peer mirrors the
        // cone cosmetically (the client mirror is INERT — client enemy-moves carry TriggerOverwatch=false, so a
        // client-side OverwatchStatus never double reaction-fires). Self-contained tactical seq (last-writer-wins).
        public const ushort TacIntentOverwatch = 0x8C;   // 140: client→host  "actor netId arms overwatch watching cone" (intent, carries nonce + cone)
        public const ushort TacOverwatchState = 0x8D;    // 141: host→all     "actor netId overwatch armed(with cone)/cleared" (state, carries seq)

        // ─── TS2: GENERIC (non shoot/melee) ability-INTENT relay ──────────────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Where 0x87 tac.intent.ability is the
        // shoot/melee DAMAGE-DEALER relay (limb-snap-tuned, UNTOUCHED), 0x8E is the RICHER generic client→host
        // intent for OWN-soldier abilities BEYOND shoot/bash (heal, recover-will, rally, psychic scream, …). It
        // carries a target-KIND discriminator (none/self · actor · pos · equipment-slot · object) so ONE surface
        // expresses every non-damage ability's target shape. The client SUPPRESSES the local Activate (frozen sim)
        // and relays this intent; the host re-resolves the ability by def guid + Activates it authoritatively; the
        // outcome rides the ALREADY-SHIPPED surfaces (0x8F AP/WP/Health/status + tac.damage 0x88 + TS1 spawn).
        // Peer-keyed IntentDedup (like 0x87). See TacticalGenericIntentCodec / TacticalCombatSync.HostOnGenericIntent.
        public const ushort TacIntentGeneric = 0x8E;     // 142: client→host  "actor netId activates ability@guid on <target-kind>" (intent, carries nonce)

        // ─── Inc T1: GENERIC per-actor STATE-DELTA spine (state-spine design §9) ────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The REUSABLE spine: a per-actor
        // STATE-DELTA (host→all) that mirrors mutable actor fields so any field/stat/status syncs by default.
        // T1 payload = AP/WP + the generic STATUS SET (buffs/debuffs/stances/disables). EXTENSIBLE: each
        // per-actor record carries a u16 fieldMask, so later increments fold in position/facing/health/armor/
        // selected-equip/overwatch-cone bits WITHOUT a wire break. Host computes a per-actor signature each
        // flush tick and broadcasts ONLY changed actors (idle actor = 0 bytes); client applies ABSOLUTE values
        // under a re-entrancy flag. Runs ALONGSIDE the existing per-action surfaces (additive convergence layer
        // — the AP/WP + targeted statuses it carries have no existing owner, so no conflict). Self-contained
        // tactical seq (last-writer-wins) like the other live surfaces. (0x8E = TacIntentGeneric, TS2 — allocated above.)
        public const ushort TacActorState = 0x8F;        // 143: host→all     "per-actor AP/WP + status-set delta" (state, carries seq)

        // ─── Feature C: client-side ATTACK ANIMATION (tac.fire.start) ────────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The host broadcasts this at the
        // MOMENT an actor BEGINS a shoot/grenade attack (ShootAbility — melee is a documented follow-on), so the
        // client mirror plays the shooting/throw animation CONCURRENTLY with the host. ANIMATION-ONLY: DAMAGE stays owned by tac.damage
        // (0x88); the client replays FireWeaponAtTargetCrt with AttackType.Synced + a neutered FireProjectile
        // (no projectile → ZERO client damage) under a camera-hint guard (no camera fly). (0x8E = TacIntentGeneric
        // TS2; 0x8F = TacActorState → this takes the next free id 0x90.)
        public const ushort TacFireStart = 0x90;         // 144: host→all     "actor netId begins attack@guid at target" (start, carries seq)

        // ─── Feature C (melee): client-side MELEE ATTACK ANIMATION (tac.melee.start) ──────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The MELEE counterpart of
        // tac.fire.start (0x90): the host broadcasts this at the MOMENT an actor BEGINS a melee swing
        // (BashAbility — which animates via its OWN BashCrt, NOT FireWeaponAtTargetCrt, so it needs its own
        // surface). ANIMATION-ONLY: DAMAGE stays owned by tac.damage (0x88). The client REPLAYS the native
        // BashAbility.BashCrt swing with damage/return-fire/known-counter/charge neutered (TacticalMeleeAnimSync
        // + MeleeAnimSyncPatches). Wire = tac.fire.start MINUS shotCount (a melee is one swing). Id 0x91.
        public const ushort TacMeleeStart = 0x91;        // 145: host→all     "actor netId begins melee swing@guid at target" (start, carries seq)

        // ─── TS1: mid-battle actor SPAWN / DESPAWN mirror ─────────────────────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Closes the structural blind spot
        // "things that are NOT deploy-time actors" (reinforcements, egg hatch, siren summon, turret/shield deploy,
        // resurrect, morph). Both host→ALL (3+ player safe), carry their own TacticalLiveSeq (last-writer-wins).
        //   • spawn (0x92): [seq][netId][faction][pos][ActorCreateData blob][ActorInstanceData blob] — the client
        //     materializes a mirror actor via ActorSpawner.SpawnActor and binds the host netId; it then joins the
        //     0x8F delta + tac.damage streams. The ComponentSetDef rides the ActorCreateData blob BY VALUE
        //     (BaseDef.SerializeDefContents), since a spawned actor's def is a runtime def (R1). See
        //     TacticalActorLifecycleSync / TacticalActorLifecycleCodec.
        //   • despawn (0x93): [seq][netId][reason] — non-damage removal (evac/morph/off-map/expiry); the client
        //     removes the mirror + registry cleanup. Damage-death stays owned by tac.damage (0x88).
        // 0x8E stays RESERVED for the future generic ability-INTENT (TS2). These take the next free ids 0x92/0x93.
        public const ushort TacActorSpawn = 0x92;        // 146: host→all     "materialize mid-battle actor@netId (blob)" (carries seq)
        public const ushort TacActorDespawn = 0x93;      // 147: host→all     "remove actor@netId (non-damage despawn)"   (carries seq)

        // ─── TS3: GROUND-SURFACE / VOLUME mirror (fire / goo / acid / mist) ────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Host→ALL (3+ player safe), carries
        // its own TacticalLiveSeq (last-writer-wins). Mirrors the ground hazard voxels the frozen client can't see
        // (on-ACTOR fire/goo already ride 0x8F; TS3 owns the GROUND volume). The one native funnel
        // TacticalVoxel.SetVoxelType (fire/goo/acid/mist spawn + removal — structural destruction is a DIFFERENT
        // system, so TS3/TS6 stay disjoint) is host-postfixed, coalesced per flush, and broadcast here; the client
        // re-applies the SAME leaf at the mirrored cells → correct display + LoS. DAMAGE stays host-owned
        // (tac.damage 0x88 / 0x8F); the client volume is PRESENTATION + LoS only (ClientSurfaceInertGuards).
        //   surface (0x94): [seq][opCount]{[op spawn/remove][voxelType][cellCount]{xyz}} — voxelType (not a def
        //   guid) is TFTV-tolerant by construction. See TacticalSurfaceSync / TacticalSurfaceCodec. Next free 0x94.
        public const ushort TacSurface = 0x94;           // 148: host→all     "ground fire/goo/acid/mist voxel op(s)"    (carries seq)

        // ─── TS4: MISSION-CONCLUSION mirror (evac + objectives + outcome / game-over) ────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Host→ALL (3+ player safe), RELIABLE,
        // carries its own TacticalLiveSeq (last-writer-wins). Closes the audit gap "the battle can't END in sync":
        // the host broadcasts the game-over/result + evac-zone + in-battle objective state at the native
        // TacticalLevelController.GameOver() chokepoint (fires GameWrappingUpEvent then GameOverEvent), so the client
        // leaves the battle when the host does — RIDING the native end-of-mission flow (it just flips the native
        // IsGameOver flag the tactical View state machine + mirror turn-loop already watch; no custom teardown).
        //   missionend (0x95): [seq][phase wrappingup/gameover][outcome][TacMissionResult blob][evac list][objective
        //   list]. The blob rides the ONE game Serializer (TacticalDeploySync.SerializeGraph). The post-mission
        //   GEOSCAPE result modal stays owned by the geoscape popup-mirror rail (MissionOutcome 0x69, deferred +
        //   non-occupying) — TS4 shows NO modal of its own → no double-outcome. See TacticalMissionEndSync /
        //   TacticalMissionEndCodec / TacticalMissionEndGate. Next free 0x95.
        public const ushort TacMissionEnd = 0x95;         // 149: host→all     "mission conclusion: game-over + result + evac/objectives" (carries seq)

        // ─── TS6: STRUCTURAL-DESTRUCTION mirror (destructibles: cover / LoS / nav) ───────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Host→ALL (3+ player safe), carries its
        // own TacticalLiveSeq (last-writer-wins). Closes the audit gap "client walls/floors stay solid → cover /
        // line-of-fire / navigation diverge". Mirrors destruction EVENTS: the host postfixes the combat-damage funnel
        // DestructableDamageReceiver.ApplyDamage (each hit TILE of a Destructable + the single receiver of a Breakable
        // — DISJOINT from TS3's TacticalVoxel.SetVoxelType, a DIFFERENT system → 0x94/0x96 never touch the same leaf),
        // buffers the hits per flush, and broadcasts them here; the client re-applies the SAME native damage to the
        // SAME destructible → the native destruction cascade runs identically (cover removed, LoS opened, nav updated).
        // Deterministic cross-side identity = DestructableBase.GuidInScene (SceneObjectId.GuidString — the game's OWN
        // destructible save key), so a given wall's guid matches host↔client on a shared map (R2 resolved).
        //   structdamage (0x96): [seq][hitCount]{[recLen][targetKind 1=destructible][guidLen][guid][point xyz][healthDamage]}.
        //   recLen frames each record (backward/forward-tolerant skip of an unknown kind). See TacticalStructDamageSync
        //   / TacticalStructDamageCodec. Next free 0x96.
        public const ushort TacStructDamage = 0x96;       // 150: host→all     "structural/voxel destruction event (re-applied natively)" (carries seq)

        // ─── TS7: PRESENTATION polish (enemy-turn camera follow + AoE / explosion VFX) ───────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Both host→ALL (3+ player safe), carry
        // their own TacticalLiveSeq (last-writer-wins). PRESENTATION ONLY — no sim mutation; damage already mirrors
        // via tac.damage (0x88) / 0x8F. Degrade silently (unresolvable actor/def → skip, no notify spam).
        //   • camerahint (0x97): [seq][actorNetId] — during an enemy turn the frozen client can't run the native
        //     camera follow (the enemy replay coroutines bypass Activate), so the host tags the acting ENEMY the
        //     player can SEE (native TacticalAbility.Activate → AbilityActivated hint, gated TrackWithCamera; VISIBLE-
        //     only host-side → no fog reveals). The client chases it (follow=true), gated to its enemy turn
        //     (ClientEnemyTurnCameraGate). See TacticalEnemyTurnCamera / EnemyTurnCameraHintPatch.
        //   • vfx (0x98): [seq][vfxDefGuid][pos][actorNetId] — the ExplosionEffect/VolumeEffect blast prefab draws
        //     host-only (the client applies flattened damage, never runs the effect), so the client replays the SAME
        //     ObjectToSpawn prefab at the mirrored cell — a particle/FX object that applies NO damage. Fire/goo/acid
        //     voxel volumes ride TS3 (0x94). See TacticalVfxSync / VfxBroadcastPatch. Next free ids 0x97/0x98.
        public const ushort TacCameraHint = 0x97;          // 151: host→all     "enemy-turn camera-follow target (visible enemy netId)" (carries seq)
        public const ushort TacVfx = 0x98;                 // 152: host→all     "AoE/explosion presentation VFX@def at pos"            (carries seq)

        // ─── LIVE mission-objective mirror (scripted mission events part 1) ─────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Host→ALL (3+ player safe), RELIABLE,
        // carries its own TacticalLiveSeq (last-writer-wins). Closes the tactical audit gap D21 "in-battle
        // objectives do not sync": scripted/custom missions flip FactionObjective state MID-battle (kill target,
        // reach zone, defend N turns, activate console) — TS4 (0x95) repaints only at mission END. The host
        // diffs the player faction's objective list at the ObjectivesManager.Evaluate/Add chokepoints and
        // broadcasts changed STATE (+ progress ints) records, index-keyed with a CLASS-NAME sanity check;
        // mid-mission scripted adds ride an ADD record (GeoMissionRecord P1 pattern) the client resolves from
        // the shared NextOnSuccess/NextOnFail def graph. The client value-stamps via the FactionObjective.State
        // private setter (display-only mirror — client objective EVALUATION is suppressed, completion logic
        // stays host-owned; mission END stays owned by TS4 0x95). Deploy re-broadcast (incl. reload-into-
        // tactical, rca-6) re-seeds the full state set with the actor seed. See TacticalObjectiveSync /
        // TacticalObjectiveCodec / TacticalObjectiveGate. Next free 0x99.
        public const ushort TacObjective = 0x99;           // 153: host→all     "objective state/progress + scripted-add mirror"       (carries seq)

        // ─── MID-MISSION INVENTORY-TRANSFER (tactical loot UI re-enable) ──────────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Re-enables the client loot/inventory
        // view (crates / ground containers / dead-body drop containers / soldier↔soldier trades). The native tactical
        // inventory UI is DEFERRED-COMMIT — every drag commits at once through UIStateInventory.ApplyInventoryActions
        // → InventoryQuery.SyncItems → InventoryComponent.RemoveItem/AddItem — so ONE commit yields a BATCH of
        // cross-inventory MOVES. Host-authoritative, spec-canon suppress+relay:
        //   • intent (0x9A): client→host, carries nonce. The mirroring client SUPPRESSES the local commit and relays
        //     the batch {actingSoldierNetId, applyCost, moves[]}; a move = {(srcNetId,srcSlot),(dstNetId,dstSlot),
        //     itemDefGuid, srcDefIndex}. slot 0 = backpack Inventory, 1 = Equipments; a container is slot 0. Item
        //     identity = (ItemDef guid, index among that def in the source, pre-move) — the host matches BY DEF
        //     (the DropItem FOLLOW-UP), never a blind slot index. Peer-keyed IntentDedup (like 0x8E).
        //   • apply (0x9B): host→all, carries seq. The host applies + validates each move against its OWN state, spends
        //     the inventory ability AP cost on the acting soldier (AP itself rides 0x8F), then broadcasts the SURVIVING
        //     set; every peer re-runs the SAME native moves under SyncApplyScope (both endpoints update, no
        //     despawn/respawn churn). The host's OWN looting rides the SAME apply surface (symmetric mirror).
        // See TacticalInventorySync / TacticalInventoryTransferCodec / InventoryTransferPatches. Next free 0x9A/0x9B.
        public const ushort TacInventoryIntent = 0x9A;     // 154: client→host  "commit inventory-view loot batch"        (intent, carries nonce)
        public const ushort TacInventoryApply  = 0x9B;     // 155: host→all     "authoritative inventory-transfer batch"  (outcome, carries seq)

        // ─── LOAD-PHASE progress heartbeat (client loading indicator, geo↔tac transitions) ────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Host→ALL, DISPLAY-ONLY (no game
        // state → no seq/dedup): while the host loads the destination level (geoscape→tactical, and stage-1 of
        // tactical→geoscape) the client would sit on a FROZEN screen with no feedback. The host pings this
        // heartbeat so the client shows the game's NATIVE loading curtain + bottom bar driven by the fraction;
        // the client eases its bar toward each ping (a dropped/out-of-order ping is harmless). The tac→geo
        // DOWNLOAD stage stays owned by SaveTransferCoordinator; the client backs off this heartbeat the moment
        // that transfer starts (TransferActive/InPhase2). See TacLoadPhaseCodec / TacticalLoadPhaseSync. Next free 0x9C.
        public const ushort TacLoadPhase = 0x9C;           // 156: host→all     "host is loading a level @ progress01"    (heartbeat, DISPLAY-only, no seq)
    }
}
