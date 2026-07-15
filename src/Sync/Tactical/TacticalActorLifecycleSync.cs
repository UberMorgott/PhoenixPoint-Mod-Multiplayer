using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Mid-battle actor SPAWN / DESPAWN mirror (spec TS1, surfaces <c>tac.actor.spawn</c> 0x92 +
    /// <c>tac.actor.despawn</c> 0x93). Closes the structural blind spot "things that are NOT deploy-time actors":
    /// reinforcements, egg hatch, siren summon, turret/shield deploy, resurrect, morph — everything that ADDS an
    /// actor after the turn-0 deploy snapshot. The frozen client never runs the spawning ability, so it must be
    /// TOLD to materialize the actor; once materialized it joins the 0x8F delta + tac.damage streams like any
    /// other mirror actor.
    ///
    /// HOST authority (never suppressed):
    ///   • <see cref="HostOnActorEnteredPlay"/> — postfix on <c>TacticalLevelController.ActorEnteredPlay</c>:
    ///     for a POST-DEPLOY actor not already in the registry, mint its netId (registry is the sole minter) and
    ///     broadcast the spawn blob.
    ///   • <see cref="HostSweepDespawns"/> — folded into the 0x8F flush heartbeat: any registered actor no longer
    ///     in the live map set (evac / morph-consume / off-map-depart / expiry) → broadcast a despawn + registry
    ///     remove. Reuses the heartbeat, no separate loop.
    ///   • <see cref="HostOnActorDied"/> — postfix on <c>TacticalLevelController.ActorDied</c>: registry cleanup
    ///     for a damage-death (the death VISUAL already mirrors via tac.damage 0x88 — this is NOT a second death
    ///     path, only so a re-minted netId can't collide the dead one).
    ///
    /// CLIENT apply (display-only, under <see cref="SyncApplyScope"/> + the tactical remote-apply guard):
    ///   • <see cref="HandleActorSpawn"/> — reconstruct the actor from the blobs and
    ///     <c>ActorSpawner.SpawnActor&lt;TacticalActorBase&gt;(componentSetDef, instanceData, callEnterPlayOnActor:true)</c>
    ///     (the exact native path, SpawnActorAbility.cs:131, closed over the common base so ground entities —
    ///     ItemContainer/StructuralTarget — materialize too), then bind the host netId. Enter-play is frozen-safe:
    ///     enemy AI is globally suppressed and the null-faction vision throw is guarded (NullFactionEnterPlayPatch).
    ///   • <see cref="HandleActorDespawn"/> — remove the mirror (native <c>ActorSpawner.DestroyActor</c>) + registry
    ///     cleanup. Idempotent.
    ///
    /// R1 (verified before coding): the def a spawned actor was built from is a RUNTIME <c>ComponentSetDef</c>
    /// (<c>GenerateInstanceComponentSetDef → CreateRuntimeDef</c>) whose guid is NOT resolvable on the client. So
    /// the spawn blob carries the actor's <c>ActorCreateData</c> (its <c>ActorSetDef</c> = that runtime def)
    /// serialized with <c>BaseDef.SerializeDefContents=true</c> — the native save mechanism that embeds a runtime
    /// def BY VALUE (SerializationComponent.cs:573). The client's game Serializer rebuilds the def locally, exactly
    /// as a mid-battle save reloads a spawned Pandoran. Blobs ride the ONE configured game Serializer via
    /// <c>TacticalDeploySync.SerializeGraph/DeserializeGraph</c>; the wire framing is the engine-free, unit-tested
    /// <see cref="TacticalActorLifecycleCodec"/>. Degrade-to-notify on any blob/def resolution failure (log + skip,
    /// never crash, never desync). All broadcasts are host→ALL (3+ player safe); every apply is seq-guarded
    /// (last-writer-wins) + idempotent.
    /// </summary>
    public static class TacticalActorLifecycleSync
    {
        // ─── HOST: spawn detection (ActorEnteredPlay postfix) ───────────────────────────────────────────

        /// <summary>HOST postfix on <c>TacticalLevelController.ActorEnteredPlay(TacticalActorBase)</c>: broadcast a
        /// spawn mirror for a MID-BATTLE actor. Gates: host + active session + not client-mirroring + not inside a
        /// remote apply + deploy already captured (so deploy-time actors — which ride the 0x80 snapshot — never
        /// double-emit) + the actor not already registered. Mints the netId FIRST (registry is the sole minter)
        /// so the client binds the host id and never mints. No-op off-host / single-player.</summary>
        public static void HostOnActorEnteredPlay(object actor)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (TacticalDeploySync.LiveTlc == null || actor == null) return;
            // Pure gate: deploy captured (post-deploy actors only) AND not already registered AND not inside a
            // remote apply (our own client materialize must never re-emit).
            if (!TacticalActorLifecycleGate.ShouldBroadcastSpawn(
                    TacticalDeploySync.HostHasBroadcastDeploy, IsActorRegistered(actor),
                    TacticalActorStateSync.IsApplyingRemote))
                return;

            try
            {
                int netId = TacticalDeploySync.Registry.AssignHost(new TacticalActorAdapter(actor));
                HostBroadcastSpawn(actor, netId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnActorEnteredPlay failed: " + ex); }
        }

        /// <summary>HOST postfix on <c>TacticalLevelController.ActorDied(DeathReport)</c>: registry cleanup ONLY.
        /// The death VISUAL already mirrors via tac.damage (0x88); this just frees the dead actor's netId so a
        /// later mint can't collide it (and keeps the despawn sweep from re-broadcasting a damage-death).</summary>
        public static void HostOnActorDied(object deathReport)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            try
            {
                object deadActor = ReadDeathReportActor(deathReport);
                if (deadActor == null) return;
                int netId = TacticalDeploySync.NetIdForLiveActor(deadActor);
                if (netId >= 0)
                {
                    TacticalDeploySync.Registry.Remove(netId);
                    Debug.Log("[Multiplayer][tac] HOST registry cleanup on death netId=" + netId +
                              " (death visual owned by tac.damage)");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnActorDied failed: " + ex); }
        }

        /// <summary>HOST: build + broadcast the spawn blob for one actor at the minted netId. Serializes the actor's
        /// <c>ActorCreateData</c> (ComponentSetDef embedded by value) + <c>SerializationData</c> (state) via the game
        /// Serializer. Degrade-to-notify: a non-spawned actor (null ActorSetDef) or a failed serialize logs + skips
        /// (the actor still rides the 0x8F delta on the host, just no client mirror).</summary>
        public static void HostBroadcastSpawn(object actor, int netId)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            byte[] bytes = TryBuildSpawnBytes(actor, netId);
            if (bytes == null) return;   // already logged — degrade-to-notify, no partial emit
            SendSpawnBytes(engine, netId, bytes);
        }

        /// <summary>HOST: build the encoded 0x92 spawn frame for one actor at the minted netId, or null when the
        /// actor cannot be mirrored (null create/serialization data, null ActorSetDef = scene-placed, empty
        /// serialize blob) — every failure logs + returns null WITHOUT any wire emit, so callers can validate
        /// re-spawnability BEFORE sending anything (the refresh path emits its despawn only after this succeeds).</summary>
        private static byte[] TryBuildSpawnBytes(object actor, int netId)
        {
            try
            {
                object createData = GetProp(actor, "ActorCreateData");
                object instData = GetProp(actor, "SerializationData");
                if (createData == null || instData == null)
                {
                    Debug.LogError("[Multiplayer][tac] HostBroadcastSpawn netId=" + netId +
                                   ": null ActorCreateData/SerializationData — skip spawn mirror");
                    return null;
                }
                object setDef = ReadActorSetDef(createData);
                if (setDef == null)
                {
                    // IsSpawned==false → ActorSetDef is null (scene-placed actor). A mid-battle spawn should be
                    // IsSpawned; if not, we cannot rebuild it client-side → degrade-to-notify.
                    Debug.LogError("[Multiplayer][tac] HostBroadcastSpawn netId=" + netId +
                                   ": ActorSetDef null (actor not IsSpawned?) — skip spawn mirror");
                    return null;
                }

                byte[] createBlob, instBlob;
                SetDefContents(true);
                try
                {
                    createBlob = TacticalDeploySync.SerializeGraph(new[] { createData }, quiet: true);
                    instBlob = TacticalDeploySync.SerializeGraph(new[] { instData }, quiet: true);
                }
                finally { SetDefContents(false); }

                if (createBlob == null || createBlob.Length == 0 || instBlob == null || instBlob.Length == 0)
                {
                    Debug.LogError("[Multiplayer][tac] HostBroadcastSpawn netId=" + netId +
                                   ": serialize produced empty blob — skip spawn mirror");
                    return null;
                }

                Vector3 pos = ReadPos(actor);
                int faction = ReadFactionIndex(actor);
                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacActorSpawn);
                Debug.Log("[Multiplayer][tac] HOST built tac.actor.spawn netId=" + netId + " seq=" + seq +
                          " faction=" + faction + " createLen=" + createBlob.Length + " instLen=" + instBlob.Length);
                return TacticalActorLifecycleCodec.EncodeSpawn(
                    new TacticalActorLifecycleCodec.SpawnPayload(seq, netId, faction, pos.x, pos.y, pos.z,
                        createBlob, instBlob));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] HostBroadcastSpawn (build) failed: " + ex);
                return null;
            }
        }

        /// <summary>HOST: put a pre-built spawn frame on the wire (see <see cref="TryBuildSpawnBytes"/>).</summary>
        private static void SendSpawnBytes(NetworkEngine engine, int netId, byte[] bytes)
        {
            try
            {
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacActorSpawn, bytes);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.actor.spawn netId=" + netId + " len=" + bytes.Length);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastSpawn (send) failed: " + ex); }
        }

        /// <summary>gap-turret-crate-loot HOST: CONTENT-REFRESH a REGISTERED actor's client mirrors — despawn
        /// (<see cref="TacticalActorLifecycleCodec.ReasonRefreshed"/>) + immediate re-spawn at the SAME netId,
        /// both existing surfaces, both idempotent client-side (the despawn frees the netId so the follow-up
        /// spawn re-materializes instead of skip-marking). Used when a mirrored actor's SERIALIZED state changed
        /// after its 0x92 spawn (a dropped item lands in a ground container AFTER EnterPlay serialized it empty).
        /// ATOMIC (review 56558d2): the FULL replacement spawn frame is built + validated FIRST (ActorSetDef,
        /// serialize, non-empty blobs — <see cref="TryBuildSpawnBytes"/>) and the despawn is emitted ONLY after
        /// that succeeds — a mirror is never destroyed client-side without its replacement already in hand
        /// (a checked-then-failed serialize would otherwise leave the client permanently missing the actor).
        /// The host registry binding is untouched (same netId, same live actor). No-op off-host / unregistered /
        /// not-refreshable (log + skip, mirror stays stale-but-present).</summary>
        public static void HostRefreshActorMirror(object actor)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost || actor == null) return;
            try
            {
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (netId < 0)
                {
                    Debug.LogWarning("[Multiplayer][tac] refresh: actor not in the registry — skip (mirror stays stale)");
                    return;
                }
                byte[] spawnBytes = TryBuildSpawnBytes(actor, netId);
                if (spawnBytes == null)
                {
                    Debug.LogWarning("[Multiplayer][tac] refresh netId=" + netId +
                                     ": replacement spawn not buildable — skip, NO despawn emitted (mirror stays stale)");
                    return;
                }
                HostBroadcastDespawn(netId, TacticalActorLifecycleCodec.ReasonRefreshed);
                SendSpawnBytes(engine, netId, spawnBytes);
                Debug.Log("[Multiplayer][tac] HOST content-refreshed mirror netId=" + netId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostRefreshActorMirror failed: " + ex); }
        }

        /// <summary>HOST: broadcast a despawn (host→all). Reason is diagnostic; the client always removes the mirror.</summary>
        public static void HostBroadcastDespawn(int netId, byte reason)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            try
            {
                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacActorDespawn);
                byte[] bytes = TacticalActorLifecycleCodec.EncodeDespawn(seq, netId, reason);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacActorDespawn, bytes);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.actor.despawn netId=" + netId +
                          " seq=" + seq + " reason=" + reason);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastDespawn failed: " + ex); }
        }

        /// <summary>HOST (folded into the 0x8F flush heartbeat): any REGISTERED actor no longer present in the live
        /// map set is a NON-DAMAGE despawn (evac / morph-consume / off-map-depart / expiry) — broadcast a despawn +
        /// remove it from the registry. Damage-deaths are already removed by <see cref="HostOnActorDied"/> before a
        /// sweep can see them, so this never double-fires a death (which tac.damage owns). Snapshots the despawn set
        /// BEFORE mutating the registry (no mutate-during-enumeration).</summary>
        public static void HostSweepDespawns()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            var reg = TacticalDeploySync.Registry;
            if (reg == null) return;
            try
            {
                var liveSet = new HashSet<object>();
                foreach (var a in TacticalDeploySync.HostLiveActorObjects())
                    if (a != null) liveSet.Add(a);

                // Snapshot (netId → wrapped actor) BEFORE the pure sweep so the registry is not mutated mid-walk.
                var registered = new List<KeyValuePair<int, object>>();
                foreach (var kv in reg.Entries)
                {
                    object actor = (kv.Value is TacticalActorAdapter ad) ? ad.Actor : null;
                    if (actor != null) registered.Add(new KeyValuePair<int, object>(kv.Key, actor));
                }

                var despawned = TacticalActorLifecycleGate.ComputeDespawnedNetIds(registered, liveSet);
                foreach (var netId in despawned)
                {
                    HostBroadcastDespawn(netId, TacticalActorLifecycleCodec.ReasonRemoved);
                    reg.Remove(netId);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostSweepDespawns failed: " + ex); }
        }

        /// <summary>HOST chokepoint on EVAC (postfix on <c>EvacuatedStatus.InitVisualState</c> — the native evac
        /// visual, gap-evac). The single reliable point that catches EVERY evac path (ExitMissionAbility, and
        /// EvacuateMountedActorsAbility for the vehicle + all passengers) since they all funnel through applying
        /// <c>EvacuatedStatus</c>. An evac'd actor keeps the Evacuated STANCE and STAYS in the host live map set, so
        /// <see cref="HostSweepDespawns"/> never flags it — without this the client keeps a ghost mirror actor +
        /// its selection decal forever. Broadcast a despawn (<see cref="TacticalActorLifecycleCodec.ReasonEvacuated"/>)
        /// on the existing 0x93 rail; the client mirrors the NATIVE evac semantics (hide, keep the actor — see
        /// <see cref="HideEvacuatedActor"/>), NOT DestroyActor (a destroyed selected actor wedged the view machine
        /// with per-frame NREs, RCA 2026-07-15) and NOT the death path (no corpse).
        /// Host-only + not-mirroring (a client's EvacuatedStatus mirror runs InitVisualState too — this must no-op there).
        /// Idempotent: a re-fire for an unmirrored / already-gone actor resolves to netId -1 → skip.</summary>
        public static void HostOnActorEvacuated(object evacStatus)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (evacStatus == null) return;
            try
            {
                object actor = GetProp(evacStatus, "TacticalActorBase");
                if (actor == null) return;
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (netId < 0) return;   // not mirrored → nothing to despawn
                HostBroadcastDespawn(netId, TacticalActorLifecycleCodec.ReasonEvacuated);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnActorEvacuated failed: " + ex); }
        }

        // ─── CLIENT: apply ──────────────────────────────────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>tac.actor.spawn</c> 0x92): seq-guard, then reconstruct + materialize the mirror
        /// actor under the apply scope, and bind the host netId. Idempotent: an already-present netId (reliable
        /// double-send, or a post-reload rebind) is marked + skipped. No-op on host. Degrade-to-notify on any
        /// deserialize / def / spawn failure.</summary>
        public static void HandleActorSpawn(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalActorLifecycleCodec.TryDecodeSpawn(payload, out var p))
            { Debug.LogError("[Multiplayer][tac] tac.actor.spawn decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacActorSpawn, p.Seq)) return;

            try
            {
                // Idempotent: already materialized (double-send / post-reload native load) → mark + skip.
                if (TacticalDeploySync.ResolveLiveActor(p.NetId) != null)
                {
                    TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacActorSpawn, p.Seq);
                    return;
                }

                object createData = TacticalDeploySync.DeserializeGraph(p.CreateBlob, ActorCreateDataType(), quiet: true);
                object instData = TacticalDeploySync.DeserializeGraph(p.InstBlob, ActorInstanceDataType(), quiet: true);
                if (createData == null || instData == null)
                {
                    Debug.LogError("[Multiplayer][tac] HandleActorSpawn netId=" + p.NetId +
                                   ": deserialize returned null (create=" + (createData != null) +
                                   " inst=" + (instData != null) + ") — skip");
                    return;
                }
                object setDef = ReadActorSetDef(createData);
                if (setDef == null)
                {
                    Debug.LogError("[Multiplayer][tac] HandleActorSpawn netId=" + p.NetId +
                                   ": reconstructed ActorSetDef null — skip");
                    return;
                }

                object spawned;
                using (SyncApplyScope.Enter())
                using (TacticalActorStateSync.EnterApplyScope())
                {
                    spawned = InvokeSpawnActor(setDef, instData);
                }
                if (spawned == null)
                {
                    Debug.LogError("[Multiplayer][tac] HandleActorSpawn netId=" + p.NetId +
                                   ": ActorSpawner.SpawnActor returned null — skip");
                    return;
                }

                TacticalDeploySync.Registry.Register(p.NetId, new TacticalActorAdapter(spawned));
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacActorSpawn, p.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT materialized tac.actor.spawn netId=" + p.NetId +
                          " seq=" + p.Seq + " faction=" + p.FactionIndex);
                // rca-spawn-reveal: a 0x97 hint for THIS actor may have arrived before the spawn (resolved=false
                // → stashed) — now that the netId resolves, re-fire it.
                TacticalEnemyTurnCamera.TryReplayPendingHint();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleActorSpawn failed: " + ex); }
        }

        /// <summary>CLIENT inbound (<c>tac.actor.despawn</c> 0x93): seq-guard, then under the apply scope either
        /// HIDE the mirror actor (ReasonEvacuated — native-evac parity, actor stays registered + in the faction so
        /// the battle summary can list it) or remove it (native DestroyActor + registry cleanup, all other reasons).
        /// Idempotent (an already-gone actor is a registry-only remove; a re-hide is a no-op). No-op on host.</summary>
        public static void HandleActorDespawn(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalActorLifecycleCodec.TryDecodeDespawn(payload, out var p))
            { Debug.LogError("[Multiplayer][tac] tac.actor.despawn decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacActorDespawn, p.Seq)) return;

            try
            {
                bool evac = p.Reason == TacticalActorLifecycleCodec.ReasonEvacuated;
                if (evac) _evacuatedNetIds.Add(p.NetId);   // client intent gate (see IsNetIdEvacuated)
                object actor = TacticalDeploySync.ResolveLiveActor(p.NetId);
                bool nativeEvac = false;
                if (actor != null)
                {
                    using (SyncApplyScope.Enter())
                    using (TacticalActorStateSync.EnterApplyScope())
                    {
                        if (evac)
                        {
                            nativeEvac = TryApplyNativeEvac(actor);
                            if (!nativeEvac) HideEvacuatedActor(actor);
                            ClearSelectionIfSelected(actor);
                        }
                        else InvokeDestroyActor(actor);
                    }
                }
                if (!evac) TacticalDeploySync.Registry.Remove(p.NetId);   // idempotent registry cleanup
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacActorDespawn, p.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT removed tac.actor.despawn netId=" + p.NetId +
                          " reason=" + p.Reason + " (mirror " +
                          (actor != null
                              ? (evac ? (nativeEvac ? "evacuated NATIVELY, kept for summary" : "hidden (fallback), kept for summary")
                                      : "destroyed")
                              : "already gone") + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleActorDespawn failed: " + ex); }
        }

        // Evacuated mirror netIds THIS mission (cleared via Reset() at OnMissionExit). The hidden actor stays
        // registered + selectable client-side — this set is what makes it non-COMMANDABLE (intent-send gate).
        private static readonly HashSet<int> _evacuatedNetIds = new HashSet<int>();

        /// <summary>CLIENT intent gate (RCA 2026-07-15 №2): was this netId evacuated this mission? The hidden
        /// mirror actor is still selectable (no mounted/off-map parity — see HideEvacuatedActor ceiling), so the
        /// send chokepoints drop intents for it instead of relaying commands the host can never run.</summary>
        internal static bool IsNetIdEvacuated(int netId) => _evacuatedNetIds.Contains(netId);

        /// <summary>HOST authoritative intent gate (RCA 2026-07-15 №2): does the live actor carry the native
        /// <c>EvacuatedStatus</c>? Executing a relayed move/ability on an evacuated host actor (animator natively
        /// disabled by EvacuatedStatus.HideActor) never finishes its presentation → per-frame animator-time error
        /// spam + IsWaitingForActiveAndQueuedAbilities stuck true → the host view wedges in UIStateWaiting at
        /// game-over. Fail-open on reflection (behaves as before the gate).</summary>
        internal static bool HasEvacuatedStatus(object actor)
        {
            try
            {
                object status = GetProp(actor, "Status");
                if (status == null) return false;
                if (_statusesField == null) _statusesField = AccessTools.Field(status.GetType(), "Statuses");
                if (_statusesField?.GetValue(status) is IEnumerable list)
                    foreach (var s in list)
                        if (s != null && s.GetType().Name == "EvacuatedStatus") return true;
            }
            catch { }
            return false;
        }

        /// <summary>Per-mission reset (called from <see cref="TacticalDeploySync.OnMissionExit"/> like the
        /// sibling sync classes) — a stale evacuated netId must not block intents in the next mission.</summary>
        internal static void Reset() => _evacuatedNetIds.Clear();

        /// <summary>Full NATIVE evac on the mirror (user directive 2026-07-15, supersedes the hide-only replica —
        /// that one left a selectable ghost + an empty clickable squad slot). Invokes the actor's OWN
        /// <c>ExitMissionAbility.HideActorInExitZone(zone)</c> (ExitMissionAbility.cs:25-30): EvacuatedStatus
        /// (force-hide + animator off + cancel actions + vision forget) + UnapplyAllStatusesFiltered + the zone's
        /// <c>VehicleComponent.ApplyMountedStatus</c> — the mirror reaches the host's exact end-state (mounted →
        /// interactable off, follow-socket, standby via TacticalActor.OnStatusApplyUnapply → gone from
        /// selection/squad bar, vanishes like single-player). Statuses applied here are NATIVE creations, NOT
        /// spine mirrors, so ClientStatusMirrorGuards do not skip their effects; the caller already holds
        /// SyncApplyScope so nothing re-relays. Mission-end stays owned by the 0x95 gate; the e2f07b4 intent
        /// gates stay as belts. False → caller falls back to the legacy hide-only replica.</summary>
        private static bool TryApplyNativeEvac(object actor)
        {
            try
            {
                object ability = FindExitMissionAbility(actor);
                if (ability == null)
                { Debug.LogWarning("[Multiplayer][tac] native evac: mirror has no ExitMissionAbility — hide-only fallback"); return false; }
                object zone = FindExitZoneFor(actor);
                if (zone == null)
                { Debug.LogWarning("[Multiplayer][tac] native evac: no TacticalExitZone resolved — hide-only fallback"); return false; }
                var hide = AccessTools.Method(ability.GetType(), "HideActorInExitZone");
                if (hide == null)
                { Debug.LogWarning("[Multiplayer][tac] native evac: HideActorInExitZone not found — hide-only fallback"); return false; }
                hide.Invoke(ability, new[] { zone });
                return true;
            }
            catch (Exception ex)
            { Debug.LogError("[Multiplayer][tac] native evac failed — hide-only fallback: " + ex); return false; }
        }

        /// <summary>The actor's own ExitMissionAbility instance — the same <c>GetAbilities&lt;TacticalAbility&gt;()</c>
        /// walk as TacticalCombatSync.ResolveAbilityByGuid, matched by TYPE NAME (the 0x93 wire carries no guid).</summary>
        private static object FindExitMissionAbility(object actor)
        {
            var tacAbilityType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
            var getAbilities = tacAbilityType != null ? AccessTools.Method(actor.GetType(), "GetAbilities") : null;
            if (getAbilities == null || !getAbilities.IsGenericMethodDefinition) return null;
            var gen = getAbilities.MakeGenericMethod(tacAbilityType);
            if (gen.GetParameters().Length != 0) return null;   // GetAbilities<T>() — the no-arg overload
            if (!(gen.Invoke(actor, null) is IEnumerable abilities)) return null;
            foreach (var a in abilities)
                if (a != null && a.GetType().Name == "ExitMissionAbility") return a;
            return null;
        }

        /// <summary>Exit zone for the mounted-status apply: native pick first (unlocked + BoxCollider bounds
        /// contain the actor — GetZonesContainingActor parity), else ANY zone — the host already validated the
        /// evac; on the mirror the zone only hosts the mount bookkeeping/socket.</summary>
        private static object FindExitZoneFor(object actor)
        {
            var zoneType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.ActorDeployment.TacticalExitZone");
            object map = GetProp(actor, "Map");
            if (zoneType == null || map == null) return null;
            // Base.Levels.BaseMap.cs:64: GetActors<T>(Func<T,bool> predicate = null) — ONE optional param.
            // (rca 2026-07-15: a zero-arity filter matched nothing → every evac fell to the hide-only path.)
            MethodInfo gen = null;
            foreach (var m in map.GetType().GetMethods())
                if (m.Name == "GetActors" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                { gen = m.MakeGenericMethod(zoneType); break; }
            if (!(gen?.Invoke(map, new object[] { null }) is IEnumerable zones)) return null;

            object first = null;
            Vector3 pos = GetProp(actor, "Pos") is Vector3 p ? p : Vector3.zero;
            foreach (var z in zones)
            {
                if (z == null) continue;
                if (first == null) first = z;
                try
                {
                    if (AccessTools.Method(z.GetType(), "IsLocked")?.Invoke(z, null) is bool locked && locked) continue;
                    // Collider type lives in UnityEngine.PhysicsModule (not referenced) — read bounds reflectively.
                    if (GetProp(GetProp(z, "BoxCollider"), "bounds") is Bounds b && b.Contains(pos)) return z;
                }
                catch { }
            }
            return first;
        }

        /// <summary>FALLBACK hide-only replica (used when <see cref="TryApplyNativeEvac"/> can't resolve the
        /// ability/zone): EvacuatedStatus.HideActor verbatim — force-hide + animator off + cancel actions +
        /// forget vision. Keeps the actor for the battle summary; the caller clears selection. Ghost stays in
        /// the squad bar on this path — the intent gates keep it non-commandable. Per-step best-effort.</summary>
        private static void HideEvacuatedActor(object actor)
        {
            try
            {
                object view = GetProp(actor, "TacticalActorView") ?? GetProp(actor, "TacticalActorViewBase");
                if (view != null) AccessTools.Method(view.GetType(), "RequestForceHidden")?.Invoke(view, null);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] evac hide: RequestForceHidden failed: " + ex); }
            try
            {
                if (GetProp(actor, "Animator") is Behaviour animator) animator.enabled = false;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] evac hide: Animator disable failed: " + ex); }
            try
            {
                object actions = GetProp(actor, "ActionComponent");
                if (actions != null) AccessTools.Method(actions.GetType(), "CancelAllActions")?.Invoke(actions, null);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] evac hide: CancelAllActions failed: " + ex); }
            try
            {
                if (_forgetForAllMethod == null)
                {
                    var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
                    _forgetForAllMethod = t != null ? AccessTools.Method(t, "ForgetForAll") : null;
                }
                _forgetForAllMethod?.Invoke(null, new object[] { actor, false });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] evac hide: ForgetForAll failed: " + ex); }
        }

        /// <summary>If the hidden actor is the view's SelectedActor, deselect (clears the selection decal via the
        /// native setter) and pop the view to UIStateInitial under the SAME guards TacticalView.OnActorExitedPlay
        /// uses (not game-over, level playing, not UIStateWaiting) so an in-flight presentation isn't yanked.</summary>
        private static void ClearSelectionIfSelected(object actor)
        {
            try
            {
                object tlc = TacticalDeploySync.LiveTlc;
                object view = GetProp(tlc, "View");
                if (view == null || !ReferenceEquals(GetProp(view, "SelectedActor"), actor)) return;
                AccessTools.Property(view.GetType(), "SelectedActor")?.SetValue(view, null, null);
                bool gameOver = GetProp(tlc, "IsGameOver") is bool go && go;
                bool playing = GetProp(GetProp(tlc, "Level"), "IsPlaying") is bool pl && pl;
                object cur = GetProp(view, "CurrentState");
                if (!gameOver && playing && (cur == null || cur.GetType().Name != "UIStateWaiting"))
                    AccessTools.Method(view.GetType(), "ResetViewState", Type.EmptyTypes)?.Invoke(view, null);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] evac hide: selection clear failed: " + ex); }
        }

        // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Is this exact live actor already bound in the registry? Keyed on the WRAPPED actor object's
        /// reference identity (the adapter has no value-equality), so a fresh adapter never false-matches.</summary>
        private static bool IsActorRegistered(object actor)
        {
            var reg = TacticalDeploySync.Registry;
            if (reg == null || actor == null) return false;
            foreach (var kv in reg.Entries)
                if (kv.Value is TacticalActorAdapter a && ReferenceEquals(a.Actor, actor)) return true;
            return false;
        }

        // Reflection cache — bound lazily (TypeByName), like the sibling tactical sync classes.
        private static Type _actorCreateDataType;
        private static Type _actorInstanceDataType;
        private static Type _tacActorType;
        private static MethodInfo _spawnActorMethod;   // ActorSpawner.SpawnActor<TacticalActorBase>(BaseDef, ActorInstanceData, bool)
        private static MethodInfo _destroyActorMethod; // ActorSpawner.DestroyActor(ActorComponent)
        private static MethodInfo _forgetForAllMethod; // TacticalFactionVision.ForgetForAll(TacticalActorBase, bool)
        private static FieldInfo _statusesField;       // StatusComponent.Statuses (public List<Status>)
        private static FieldInfo _actorSetDefField;    // ActorCreateData.ActorSetDef
        private static FieldInfo _serializeDefContentsField; // BaseDef.SerializeDefContents (static)
        private static bool _defContentsResolved;

        private static Type ActorCreateDataType()
            => _actorCreateDataType ?? (_actorCreateDataType = AccessTools.TypeByName("Base.Entities.ActorCreateData"));

        private static Type ActorInstanceDataType()
            => _actorInstanceDataType ?? (_actorInstanceDataType = AccessTools.TypeByName("Base.Entities.ActorInstanceData"));

        /// <summary>Read <c>ActorCreateData.ActorSetDef</c> (the ComponentSetDef; null for a non-spawned actor).</summary>
        private static object ReadActorSetDef(object createData)
        {
            if (createData == null) return null;
            if (_actorSetDefField == null || _actorSetDefField.DeclaringType == null ||
                !_actorSetDefField.DeclaringType.IsInstanceOfType(createData))
                _actorSetDefField = AccessTools.Field(createData.GetType(), "ActorSetDef");
            return _actorSetDefField?.GetValue(createData);
        }

        /// <summary>Set <c>BaseDef.SerializeDefContents</c> so a runtime def in the write graph serializes BY VALUE
        /// (the native save mechanism). Silent no-op if the field can't be resolved (the serialize then falls back
        /// to guid-only — HandleActorSpawn's degrade-to-notify catches the resulting empty def on the client).</summary>
        private static void SetDefContents(bool value)
        {
            if (!_defContentsResolved)
            {
                _defContentsResolved = true;
                var t = AccessTools.TypeByName("Base.Defs.BaseDef");
                _serializeDefContentsField = t != null ? AccessTools.Field(t, "SerializeDefContents") : null;
                if (_serializeDefContentsField == null)
                    Debug.LogError("[Multiplayer][tac] BaseDef.SerializeDefContents not found — runtime def will not embed by value");
            }
            _serializeDefContentsField?.SetValue(null, value);
        }

        /// <summary>Invoke <c>ActorSpawner.SpawnActor&lt;TacticalActorBase&gt;(componentSetDef, instanceData,
        /// callEnterPlayOnActor:true)</c> — the exact native spawn path (SpawnActorAbility.cs:131), closed over
        /// the COMMON BASE. gap-turret-crate-loot fix: the old <c>&lt;TacticalActor&gt;</c> closure THREW for a
        /// GROUND-ENTITY blob — <c>DefRepository.Instantiate&lt;T&gt;</c> hard-throws when the built root is not a
        /// T (DefRepository.cs:191-195), and ItemContainer / StructuralTarget are <c>TacticalActorBase</c>, NOT
        /// TacticalActor — so a dropped-loot container mirror always failed to materialize. T is only the return
        /// cast (ActorSpawner.cs:12-27), so the base close spawns every TS1 entity kind identically.</summary>
        private static object InvokeSpawnActor(object componentSetDef, object instanceData)
        {
            if (_spawnActorMethod == null)
            {
                var spawnerType = AccessTools.TypeByName("Base.Entities.ActorSpawner");
                _tacActorType = _tacActorType ?? AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
                var open = spawnerType != null ? AccessTools.Method(spawnerType, "SpawnActor") : null;
                if (open == null || _tacActorType == null || !open.IsGenericMethodDefinition)
                {
                    Debug.LogError("[Multiplayer][tac] ActorSpawner.SpawnActor<TacticalActorBase> not resolvable");
                    return null;
                }
                _spawnActorMethod = open.MakeGenericMethod(_tacActorType);
            }
            return _spawnActorMethod.Invoke(null, new object[] { componentSetDef, instanceData, true });
        }

        /// <summary>Invoke native <c>ActorSpawner.DestroyActor(ActorComponent)</c> (OnExitPlay + Destroy GameObject) —
        /// the same removal the deploy hydrate uses for client-only extras.</summary>
        private static void InvokeDestroyActor(object actor)
        {
            if (_destroyActorMethod == null)
            {
                var spawnerType = AccessTools.TypeByName("Base.Entities.ActorSpawner");
                _destroyActorMethod = spawnerType != null ? AccessTools.Method(spawnerType, "DestroyActor") : null;
                if (_destroyActorMethod == null)
                {
                    Debug.LogError("[Multiplayer][tac] ActorSpawner.DestroyActor not found — mirror not removed natively");
                    return;
                }
            }
            _destroyActorMethod.Invoke(null, new[] { actor });
        }

        /// <summary>Best-effort DIAGNOSTIC faction index (position of the actor's faction in the live TLC's Factions
        /// list). The authoritative faction rides the instance-data blob (restored on enter-play); this is only a
        /// wire diagnostic / display hint. Returns -1 on any failure.</summary>
        private static int ReadFactionIndex(object actor)
        {
            try
            {
                object faction = GetProp(actor, "TacticalFaction");
                if (faction == null) return -1;
                object factions = GetProp(TacticalDeploySync.LiveTlc, "Factions");
                if (factions is IEnumerable en)
                {
                    int i = 0;
                    foreach (var f in en)
                    {
                        if (ReferenceEquals(f, faction)) return i;
                        i++;
                    }
                }
            }
            catch { }
            return -1;
        }

        private static Vector3 ReadPos(object actor)
        {
            try
            {
                object pos = GetProp(actor, "Pos");
                if (pos is Vector3 v) return v;
            }
            catch { }
            return Vector3.zero;
        }

        private static object ReadDeathReportActor(object deathReport)
        {
            if (deathReport == null) return null;
            // DeathReport.Actor is a public FIELD (TacticalActorBase Actor).
            var f = AccessTools.Field(deathReport.GetType(), "Actor");
            return f?.GetValue(deathReport);
        }

        private static readonly Dictionary<string, PropertyInfo> _propCache = new Dictionary<string, PropertyInfo>();
        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            string key = obj.GetType().FullName + "::" + name;
            if (!_propCache.TryGetValue(key, out var pi))
            {
                pi = AccessTools.Property(obj.GetType(), name);
                _propCache[key] = pi;
            }
            return pi?.GetValue(obj, null);
        }
    }
}
