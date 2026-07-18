using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE RAIL, client side (law 3 projector): decodes <see cref="SurfaceIds.GeoRail"/> delta packets,
    /// locates the live entity by walking the SAME <see cref="IdentityResolver"/> keys over the client's
    /// own graph (key resolution is symmetric — the client mirrors the host structure), and sets the
    /// field through the cached metadata accessor. The whole batch runs inside
    /// <see cref="SyncApplyScope"/> (law 8). After the batch, every touched entity fires its native
    /// repaint through <see cref="UiEventMap"/> (law 11 — open UI repaints instantly).
    ///
    /// Unknown entity path (not spawned on the client yet) → log once + skip: creating identity is the
    /// STRUCTURAL layer's job (law 3), never the value rail's. Seq gap → request a full resend from the
    /// host (law 7 resync-on-gap), while still applying the current packet (values are idempotent).
    /// </summary>
    public static class GenericApplier
    {
        private static readonly SurfaceSeq Seq = new SurfaceSeq();
        private static readonly Dictionary<byte, RailType> _kinds = new Dictionary<byte, RailType>();
        private static readonly HashSet<byte> _brokenKinds = new HashSet<byte>();
        private static Dictionary<string, object> _pathCache = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly HashSet<string> _loggedMisses = new HashSet<string>(StringComparer.Ordinal);
        private static uint _lastSeq;
        private static float _nextResyncReqAt;

        public static void Reset()
        {
            ResetForReloadBoundary();
            Seq.Reset();
            _kinds.Clear();
            _brokenKinds.Clear();
            _lastSeq = 0;
        }

        public static void ResetForReloadBoundary()
        {
            _pathCache = new Dictionary<string, object>(StringComparer.Ordinal);
            _loggedMisses.Clear();
            // The transferred save just replaced this client's clock — re-seed the anchor scratch from it,
            // or the next partial anchor would layer onto pre-reload values.
            TimeAnchor.Reset();
            // seq + kind registry persist (rca-3 contract: host counters keep increasing across reloads)
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>Returns true when the surface was consumed (GeoRail, either direction).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoRail) return false;
            if (payload == null || payload.Length == 0) return true;
            try
            {
                if (payload[0] == DiffEngine.MsgResyncRequest)
                {
                    if (engine != null && engine.IsHost) DiffEngine.RequestFullResend();
                    return true;
                }
                if (engine == null || engine.IsHost) return true; // host never applies its own surface
                if (payload[0] == DiffEngine.MsgDelta) ApplyDelta(engine, payload);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] GenericApplier inbound failed: " + ex); }
            return true;
        }

        private static void ApplyDelta(NetworkEngine engine, byte[] payload)
        {
            var geo = GeoLevel();
            if (geo == null) return; // mid-load: the reload boundary + save transfer own this window

            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                r.ReadByte(); // MsgDelta
                uint seq = r.ReadUInt32();
                if (!Seq.ShouldApply(SurfaceIds.GeoRail, seq)) return; // stale
                if (_lastSeq != 0 && seq > _lastSeq + 1) RequestResync(engine, seq);
                _lastSeq = seq;
                Seq.Mark(SurfaceIds.GeoRail, seq);

                int defCount = r.ReadByte();
                for (int i = 0; i < defCount; i++) RegisterKind(r.ReadByte(), r.ReadString(), r.ReadUInt16());

                int n = r.ReadUInt16();
                _pathCache.Clear(); // batch-local: a new instance under the same key (re-queued research) must re-resolve
                var touched = new HashSet<object>();
                using (SyncApplyScope.Enter())
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte kindId = r.ReadByte();
                        string path = r.ReadString();
                        ushort fieldIdx = r.ReadUInt16();
                        string subKey = r.ReadString();
                        var value = r.ReadBytes(r.ReadUInt16());
                        ApplyEntry(geo, kindId, path, fieldIdx, subKey, value, touched);
                    }
                }
                // The anchor is a DTO, so the leaf applies above only filled it in — this is where it becomes
                // the clock. Post-batch, and outside SyncApplyScope because ProcessInstanceData fires nothing.
                TimeAnchor.ApplyIfTouched(geo, touched);
                UiEventMap.Fire(touched, geo);
                // Law 11 UNIVERSAL: after the batch, re-drive the open geoscape screen through its native
                // full-rebuild so ALL screens repaint with no per-panel code. Dirty flag only — coalesced
                // to one re-enter per frame by OpenUiRepaint.FlushIfDirty (SyncEngine.Tick). Skip a no-op
                // batch (every entry missed → nothing changed on this client).
                if (touched.Count > 0) OpenUiRepaint.MarkDirty();
            }
        }

        private static void RegisterKind(byte kindId, string typeName, ushort fieldCount)
        {
            if (_kinds.ContainsKey(kindId) || _brokenKinds.Contains(kindId)) return;
            var t = AccessTools.TypeByName(typeName);
            var rt = t == null ? null : RailType.Get(t);
            if (rt == null || rt.Fields.Count != fieldCount)
            {
                _brokenKinds.Add(kindId);
                Debug.LogError("[Multiplayer][rail] GenericApplier: kind " + kindId + " (" + typeName + ") " +
                               (rt == null ? "unresolvable" : "field count mismatch " + rt.Fields.Count + "≠" + fieldCount) +
                               " — entries of this kind will be skipped (mod parity?)");
                return;
            }
            _kinds[kindId] = rt;
        }

        private static void ApplyEntry(GeoLevelController geo, byte kindId, string path, ushort fieldIdx,
                                       string subKey, byte[] value, HashSet<object> touched)
        {
            if (!_kinds.TryGetValue(kindId, out var rt))
            {
                // Broken kind already logged at register; a kindId never registered at all (def packet
                // lost before its first referencing entry) would otherwise drop silently — log it once.
                if (!_brokenKinds.Contains(kindId)) LogMissOnce("unknown kindId " + kindId + " (def not received — resync?)");
                return;
            }
            if (fieldIdx >= rt.Fields.Count) return;
            var field = rt.Fields[fieldIdx];
            if (field.Class == FieldClass.Excluded) return;

            var entity = IdentityResolver.Resolve(geo, path, _pathCache);
            if (entity == null)
            {
                // Possibly a stale cache after a structural change — retry once uncached.
                _pathCache.Remove(path);
                entity = IdentityResolver.Resolve(geo, path, _pathCache);
            }
            if (entity == null) { LogMissOnce("entity not found: " + path); return; }
            if (!rt.Type.IsInstanceOfType(entity)) { LogMissOnce("type mismatch at " + path + ": " + entity.GetType().Name + " vs " + rt.Type.Name); return; }

            try
            {
                switch (field.Class)
                {
                    case FieldClass.Leaf:
                    {
                        var v = RailMeta.DecodeFieldValue(value, field, geo, out _);
                        field.SetValue(entity, v);
                        break;
                    }
                    case FieldClass.LeafDict:
                    {
                        var target = field.GetValue(entity);
                        if (!(target is IDictionary dict))
                        {
                            if (target != null) LogMissOnce("dict field not a non-generic IDictionary at " + path + "." + field.Name + " (" + target.GetType().Name + ")");
                            return;
                        }
                        var key = RailMeta.DecodeDictKey(subKey, field.KeyType);
                        // Explicit delete carries the tombstone sentinel; LeafKind.Null is a genuine present-null value.
                        if (value.Length == 1 && value[0] == RailMeta.DictTombstone) { dict.Remove(key); break; }
                        dict[key] = RailMeta.DecodeFieldValue(value, field, geo, out _);
                        break;
                    }
                    case FieldClass.LeafList:
                    {
                        var items = RailMeta.DecodeFieldValue(value, field, geo, out var isNull) as List<object>;
                        RailMeta.ApplyList(entity, field, isNull ? null : items);
                        break;
                    }
                    case FieldClass.EntityList:
                    case FieldClass.EntityCollection: // host walk-time fallback: unkeyable/duplicate elements ride as one list blob
                    {
                        // EntityCollection descend itself never carries values — a valued entry here is
                        // always an EntityList blob (whole list, order inside the payload, law 2).
                        if (value.Length == 0 || value[0] != RailMeta.EntityListMarker) return;
                        RailMeta.ApplyList(entity, field, RailMeta.DecodeEntityList(value, field, geo));
                        break;
                    }
                    case FieldClass.GeoItemDict:
                    {
                        var target = field.GetValue(entity);
                        if (!(target is IDictionary dict))
                        {
                            if (target != null) LogMissOnce("GeoItemDict field not a non-generic IDictionary at " + path + "." + field.Name + " (" + target.GetType().Name + ")");
                            return;
                        }
                        var def = GeoItemCodec.ResolveDef(subKey); // key IS the ItemDef
                        if (def == null) { LogMissOnce("GeoItemDict unknown item def " + subKey + " at " + path); return; }
                        // DIRECT dict write / remove — NOT AddItem/RemoveItem (those fire StorageChanged/ItemAdded
                        // events + faction ammo-unload = gameplay side-effects a projector client must not run).
                        if (value.Length == 1 && value[0] == RailMeta.DictTombstone) { dict.Remove(def); break; }
                        var giv = GeoItemCodec.Decode(value, def);
                        dict[def] = giv;
                        // [mfgdiag] boundary: mirrored client count per def (real-loss vs visual proof; remove after diag).
                        Debug.Log("[Multiplayer][mfgdiag] CLIENT storage def=" + subKey + " -> clientMirrorCount=" + giv.CommonItemData.Count);
                        break;
                    }
                    default:
                        return; // Descend never carries values
                }
                touched.Add(entity);
            }
            catch (Exception ex)
            {
                LogMissOnce("apply failed " + path + "." + field.Name + ": " + ex.Message);
            }
        }

        private static void LogMissOnce(string msg)
        {
            if (_loggedMisses.Count < 500 && _loggedMisses.Add(msg))
                Debug.LogWarning("[Multiplayer][rail] GenericApplier: " + msg);
        }

        private static void RequestResync(NetworkEngine engine, uint seq)
        {
            if (Time.realtimeSinceStartup < _nextResyncReqAt) return;
            _nextResyncReqAt = Time.realtimeSinceStartup + 5f;
            Debug.LogWarning("[Multiplayer][rail] GenericApplier: seq gap (" + _lastSeq + "→" + seq + ") — requesting full resend");
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoRail, SyncKind.ActionRequest,
                                                      new[] { DiffEngine.MsgResyncRequest });
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] GenericApplier resync request failed: " + ex.Message); }
        }
    }
}
