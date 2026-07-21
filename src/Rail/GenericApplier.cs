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
                if (_lastSeq != 0 && seq > _lastSeq + 1)
                    RequestResync(engine, "seq gap (" + _lastSeq + "→" + seq + ")");

                var touched = new HashSet<object>();
                try
                {
                    int defCount = r.ReadByte();
                    for (int i = 0; i < defCount; i++) RegisterKind(r.ReadByte(), r.ReadString(), r.ReadUInt16());

                    int n = r.ReadUInt16();
                    _pathCache.Clear(); // batch-local: a new instance under the same key (re-queued research) must re-resolve
                    using (SyncApplyScope.Enter())
                    {
                        for (int i = 0; i < n; i++)
                        {
                            byte kindId = r.ReadByte();
                            string path = r.ReadString();
                            ushort fieldIdx = r.ReadUInt16();
                            string subKey = r.ReadString();
                            var value = r.ReadBytes(r.ReadUInt16());
                            ApplyEntry(engine, geo, kindId, path, fieldIdx, subKey, value, touched);
                        }
                    }
                    // The anchor is a DTO, so the leaf applies above only filled it in — this is where it becomes
                    // the clock. Post-batch, and outside SyncApplyScope because ProcessInstanceData fires nothing.
                    TimeAnchor.ApplyIfTouched(geo, touched);
                }
                catch (Exception ex)
                {
                    // Reader-level failure mid-batch: leave the seq UNMARKED (SurfaceSeq contract — a failed
                    // apply must not consume the seq) and recover the lost entries via the throttled resync.
                    // Per-entry failures never land here (ApplyEntry catches its own); this is a torn packet.
                    Debug.LogError("[Multiplayer][rail] GenericApplier: batch failed at seq " + seq + ": " + ex);
                    RequestResync(engine, "batch failed at seq " + seq);
                    return;
                }
                // Mark ONLY after the whole batch applied (SurfaceSeq.cs contract). Native events raised
                // during the apply run synchronously inside SyncApplyScope and never pump the network, so
                // nothing re-enters the seq logic while it is still unmarked.
                _lastSeq = seq;
                Seq.Mark(SurfaceIds.GeoRail, seq);
                // Law 11 lives ENTIRELY in UiEventMap.Fire: mapped kinds repaint through their own native
                // events, unmapped kinds + ItemStorage mark the open screen dirty there. No unconditional
                // MarkDirty here — a batch of only mapped kinds (host clock ticking Timing/Wallet/Research
                // every geo hour) must NOT Exit+Enter the client's open screen.
                UiEventMap.Fire(touched, geo);
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

        private static void ApplyEntry(NetworkEngine engine, GeoLevelController geo, byte kindId, string path, ushort fieldIdx,
                                       string subKey, byte[] value, HashSet<object> touched)
        {
            if (!_kinds.TryGetValue(kindId, out var rt))
            {
                // Broken kind already logged at register. A kindId never registered at all = this client
                // missed the def packet — the mid-session joiner shape: host sends each kind def ONCE per
                // client set, the joiner dropped the in-load packets pre-seq, and _lastSeq==0 skipped the
                // gap check, so without this every pre-join kind stays dead FOREVER. The full resend clears
                // the host's _sentKinds, so defs re-ship with it. Throttled (one request per window), and
                // the values missed during the load window ride the same resend.
                if (!_brokenKinds.Contains(kindId))
                {
                    LogMissOnce("unknown kindId " + kindId + " (def not received)");
                    RequestResync(engine, "unknown kindId " + kindId);
                }
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
            if (!rt.Type.IsInstanceOfType(entity))
            {
                // The resolver returned the LIVE TWIN of a recorded *InstanceData DTO (writes into the
                // getter-minted DTO are void — see IdentityResolver). Same member source + ordinal sort ⇒
                // the wire fieldIdx addresses the same-named member in the bridged table.
                var bt = RailType.GetBridged(entity.GetType(), rt.Type);
                var bf = bt != null && bt.Fields.Count == rt.Fields.Count ? bt.Fields[fieldIdx] : null;
                if (bf == null || !string.Equals(bf.Name, field.Name, StringComparison.Ordinal))
                { LogMissOnce("type mismatch at " + path + ": " + entity.GetType().Name + " vs " + rt.Type.Name); return; }
                if (bf.Class == FieldClass.Excluded)
                { LogMissOnce("dto-twin gap: " + rt.Type.Name + "." + bf.Name + " has no live counterpart on " + entity.GetType().Name + " (" + bf.Exclude + ") — not mirrored"); return; }
                field = bf;
            }
            if (Unchanged(entity, field, subKey, value)) return; // no-op entry: not applied, not touched, no repaint

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
                    {
                        // EntityList ONLY. EntityCollection is element-addressed (path.Name#key); the one
                        // field-level entry it ships is the order vector (own case below) — never a blob,
                        // because rebuilding keyed elements from a blob husks them (see DiffEngine's keyless
                        // branch). Ship side and apply side stay symmetric: what the host refuses to send,
                        // the client refuses to reconstruct.
                        if (value.Length == 0 || value[0] != RailMeta.EntityListMarker) return;
                        var items = RailMeta.DecodeEntityList(value, field, geo);
                        // Keep LIVE instances wherever the blob element is value-identical: a pure reorder
                        // then MOVES the client's existing objects (order rides inside the blob), and state
                        // the blob cannot carry (AmmoManager) survives on every unchanged element.
                        RailMeta.ReuseLiveElements(field, field.GetValue(entity), items);
                        RailMeta.ApplyList(entity, field, items);
                        break;
                    }
                    case FieldClass.EntityCollection:
                    {
                        // The keyed-collection ORDER channel: the only field-level entry an EntityCollection
                        // ever ships is the host's live key sequence of an ordered container (DiffEngine.
                        // AddKeyOrder). Reorder the live list IN PLACE by key — elements are live entities,
                        // never rebuilt here; unknown keys wait for their structural create.
                        if (value.Length == 0 || value[0] != RailMeta.OrderVectorMarker) return;
                        if (!RailMeta.ReorderByKeys(field.GetValue(entity), RailMeta.DecodeKeyOrder(value))) return;
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
                        dict[def] = GeoItemCodec.Decode(value, def);
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

        /// <summary>
        /// UNIVERSAL no-op test — the gate between "a delta arrived" and "something on this client changed".
        /// Encodes what the client ALREADY holds with the SAME canonical codec the host used to produce the
        /// wire value, then compares bytes. Identical ⇒ the entry is dropped: not written, not added to
        /// <c>touched</c>, so it drives neither <see cref="UiEventMap"/> nor <see cref="OpenUiRepaint"/>.
        /// That is what makes law 11 fire on real CHANGE instead of on traffic — a redelivered packet or a
        /// resync full-resend (host re-emits every covered pair, all of them already equal here) used to
        /// force a full Exit→Enter screen rebuild per batch.
        ///
        /// Symmetric by construction, not by a table: the host's own "changed?" question is byte inequality
        /// of these same encoders (DiffEngine snapshot), so no field/kind can be classified here that the
        /// host classifies differently, and no per-kind allowlist exists to drift.
        ///
        /// Encoder throws, or a shape this codec cannot restate ⇒ CHANGED. Applying a value that was already
        /// equal is idempotent (law 7) and costs one repaint; wrongly skipping one strands a stale screen,
        /// which law 11 forbids. The asymmetry is deliberate — the fallback is always "repaint".
        /// </summary>
        private static bool Unchanged(object entity, RailField field, string subKey, byte[] value)
        {
            try
            {
                switch (field.Class)
                {
                    case FieldClass.Leaf:
                    case FieldClass.LeafList:
                        return SameBytes(value, RailMeta.EncodeFieldValue(field, field.GetValue(entity)));
                    case FieldClass.EntityList:
                        return SameBytes(value, RailMeta.EncodeEntityList(field, field.GetValue(entity)));
                    case FieldClass.EntityCollection:
                    {
                        // Order vector: restate the local live key sequence with the host's own encoder.
                        // Unkeyable local element → null → "changed" (ReorderByKeys copes per element).
                        var mine = RailMeta.EncodeKeyOrderOf(field.GetValue(entity));
                        return mine != null && SameBytes(value, mine);
                    }
                    case FieldClass.LeafDict:
                    {
                        if (!(field.GetValue(entity) is IDictionary dict)) return false;
                        var key = RailMeta.DecodeDictKey(subKey, field.KeyType);
                        bool tomb = value.Length == 1 && value[0] == RailMeta.DictTombstone;
                        if (key == null || !dict.Contains(key)) return tomb; // absent: a delete is the no-op
                        return !tomb && SameBytes(value, RailMeta.EncodeFieldValue(field, dict[key]));
                    }
                    case FieldClass.GeoItemDict:
                    {
                        if (!(field.GetValue(entity) is IDictionary dict)) return false;
                        var def = GeoItemCodec.ResolveDef(subKey);
                        bool tomb = value.Length == 1 && value[0] == RailMeta.DictTombstone;
                        if (def == null || !dict.Contains(def)) return tomb;
                        return !tomb && SameBytes(value, GeoItemCodec.Encode(dict[def]));
                    }
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void LogMissOnce(string msg)
        {
            if (_loggedMisses.Count < 500 && _loggedMisses.Add(msg))
                Debug.LogWarning("[Multiplayer][rail] GenericApplier: " + msg);
        }

        /// <summary>ONE throttled gate onto the law-7 resync path, whatever noticed the divergence (seq gap,
        /// unknown kind def, torn batch). The throttle is global on purpose: a systematic miss hits every
        /// entry of every packet, and one full resend answers all of them.</summary>
        private static void RequestResync(NetworkEngine engine, string reason)
        {
            if (engine == null || Time.realtimeSinceStartup < _nextResyncReqAt) return;
            _nextResyncReqAt = Time.realtimeSinceStartup + 5f;
            Debug.LogWarning("[Multiplayer][rail] GenericApplier: " + reason + " — requesting full resend");
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
