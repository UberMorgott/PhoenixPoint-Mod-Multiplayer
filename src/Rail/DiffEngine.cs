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
        private static int _reportedIncidents = -1;
        private static readonly HashSet<string> _walkIncidents = new HashSet<string>(); // "(Type.Field): reason [path]" dedup
        private static readonly Dictionary<Type, int> _entityCounts = new Dictionary<Type, int>();
        // Membership of every keyed EntityCollection, "<ownerPath>.<Field>" → sorted element keys joined.
        // Per-element descend carries element VALUES only; birth/death of an element lives here.
        private static Dictionary<string, string> _collSig = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> _collSigNext = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _loud = new HashSet<string>(StringComparer.Ordinal);

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
            _collSig = new Dictionary<string, string>(StringComparer.Ordinal);
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
            if (engine == null || !engine.IsActiveSession) return;
            // The CLIENT half of the same rail tick: enforce the host's membership statements against the
            // client's own collections (see GenericApplier.ConvergenceTick). Lives here because this is the
            // per-frame driver both peers already share; it self-throttles.
            if (!engine.IsHost) { GenericApplier.ConvergenceTick(engine); return; }
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
            _collSigNext = new Dictionary<string, string>(_collSig.Count + 16, StringComparer.Ordinal);
            var ordered = new List<Entry>(_snapshot.Count + 64);
            var index = new Dictionary<string, int>(_snapshot.Count + 64, StringComparer.Ordinal);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var root in IdentityResolver.Roots(geo))
                VisitEntity(root.Key, root.Value, visited, ordered, index, 0);
            long walkMs = sw.ElapsedMilliseconds;
            _collSig = _collSigNext; // rebuilt every walk — vanished owner paths prune themselves

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

            // The report is the stated instrument ("read the report, not the bug tracker") but it was written
            // ONCE on the first walk — before any collection had been walked deep enough to raise an incident —
            // so it always reported incidents=0 and the real frontier was only ever visible as scattered log
            // warnings. Rewrite whenever the incident count actually moves: accurate numbers, no per-tick I/O.
            if (!_reportWritten || _walkIncidents.Count != _reportedIncidents)
            { WriteCoverageReport(ordered.Count); _reportWritten = true; _reportedIncidents = _walkIncidents.Count; }

            if (!_baselined && !wasForceFull)
            {
                _baselined = true;
                // Baseline suppresses VALUES (the client got them from the native save transfer, law 1) but
                // MUST ship the MEMBERSHIP statements: they are protocol metadata, not state, and without
                // them a client that creates a local entry right after joining stays unpoliced until the
                // host happens to change that same collection — which may be never. One burst, then silence.
                var stmts = changed.Where(e => e.SubKey == RailMeta.MembershipSubKey).ToList();
                int mp = 0, mb = 0;
                if (stmts.Count > 0) Emit(engine, stmts, ref mp, ref mb);
                Debug.Log("[Multiplayer][rail] DiffEngine BASELINE: entities=" + _entityCounts.Values.Sum() +
                          " fields=" + ordered.Count + " walk=" + walkMs + "ms (no value emit — clients share the save)" +
                          " membership=" + stmts.Count + " stmts sent=" + mp + "pkt/" + mb + "B");
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
                        var dictHashes = new List<uint>(keys.Count);
                        foreach (var (sub, v) in keys)
                        {
                            // Hash BEFORE the encode: the host owns this key even if its value fails to
                            // encode — leaving it out of the statement would make the client delete it.
                            dictHashes.Add(RailMeta.KeyHash(sub));
                            byte[] enc;
                            try { enc = RailMeta.EncodeFieldValue(f, v); }
                            catch (Exception ex) { Incident(rt.Type, f.Name, "dict encode failed: " + ex.Message, path); continue; }
                            Add(ordered, index, new Entry { KindId = kindId, Path = path, FieldIdx = (ushort)i, SubKey = sub, Value = enc });
                        }
                        AddMembership(rt, f, (ushort)i, kindId, path, dictHashes, ordered, index);
                        break;
                    }
                    case FieldClass.GeoItemDict:
                    {
                        if (val == null) break;
                        if (!(val is IDictionary items)) { Incident(rt.Type, f.Name, "GeoItemDict without non-generic IDictionary (" + val.GetType().Name + ")", path); break; }
                        // Guard (per-instance): only faction/auto-unload storages carry lossless 3-int entries.
                        // A non-auto-unload storage could hold a loaded weapon whose nested ammo we'd drop → exclude it.
                        if (!GeoItemCodec.OwnerAutoUnloads(obj)) { Incident(rt.Type, f.Name, "non-faction storage (loaded-weapon ammo would be lost) — excluded", path); break; }
                        var entries = new List<(string sub, object v)>();
                        foreach (DictionaryEntry de in items)
                            if (de.Key != null && de.Value != null) entries.Add((GeoItemCodec.SubKey(de.Key), de.Value));
                        entries.Sort((a, b) => string.CompareOrdinal(a.sub, b.sub)); // canonical (law 6)
                        var itemHashes = new List<uint>(entries.Count);
                        foreach (var (sub, v) in entries)
                        {
                            itemHashes.Add(RailMeta.KeyHash(sub));
                            byte[] enc;
                            try { enc = GeoItemCodec.Encode(v); }
                            catch (Exception ex) { Incident(rt.Type, f.Name, "GeoItem encode failed: " + ex.Message, path); continue; }
                            Add(ordered, index, new Entry { KindId = kindId, Path = path, FieldIdx = (ushort)i, SubKey = sub, Value = enc });
                        }
                        // THE storage fix: an item def the client owns and the host does not is otherwise
                        // invisible to the protocol forever (equip-screen quick-produce, RCA 2026-07-18).
                        AddMembership(rt, f, (ushort)i, kindId, path, itemHashes, ordered, index);
                        break;
                    }
                    case FieldClass.Descend:
                        if (val != null) VisitEntity(path + "." + f.Name, val, visited, ordered, index, depth + 1);
                        break;
                    case FieldClass.EntityList:
                        // Keyless-element list: the WHOLE list is one canonical value blob (order inside
                        // the payload — law 2 forbids element indices in the path, so no key is needed).
                        AddEntityListEntry(rt, f, (ushort)i, kindId, path, val, ordered, index);
                        break;
                    case FieldClass.EntityCollection:
                    {
                        if (val == null) break;
                        var elems = new List<(string key, object o)>();
                        bool keyless = false;
                        foreach (var e in (IEnumerable)val)
                        {
                            if (e == null) continue;
                            var k = IdentityResolver.KeyOf(e);
                            if (k == null) { keyless = true; break; }
                            elems.Add((k, e));
                        }
                        // ORDER IS STATE when the container is ordered (List<T>/T[]): the game reads element
                        // POSITION as meaning, but a keyed collection is addressed as a SET — so a pure
                        // reorder changes no element value and no key set, and this walk would emit NOTHING.
                        // That is the generic gap behind "inventory counts sync but slots don't": list order
                        // IS the equip-screen slot placement (UIInventoryList.ItemChangedHandler:855 inserts
                        // the item at Slots.IndexOf(slot); UpdateList:597 re-packs first-fit from list order),
                        // and GeoItem became key-addressed the moment the unique-BaseDef key probe landed.
                        // Capture the live sequence BEFORE the canonical sort and sign THAT, so the
                        // birth/death gate below also fires on a reorder and ships the whole ordered blob.
                        List<string> liveKeys = null;
                        if (!keyless)
                        {
                            if (!f.Unordered) liveKeys = elems.Select(e => e.key).ToList();
                            elems.Sort((a, b) => string.CompareOrdinal(a.key, b.key)); // canonical (law 6)
                            for (int d = 1; d < elems.Count && !keyless; d++)
                                if (string.Equals(elems[d - 1].key, elems[d].key, StringComparison.Ordinal))
                                    keyless = true; // duplicate keys = keyless duplicates (e.g. two identical vehicle modules)
                        }
                        if (keyless)
                        {
                            // Per-instance fallback: this list cannot be element-addressed right now →
                            // ride it as ONE EntityList blob instead of aborting the field.
                            if (IdentityResolver.IsRootEntityType(f.ElemType))
                            { Incident(rt.Type, f.Name, "unkeyable ROOT-entity list — identity creation is structural (law 3)", path); break; }
                            AddEntityListEntry(rt, f, (ushort)i, kindId, path, val, ordered, index);
                            break;
                        }
                        // MEMBERSHIP (the generic gap per-element descend cannot express): a newly born element's
                        // entries land on a path the client cannot resolve (a value apply never creates identity)
                        // and a vanished one leaves no tombstone — both silently lost. Compare the element-key set
                        // against the previous walk; on any birth/death ride the WHOLE list as one blob for that
                        // tick — the same mechanism the keyless branch above already uses — so the client rebuilds
                        // the collection wholesale. Steady state (set unchanged) emits nothing extra.
                        var sigKey = path + "." + f.Name;
                        // "\u0002" separator: keys never contain control chars, so "ab"+"c" cannot alias "a"+"bc".
                        var sig = string.Join("\u0002", liveKeys ?? elems.Select(e => e.key).ToList());
                        _collSigNext[sigKey] = sig;
                        // Same statement for keyed entity collections. _collSig below still decides when to
                        // ship the whole-list REBUILD blob (a HOST-side birth/death); this states the key set
                        // so the client can also spot a birth only IT has — which no host-side signal can see.
                        AddMembership(rt, f, (ushort)i, kindId, path,
                                      elems.Select(e => RailMeta.KeyHash(e.key)).ToList(), ordered, index);
                        if (_forceFull || (_collSig.TryGetValue(sigKey, out var prevSig) && prevSig != sig))
                        {
                            if (IdentityResolver.IsRootEntityType(f.ElemType))
                                LoudOnce(rt.Type.Name + "." + f.Name + ": keyed ROOT-entity membership changed at " +
                                         path + " — identity create/destroy belongs to the structural layer (law 3); " +
                                         "the value rail cannot carry it and the client will stay stale");
                            else
                            {
                                // ponytail: the client rebuilds the WHOLE collection from the blob, so surviving
                                // elements become fresh instances — fine for plain data (storages, modules,
                                // objectives), and it only fires on an actual birth/death/reorder. If a collection
                                // whose elements carry live Unity views or backrefs (facilities, haven zones) ever
                                // shows breakage here, that collection graduates to a hand-written structural
                                // applier (law 3) — the blob codec already refuses Unity objects loudly.
                                if (liveKeys != null) // TEMP [MP][inv] diag — strip once slot sync is confirmed
                                    Debug.Log("[MP][inv] host SHIP ordered blob " + sigKey + " n=" + elems.Count +
                                              " order=" + string.Join("|", liveKeys));
                                AddEntityListEntry(rt, f, (ushort)i, kindId, path, val, ordered, index, elems.Count);
                            }
                        }
                        foreach (var (key, e) in elems)
                            VisitEntity(path + "." + f.Name + "#" + key, e, visited, ordered, index, depth + 1);
                        break;
                    }
                }
            }
        }

        /// <summary>Emit this collection field's MEMBERSHIP STATEMENT: "the host's key set here is EXACTLY
        /// this, nothing else". One extra snapshot entry per keyed collection field, carried under the
        /// reserved <see cref="RailMeta.MembershipSubKey"/> so the existing diff/chunk/seq/tombstone
        /// machinery handles it — which also means it costs wire bytes ONLY on a tick where the set changed,
        /// and "no statement" is itself the assertion "unchanged" (so a retained statement is never stale).
        ///
        /// This is the one thing the walk cannot otherwise say. Everything else the rail emits is derived
        /// from what the HOST HAS, so an entry only the CLIENT has is invisible to the protocol and lives
        /// forever. Universal by construction: the caller is chosen by FieldClass alone (LeafDict /
        /// GeoItemDict / keyed EntityCollection) — no subsystem knows this exists.</summary>
        private static void AddMembership(RailType rt, RailField f, ushort fieldIdx, byte kindId, string path,
                                          List<uint> hashes, List<Entry> ordered, Dictionary<string, int> index)
        {
            if (2 + 4 * hashes.Count > MaxValueBytes)
            {
                LoudOnce(rt.Type.Name + "." + f.Name + " at " + path + ": " + hashes.Count +
                         " keys exceed the membership-statement cap — clients CANNOT prune stale keys of this field");
                return;
            }
            Add(ordered, index, new Entry
            {
                KindId = kindId, Path = path, FieldIdx = fieldIdx,
                SubKey = RailMeta.MembershipSubKey, Value = RailMeta.EncodeMembership(hashes)
            });
        }

        private static void AddEntityListEntry(RailType rt, RailField f, ushort fieldIdx, byte kindId, string path,
                                               object val, List<Entry> ordered, Dictionary<string, int> index,
                                               int liveCount = -1)
        {
            byte[] enc;
            try { enc = RailMeta.EncodeEntityList(f, val); }
            catch (Exception ex) { Incident(rt.Type, f.Name, "entity-list encode failed: " + ex.Message, path); return; }
            Add(ordered, index, new Entry { KindId = kindId, Path = path, FieldIdx = fieldIdx, SubKey = "", Value = enc });
            // Membership self-check (the birth/death gate): this blob is the ONLY carrier of a collection's
            // element set, so an element the codec drops is a key that silently never reaches the client —
            // exactly the class of bug this path exists to fix. Two bytes off the wire, no decode needed.
            if (liveCount >= 0)
            {
                int wireCount = enc.Length >= 4 && enc[1] == 1 ? enc[2] | (enc[3] << 8) : 0;
                if (wireCount != liveCount)
                    LoudOnce(rt.Type.Name + "." + f.Name + " at " + path + ": membership blob carries " + wireCount +
                             " of " + liveCount + " live elements — births/removals of this collection WILL desync");
            }
            SelfCheckEntityList(rt.Type, f, enc);
        }

        /// <summary>One-shot LOUD error: a change the rail detected but cannot carry. Never a warning —
        /// this class of gap has already cost several silent test cycles.</summary>
        private static void LoudOnce(string msg)
        {
            if (_loud.Count < 200 && _loud.Add(msg))
                Debug.LogError("[Multiplayer][rail] DiffEngine: " + msg);
        }

        /// <summary>Encode→decode→re-encode round-trip check on the host's own graph: byte-identical
        /// re-encode + preserved element count, or a loud error. THE runnable gate for the blob codec.
        /// One-shot per (type.field, empty|populated) — an empty first sighting can NOT retire the
        /// check for the populated path (the 2026-07-18 lesson: 16/16 "OK" on a 4-byte empty
        /// _inventoryItems while the populated path was never exercised). The populated pass
        /// additionally round-trips a REORDERED copy (order rides inside the payload) and a
        /// DUPLICATED copy (value-equal elements — GeoItem.Equals collapses identical grenades in any
        /// set/Distinct — must NOT collapse in the codec).</summary>
        private static readonly HashSet<string> _roundTripChecked = new HashSet<string>(StringComparer.Ordinal);
        private static void SelfCheckEntityList(Type owner, RailField f, byte[] enc)
        {
            // wire layout: [marker][hasList:bool][count:u16 LE]…
            int wireCount = enc.Length >= 4 && enc[1] == 1 ? enc[2] | (enc[3] << 8) : 0;
            var key = owner.Name + "." + f.Name + (wireCount > 0 ? " populated" : " empty");
            if (!_roundTripChecked.Add(key)) return;
            try
            {
                var geo = GeoLevel();
                var items = RailMeta.DecodeEntityList(enc, f, geo);
                var enc2 = RailMeta.EncodeEntityList(f, items);
                string fail = null;
                if (!RailMeta.BytesEqual(enc, enc2)) fail = "re-encode differs (" + enc.Length + "B → " + enc2.Length + "B)";
                else if ((items?.Count ?? 0) != wireCount) fail = "count " + wireCount + " → " + (items?.Count ?? 0);
                if (fail == null && wireCount > 0)
                {
                    var rev = new List<object>(items); rev.Reverse();
                    var encR = RailMeta.EncodeEntityList(f, rev);
                    var itemsR = RailMeta.DecodeEntityList(encR, f, geo);
                    if (!RailMeta.BytesEqual(encR, RailMeta.EncodeEntityList(f, itemsR)) || (itemsR?.Count ?? 0) != wireCount)
                        fail = "reordered copy did not survive round-trip";
                    else
                    {
                        var dup = new List<object>(items); dup.AddRange(items);
                        var itemsD = RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(f, dup), f, geo);
                        if ((itemsD?.Count ?? 0) != wireCount * 2)
                            fail = "duplicated elements collapsed (" + wireCount * 2 + " → " + (itemsD?.Count ?? 0) + ")";
                    }
                }
                if (fail == null)
                    Debug.Log("[Multiplayer][rail] EntityList round-trip OK: " + key + " (" + enc.Length + "B, n=" + wireCount + ")");
                else
                    Debug.LogError("[Multiplayer][rail] EntityList round-trip MISMATCH: " + key + " — " + fail +
                                   " — codec bug, this field will desync");
            }
            catch (Exception ex)
            { Debug.LogError("[Multiplayer][rail] EntityList round-trip check FAILED for " + key + ": " + ex.Message); }
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
