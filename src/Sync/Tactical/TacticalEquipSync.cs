using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// LIVE host-authoritative WEAPON / EQUIPMENT-SWAP replication (Inc Equip). Mirrors
    /// <see cref="TacticalMoveSync"/>'s intent→host→outcome→all shape, applied to the single universal
    /// equipment-swap choke point <c>EquipmentComponent.SetSelectedEquipment(Equipment)</c> (the sink ALL
    /// selection paths funnel through — the player weapon-wheel UI via
    /// <c>UIStateCharacterSelected.SetSelectedEquipment</c>, the AI, reload, drop, deploy and the death cascade):
    ///   • CLIENT swap intent: a mirroring client's <c>SetSelectedEquipment</c> is suppressed; instead it sends
    ///     <c>tac.intent.equip</c> {actorNetId, equipIndex, nonce} to the host (the index of the picked equipment
    ///     in the actor's ordered <c>Equipments.Equipments</c> list — stable across host/client via the shared
    ///     save; -1 = select null).
    ///   • HOST intent handler: resolve actor + the equipment by index, re-invoke the REAL
    ///     <c>SetSelectedEquipment</c> on the host actor. That host invoke trips the host postfix below.
    ///   • HOST postfix broadcast: on the host, after <c>SetSelectedEquipment</c> ran, read the actor's NOW-current
    ///     <c>SelectedEquipment</c> index and broadcast <c>tac.equip</c> to every peer (mirrors
    ///     <c>TacticalMoveSync.HostBroadcastMoveOutcome</c> — host's own UI click AND a relayed client intent both
    ///     run through the patched method, so both trip the postfix exactly once).
    ///   • CLIENT apply: resolve actor + equipment by index, call the REAL <c>SetSelectedEquipment</c> under a
    ///     re-entrancy flag so the client PREFIX passes it through (no re-send) — this updates BOTH the visible
    ///     weapon (holster/draw-out + EquipmentChangedEvent → native UI refresh) AND the abilities the actor
    ///     exposes, so a subsequent client shoot resolves against the synced weapon.
    ///
    /// Selecting a weapon is FREE in the engine (the AP cost lives in the abilities the weapon exposes, charged at
    /// fire time — <c>SetSelectedEquipment</c> spends no AP/WP), so no AP-after rides the wire. All game types are
    /// reached by name via <see cref="AccessTools"/>. The PURE wire codec is <see cref="TacticalLiveCodec"/>
    /// (unit-tested).
    /// </summary>
    public static class TacticalEquipSync
    {
        private static uint _nonceCounter;
        private static uint NextNonce() => unchecked(++_nonceCounter);

        // Re-entrancy guard: true only while THIS instance is applying a host/relayed selection through the
        // patched SetSelectedEquipment (client apply OR a value the host is about to re-broadcast). The PREFIX
        // checks it to PASS THROUGH (so a client apply does not re-send an intent, which would loop); it is
        // defense-in-depth on the host postfix too.
        [ThreadStatic] private static bool _applyingRemote;

        // ─── CLIENT: intercept the local swap, send intent, suppress ──────────────────────────────
        /// <summary>
        /// CLIENT (mirroring) prefix entry from <c>SetSelectedEquipmentPatch</c>: capture {actorNetId, equipIndex}
        /// and send <c>tac.intent.equip</c> to the host. Returns false to SUPPRESS the local swap (the host runs
        /// the authoritative swap + broadcasts the outcome). Returns true (let the swap run) when:
        ///   • we are re-entrantly applying a host outcome (<see cref="_applyingRemote"/>) — must run locally, no re-send;
        ///   • this is NOT a mirroring client (host / single-player) — the host runs it and its postfix broadcasts;
        ///   • the actor netId can't be read — a mirroring client still SUPPRESSES on a read miss (it must never
        ///     diverge its own selection from the host); only the non-mirror paths fail-open.
        /// </summary>
        public static bool ClientInterceptEquip(object equipmentComponent, object equipment)
        {
            if (_applyingRemote) return true;                         // applying a host outcome → run locally, no re-send
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / single-player / non-mirror → run + (host) postfix broadcasts
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return true;

            try
            {
                object actor = ResolveActor(equipmentComponent);
                if (actor == null)
                {
                    Debug.LogError("[Multiplayer][tac] equip intent: no actor on EquipmentComponent — suppressing local swap");
                    return false;
                }
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0)
                {
                    Debug.LogError("[Multiplayer][tac] equip intent: unknown actor netId — suppressing local swap");
                    return false;
                }

                int equipIndex = EquipIndexOf(equipmentComponent, equipment);   // -1 if equipment is null or not found

                byte[] payload = TacticalLiveCodec.EncodeEquipIntent(actorNetId, equipIndex, NextNonce());
                TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacIntentEquip, payload);
                Debug.Log("[Multiplayer][tac] CLIENT sent tac.intent.equip actorNetId=" + actorNetId +
                          " equipIndex=" + equipIndex);
                return false;   // suppress local swap
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ClientInterceptEquip failed: " + ex);
                return false;   // a mirroring client must not run a local swap even on error
            }
        }

        // ─── HOST: a client equip intent arrived → run the real swap ──────────────────────────────
        /// <summary>HOST inbound (<c>tac.intent.equip</c>): resolve actor → equipment-by-index, then invoke the
        /// real <c>SetSelectedEquipment</c> on the host actor. The host is NOT mirroring, so the prefix passes
        /// through and the host postfix (<see cref="OnHostEquipChanged"/>) broadcasts <c>tac.equip</c>. No-op
        /// off-host / off-session.</summary>
        public static void HostOnEquipIntent(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeEquipIntent(payload, out var intent)) { Debug.LogError("[Multiplayer][tac] equip intent decode failed"); return; }
            if (!TacticalDeploySync.IntentDedup.IsNew(TacticalSurfaceIds.TacIntentEquip, intent.Nonce)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(intent.ActorNetId);
            Debug.Log("[Multiplayer][tac][DIAG] HOSTINTENT decoded equip actorNetId=" + intent.ActorNetId +
                      " equipIndex=" + intent.EquipIndex + " actorResolved=" + (actor != null));
            if (actor == null) { Debug.LogError("[Multiplayer][tac] equip intent: no actor for netId " + intent.ActorNetId); return; }

            try
            {
                object ec = ResolveEquipmentComponent(actor);
                if (ec == null) { Debug.LogError("[Multiplayer][tac] equip intent: actor has no EquipmentComponent"); return; }

                if (!TryResolveEquipmentByIndex(ec, intent.EquipIndex, out object equipment))
                {
                    Debug.LogError("[Multiplayer][tac] equip intent: index " + intent.EquipIndex + " not applicable to actor " + intent.ActorNetId);
                    return;
                }

                // The host re-invokes the real swap; its postfix (OnHostEquipChanged) then broadcasts tac.equip.
                if (!InvokeSetSelectedEquipment(ec, equipment))
                {
                    Debug.LogError("[Multiplayer][tac] equip intent: SetSelectedEquipment invoke failed for actor " + intent.ActorNetId);
                    return;
                }
                Debug.Log("[Multiplayer][tac] HOST executed equip swap actorNetId=" + intent.ActorNetId + " equipIndex=" + intent.EquipIndex);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnEquipIntent exec failed: " + ex); }
        }

        // ─── HOST: SetSelectedEquipment ran → broadcast the now-selected equipment ─────────────────
        /// <summary>HOST postfix on <c>EquipmentComponent.SetSelectedEquipment</c>: read the actor + its NOW-current
        /// <c>SelectedEquipment</c> index and broadcast <c>tac.equip</c> to all peers. Gated: only on the host, only
        /// in a live session, and NOT while applying a relayed selection (<see cref="_applyingRemote"/>). Idempotent
        /// on the client via the per-surface seq, so a redundant broadcast (e.g. a no-op set) is harmless.</summary>
        public static void OnHostEquipChanged(object equipmentComponent)
        {
            if (_applyingRemote) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;   // defensive: host is never mirroring
            if (equipmentComponent == null) return;

            try
            {
                object actor = ResolveActor(equipmentComponent);
                if (actor == null) return;
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0) return;   // an unregistered actor (e.g. a transient turret) — skip

                int equipIndex = EquipIndexOf(equipmentComponent, GetProp(equipmentComponent, "SelectedEquipment"));

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacEquip);
                byte[] payload = TacticalLiveCodec.EncodeEquip(seq, actorNetId, equipIndex);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacEquip, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.equip seq=" + seq + " actorNetId=" + actorNetId +
                          " equipIndex=" + equipIndex);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] OnHostEquipChanged failed: " + ex); }
        }

        // ─── CLIENT: apply the host equip outcome ──────────────────────────────────────────────────
        /// <summary>CLIENT inbound (<c>tac.equip</c>): resolve actor → equipment-by-index, then call the REAL
        /// <c>SetSelectedEquipment</c> under <see cref="_applyingRemote"/> so the client prefix passes it through
        /// (no re-send). Guarded by the per-surface seq (last-writer-wins). This single call updates BOTH the
        /// visible weapon and the abilities the actor exposes. No-op off-client / off-session.</summary>
        public static void HandleEquip(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeEquip(payload, out var o)) { Debug.LogError("[Multiplayer][tac] tac.equip decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacEquip, o.Seq)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(o.ActorNetId);
            if (actor == null) { Debug.LogError("[Multiplayer][tac] tac.equip: no actor for netId " + o.ActorNetId); return; }

            try
            {
                object ec = ResolveEquipmentComponent(actor);
                if (ec == null) { Debug.LogError("[Multiplayer][tac] tac.equip: actor has no EquipmentComponent"); return; }

                if (!TryResolveEquipmentByIndex(ec, o.EquipIndex, out object equipment))
                {
                    Debug.LogError("[Multiplayer][tac] tac.equip: index " + o.EquipIndex + " not applicable to actor " + o.ActorNetId);
                    return;
                }

                _applyingRemote = true;
                bool ok;
                try { ok = InvokeSetSelectedEquipment(ec, equipment); }
                finally { _applyingRemote = false; }
                if (!ok) { Debug.LogError("[Multiplayer][tac] tac.equip: SetSelectedEquipment invoke failed for actor " + o.ActorNetId); return; }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacEquip, o.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.equip seq=" + o.Seq + " actorNetId=" + o.ActorNetId +
                          " equipIndex=" + o.EquipIndex);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleEquip failed: " + ex); }
        }

        // ─── Engine reflection helpers ──────────────────────────────────────────────────────────────

        /// <summary>The <c>TacticalActor</c> that owns an <c>EquipmentComponent</c> (InventoryComponent.Actor).
        /// Prefer the typed <c>TacticalActor</c> getter (the registry knows the actor as a TacticalActorBase);
        /// fall back to the base <c>Actor</c> property.</summary>
        private static object ResolveActor(object equipmentComponent)
        {
            if (equipmentComponent == null) return null;
            object actor = GetProp(equipmentComponent, "TacticalActor");
            return actor ?? GetProp(equipmentComponent, "Actor");
        }

        /// <summary>The actor's <c>EquipmentComponent</c> (<c>TacticalActor.Equipments</c>).</summary>
        private static object ResolveEquipmentComponent(object actor)
            => actor == null ? null : GetProp(actor, "Equipments");

        /// <summary>The ordered equipment list (<c>EquipmentComponent.Equipments</c>, a <c>List&lt;Equipment&gt;</c>)
        /// snapshotted as a plain list so index lookups are O(1) and stable for the duration of the call.</summary>
        private static IList ReadEquipmentList(object equipmentComponent)
            => GetProp(equipmentComponent, "Equipments") as IList;

        /// <summary>The index of <paramref name="equipment"/> in the actor's ordered equipment list, or -1
        /// (the null sentinel) when the equipment is null or not present. Used on send (client prefix) and on
        /// the host broadcast — both sides resolve the SAME list from the shared save, so the index round-trips.</summary>
        private static int EquipIndexOf(object equipmentComponent, object equipment)
        {
            if (equipment == null) return TacticalLiveCodec.EquipIndexNone;
            IList list = ReadEquipmentList(equipmentComponent);
            if (list == null) return TacticalLiveCodec.EquipIndexNone;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], equipment)) return i;
            return TacticalLiveCodec.EquipIndexNone;
        }

        /// <summary>Resolve the equipment to select from a wire index: the null sentinel (-1) yields null
        /// ("select null"); a real index yields the list element. Rejects (returns false) an index that is not
        /// applicable to the current list (<see cref="TacticalLiveCodec.IsApplicableEquipIndex"/>) so a desynced
        /// list never indexes out of range.</summary>
        private static bool TryResolveEquipmentByIndex(object equipmentComponent, int equipIndex, out object equipment)
        {
            equipment = null;
            IList list = ReadEquipmentList(equipmentComponent);
            int count = list?.Count ?? 0;
            if (!TacticalLiveCodec.IsApplicableEquipIndex(equipIndex, count)) return false;
            if (equipIndex == TacticalLiveCodec.EquipIndexNone) { equipment = null; return true; }
            equipment = list[equipIndex];
            return true;
        }

        /// <summary>Invoke <c>EquipmentComponent.SetSelectedEquipment(Equipment)</c> (exact-param match against the
        /// game's <c>Equipment</c> type). Accepts a null equipment (the engine supports clearing the selection).
        /// Returns false if the method can't be bound.</summary>
        private static MethodBaseCache _setSelected;
        private static bool InvokeSetSelectedEquipment(object equipmentComponent, object equipment)
        {
            var m = ResolveSetSelected(equipmentComponent);
            if (m == null) { Debug.LogError("[Multiplayer][tac] SetSelectedEquipment(Equipment) not found"); return false; }
            m.Invoke(equipmentComponent, new[] { equipment });
            return true;
        }

        private static System.Reflection.MethodInfo ResolveSetSelected(object equipmentComponent)
        {
            if (_setSelected.Method != null) return _setSelected.Method;
            var equipmentType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.Equipment");
            if (equipmentType == null) return null;
            // EquipmentComponent.SetSelectedEquipment(Equipment) — exact param match (the choke point all
            // selection paths funnel through). Bind off the live component's concrete type.
            var m = AccessTools.Method(equipmentComponent.GetType(), "SetSelectedEquipment", new[] { equipmentType });
            _setSelected = new MethodBaseCache { Method = m };
            return m;
        }

        // A tiny struct cache so the [ThreadStatic]-free MethodInfo is resolved once (the component type is
        // stable across the mission). Boxed reference; null until first resolve.
        private struct MethodBaseCache { public System.Reflection.MethodInfo Method; }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }
    }
}
