using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Base.Core;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE RAIL, host side (laws 5+6): a UNIVERSAL recursive walk of the live geoscape value layer,
    /// guided ONLY by serializer type metadata (<see cref="RailMeta"/>) — the walker knows NO subsystem.
    /// Every ~0.5 s: walk → flat canonical snapshot (path, fieldIdx, subKey) → boxed-encoded value →
    /// compare against the previous snapshot → emit ONLY changed pairs on surface
    /// <see cref="SurfaceIds.GeoRail"/>. Canonical (law 6): roots and collection children sorted by
    /// stable key, fields in the fixed metadata order, dictionary subkeys sorted — same state ⇒
    /// byte-identical delta.
    ///
    /// Coverage report (the opt-out guarantee): the first full walk of a session dumps every visited
    /// type — covered fields (class, live alias) vs EXCLUDED fields (reason) plus per-walk exclusion
    /// incidents (unkeyable collections etc.) — to the log and to
    /// <c>persistentDataPath/Multiplayer/rail-coverage.txt</c>. Read the report, not the bug tracker.
    ///
    /// First walk after a (re)connect boundary is a BASELINE (no emit): the client received the same
    /// state via the native save transfer (law 1 — join is not a delta). Resync-on-gap (law 7): the
    /// client requests a full resend; the host then emits every covered pair (just a big delta).
    /// </summary>
    public static class DiffEngine
    {
        public const byte MsgDelta = 1;
        public const byte MsgResyncRequest = 2;

        private const float TickInterval = 0.5f;   // ≤2 Hz
        private const int MaxPacketBytes = 45000;  // chunk flush threshold
        private const int MaxValueBytes = 8192;    // per-entry cap: 45000 + 8192 stays under the u16 envelope
        private const int MaxEntities = 50000;     // graph-chase brake
        private const int MaxDepth = 12;

        internal struct Entry
        {
            public byte KindId;
            public string Path;
            public ushort FieldIdx;
            public string SubKey;
            public byte[] Value;
        }

        private static readonly SurfaceSeq Seq = new SurfaceSeq();
        private static Dictionary<string, Entry> _snapshot = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, byte> _kindIds = new Dictionary<Type, byte>();
        private static readonly List<Type> _kinds = new List<Type>();
        private static readonly HashSet<byte> _sentKinds = new HashSet<byte>();
        private static bool _baselined;
        private static bool _forceFull;
        private static float _nextTickAt;
        private static float _nextPerfLogAt;
        private static bool _reportWritten;
        private static readonly HashSet<string> _walkIncidents = new HashSet<string>(); // "(Type.Field): reason [path]" dedup
        private static readonly Dictionary<Type, int> _entityCounts = new Dictionary<Type, int>();

        // ─── Lifecycle (driven by SyncEngine) ──────────────────────────────

        public static void Reset()
        {
            ResetForReloadBoundary();
            Seq.Reset();
            _kindIds.Clear(); _kinds.Clear();
            _reportWritten = false;
        }

        /// <summary>Reload boundary: drop snapshot + baseline (post-reload state reaches clients via the
        /// save transfer); seq streams PERSIST (rca-3 contract) so later deltas keep applying.</summary>
        public static void ResetForReloadBoundary()
        {
            _snapshot = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _sentKinds.Clear();
            _baselined = false;
            _forceFull = false;
        }

        /// <summary>Client lost the stream (seq gap): resend EVERYTHING covered — it is just a big delta.</summary>
        public static void RequestFullResend()
        {
            _forceFull = true;
            _sentKinds.Clear();
            Debug.Log("[Multiplayer][rail] DiffEngine: full resend requested");
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── Host tick: walk → diff → emit ─────────────────────────────────

        public static void HostTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost || !engine.IsActiveSession) return;
            if (Time.realtimeSinceStartup < _nextTickAt) return;
            _nextTickAt = Time.realtimeSinceStartup + TickInterval;
            var geo = GeoLevel();
            if (geo == null) return;

            try { Tick(engine, geo); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] DiffEngine tick failed: " + ex); }
        }

        private static void Tick(NetworkEngine engine, GeoLevelController geo)
        {
            var sw = Stopwatch.StartNew();
            _entityCounts.Clear();
            var ordered = new List<Entry>(_snapshot.Count + 64);
            var index = new Dictionary<string, int>(_snapshot.Count + 64, StringComparer.Ordinal);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var root in IdentityResolver.Roots(geo))
                VisitEntity(root.Key, root.Value, visited, ordered, index, 0);
            long walkMs = sw.ElapsedMilliseconds;

            // Diff: changed/new pairs in walk order (canonical), then subKey deletions.
            var changed = new List<Entry>();
            var newSnap = new Dictionary<string, Entry>(ordered.Count, StringComparer.Ordinal);
            foreach (var e in ordered)
            {
                var key = SnapKey(e);
                newSnap[key] = e;
                if (_forceFull || !_snapshot.TryGetValue(key, out var old) || !RailMeta.BytesEqual(old.Value, e.Value))
                    changed.Add(e);
            }
            var livePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in ordered) livePaths.Add(e.Path);
            foreach (var kv in _snapshot)
            {
                if (kv.Value.SubKey.Length == 0 || newSnap.ContainsKey(kv.Key)) continue;
                // Suppress the tombstone when the whole entity path is gone (moved/vanished) — that is a
                // structural change, not a dict-key delete; emitting one would false-delete on the client
                // (and could hit a different entity that took over the old path). ponytail: an entity whose
                // ONLY covered field is this dict would lose its stale keys here, but such entities don't exist.
                if (!livePaths.Contains(kv.Value.Path)) continue;
                // dictionary key removed on host while the entity persists → explicit tombstone (distinct
                // sentinel so the client never confuses it with a genuine present-null value).
                changed.Add(new Entry { KindId = kv.Value.KindId, Path = kv.Value.Path, FieldIdx = kv.Value.FieldIdx, SubKey = kv.Value.SubKey, Value = new[] { RailMeta.DictTombstone } });
            }
            long diffMs = sw.ElapsedMilliseconds - walkMs;
            _snapshot = newSnap;
            bool wasForceFull = _forceFull;
            _forceFull = false;

            if (!_reportWritten) { WriteCoverageReport(ordered.Count); _reportWritten = true; }

            if (!_baselined && !wasForceFull)
            {
                _baselined = true;
                Debug.Log("[Multiplayer][rail] DiffEngine BASELINE: entities=" + _entityCounts.Values.Sum() +
                          " fields=" + ordered.Count + " walk=" + walkMs + "ms (no emit — clients share the save)");
                return;
            }
            _baselined = true;

            int packets = 0, bytes = 0;
            if (changed.Count > 0)
                Emit(engine, changed, ref packets, ref bytes);

            if (changed.Count > 0 || Time.realtimeSinceStartup >= _nextPerfLogAt)
            {
                _nextPerfLogAt = Time.realtimeSinceStartup + 10f;
                Debug.Log("[Multiplayer][rail] DiffEngine tick: entities=" + _entityCounts.Values.Sum() +
                          " fields=" + ordered.Count + " changed=" + changed.Count +
                          " walk=" + walkMs + "ms diff=" + diffMs + "ms" +
                          (packets > 0 ? " sent=" + packets + "pkt/" + bytes + "B" : ""));
            }
        }

        // "\u0001" separators: paths/subKeys never contain control chars, so keys cannot collide.
        private static string SnapKey(Entry e) => e.Path + "\u0001" + e.FieldIdx + "\u0001" + e.SubKey;

        // ─── The universal walker (NO subsystem knowledge) ─────────────────

        private static void VisitEntity(string path, object obj, HashSet<object> visited, List<Entry> ordered,
                                        Dictionary<string, int> index, int depth)
        {
            if (obj == null || !visited.Add(obj)) return;
            if (depth > MaxDepth) { Incident(obj.GetType(), "(depth)", "max depth exceeded", path); return; }
            if (visited.Count > MaxEntities) { Incident(obj.GetType(), "(brake)", "entity cap " + MaxEntities + " exceeded — graph tail not walked", path); return; }

            var rt = RailType.Get(obj.GetType());
            if (rt == null) return;
            if (rt.Fields.Count == 0) { Incident(obj.GetType(), "(type)", "no persistent members", path); return; }
            _entityCounts.TryGetValue(rt.Type, out var c);
            _entityCounts[rt.Type] = c + 1;
            byte kindId = KindIdOf(rt.Type);

            for (int i = 0; i < rt.Fields.Count; i++)
            {
                var f = rt.Fields[i];
                if (f.Class == FieldClass.Excluded) continue;
                object val;
                try { val = f.GetValue(obj); }
                catch (Exception ex) { Incident(rt.Type, f.Name, "getter threw " + ex.GetType().Name, path); continue; }

                switch (f.Class)
                {
                    case FieldClass.Leaf:
                    case FieldClass.LeafList:
                    {
                        byte[] enc;
                        try { enc = RailMeta.EncodeFieldValue(f, val); }
                        catch (Exception ex) { Incident(rt.Type, f.Name, "encode failed: " + ex.Message, path); continue; }
                        Add(ordered, index, new Entry { KindId = kindId, Path = path, FieldIdx = (ushort)i, SubKey = "", Value = enc });
                        break;
                    }
                    case FieldClass.LeafDict:
                    {
                        if (val == null) break;
                        if (!(val is IDictionary dict)) { Incident(rt.Type, f.Name, "IDictionary<> without non-generic IDictionary (" + val.GetType().Name + ") — not walked", path); break; }
                        var keys = new List<(string sub, object v)>();
                        foreach (DictionaryEntry de in dict) keys.Add((RailMeta.EncodeDictKey(de.Key), de.Value));
                        keys.Sort((a, b) => string.CompareOrdinal(a.sub, b.sub));
                        foreach (var (sub, v) in keys)
                        {
                            byte[] enc;
                            try { enc = RailMeta.EncodeFieldValue(f, v); }
                            catch (Exception ex) { Incident(rt.Type, f.Name, "dict encode failed: " + ex.Message, path); continue; }
                            Add(ordered, index, new Entry { KindId = kindId, Path = path, FieldIdx = (ushort)i, SubKey = sub, Value = enc });
                        }
                        break;
                    }
                    case FieldClass.Descend:
                        if (val != null) VisitEntity(path + "." + f.Name, val, visited, ordered, index, depth + 1);
                        break;
                    case FieldClass.EntityCollection:
                    {
                        if (val == null) break;
                        var elems = new List<(string key, object o)>();
                        bool bad = false;
                        foreach (var e in (IEnumerable)val)
                        {
                            if (e == null) continue;
                            var k = IdentityResolver.KeyOf(e);
                            if (k == null) { Incident(rt.Type, f.Name, "element has no stable key (" + e.GetType().Name + ")", path); bad = true; break; }
                            elems.Add((k, e));
                        }
                        if (bad) break;
                        if (elems.Select(e => e.key).Distinct(StringComparer.Ordinal).Count() != elems.Count)
                        { Incident(rt.Type, f.Name, "duplicate element keys", path); break; }
                        foreach (var (key, e) in elems.OrderBy(e => e.key, StringComparer.Ordinal))
                            VisitEntity(path + "." + f.Name + "#" + key, e, visited, ordered, index, depth + 1);
                        break;
                    }
                }
            }
        }

        private static void Add(List<Entry> ordered, Dictionary<string, int> index, Entry e)
        {
            var key = SnapKey(e);
            if (index.ContainsKey(key)) return; // first deterministic path wins
            index[key] = ordered.Count;
            ordered.Add(e);
        }

        private static byte KindIdOf(Type t)
        {
            if (_kindIds.TryGetValue(t, out var id)) return id;
            if (_kinds.Count >= byte.MaxValue) { Debug.LogError("[Multiplayer][rail] DiffEngine: kind id space exhausted"); return byte.MaxValue; }
            id = (byte)_kinds.Count;
            _kinds.Add(t);
            _kindIds[t] = id;
            return id;
        }

        private static void Incident(Type t, string field, string reason, string path)
        {
            var line = t.Name + "." + field + ": " + reason + " [" + path + "]";
            if (_walkIncidents.Add(line) && _reportWritten)
                Debug.LogWarning("[Multiplayer][rail] DiffEngine excluded: " + line);
        }

        // ─── Wire emit (chunked; each packet its own seq on one ordered stream) ───

        private static void Emit(NetworkEngine engine, List<Entry> changed, ref int packets, ref int bytes)
        {
            int i = 0;
            while (i < changed.Count)
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(MsgDelta);
                    w.Write(Seq.Next(SurfaceIds.GeoRail));

                    // New kind defs referenced from this packet onward.
                    var defs = new List<byte>();
                    for (int j = i; j < changed.Count; j++)
                        if (!_sentKinds.Contains(changed[j].KindId) && !defs.Contains(changed[j].KindId)) defs.Add(changed[j].KindId);
                    w.Write((byte)Math.Min(defs.Count, byte.MaxValue));
                    foreach (var kid in defs.Take(byte.MaxValue))
                    {
                        var kt = _kinds[kid];
                        w.Write(kid);
                        w.Write(kt.FullName);
                        w.Write((ushort)RailType.Get(kt).Fields.Count);
                        _sentKinds.Add(kid);
                    }

                    var countPos = ms.Position;
                    w.Write((ushort)0);
                    int n = 0;
                    while (i < changed.Count && ms.Length < MaxPacketBytes && n < ushort.MaxValue)
                    {
                        var e = changed[i++];
                        if (e.Value.Length > MaxValueBytes)
                        {
                            Incident(_kinds[e.KindId], "(fieldIdx " + e.FieldIdx + ")", "value " + e.Value.Length + "B exceeds cap — not emitted", e.Path);
                            continue;
                        }
                        w.Write(e.KindId);
                        w.Write(e.Path);
                        w.Write(e.FieldIdx);
                        w.Write(e.SubKey);
                        w.Write((ushort)e.Value.Length);
                        w.Write(e.Value);
                        n++;
                    }
                    var end = ms.Position;
                    ms.Position = countPos;
                    w.Write((ushort)n);
                    ms.Position = end;

                    try
                    {
                        var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoRail, SyncKind.StateDelta, ms.ToArray());
                        engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                        packets++;
                        bytes += (int)ms.Length;
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer][rail] DiffEngine emit failed: " + ex.Message); return; }
                }
            }
        }

        // ─── Coverage report — the opt-out guarantee ───────────────────────

        private static void WriteCoverageReport(int totalFields)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("RAIL COVERAGE REPORT " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("roots: T (level clock) | F#<factionDefGuid> | S#<siteId> | U#<tacUnitId> | V#<vehicleId>");
                sb.AppendLine("total snapshot fields: " + totalFields);
                sb.AppendLine();
                int cov = 0, exc = 0;
                foreach (var kv in _entityCounts.OrderBy(k => k.Key.FullName, StringComparer.Ordinal))
                {
                    var rt = RailType.Get(kv.Key);
                    sb.AppendLine(kv.Key.FullName + "  [" + rt.Source + "]  instances=" + kv.Value +
                                  "  covered=" + rt.CoveredCount + "/" + rt.Fields.Count);
                    foreach (var f in rt.Fields)
                    {
                        if (f.Class == FieldClass.Excluded)
                        { sb.AppendLine("  - EXCLUDED " + f.Name + " (" + f.ValueType.Name + "): " + f.Exclude); exc++; }
                        else
                        { sb.AppendLine("  + " + f.Class + " " + f.Name + " (" + f.ValueType.Name + ")" + (f.LiveAlias != null ? " -> live " + f.LiveAlias : "")); cov++; }
                    }
                }
                sb.AppendLine();
                sb.AppendLine("walk incidents (collections excluded at walk time, first path shown):");
                foreach (var line in _walkIncidents.OrderBy(s => s, StringComparer.Ordinal))
                    sb.AppendLine("  ! " + line);
                sb.AppendLine();
                sb.AppendLine("summary: covered fields=" + cov + " excluded fields=" + exc + " incidents=" + _walkIncidents.Count);

                var dir = Path.Combine(Application.persistentDataPath, "Multiplayer");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "rail-coverage.txt");
                File.WriteAllText(file, sb.ToString());
                Debug.Log("[Multiplayer][rail] coverage report: " + cov + " covered / " + exc + " excluded fields across " +
                          _entityCounts.Count + " types, " + _walkIncidents.Count + " walk incidents → " + file);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] coverage report failed: " + ex.Message); }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
