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

        // ─── Membership convergence (what makes the mirror a MIRROR) ───────
        // Every other entry on the rail is derived from what the HOST HAS, so a key only the CLIENT has is
        // invisible to the protocol and would live forever. The host's membership statement (DiffEngine
        // AddMembership) is the missing assertion — "the set is EXACTLY this" — and it is the client that
        // enforces it, locally and for free: no extra traffic, no round trip, no host-side detection of a
        // divergence only the client can see.
        private sealed class Statement
        {
            public RailType Rt; public ushort FieldIdx; public string Path; public HashSet<uint> Hashes;
        }
        private static Dictionary<string, Statement> _membership = new Dictionary<string, Statement>(StringComparer.Ordinal);
        // (field, key) → realtime it FIRST looked unstated. The pending-intent / chunk-boundary grace:
        // a key whose value entry landed in an earlier chunk than its statement, or whose intent is still
        // in flight, is never deleted because the statement always arrives well inside GraceSeconds.
        private static Dictionary<string, float> _suspectSince = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly HashSet<string> _loud = new HashSet<string>(StringComparer.Ordinal);
        private static float _nextSweepAt;
        private const float SweepInterval = 1f;
        private const float GraceSeconds = 3f;

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
            // Statements describe the PRE-reload graph; the host's own snapshot resets at the same boundary
            // and re-baselines, which re-ships every statement. Holding stale ones would sweep live state.
            _membership = new Dictionary<string, Statement>(StringComparer.Ordinal);
            _suspectSince = new Dictionary<string, float>(StringComparer.Ordinal);
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
                    RequestResync(engine, "seq gap (" + _lastSeq + " -> " + seq + ")");
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

            // A membership statement is protocol metadata, not a value: record it and return. Enforcement is
            // deferred to ConvergenceTick so an in-flight batch (values and statement can land in different
            // chunks) is never mistaken for divergence.
            if (subKey == RailMeta.MembershipSubKey)
            {
                var stKey = path + "\u0001" + fieldIdx;
                // The field vanished on the host (dict went null / collection removed) → the tombstone loop
                // tombstones the statement's own subKey. Withdraw it: state nothing rather than sweep blind.
                if (value.Length == 1 && value[0] == RailMeta.DictTombstone) { _membership.Remove(stKey); return; }
                var set = RailMeta.DecodeMembership(value);
                if (set == null) { LogMissOnce("malformed membership statement at " + path + "." + field.Name); return; }
                _membership[stKey] = new Statement { Rt = rt, FieldIdx = fieldIdx, Path = path, Hashes = set };
                return;
            }

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
                        if (MpDiag.On)
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

        // ─── THE CONVERGENCE SWEEP (client) ───────────────────────────────

        /// <summary>Client half of the rail tick (driven from <see cref="DiffEngine.HostTick"/>, the shared
        /// per-frame driver). Enforces every membership statement the host has made against this client's
        /// OWN collections: a key the client holds that the authoritative statement does not list is
        /// client-only garbage and is removed.
        ///
        /// Why this direction: a diff of the host graph can only ever express keys the HOST HAS, so the
        /// host is structurally blind to a divergence that exists only on the client. The statement moves
        /// the detection to the only peer that can see it, and costs the wire nothing — the client is
        /// checking state it already has against a statement it already holds.
        ///
        /// A statement is always authoritative-COMPLETE, never incremental (the host emits the whole key
        /// set or nothing at all, and "nothing" means "unchanged"), so acting on a retained one is safe.
        /// No statement for a field (never sent, withdrawn, or the host excluded that instance) → that
        /// field is simply not swept: we never guess.</summary>
        public static void ConvergenceTick(NetworkEngine engine)
        {
            if (_membership.Count == 0 || Time.realtimeSinceStartup < _nextSweepAt) return;
            _nextSweepAt = Time.realtimeSinceStartup + SweepInterval;
            var geo = GeoLevel();
            if (geo == null) return;
            try { Sweep(engine, geo); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] GenericApplier sweep failed: " + ex); }
        }

        private static void Sweep(NetworkEngine engine, GeoLevelController geo)
        {
            var cache = new Dictionary<string, object>(StringComparer.Ordinal);
            var stillSuspect = new Dictionary<string, float>(StringComparer.Ordinal);
            float now = Time.realtimeSinceStartup;
            var touched = new HashSet<object>();
            bool wantResync = false;
            int removed = 0;

            using (SyncApplyScope.Enter()) // law 8: a removal must never echo back as an intent
            foreach (var st in _membership.Values)
            {
                var entity = IdentityResolver.Resolve(geo, st.Path, cache);
                if (entity == null || !st.Rt.Type.IsInstanceOfType(entity)) continue; // not on this client — nothing to police
                var field = st.Rt.Fields[st.FieldIdx];
                object val;
                try { val = field.GetValue(entity); } catch { continue; }
                if (val == null) continue;

                if (field.Class == FieldClass.EntityCollection)
                {
                    if (!(val is IEnumerable col)) continue;
                    foreach (var e in col)
                    {
                        var k = e == null ? null : IdentityResolver.KeyOf(e);
                        if (k == null || st.Hashes.Contains(RailMeta.KeyHash(k))) continue;
                        if (Age(stillSuspect, now, st.Path, st.FieldIdx, k) < GraceSeconds) continue;
                        // Deleting an ELEMENT is identity destruction = the structural layer (law 3); the
                        // value rail must not do it. Ask for a resync instead: the host's force-full re-emits
                        // this collection's whole-list blob, which the client applies wholesale — the local
                        // extra dies in the rebuild. Throttled to 5 s by RequestResync.
                        Loud("client-only element " + k + " in " + st.Path + "." + field.Name +
                             " — absent from the host membership statement; requesting a structural rebuild");
                        wantResync = true;
                        break;
                    }
                    continue;
                }

                if (!(val is IDictionary dict)) continue;
                List<object> kill = null;
                foreach (DictionaryEntry de in dict)
                {
                    if (de.Key == null) continue;
                    string sub;
                    try
                    {
                        sub = field.Class == FieldClass.GeoItemDict
                            ? GeoItemCodec.SubKey(de.Key)
                            : RailMeta.EncodeDictKey(de.Key);
                    }
                    catch { continue; }
                    if (sub == null || st.Hashes.Contains(RailMeta.KeyHash(sub))) continue;
                    if (Age(stillSuspect, now, st.Path, st.FieldIdx, sub) < GraceSeconds) continue;
                    (kill ?? (kill = new List<object>())).Add(de.Key);
                    // THE SELF-CHECK: on a correctly sealed client this line can never run — every write to
                    // authoritative state is supposed to come from the host. Reaching it means some seam let
                    // the client create state locally, so it is an ERROR, not an info line. Loud is one-shot
                    // per distinct message, so a stuck field cannot drown the log.
                    Loud("client-only key " + sub + " in " + st.Path + "." + field.Name +
                         " — absent from the host membership statement; REMOVED. A seam is writing " +
                         "authoritative state locally instead of sending an intent — find it.");
                }
                if (kill == null) continue;
                foreach (var k in kill) dict.Remove(k); // deferred: never mutate mid-enumeration
                removed += kill.Count;
                touched.Add(entity);
            }

            _suspectSince = stillSuspect; // rebuilt each sweep — a key that stopped being suspect self-prunes
            if (removed > 0)
            {
                UiEventMap.Fire(touched, geo);
                OpenUiRepaint.MarkDirty(); // law 11: the open screen must show the corrected state at once
                Debug.LogError("[Multiplayer][rail] GenericApplier CONVERGENCE: pruned " + removed +
                               " client-only key(s) across " + touched.Count + " entities");
            }
            if (wantResync) RequestResync(engine, "client-only collection element");
        }

        /// <summary>Seconds this (field, key) has looked unstated WITHOUT interruption. The grace window is
        /// the answer to both races: a value entry that landed in an earlier chunk than its statement, and a
        /// client-side write that is legitimately pending its host echo. Both resolve in milliseconds; the
        /// window is seconds. Returning 0 on first sighting is what makes the first sweep never delete.</summary>
        private static float Age(Dictionary<string, float> stillSuspect, float now, string path, ushort fieldIdx, string sub)
        {
            var k = path + "\u0001" + fieldIdx + "\u0001" + sub;
            float since = _suspectSince.TryGetValue(k, out var t) ? t : now;
            stillSuspect[k] = since;
            return now - since;
        }

        private static void Loud(string msg)
        {
            if (_loud.Count < 200 && _loud.Add(msg))
                Debug.LogError("[Multiplayer][rail] GenericApplier CONVERGENCE: " + msg);
        }

        private static void RequestResync(NetworkEngine engine, string reason)
        {
            if (Time.realtimeSinceStartup < _nextResyncReqAt) return;
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
