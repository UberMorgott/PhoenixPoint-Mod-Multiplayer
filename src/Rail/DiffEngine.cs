using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// Every ~0.5 s a CYCLE (periodic walk TIME-SLICED at ~3 ms/frame; forced walks single-shot):
    /// walk → flat canonical snapshot (path, fieldIdx, subKey) → boxed-encoded value →
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
        private const float ExceptionRetryBackoff = 1f; // min pause between forced retries after a tick exception
        private const int MaxPacketBytes = 45000;  // chunk flush threshold
        private const int MaxValueBytes = 8192;    // per-entry cap: 45000 + 8192 stays under the u16 envelope
        private const int MaxEntities = 50000;     // graph-chase brake
        private const int MaxDepth = 12;
        private const double SliceBudgetMs = 3.0;  // per-frame walk budget of the sliced periodic cycle

        internal struct Entry
        {
            public byte KindId;
            public string Path;
            public ushort FieldIdx;
            public string SubKey;
            public byte[] Value;
            public string Key;   // SnapKey(Path, FieldIdx, SubKey), built once at Add time — local only, never on the wire
        }

        private static readonly SurfaceSeq Seq = new SurfaceSeq();
        private static Dictionary<string, Entry> _snapshot = new Dictionary<string, Entry>(StringComparer.Ordinal);
        // The tick's working set is DOUBLE-BUFFERED rather than reallocated: at ~22k fields, a fresh
        // dictionary + list + visited-set every 0.5 s was megabytes of garbage per tick for no benefit.
        // All three are refilled from scratch each tick, so nothing carries over.
        private static Dictionary<string, Entry> _snapshotBack = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly List<Entry> _ordered = new List<Entry>();
        private static readonly HashSet<object> _visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Type, byte> _kindIds = new Dictionary<Type, byte>();
        private static readonly List<Type> _kinds = new List<Type>();
        private static readonly HashSet<byte> _sentKinds = new HashSet<byte>();
        private static bool _baselined;
        private static bool _forceFull;
        private static readonly List<string> _forcePrefixes = new List<string>(); // scoped forced re-emit, see ForceReemit
        private static float _nextTickAt;
        private static float _nextPerfLogAt;
        private static bool _reportWritten;
        private static Timing _armedTiming;                                                 // N3, see ArmChangeDrivenFlush
        private static readonly List<Entry> _censusEntries = new List<Entry>(); // forced-tick dict censuses, see AddCensus
        private static readonly HashSet<string> _walkIncidents = new HashSet<string>(); // "(Type.Field): reason [path]" dedup
        private static readonly Dictionary<Type, int> _entityCounts = new Dictionary<Type, int>();

        // Sliced periodic cycle (non-null = cycle in progress). The PERIODIC walk is spread over frames
        // at ~SliceBudgetMs of work per frame so it can never spike a frame; roots are SNAPSHOTTED here
        // at cycle start (never an enumerator over a live game collection held across frames). Forced
        // walks (FlushNow / ForceReemit / full resend) stay single-shot — see HostTick.
        private static List<KeyValuePair<string, object>> _cycleRoots;
        private static int _cycleNext;
        private static int _cycleFrames;
        private static double _cycleWalkMs;
        private static double _maxSliceMs;
        private static bool _flushPending;

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
            AbandonCycle();
            _flushPending = false;
            _snapshot = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _sentKinds.Clear();
            _baselined = false;
            _forceFull = false;
            _forcePrefixes.Clear();
            ArmChangeDrivenFlush(null); // drop the old level's Timing; the next HostTick arms the new one
            TimeAnchor.Reset();         // post-load the clock jumped: re-latch rather than re-publish the old anchor
            DefOwnership.Invalidate();  // a loaded save can mint runtime defs — rebuild the ownership set
        }

        /// <summary>Client lost the stream (seq gap): resend EVERYTHING covered — it is just a big delta.</summary>
        public static void RequestFullResend()
        {
            _forceFull = true;
            _sentKinds.Clear();
            // A resend re-emits the STORED anchor; re-latch first so the client is not rewound to whenever
            // that anchor was taken (it stays current for as long as pause/speed do not change).
            TimeAnchor.Reset();
            Debug.Log("[Multiplayer][rail] DiffEngine: full resend requested");
        }

        /// <summary>Scoped forced re-emit — the targeted sibling of <see cref="RequestFullResend"/>: on the
        /// next tick every covered pair whose path sits under <paramref name="pathPrefix"/> is re-emitted
        /// with its CURRENT host value even though the snapshot says unchanged. For the intent seams whose
        /// client half mutates the local mirror natively BEFORE host confirmation (equip gestures): a host
        /// reject changes no host state, so the normal diff emits nothing and that client stays diverged —
        /// re-emitting the host truth for the touched subtree converges it. Prefix matches whole path
        /// segments ("U#3" never matches "U#30"). No new wire concept for VALUES: the client sees an
        /// ordinary delta. Dicts under the prefix additionally ship their CENSUS (present-key list), so a
        /// client-side EXTRA key — which no host-side change will ever tombstone — is pruned too.</summary>
        public static void ForceReemit(string pathPrefix)
        {
            if (string.IsNullOrEmpty(pathPrefix)) return;
            if (!_forcePrefixes.Contains(pathPrefix)) _forcePrefixes.Add(pathPrefix);
            FlushNow();
        }

        private static bool MatchesForcePrefix(string path)
        {
            for (int i = 0; i < _forcePrefixes.Count; i++)
            {
                var p = _forcePrefixes[i];
                if (path.Length >= p.Length && string.CompareOrdinal(path, 0, p, 0, p.Length) == 0 &&
                    (path.Length == p.Length || path[p.Length] == '.'))
                    return true;
            }
            return false;
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── Host tick: walk → diff → emit ─────────────────────────────────

        /// <summary>Ship host state NOW instead of waiting out the 0.5 s poll: the next HostTick call
        /// (same frame when the seam fires before NetworkEngine.Update, else next frame) runs one
        /// SINGLE-SHOT monolithic tick — never sliced, so the whole graph is read and shipped in that one
        /// frame (and a ForceReemit scope gets its census from root 0). An in-progress sliced cycle is
        /// abandoned first: it has shipped nothing (shipping happens only at cycle completion), so nothing
        /// is lost and nothing double-ships.
        /// No guards needed: <see cref="HostTick"/> still returns on non-host / no session / no geoscape,
        /// so this can be called from any seam that knows the host just changed something the client
        /// must not wait for.</summary>
        public static void FlushNow() { _nextTickAt = 0f; _flushPending = true; }

        /// <summary>N3 third arm — HOST-LOCAL gestures. The law-4a capture seams fire on the host too
        /// (they conclude "run native"); this is their one-line exit into the same change-driven flush
        /// the client-intent dispatch gets, so clients stop waiting out the 0.5 s poll to see the host
        /// act. Guarded HERE, not at call sites: no-op unless we are the in-session HOST outside an
        /// apply scope (host repaints re-enter capture seams under SyncApplyScope — not gestures).
        /// <see cref="FlushNow"/> is a flag, so a same-frame burst — rapid clicks, or a gesture that
        /// reached the seam both natively and as a dispatched client intent — coalesces into ONE
        /// single-shot walk, never N.</summary>
        public static void FlushOnHostGesture()
        {
            var engine = NetworkEngine.Instance;
            if (engine != null && engine.IsHost && engine.IsActiveSession && !SyncApplyScope.Active)
                FlushNow();
        }

        /// <summary>
        /// N3 — change-driven flush on the game's OWN event, not a new channel. <c>Timing</c> raises
        /// <c>EffectiveScaleChangedEvent</c> (Base.Core/Timing.cs:186) from BOTH the <c>Scale</c> setter
        /// (:95) and the <c>Paused</c> setter (:126), so ONE subscription covers speed and pause alike.
        ///
        /// Pause/speed keep riding as ordinary <c>Timing.Paused</c> / <c>Timing.Scale</c> leaves on root
        /// "T" (IdentityResolver.cs:115) — no new packet, no surface id, no DTO. All this does is collapse
        /// the 0..0.5 s poll latency to one frame.
        ///
        /// The covered class is "a host change that must not wait out the poll", NOT "the clock": every
        /// later intent seam and structural applier reuses <see cref="FlushNow"/> the same way.
        ///
        /// Armed from <see cref="HostTick"/>, which has already returned for a non-host, so a client never
        /// subscribes. The <c>Timing</c> instance is replaced across level loads, hence the identity check.
        /// </summary>
        private static void ArmChangeDrivenFlush(Timing timing)
        {
            if (ReferenceEquals(timing, _armedTiming)) return;
            if (_armedTiming != null) _armedTiming.EffectiveScaleChangedEvent -= OnEffectiveScaleChanged;
            _armedTiming = timing;
            if (timing != null) timing.EffectiveScaleChangedEvent += OnEffectiveScaleChanged;
        }

        private static void OnEffectiveScaleChanged(Timing timing) => FlushNow();

        // ─── N7: the def-aliasing falsifier (a MEASUREMENT, not a fix) ─────

        private static bool _aliasProbed;

        /// <summary>
        /// Decides whether <c>RailMeta._presentationTypes</c> (the LocalizedTextBind refusal) may stay a
        /// cheap type-name stopgap or must be replaced by the general reference-identity law.
        ///
        /// The question: are any live <c>GeoSite.SiteName</c> / <c>.Motto</c> binds the SAME OBJECT as a
        /// bind owned by a def — <c>HavenSettingDbDef.HavenSettings[*]</c>
        /// (HavenName/HavenMotto/LeaderName, HavenSetting.cs:12/14/18) or
        /// <c>ArcheologySettingsDef.AncientSiteSetting[*]</c> (HarvestSiteName/RefinerySiteName,
        /// ArcheologySettingsDef.cs:49/51)? Only reference identity can answer it; a type name cannot.
        ///
        ///   aliased == 0 on a FRESH campaign → writing a bind could never land in shared def state, the
        ///     type-name refusal is merely coarse, and it stays as a permanent stopgap.
        ///   aliased  > 0 → the refusal is load-bearing for the wrong reason and the real law (a reference
        ///     index over DefRepositoryDef.AllDefs) must be built, and the type list deleted with it.
        ///
        /// MUST be read on a FRESH campaign, not a loaded save: LocalizedTextBind is Embedded, so a load
        /// un-shares every bind and would report 0 whatever the truth is.
        ///
        /// ponytail: one-shot, host-only, behind MpDiag.On — cost is one pass over the site list on a
        /// single tick, and zero when the flag is off. Delete this whole member once the count is known.
        /// </summary>
        private static void ProbeDefAliasedBinds(GeoLevelController geo)
        {
            if (_aliasProbed || !MpDiag.On) return;
            _aliasProbed = true;
            try
            {
                var repo = GameUtl.GameComponent<Base.Defs.DefRepository>();
                if (repo == null) { Debug.Log("[MP][n7] no DefRepository — probe skipped"); return; }

                var defOwned = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var db in repo.GetAllDefs<PhoenixPoint.Geoscape.Levels.HavenSettingDbDef>())
                    foreach (var hs in db?.HavenSettings ?? new List<PhoenixPoint.Geoscape.Levels.HavenSetting>())
                    {
                        if (hs == null) continue;
                        if (hs.HavenName != null) defOwned.Add(hs.HavenName);
                        if (hs.HavenMotto != null) defOwned.Add(hs.HavenMotto);
                        if (hs.LeaderName != null) defOwned.Add(hs.LeaderName);
                    }
                foreach (var ar in repo.GetAllDefs<PhoenixPoint.Geoscape.Levels.ArcheologySettingsDef>())
                    foreach (var s in ar?.AncientSiteSetting ?? new List<PhoenixPoint.Geoscape.Levels.ArcheologySettingsDef.AncientSiteSettings>())
                    {
                        if (s == null) continue;
                        if (s.HarvestSiteName != null) defOwned.Add(s.HarvestSiteName);
                        if (s.RefinerySiteName != null) defOwned.Add(s.RefinerySiteName);
                    }

                int sites = 0, aliased = 0;
                foreach (var site in geo.Map?.AllSites ?? Enumerable.Empty<PhoenixPoint.Geoscape.Entities.GeoSite>())
                {
                    if (site == null) continue;
                    sites++;
                    if (site.SiteName != null && defOwned.Contains(site.SiteName)) aliased++;
                    else if (site.Motto != null && defOwned.Contains(site.Motto)) aliased++;
                }
                Debug.Log("[MP][n7] def-aliased LocalizedTextBind falsifier: defBinds=" + defOwned.Count +
                          " sites=" + sites + " aliased=" + aliased +
                          (aliased == 0
                              ? " — ZERO: the type-name refusal in RailMeta._presentationTypes stays a permanent stopgap; delete this probe."
                              : " — NONZERO: build the reference-identity law and DELETE the type-name list."));
            }
            catch (Exception ex) { Debug.LogWarning("[MP][n7] probe failed: " + ex.Message); }
        }

        public static void HostTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost || !engine.IsActiveSession) return;
            float now = Time.realtimeSinceStartup;
            // A forced walk (FlushNow / ForceReemit / full resend) must see its scope from root 0: a
            // half-walked cycle can no longer census or force-re-emit roots it already passed. Nothing
            // has shipped (shipping happens only at cycle completion), so dropping the partial walk
            // loses nothing and cannot double-ship.
            if ((_flushPending || _forceFull) && _cycleRoots != null) AbandonCycle();
            if (_cycleRoots == null && now < _nextTickAt) return;
            var geo = GeoLevel();
            if (geo == null) { AbandonCycle(); _nextTickAt = now + TickInterval; return; }
            ArmChangeDrivenFlush(geo.Timing);
            ProbeDefAliasedBinds(geo);

            try
            {
                if (_cycleRoots != null) { RunSlice(engine); return; }
                _nextTickAt = now + TickInterval;
                if (_flushPending || _forceFull)
                {
                    // Forced walks stay SINGLE-SHOT (the pre-slicing path, semantics byte-identical):
                    // rare and event-driven (pause/speed click, intent seam, resync) — one hitch, never
                    // rhythmic.
                    _flushPending = false;
                    Tick(engine, geo);
                }
                else
                {
                    BeginCycle(geo);
                    RunSlice(engine);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][rail] DiffEngine tick failed: " + ex);
                AbandonCycle();
                // _forceFull self-retries monolithically (checked at the top of the try); a pending
                // ForceReemit scope retries the same way — dropping the prefixes here would silently lose
                // the client's convergence re-emit (nothing shipped this tick).
                if (_forcePrefixes.Count > 0) _flushPending = true;
                // Backoff, never latch-off: FlushNow here (_nextTickAt=0) retried monolithically —
                // and re-logged the exception — EVERY FRAME while the failure persisted. Hold the
                // still-armed retry ≥1 s; a fresh external FlushNow (intent seam, pause/speed click)
                // resets _nextTickAt to 0 and still fires immediately.
                _nextTickAt = now + ExceptionRetryBackoff;
            }
        }

        // ─── Sliced periodic cycle (walk spread over frames; ONE batch at completion) ───

        /// <summary>Reset the walk working set (shared by the sliced cycle and the monolithic tick).
        /// The walk fills the NEW snapshot directly — it already had to dedup by SnapKey, and a second
        /// 22k-entry index holding the same keys bought nothing.</summary>
        private static void BeginWalkState()
        {
            _entityCounts.Clear();
            _censusEntries.Clear();
            _ordered.Clear();
            _visited.Clear();
            _snapshotBack.Clear();
        }

        /// <summary>Cycle start: SNAPSHOT the root list — the only live-graph enumeration that would
        /// otherwise span frames. Every deeper enumeration happens inside one slice (VisitEntity finishes
        /// its whole root synchronously), so no enumerator over live game state survives a frame.</summary>
        private static void BeginCycle(GeoLevelController geo)
        {
            BeginWalkState();
            _cycleRoots = new List<KeyValuePair<string, object>>();
            foreach (var r in IdentityResolver.Roots(geo)) _cycleRoots.Add(r);
            _cycleNext = 0; _cycleFrames = 0; _cycleWalkMs = 0; _maxSliceMs = 0;
        }

        /// <summary>One frame's worth of walk: whole roots until the ~<see cref="SliceBudgetMs"/> budget
        /// is spent (always ≥1 root, so a cycle terminates even if a single root overruns the budget).
        /// Fields read on different frames landing in one batch = ACCEPTED tearing (ARCHITECTURE.md) —
        /// same consistency class as the old monolithic mid-mutation read, coarser grain; the next cycle
        /// converges. A root destroyed since cycle start is skipped by the Unity fake-null guard (the
        /// same guard IdentityResolver.Resolve uses client-side); death deeper in the graph rides the
        /// existing getter-throw → Incident path in VisitEntity.</summary>
        private static void RunSlice(NetworkEngine engine)
        {
            var sw = Stopwatch.StartNew();
            while (_cycleNext < _cycleRoots.Count)
            {
                var root = _cycleRoots[_cycleNext++];
                if (!(root.Value is UnityEngine.Object uo) || uo != null)
                    VisitEntity(root.Key, root.Value, _visited, _ordered, _snapshotBack, 0);
                if (sw.Elapsed.TotalMilliseconds >= SliceBudgetMs) break;
            }
            _cycleFrames++;
            double ms = sw.Elapsed.TotalMilliseconds;
            _cycleWalkMs += ms;
            if (ms > _maxSliceMs) _maxSliceMs = ms;
            if (_cycleNext < _cycleRoots.Count) return;
            int roots = _cycleRoots.Count;
            _cycleRoots = null;
            DiffAndEmit(engine, (long)Math.Round(_cycleWalkMs), _maxSliceMs, _cycleFrames, roots);
        }

        /// <summary>Drop an unfinished cycle (forced walk, reload boundary, level gone, tick exception).
        /// Nothing shipped and _snapshot untouched, so this is free; the walk scratch is cleared so it
        /// does not pin dead level objects until the next cycle.</summary>
        private static void AbandonCycle()
        {
            if (_cycleRoots == null) return;
            _cycleRoots = null;
            BeginWalkState();
        }

        /// <summary>Single-shot walk+diff+emit — the pre-slicing shape, kept for the forced paths
        /// (FlushNow same-frame contract, ForceReemit census scope, full resend). Rare, one hitch
        /// accepted.</summary>
        private static void Tick(NetworkEngine engine, GeoLevelController geo)
        {
            var sw = Stopwatch.StartNew();
            BeginWalkState();
            int roots = 0;
            foreach (var root in IdentityResolver.Roots(geo))
            {
                roots++;
                VisitEntity(root.Key, root.Value, _visited, _ordered, _snapshotBack, 0);
            }
            long walkMs = sw.ElapsedMilliseconds;
            DiffAndEmit(engine, walkMs, walkMs, 1, roots);
        }

        private static void DiffAndEmit(NetworkEngine engine, long walkMs, double maxSliceMs, int frames, int roots)
        {
            var sw = Stopwatch.StartNew();
            var ordered = _ordered;
            var newSnap = _snapshotBack;

            // Diff: changed/new pairs in walk order (canonical), then subKey deletions. An unchanged field
            // kept the previous tick's array (RailMeta.EncodeFieldValue with prev), so BytesEqual's
            // ReferenceEquals fast path settles it without a byte compare.
            var changed = new List<Entry>();
            bool anyForced = _forcePrefixes.Count > 0;
            foreach (var e in ordered)
                if (_forceFull || !_snapshot.TryGetValue(e.Key, out var old) || !RailMeta.BytesEqual(old.Value, e.Value) ||
                    (anyForced && MatchesForcePrefix(e.Path)))
                    changed.Add(e);

            // Built only if a stale subKey actually survives both guards below — normally nothing does, and
            // 22k inserts for a set nobody reads is pure tick cost.
            HashSet<string> livePaths = null;
            foreach (var kv in _snapshot)
            {
                if (kv.Value.SubKey.Length == 0 || newSnap.ContainsKey(kv.Key)) continue;
                if (livePaths == null)
                {
                    livePaths = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var e in ordered) livePaths.Add(e.Path);
                }
                // Suppress the tombstone when the whole entity path is gone (moved/vanished) — that is a
                // structural change, not a dict-key delete; emitting one would false-delete on the client
                // (and could hit a different entity that took over the old path). ponytail: an entity whose
                // ONLY covered field is this dict would lose its stale keys here, but such entities don't exist.
                if (!livePaths.Contains(kv.Value.Path)) continue;
                // dictionary key removed on host while the entity persists → explicit tombstone (distinct
                // sentinel so the client never confuses it with a genuine present-null value).
                changed.Add(new Entry { KindId = kv.Value.KindId, Path = kv.Value.Path, FieldIdx = kv.Value.FieldIdx, SubKey = kv.Value.SubKey, Value = new[] { RailMeta.DictTombstone }, Key = kv.Key });
            }
            // Forced-tick dict censuses (never in the snapshot — normal ticks stay wire-identical). AFTER
            // the value entries, so within one tick the client prunes against the same key set it received.
            if (_censusEntries.Count > 0) changed.AddRange(_censusEntries);
            long diffMs = sw.ElapsedMilliseconds;
            _snapshotBack = _snapshot;   // this tick's snapshot becomes next tick's scratch, and vice versa
            _snapshot = newSnap;
            bool wasForceFull = _forceFull;
            _forceFull = false;

            if (!_reportWritten) { WriteCoverageReport(ordered.Count); _reportWritten = true; }

            // The line the stutter hunt reads: did the periodic walk stay inside its per-frame budget.
            if (MpDiag.On)
                Debug.Log("[MP][rail] cycle: frames=" + frames + " walk=" + walkMs + "ms maxSlice=" +
                          maxSliceMs.ToString("F1", CultureInfo.InvariantCulture) + "ms roots=" + roots +
                          " changed=" + changed.Count);

            if (!_baselined && !wasForceFull)
            {
                _baselined = true;
                Debug.Log("[Multiplayer][rail] DiffEngine BASELINE: entities=" + _entityCounts.Values.Sum() +
                          " fields=" + ordered.Count + " walk=" + walkMs + "ms (no emit — clients share the save)");
                // A ForceReemit that raced the baseline shipped NOTHING — keep its prefixes armed and
                // re-run forced, instead of silently dropping the convergence re-emit.
                if (_forcePrefixes.Count > 0) FlushNow();
                return;
            }
            _baselined = true;
            _forcePrefixes.Clear(); // consumed: the emit below carries the forced scope (censuses + values)

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
        // Built ONCE per entry (stored in Entry.Key); it used to be rebuilt for every entry in the diff loop.
        // ponytail: ~22k SnapKey + child-path concats per walk cycle (here + the Descend/EntityCollection
        // appends in VisitEntity). A per-entity path/key cache would need object-keyed invalidation —
        // non-root entities MOVE (element rekeyed / reparented ⇒ path changes), so a stale hit is silent
        // wire divergence, not a perf bug. Upgrade path if the GC ever cares: cache keyed on
        // (parent Entry.Key, fieldIdx, subKey) validated per cycle against the walk's own visit order.
        private static string SnapKey(string path, ushort fieldIdx, string subKey) =>
            path + "\u0001" + fieldIdx.ToString(CultureInfo.InvariantCulture) + "\u0001" + subKey;

        // ─── The universal walker (NO subsystem knowledge) ─────────────────

        private static void VisitEntity(string path, object obj, HashSet<object> visited, List<Entry> ordered,
                                        Dictionary<string, Entry> snap, int depth)
        {
            if (obj == null || !visited.Add(obj)) return;
            if (depth > MaxDepth) { Incident(obj.GetType(), "(depth)", "max depth exceeded", path); return; }
            if (visited.Count > MaxEntities) { Incident(obj.GetType(), "(brake)", "entity cap " + MaxEntities + " exceeded — graph tail not walked", path); return; }
            // Walk-time ownership law (DefOwnership): an instance the def graph also reaches is
            // DEF-OWNED — the rail writes in place, so descending it would ship shared def state as
            // writable entity state and the client apply would clobber its defs. Never walked. One
            // O(1) reference-hash lookup per visited entity; the set builds once (defs immutable).
            if (DefOwnership.IsDefOwned(obj)) { Incident(obj.GetType(), "(def-owned)", "instance aliased into the def graph — not walked (ownership law)", path); return; }

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

                // Ownership law, container/descend arm: a def-owned CONTAINER (shared list/dict) or
                // sub-object must not ship either — the client applies these by MUTATING its own
                // aliased instance in place (ApplyList Clear+Add, dict writes, ReorderByKeys).
                // Leaves are exempt by construction: they ship by value and apply by replacing the
                // ENTITY's reference, never by writing into the shared instance — and exempting them
                // keeps the check off the ~22k-leaf hot path.
                if (val != null && f.Class != FieldClass.Leaf && DefOwnership.IsDefOwned(val))
                { Incident(rt.Type, f.Name, "def-owned instance — not shipped (ownership law)", path); continue; }

                switch (f.Class)
                {
                    case FieldClass.Leaf:
                    case FieldClass.LeafList:
                        AddEncoded(ordered, snap, rt, f, (ushort)i, kindId, path, "", val, "encode failed: ");
                        break;
                    case FieldClass.LeafDict:
                    {
                        if (val == null) break;
                        if (!(val is IDictionary dict)) { Incident(rt.Type, f.Name, "IDictionary<> without non-generic IDictionary (" + val.GetType().Name + ") — not walked", path); break; }
                        var keys = new List<(string sub, object v)>();
                        foreach (DictionaryEntry de in dict) keys.Add((RailMeta.EncodeDictKey(de.Key), de.Value));
                        keys.Sort((a, b) => string.CompareOrdinal(a.sub, b.sub));
                        foreach (var (sub, v) in keys)
                            AddEncoded(ordered, snap, rt, f, (ushort)i, kindId, path, sub, v, "dict encode failed: ");
                        if (ForcedNow(path)) AddCensus(rt, f, (ushort)i, kindId, path, keys);
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
                        foreach (var (sub, v) in entries)
                        {
                            byte[] enc;
                            try { enc = GeoItemCodec.Encode(v); }
                            catch (Exception ex) { Incident(rt.Type, f.Name, "GeoItem encode failed: " + ex.Message, path); continue; }
                            Add(ordered, snap, new Entry { KindId = kindId, Path = path, FieldIdx = (ushort)i, SubKey = sub, Value = enc, Key = SnapKey(path, (ushort)i, sub) });
                        }
                        if (ForcedNow(path)) AddCensus(rt, f, (ushort)i, kindId, path, entries);
                        break;
                    }
                    case FieldClass.Descend:
                        if (val != null) VisitEntity(path + "." + f.Name, val, visited, ordered, snap, depth + 1);
                        break;
                    case FieldClass.EntityList:
                        // Keyless-element list: the WHOLE list is one canonical value blob (order inside
                        // the payload — law 2 forbids element indices in the path, so no key is needed).
                        AddEntityListEntry(rt, f, (ushort)i, kindId, path, val, ordered, snap);
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
                        List<string> liveKeys = null;
                        if (!keyless)
                        {
                            // ORDER IS STATE when the container is ordered (List<T>/T[]): a keyed collection
                            // is addressed as a SET, so a pure reorder changes no element value and no key
                            // set — without a carrier the walk emits NOTHING for it. Capture the LIVE
                            // sequence BEFORE the canonical sort; AddKeyOrder below rides it through the
                            // normal snapshot diff, so it ships exactly when membership/order changed and
                            // stays silent on idle ticks. Singletons cannot reorder — no entry.
                            if (!f.Unordered && elems.Count > 1)
                            {
                                liveKeys = new List<string>(elems.Count);
                                foreach (var (k, _) in elems) liveKeys.Add(k);
                            }
                            // Sort FIRST (the canonical visit order), then duplicates are adjacent — same
                            // answer as the old Select/Distinct/Count, without the per-field LINQ chain. The
                            // sort is unstable but every key here is unique (a duplicate aborts the field),
                            // so the resulting order is exactly what OrderBy produced.
                            elems.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
                            for (int j = 1; j < elems.Count; j++)
                                if (string.CompareOrdinal(elems[j - 1].key, elems[j].key) == 0)
                                { keyless = true; break; } // duplicate keys = keyless duplicates (e.g. two identical vehicle modules)
                        }
                        if (keyless)
                        {
                            // NO whole-list blob fallback here. ApplyList would Clear() the live list and
                            // re-add elements built by Activator.CreateInstance + table fields, so every
                            // reference member the table does not carry lands NULL — the 7ef0a30
                            // ResearchElement husk (ResearchDef null -> NOTEXT labels + Research.get_Progress
                            // NRE), which is why 7ef0a30 was dropped in the first place.
                            //
                            // Classify-time EntityList is argued husk-by-husk in docs/rail-baseline.txt; a
                            // RUNTIME fallback is invisible there (the baseline still prints EntityCollection),
                            // so it can only ever smuggle a husk past review. Concretely: every
                            // EntityCollection field in the current closure holds ResearchElement
                            // (Research.AllResearchesArray/_researchQueue/_oldResearchQueue), so ONE KeyOf
                            // returning null — FormatKeyValue nulls on empty string, negative int, or a key
                            // containing '.'/'#' — or one duplicate at any single tick was enough to rebuild
                            // every research as a husk. Keyless -> abort the field, visibly.
                            Incident(rt.Type, f.Name, IdentityResolver.IsRootEntityType(f.ElemType)
                                ? "unkeyable ROOT-entity list — identity creation is structural (law 3)"
                                : "unkeyable/duplicate element keys — blob rebuild would husk the elements", path);
                            break;
                        }
                        if (liveKeys != null)
                            AddKeyOrder(ordered, snap, rt, f, (ushort)i, kindId, path, liveKeys);
                        foreach (var (key, e) in elems) // already sorted ordinal above
                            VisitEntity(path + "." + f.Name + "#" + key, e, visited, ordered, snap, depth + 1);
                        break;
                    }
                }
            }
        }

        private static void AddEntityListEntry(RailType rt, RailField f, ushort fieldIdx, byte kindId, string path,
                                               object val, List<Entry> ordered, Dictionary<string, Entry> snap)
        {
            byte[] enc;
            try { enc = RailMeta.EncodeEntityList(f, val); }
            catch (Exception ex) { Incident(rt.Type, f.Name, "entity-list encode failed: " + ex.Message, path); return; }
            Add(ordered, snap, new Entry { KindId = kindId, Path = path, FieldIdx = fieldIdx, SubKey = "", Value = enc, Key = SnapKey(path, fieldIdx, "") });
            // 68cd934's SelfCheckEntityList USED to round-trip the blob here. Deliberately not re-landed:
            // it ran the FULL decode on the HOST, constructing real game objects and firing InvokePostRead
            // on every one of them — a live side-effect channel pointed straight into the host's own walk,
            // to verify a codec. The same round-trip is asserted OFFLINE by the stage-1 harness
            // (tools/RailCheck/Program.cs, L4), where a constructed object can hurt nothing.
        }

        private static void Add(List<Entry> ordered, Dictionary<string, Entry> snap, Entry e)
        {
            if (snap.ContainsKey(e.Key)) return; // first deterministic path wins
            snap[e.Key] = e;
            ordered.Add(e);
        }

        /// <summary>True when this walk position is inside a forced re-emit scope (full resend, or a
        /// <see cref="ForceReemit"/> prefix) — the only ticks that ship dict censuses.</summary>
        private static bool ForcedNow(string path) =>
            _forceFull || (_forcePrefixes.Count > 0 && MatchesForcePrefix(path));

        /// <summary>Resync-only dict CENSUS: the field's full present-key set, appended to this tick's
        /// emit but NEVER to the snapshot (normal ticks stay wire-identical). Closes the delete half of
        /// forced re-emits: a client-side EXTRA key has no host-side change to tombstone it, so values
        /// alone can never remove it — the census lets the client prune everything not listed. Rides
        /// SubKey "" (a real dict entry always carries its key), discriminated by DictCensusMarker.</summary>
        private static void AddCensus(RailType rt, RailField f, ushort fieldIdx, byte kindId, string path,
                                      List<(string sub, object v)> entries)
        {
            var subs = new List<string>(entries.Count);
            foreach (var (sub, _) in entries) subs.Add(sub);
            byte[] enc;
            try { enc = RailMeta.EncodeDictCensus(subs); }
            catch (Exception ex) { Incident(rt.Type, f.Name, "census encode failed: " + ex.Message, path); return; }
            _censusEntries.Add(new Entry { KindId = kindId, Path = path, FieldIdx = fieldIdx, SubKey = "", Value = enc, Key = "" });
        }

        /// <summary>Encode a leaf/leaf-list/dict-value and record it. The encode goes through RailMeta's
        /// reusable writer and hands it the PREVIOUS tick's bytes for this key: unchanged values (i.e. almost
        /// all of them on an idle tick) come back as that same array, so the field costs no allocation and
        /// the diff settles it by reference. The bytes are identical either way.</summary>
        private static void AddEncoded(List<Entry> ordered, Dictionary<string, Entry> snap, RailType rt, RailField f,
                                       ushort fieldIdx, byte kindId, string path, string subKey, object val, string failedWhat)
        {
            var key = SnapKey(path, fieldIdx, subKey);
            if (snap.ContainsKey(key)) return; // first deterministic path wins
            byte[] enc;
            try { enc = RailMeta.EncodeFieldValue(f, val, _snapshot.TryGetValue(key, out var prev) ? prev.Value : null); }
            catch (Exception ex) { Incident(rt.Type, f.Name, failedWhat + ex.Message, path); return; }
            var e = new Entry { KindId = kindId, Path = path, FieldIdx = fieldIdx, SubKey = subKey, Value = enc, Key = key };
            snap[key] = e;
            ordered.Add(e);
        }

        /// <summary>The keyed-collection ORDER entry (SubKey "" on an EntityCollection field — a slot no
        /// other entry uses: element descends live under their own paths). Same prev-reuse contract as
        /// <see cref="AddEncoded"/>: an unchanged sequence returns the previous tick's array and the diff
        /// settles it by reference.</summary>
        private static void AddKeyOrder(List<Entry> ordered, Dictionary<string, Entry> snap, RailType rt, RailField f,
                                        ushort fieldIdx, byte kindId, string path, List<string> liveKeys)
        {
            var key = SnapKey(path, fieldIdx, "");
            if (snap.ContainsKey(key)) return; // first deterministic path wins
            byte[] enc;
            try { enc = RailMeta.EncodeKeyOrder(liveKeys, _snapshot.TryGetValue(key, out var prev) ? prev.Value : null); }
            catch (Exception ex) { Incident(rt.Type, f.Name, "key-order encode failed: " + ex.Message, path); return; }
            var e = new Entry { KindId = kindId, Path = path, FieldIdx = fieldIdx, SubKey = "", Value = enc, Key = key };
            snap[key] = e;
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
                sb.AppendLine("roots: T (level clock) | F#<factionDefGuid> | S#<siteId> | U#<tacUnitId> | V#<vehicleId> | ES (event system) | MG (mission generator) | MK (marketplace)");
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
