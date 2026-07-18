using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.View.ViewControllers.Inventory;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// UX advisory item-claim: while one player DRAGS an item on the equip/storage screen, that
    /// item's def greys out (native "Incompatible" look, handlers dead) for every OTHER peer, so two
    /// people don't grab the same thing. NOT a lock — the host already serializes all writes; a lost
    /// race is merely a host reject. Claim state is EPHEMERAL presence data: broadcast on surface
    /// 0xAF, kept in memory only, never on the persistent value rail.
    ///
    /// Capture seam (law 4c, presentation): every drag lifecycle path in UIModuleSoldierEquip funnels
    /// through UIInventoryItemDragIcon.BeginDrag/EndDrag (drop :1372, interrupt :1407, force-cancel
    /// :899/:1102, screen re-open OnMenuEnter :32) — one postfix pair covers begin + drop + interrupt
    /// + screen exit. Release is unconditional + idempotent (EndDrag with no local claim = no-op).
    ///
    /// Grey visual (native, reused): UIInventorySlot.EventHandlersEnabled=false BOTH kills the slot's
    /// drag/click handlers (:555/:592/:607/:622) AND is OR-ed into the native grey animator state
    /// ("Incompatible" bool, :238) — one property = greyed AND not-takeable; Disabled is a bare flag
    /// with no visual, Incompatible alone greys but stays draggable. Applied in a SetItem/OnEnable
    /// postfix so every native rebuild (incl. the OpenUiRepaint Exit→Enter) restyles itself; OnEnable
    /// natively resets EventHandlersEnabled=true (:296) so un-grey rides the same reset.
    ///
    /// Repaint: claim table changes go through the EXISTING universal seam (OpenUiRepaint.MarkDirty)
    /// — deferred while OUR OWN drag is in flight (the Exit→Enter would kill it), flushed when it ends.
    ///
    /// Identity limits (honest): items have NO instance identity (GeoItem hashes by def), so a claim
    /// is (ItemDef.Guid) only — dragging one of a stack of 5 greys the whole stack AND the same def
    /// equipped elsewhere on the open screen, for everyone else. Advisory; host still arbitrates.
    /// </summary>
    public static class ClaimSync
    {
        private const byte OpClaim = 1;
        private const byte OpRelease = 2;
        private const byte OpReleaseAll = 3;   // all claims of one token (peer disconnect)

        // ponytail: 30 s TTL backstop — a stuck grey is worse than an early clear; a genuine
        // longer-than-30s drag loses its grey on remote screens (no keep-alive; add one if it matters).
        private const float ClaimTtlSeconds = 30f;

        /// <summary>Session-local sender identity riding inside the payload (peers can't compare raw
        /// transport peer ids across hops); receivers drop ops carrying their own token.</summary>
        private static readonly uint Token = (uint)Guid.NewGuid().GetHashCode();

        private struct Claim { public uint Tok; public float Deadline; }
        private static readonly Dictionary<string, Claim> Claims = new Dictionary<string, Claim>();     // defGuid → REMOTE claim
        private static readonly Dictionary<ulong, uint> PeerTokens = new Dictionary<ulong, uint>();     // host: sender peer → token (disconnect cleanup)
        private static readonly HashSet<UIInventorySlot> Touched = new HashSet<UIInventorySlot>();      // slots WE greyed (only these are ever restored)
        private static string _localClaimGuid;      // def guid of our own in-flight drag (null = none)
        private static bool _deferredDirty;         // repaint postponed while our own drag is in flight
        private static NetworkEngine _subscribedEngine;

        // ─── Harmony: local drag capture (all drag paths funnel through the drag icon) ───

        [HarmonyPatch(typeof(UIInventoryItemDragIcon), nameof(UIInventoryItemDragIcon.BeginDrag))]
        private static class BeginDragPatch
        {
            private static void Postfix(ItemDef itemDef) => LocalDragBegan(itemDef);
        }

        [HarmonyPatch(typeof(UIInventoryItemDragIcon), nameof(UIInventoryItemDragIcon.EndDrag))]
        private static class EndDragPatch
        {
            private static void Postfix() => LocalDragEnded();
        }

        // ─── Harmony: native grey restyle (every slot rebuild path ends in SetItem; OnEnable is the
        //     native enabled-reset, so re-assert / clear right after both) ───

        [HarmonyPatch(typeof(UIInventorySlot), "SetItem")]
        private static class SetItemPatch
        {
            private static void Postfix(UIInventorySlot __instance) => Restyle(__instance);
        }

        [HarmonyPatch(typeof(UIInventorySlot), "OnEnable")]
        private static class OnEnablePatch
        {
            private static void Postfix(UIInventorySlot __instance) => Restyle(__instance);
        }

        // ─── local drag lifecycle ───

        private static void LocalDragBegan(ItemDef itemDef)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || itemDef == null || string.IsNullOrEmpty(itemDef.Guid)) return;
            _localClaimGuid = itemDef.Guid;
            SendEnvelope(engine, BuildPayload(OpClaim, Token, _localClaimGuid));
            Debug.Log("[MP][claim] claim sent def=" + _localClaimGuid);
        }

        private static void LocalDragEnded()
        {
            if (_localClaimGuid != null)
            {
                string guid = _localClaimGuid;
                _localClaimGuid = null;
                var engine = NetworkEngine.Instance;
                if (engine != null && engine.IsActiveSession)
                {
                    SendEnvelope(engine, BuildPayload(OpRelease, Token, guid));
                    Debug.Log("[MP][claim] release sent def=" + guid);
                }
            }
            if (_deferredDirty) { _deferredDirty = false; OpenUiRepaint.MarkDirty(); }
        }

        // ─── wire ───

        private static byte[] BuildPayload(byte op, uint token, string defGuid)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(op);
                w.Write(token);
                w.Write(defGuid ?? "");
                return ms.ToArray();
            }
        }

        private static void SendEnvelope(NetworkEngine engine, byte[] inner)
        {
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoItemClaim,
                    engine.IsHost ? SyncKind.StateDelta : SyncKind.ActionRequest, inner);
                var msg = new NetworkMessage(PacketType.SyncEnvelope, env);
                if (engine.IsHost) engine.BroadcastToAll(msg);
                else engine.SendToHost(msg);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][claim] send failed: " + ex.Message);
            }
        }

        /// <summary>Armed as part of SurfaceRouter.GeoscapeInbound. Consumes surface 0xAF only.
        /// Host additionally relays every claim op to all clients (originators drop their own token).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoItemClaim) return false;
            try
            {
                byte op;
                uint token;
                string guid;
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    op = r.ReadByte();
                    token = r.ReadUInt32();
                    guid = r.ReadString();
                }
                if (engine != null && engine.IsHost)
                {
                    PeerTokens[senderPeerId] = token;   // disconnect-cleanup map
                    var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoItemClaim, SyncKind.StateDelta, payload);
                    engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                }
                if (token != Token) Apply(op, token, guid);   // own echo dropped
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][claim] inbound failed: " + ex.Message);
            }
            return true;
        }

        // ─── claim table ───

        private static void Apply(byte op, uint token, string defGuid)
        {
            bool changed = false;
            if (op == OpClaim && !string.IsNullOrEmpty(defGuid))
            {
                Claims[defGuid] = new Claim { Tok = token, Deadline = Time.realtimeSinceStartup + ClaimTtlSeconds };
                changed = true;
                Debug.Log("[MP][claim] claim received def=" + defGuid);
            }
            else if (op == OpRelease && !string.IsNullOrEmpty(defGuid))
            {
                Claim c;
                if (Claims.TryGetValue(defGuid, out c) && c.Tok == token)
                {
                    Claims.Remove(defGuid);
                    changed = true;
                    Debug.Log("[MP][claim] claim released def=" + defGuid);
                }
            }
            else if (op == OpReleaseAll)
            {
                var dead = new List<string>();
                foreach (var kv in Claims) if (kv.Value.Tok == token) dead.Add(kv.Key);
                foreach (var g in dead)
                {
                    Claims.Remove(g);
                    Debug.Log("[MP][claim] claim released def=" + g + " (peer gone)");
                }
                changed = dead.Count > 0;
            }
            if (!changed) return;
            SweepTouched();
            RepaintOrDefer();
        }

        private static void RepaintOrDefer()
        {
            // Exit→Enter would force-cancel OUR in-flight drag (UIModuleSoldierEquip.OnDataChanged:919)
            // — defer until it ends; the SetItem/OnEnable postfixes still grey any slot that rebuilds meanwhile.
            if (_localClaimGuid != null) { _deferredDirty = true; return; }
            OpenUiRepaint.MarkDirty();
        }

        // ─── native grey ───

        private static void Restyle(UIInventorySlot slot)
        {
            if (slot == null || (Claims.Count == 0 && Touched.Count == 0)) return;
            var item = slot.Item;
            string guid = item == null || item.ItemDef == null ? null : item.ItemDef.Guid;
            if (guid != null && Claims.ContainsKey(guid))
            {
                slot.EventHandlersEnabled = false;                 // native: handlers dead + OR-ed into grey animator state
                var a = slot.Animator;
                if (a != null) a.SetBool("Incompatible", true);    // trigger the animator refresh the Incompatible setter does
                Touched.Add(slot);
            }
            else if (Touched.Remove(slot))
            {
                slot.EventHandlersEnabled = true;
                var a = slot.Animator;
                if (a != null) a.SetBool("Incompatible", false);
            }
        }

        /// <summary>Directly restore live slots we greyed whose claim is gone — covers the corner where
        /// the repaint rebuild early-returns in SetItem (same item instance) and OnEnable never refires.</summary>
        private static void SweepTouched()
        {
            if (Touched.Count == 0) return;
            var drop = new List<UIInventorySlot>();
            foreach (var slot in Touched)
            {
                if (slot == null) { drop.Add(slot); continue; }    // Unity-destroyed
                var item = slot.Item;
                string guid = item == null || item.ItemDef == null ? null : item.ItemDef.Guid;
                if (guid != null && Claims.ContainsKey(guid)) continue;
                slot.EventHandlersEnabled = true;
                var a = slot.Animator;
                if (a != null) a.SetBool("Incompatible", false);
                drop.Add(slot);
            }
            foreach (var s in drop) Touched.Remove(s);
        }

        // ─── lifecycle (driven from SyncEngine) ───

        /// <summary>Once per frame: TTL expiry sweep + lazy disconnect-cleanup subscription.</summary>
        public static void Tick(NetworkEngine engine)
        {
            if (engine != null && !ReferenceEquals(engine, _subscribedEngine))
            {
                engine.OnClientDisconnected += OnPeerDisconnected;
                _subscribedEngine = engine;
            }
            if (Claims.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<string> expired = null;
            foreach (var kv in Claims)
                if (kv.Value.Deadline < now) (expired = expired ?? new List<string>()).Add(kv.Key);
            if (expired == null) return;
            foreach (var g in expired)
            {
                Claims.Remove(g);
                Debug.Log("[MP][claim] claim expired def=" + g);
            }
            SweepTouched();
            RepaintOrDefer();
        }

        /// <summary>Host: a peer dropped — release everything it claimed, locally and for all clients.</summary>
        private static void OnPeerDisconnected(ulong peerId)
        {
            uint token;
            if (!PeerTokens.TryGetValue(peerId, out token)) return;
            PeerTokens.Remove(peerId);
            Apply(OpReleaseAll, token, null);
            var engine = _subscribedEngine;
            if (engine != null && engine.IsHost && engine.IsActiveSession)
                SendEnvelope(engine, BuildPayload(OpReleaseAll, token, null));
        }

        /// <summary>Session teardown / reload boundary: claims are ephemeral — drop everything, un-grey.</summary>
        public static void Reset()
        {
            Claims.Clear();
            PeerTokens.Clear();
            _localClaimGuid = null;
            _deferredDirty = false;
            SweepTouched();     // Claims now empty → restores every slot we touched
            Touched.Clear();
        }

        public static void ResetForReloadBoundary() => Reset();
    }
}
