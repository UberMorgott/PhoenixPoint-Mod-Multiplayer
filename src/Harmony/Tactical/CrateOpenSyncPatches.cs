using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// WORLD-VISUAL crate-open mirror (tac.crate.open 0x9E) — rca-inventory part 2, 3-instance live test 2026-07-14.
    ///
    /// Symptom: a looted crate opened its WORLD visual (lid-open animation + the blue "unlooted" highlight beam
    /// turning OFF) only on the HOST; on every client it stayed closed and still glowed blue, even though its
    /// contents synced fine (0x9A/0x9B). RCA: the visual is driven ENTIRELY by <c>CrateComponent.Open()</c>
    /// (CrateComponent.cs:31 — sets the Animator "open" bool, hides <c>_highlightObj</c>, refreshes the item
    /// label) — a purely local call with NO networking. Peers never run <c>OpenCrateAbility</c> (a client's move
    /// is suppressed + relayed; the HOST runs the authoritative move and its native OpenCrate side-effect), so
    /// <c>Open()</c> never fires on peers. The auto-open path is a host side-effect that bypasses the 0x8E ability
    /// relay entirely, and the 0x8F actor-state spine skips container actors (no CharacterStats), so nothing
    /// carried the opened flag to peers.
    ///
    /// Fix: patch the SINGLE native chokepoint <c>CrateComponent.Open()</c> — covers EVERY trigger (relayed-move
    /// auto-open, direct OpenCrate click, host's own crate walk) uniformly. HOST postfix broadcasts the crate's
    /// netId; every peer resolves its own mirror of that container actor and calls the native <c>Open()</c> to flip
    /// the same world-visual. This is DECOUPLED from the UI-open path (<see cref="TacticalInventoryViewGuard"/> /
    /// InventoryAbility.Activate, gated origin-only in part 1): <c>Open()</c> does the world-visual only, never
    /// pushes UIStateInventory — so the visual mirrors to ALL instances while the loot UI stays origin-only.
    /// </summary>
    public static class TacticalCrateSync
    {
        private static Type _crateType;
        private static Type _containerType;

        /// <summary>HOST: a crate actor's world-visual just opened → broadcast its netId so every peer flips the
        /// same lid-open animation + hides the blue "unlooted" highlight. No-op off-host / off-session / for an
        /// unregistered crate (its contents wouldn't sync either — nothing to mirror).</summary>
        public static void HostBroadcastCrateOpen(object crateComponent)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            object container = ContainerOfCrate(crateComponent);
            int netId = container != null ? TacticalDeploySync.NetIdForLiveActor(container) : -1;
            if (netId < 0) return;
            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacCrateOpen);
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacCrateOpen, Encode(seq, netId));
            Debug.Log("[Multiplayer][tac] HOST broadcast tac.crate.open seq=" + seq + " netId=" + netId);
        }

        /// <summary>CLIENT inbound (<c>tac.crate.open</c> 0x9E): flip the mirrored crate's world-visual to OPEN via
        /// the native <c>CrateComponent.Open()</c> on the peer's own crate actor. Idempotent (sets the animator
        /// bool + hides the highlight); its own postfix won't rebroadcast (!IsHost) so there is no echo loop.
        /// No-op on host / stale seq / unresolvable crate.</summary>
        public static void HandleCrateOpen(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TryDecode(payload, out uint seq, out int netId)) { Debug.LogError("[Multiplayer][tac] tac.crate.open decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacCrateOpen, seq)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(netId);
            object crate = CrateOfActor(actor);
            if (crate == null) { Debug.LogWarning("[Multiplayer][tac] tac.crate.open: no CrateComponent for netId " + netId); return; }
            try { AccessTools.Method(crate.GetType(), "Open", Type.EmptyTypes)?.Invoke(crate, null); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.crate.open apply failed: " + ex); return; }
            TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacCrateOpen, seq);
            Debug.Log("[Multiplayer][tac] CLIENT applied tac.crate.open seq=" + seq + " netId=" + netId);
        }

        // The registered ItemContainer actor sharing the crate's GameObject (CrateComponent + [Crate]ItemContainer
        // sit on the SAME GO — OpenCrateAbility reads both off item.gameObject). GetComponent matches subclasses,
        // so the base ItemContainer type resolves a CrateItemContainer too.
        private static object ContainerOfCrate(object crateComponent)
        {
            if (!(crateComponent is Component mb)) return null;
            if (_containerType == null) _containerType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.ItemContainer");
            return _containerType != null ? mb.GetComponent(_containerType) : null;
        }

        // The CrateComponent sharing a resolved container actor's GameObject.
        private static object CrateOfActor(object actor)
        {
            if (!(actor is Component mb)) return null;
            if (_crateType == null) _crateType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.CrateComponent");
            return _crateType != null ? mb.GetComponent(_crateType) : null;
        }

        private static byte[] Encode(uint seq, int netId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            { w.Write(seq); w.Write(netId); return ms.ToArray(); }
        }

        private static bool TryDecode(byte[] data, out uint seq, out int netId)
        {
            seq = 0; netId = -1;
            if (data == null || data.Length < 8) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                { seq = r.ReadUInt32(); netId = r.ReadInt32(); return true; }
            }
            catch { return false; }
        }
    }

    /// <summary>Thin Harmony glue: HOST postfix on <c>CrateComponent.Open()</c> broadcasts the crate-open so peers
    /// mirror the world-visual (<see cref="TacticalCrateSync"/>). The single native chokepoint every open path
    /// funnels through. Auto-register via PatchAll.</summary>
    [HarmonyPatch]
    public static class CrateComponentOpenBroadcastPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.CrateComponent");
            if (t == null) return false;
            _target = AccessTools.Method(t, "Open", Type.EmptyTypes);   // public void Open()
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try { TacticalCrateSync.HostBroadcastCrateOpen(__instance); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] CrateComponentOpenBroadcastPatch failed: " + ex); }
        }
    }
}
