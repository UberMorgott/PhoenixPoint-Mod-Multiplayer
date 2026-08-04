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
        public const byte MsgStructural = 3; // root create/destroy (law 3): native-Serializer blob payloads (law 6)
        public const byte MsgCrcReport = 4;  // client→host subtree CRC (law 7 drift backstop, see HandleCrcReport)

        // The IDLE FLOOR between cycles, NOT the delivery cadence — and this is the difference the
        // 0.5 f it used to hold got wrong. A cycle is time-sliced, so the real 625-root walk takes ~0.25 s
        // of wall clock at SliceBudgetMs; the old value then parked the rail for another ~0.25 s doing
        // nothing before the next one could even START. A host change that no GESTURE flushes — every
        // change the SIM produces: an exploration finishing, resources ticking, a mission spawning, an
        // aircraft arriving — therefore waited a whole idle period plus a whole walk, i.e. 0.25-0.75 s
        // with a mean of ~0.5 s. That mean IS the user's "constant lag of about half a second in
        // everything" (3-instance session 2026-08-04 23:22); measured against it, the gesture path is
        // already fine (141 ms end to end, IntentRail's FlushNow).
        // The cost governor is SliceBudgetMs, never this: the walk can only ever spend ~3 ms of a frame
        // whether it runs back-to-back or not, so removing the idle buys latency and no frame time. What
        // this floor still guards is the DEGENERATE graph — a geoscape small enough to walk in under one
        // slice would otherwise run a full walk + 22k-entry diff + snapshot swap EVERY frame at 60 Hz.
        // ponytail: this halves the mean (0.5 s → ~0.25 s = one walk). Going below one walk needs either a
        // bigger SliceBudgetMs (pays frame time on every peer) or a per-slice emit (the whole-cycle
        // deletion set stops being computable) — do neither until in-game says 0.25 s is still too slow.
        private const float TickInterval = 0.1f;   // ≤10 Hz floor; a real cycle is slower and runs back-to-back
        private const float ExceptionRetryBackoff = 1f; // min pause between forced retries after a tick exception
        private const int MaxPacketBytes = 45000;  // chunk flush threshold
        internal const int MaxValueBytes = 8192;   // per-entry cap: 45000 + 8192 stays under the u16 envelope
        private const int MaxEntities = 50000;     // graph-chase brake
        private const int MaxDepth = 12;
        // Per-frame walk budget. A static readonly FIELD, not a const, on purpose: a const is inlined into
        // the IL as a literal and RailCheck L50 could then no longer see that the walk loop actually reads
        // a budget — i.e. it could not tell a budget-gated walk from a monolithic one.
        internal static readonly double SliceBudgetMs = 3.0;
        // The URGENT budget: a cycle a gesture asked for (FlushNow) may spend this much per frame instead.
        // Measured need — an inventory/equip change took ~¼-⅓ s to appear on the other peers, because
        // DiffAndEmit only runs when the WHOLE 625-root graph has been walked: at 3 ms/frame that is ~15
        // frames on top of the tick wait, and FlushOnHostGesture only makes a cycle START earlier, never
        // FINISH earlier. At 8 ms the same cycle completes in ~6 frames (~100 ms) while still costing well
        // under one 60 fps frame, so the walk can never own a frame outright. Spent ONLY when the local user
        // has no uncommitted input in flight (RunSlice) — that drag is the one thing the 3 ms floor was
        // bought for. Field, not const, for the same reason as SliceBudgetMs: RailCheck L74 must SEE the read.
        internal static readonly double UrgentSliceBudgetMs = 8.0;

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
        private static bool _cycleUrgent;   // a FlushNow is outstanding — see RunSlice / UrgentSliceBudgetMs
        private static float _cycleStartedAt;

        /// <summary>WALL seconds the last completed cycle took, BeginCycle → DiffAndEmit. Not a perf
        /// counter (RailCost owns that): it is the PUBLISH LAG of every value this walk carries, and
        /// <see cref="TimeAnchor"/> is the one consumer that must price it, because a clock latched at
        /// BeginCycle and delivered a whole walk later lands the client's clock exactly this far in the
        /// past — a bias no drift check can see, since both sides then agree with the anchor. Deliberately
        /// NOT cleared by BeginWalkState: the latch happens INSIDE BeginCycle (IdentityResolver.Roots), so
        /// zeroing it there would zero the very number the latch is about to read. It is last-value-wins,
        /// which is all it has to be — a stale duration misprices exactly one latch, and the consumer
        /// clamps it anyway.</summary>
        internal static float LastCycleSeconds { get; private set; }
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
        // ─── Structural layer (law 3): root-SET diff — a root key appearing/vanishing between walks
        // is an entity create/destroy, which value deltas can never express. Enabled per root-key
        // prefix (the one enable table of the ONE mechanism — everything else logs a visible opt-out,
        // same contract as the coverage report). "U#" = GeoCharacter: a plain serializable class the
        // game itself registers by dict write on load (GeoLevelController.ProcessInstanceData:607-610);
        // "V#" = GeoVehicle: a MonoBehaviour-bound ACTOR, so it rides the actor payload arm below and NEVER
        // a native graph blob (law 3 "never replace a MonoBehaviour-bound instance" / RailCheck L3).
        private static readonly string[] StructuralPrefixes = { "U#", "V#" };

        /// <summary>Root kinds whose entity type is a <c>UnityEngine.Object</c> and which are DELIBERATELY
        /// not structurally enabled, each with the grounded reason. Read by RailCheck L31, which otherwise
        /// demands that every actor-typed root be either enabled or listed here: an actor root that can
        /// appear or vanish at runtime and is neither is the exact silent swallow this batch closed for
        /// "V#" (create logs one "not enabled" line, then every value delta under it dies at "entity not
        /// found" forever). Order/format is the table itself — a row is a claim, so it carries its proof.
        ///
        /// "S#" (GeoSite) is the load-bearing row and it is a FACT, not a deferral: mid-campaign the site
        /// SET never grows or shrinks. `Instantiate&lt;GeoSite&gt;` does not exist anywhere in the game;
        /// `GeoActorSpawner.SpawnSite` (GeoActorSpawner.cs:14) has exactly ONE caller,
        /// `GeoInitialWorldSetup.InstanciateSites:458` = campaign WORLD GENERATION, before any save and so
        /// before any join; `AllSites` is added to / removed from in one place each (GeoMap.cs:380 / :407,
        /// inside RegisterActor/UnregisterActor); and the allocator the RCA named,
        /// `GeoSitesMapper.GetNextId()` (GeoSitesMapper.cs:205), has ZERO callers — `_nextSiteId` is
        /// derived from the SCENE def list by `BuildSiteIds()` (:182-203) and `SiteId` is only ever set
        /// from scene data (GeoInitialWorldSetup.cs:99) or restored from the save DTO (GeoSite.cs:1572).
        /// The one runtime site mutation is `GeoActorSpawner.ChangeSiteType` (:26-68, sole caller
        /// GeoFactionReward.cs:584): it calls OnExitPlay() then DoEnterPlay() on the SAME GameObject
        /// SYNCHRONOUSLY and never touches SiteId, so the 0.5 s walk cannot even observe the gap.
        /// COST, stated: a site CHANGING TYPE therefore does not mirror — but that is a different layer,
        /// not this one. It is a twin gap (`GeoSiteInstaceData.SiteType`, docs/rail-baseline.txt: EXCLUDED
        /// dto-twin unresolved), and it cannot be closed by a twin row either: writing `GeoSite.Type`
        /// without ChangeSiteType's component swap leaves a client site labelled PhoenixBase with no
        /// GeoPhoenixBase component, which is worse than today. That needs a FOURTH shape ("an existing
        /// actor re-typed in place") and is named here rather than half-built.
        ///
        /// The four singleton component roots need no create/destroy by construction: each is yielded from
        /// a fixed accessor on the level (IdentityResolver.RootKinds' `Get` rows) and lives exactly as long
        /// as the level does.</summary>
        /// <summary>ONE definition of "a create of this thing ships the ACTOR frame, never a native graph
        /// blob" — read by the host payload choice in <see cref="EmitStructural"/> AND by RailCheck L31, so
        /// the wire rule and the law cannot drift (the same single-discriminator contract as
        /// <see cref="IsDescendPath"/>). A <c>UnityEngine.Object</c> is the whole test: a blob of one would
        /// re-CREATE it and every actor its members reach, because the game's own spawners run inside the
        /// deserializer (<c>[SerializeCustomCreate] ActorComponent.CreateActor</c>, decompile
        /// ActorComponent.cs:336-377). Law 3, and RailCheck L3's apply-side twin.</summary>
        internal static bool IsActorPayloadType(Type t) => t != null && typeof(UnityEngine.Object).IsAssignableFrom(t);

        internal enum CreatePayload { ActorFrame, DescendFrame, GraphBlob }

        /// <summary>The host's create-payload DECISION, as one named function rather than an inline ternary,
        /// so RailCheck L31 can drive the real choice instead of re-deriving it. That distinction is the
        /// difference between a law and decoration: a predicate over types stays green if the emit arm is
        /// reverted, this does not (same reason L30 exists beside L29). Order is load-bearing — the actor
        /// test comes FIRST, because an actor reached through a Descend field is still an actor and must
        /// never take the blob.</summary>
        internal static CreatePayload PayloadFor(string rootKey, Type valueType) =>
            IsActorPayloadType(valueType) ? CreatePayload.ActorFrame
            : IsDescendPath(rootKey) ? CreatePayload.DescendFrame
            : CreatePayload.GraphBlob;

        /// <summary>The enable table above, for RailCheck L31 — ONE table, same contract as
        /// <see cref="StructuralDescendKinds"/>.</summary>
        internal static string[] StructuralRootPrefixes => StructuralPrefixes;

        internal static readonly (string Prefix, string Why)[] StructuralRootOptOuts =
        {
            ("S#", "no runtime create/destroy exists: sole spawn is GeoInitialWorldSetup.InstanciateSites:458 " +
                   "(world generation), GeoSitesMapper.GetNextId():205 has zero callers, and ChangeSiteType:26-68 " +
                   "reuses the same GameObject and SiteId — the site set is fixed and rides the save transfer (law 1)"),
            ("ES", "level-lifetime singleton component (GeoLevelController.EventSystem) — cannot appear or vanish"),
            ("MG", "level-lifetime singleton component (GeoLevelController.MissionGenerator) — cannot appear or vanish"),
            ("MK", "level-lifetime singleton component (GeoLevelController.Marketplace) — cannot appear or vanish"),
            ("GL", "the level controller itself — its existence IS the session"),
        };
        // Keyed-COLLECTION elements ride the same set-diff: the walk's EntityCollection arm records
        // enabled element types under their full element path (…Layout._facilities#<FacilityId>), so
        // create/destroy falls out of the identical _prevRoots comparison. Only listed types are
        // recorded — an element path in the set is enabled BY CONSTRUCTION (StructuralEnabled's
        // '.'-rule). GeoPhoenixFacility: plain class, native-blob constructable; client wiring =
        // the game's own load path (GeoPhoenixBase.InitFacility, see GenericApplier).
        private static readonly HashSet<Type> StructuralElemTypes = new HashSet<Type>
        {
            typeof(PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility),
        };

        /// <summary>Apply-side twin of the set above (GenericApplier): structurally-owned elements get
        /// their membership from the create/destroy set-diff, so the order-vector must stay order-only
        /// for them (vector-driven removal would also delete the client's corridors — per-peer ids).</summary>
        internal static bool IsStructuralElemType(Type t) => t != null && StructuralElemTypes.Contains(t);

        /// <summary>PEER-LOCAL: derived decoration whose identity every peer mints for ITSELF, so the rail
        /// deliberately mirrors none of it. Base corridors are the sole member today — load STRIPS them
        /// (GeoPhoenixBaseLayout.InitAfterDeserialiaztion:903-918), each peer's native rebuild re-mints ids
        /// off its own `++_lastFacilityId` (AddFacility:585), reshape replaces the object reusing the id and
        /// removal zeroes it: the same visual corridor legitimately carries a different id on every peer, and
        /// mirroring them yields ghost corridors.
        ///
        /// Consumed at the container's ELEMENT-COLLECTION point (VisitEntity's EntityCollection arm), which is
        /// the whole law (RailCheck L58): a peer-local element is invisible to its OWNER, not merely inside its
        /// own subtree — it contributes neither its own entries nor its KEY to the container's order/membership
        /// vector. Declaring it only as a subtree PREFIX was a permanent CRC divergence on every base site
        /// (S#98/S#99/S#170): the vector rides at the OWNER path, which is SHORTER than any peer-local prefix
        /// and therefore unmatchable by <see cref="CrcOfEntries"/>'s whole-segment PrefixMatch, so each peer
        /// hashed its own ids forever — and the shipped vector made ReorderByKeys shuffle the client's own
        /// corridors against host ids it does not have.</summary>
        internal static bool IsPeerLocal(object e) =>
            e is PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility f && f.IsCorridor;

        // A Descend FIELD going null↔non-null is the THIRD structural shape — neither a root key nor a
        // keyed-collection element, and the one the set-diff had no representation for. Untreated it is a
        // silent swallow in BOTH directions: on null→obj every value entry under the path dies at
        // GenericApplier's "entity not found" (the object it addresses does not exist on the client), and
        // on obj→null NOTHING is emitted at all — the tombstone loop only ever deletes dict SUBkeys
        // (:467-468), so a vanished path leaves the client's object in place forever. Recording the child
        // path in VisitEntity's Descend arm makes both directions fall out of the identical _prevRoots
        // comparison in EmitStructural, with no new message kind.
        //
        // Gated on the DECLARED field type, so one row covers a whole abstract family (GeoMission has 12
        // concretions; a runtime-type set would have to list them all). GeoMission: plain class
        // (decompile GeoMission.cs:44), every classified member covered by the value rail
        // (docs/rail-baseline.txt), client wiring = the game's OWN load path
        // (GeoSite.ProcessInstanceData:1621-1631 — see GenericApplier.ApplyDescendCreate).
        //
        // The rows below the first are the SAME shape one level down: a nullable Descend INSIDE an already
        // enabled family is the identical swallow, so enabling it is a row here and nothing else (RailCheck
        // L29 nullable-unenabled sweeps for exactly this). Both are safe to CONSTRUCT on a client because
        // neither carries identity another object owns — verified, not assumed:
        //   • UnityDateTime (GeoMission.GlobalTime) — assigned through the implicit DateTime→UnityDateTime
        //     operator, which allocates a fresh instance every time (UnityDateTime.cs:33-38, used at
        //     GeoMission.cs:237), so it is never aliased. Its one member needed LeafKind.DateTimeTicks
        //     first, or the create would have shipped a phantom 01/01/0001 clock (see LeafKindOf).
        //   • GeoSquad (GeoMission._squad) — plain [SerializeType] class, parameterless ctor, and every
        //     construction site in the game is a fresh `new GeoSquad(...)` built for that one mission
        //     (GeoSite.cs:1170, GeoscapeView.cs:1045, UIStateRosterDeployment.cs:324). Its `Units` ride as
        //     EntityRefs, so the client's copy points at the client's OWN GeoCharacters.
        // DELIBERATELY ABSENT — GeoHavenZone (GeoStealAircraftMission._zone, GeoFaction.BuildingZone):
        // constructing one would hand the client a phantom instead of the haven's own zone. It was a KEYING
        // gap, and it is closed as one: the zone is named by the path its haven addresses it by
        // (IdentityResolver.IsRefAddressableType / HavenZoneRef), so the field rides as a Leaf/EntityRef —
        // a reference, never a create. Belt = RailCheck L28 subentity-ref-*.
        private static readonly Type[] StructuralDescendKindTable =
        {
            typeof(PhoenixPoint.Geoscape.Entities.GeoMission),
            typeof(Base.Utils.UnityDateTime),
            typeof(PhoenixPoint.Geoscape.Entities.GeoSquad),
        };
        private static readonly HashSet<Type> StructuralDescendTypes = new HashSet<Type>(StructuralDescendKindTable);

        /// <summary>The declaration above, for RailCheck L29 and its closure seed — ONE table, so a new
        /// row cannot land with the harness still sweeping the old set (same contract as
        /// <see cref="IdentityResolver.RootKinds"/>).</summary>
        internal static Type[] StructuralDescendKinds => StructuralDescendKindTable;
        internal static bool IsStructuralDescendType(Type t) => t != null && StructuralDescendTypes.Contains(t);

        /// <summary>ONE definition of the three structural path shapes, read by the host emit AND the
        /// client apply (two copies would be free to disagree — the GeoItem/TypeKeyable bug shape):
        /// root key = no '.', keyed-collection ELEMENT = the last segment carries '#', Descend FIELD =
        /// neither. The shape decides the CREATE payload: a Descend field ships its concrete TYPE NAME,
        /// not a native graph blob, because the blob is not expressible here — a mission's serialized
        /// members include `Site` (GeoSite) and `_stealAircraft` (GeoVehicle), both MonoBehaviour-bound
        /// actors with [SerializeCustomCreate] spawners, so decoding such a blob would CREATE duplicate
        /// site/vehicle actors on the client (law 3: never replace a MonoBehaviour-bound instance). The
        /// type name is enough because the field's own value entries ride the SAME batch immediately
        /// behind the create packet (EmitStructural runs before Emit), and the rail covers the whole
        /// object — including `Site`, which classifies as a stable-id Leaf/EntityRef, not a walk-into.
        /// A type name on this wire is the existing convention, not a new concept: kind defs already
        /// ship Type.FullName (:1095) and the client resolves them with AccessTools.TypeByName.</summary>
        internal static bool IsDescendPath(string key)
        {
            int d = key.LastIndexOf('.');
            return d > 0 && key.IndexOf('#', d) < 0;
        }
        private static readonly Dictionary<string, object> _walkRoots = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly HashSet<string> _prevRoots = new HashSet<string>(StringComparer.Ordinal);
        private static bool _rootsSeeded;
        private static readonly HashSet<string> _structuralSkipsLogged = new HashSet<string>(StringComparer.Ordinal);

        // ─── Law-7 drift backstop (see RootCrc / HandleCrcReport) ──────────
        // Per-root seq of the last delta the host EMITTED under it: the quiescence gate of the CRC
        // compare. Without it every busy root reports "diverged" on every report (the client's hash is
        // always one round-trip old) and re-emits forever — the rhythmic walk cost this rail already paid.
        private static readonly Dictionary<string, uint> _rootTouchedSeq = new Dictionary<string, uint>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _crcHeals = new Dictionary<string, int>(StringComparer.Ordinal); // "<peer>|<root>" → re-emits spent
        private static bool _crcWalk;   // inside RootCrc: the walk must ship NOTHING and touch no tick state
        // RootCrc's own scratch — see there. Separate from the periodic walk's set on purpose: a CRC may be
        // taken on the host mid-cycle (HandleCrcReport) and must not disturb the cycle in progress.
        private static readonly List<Entry> _crcOrdered = new List<Entry>();
        private static readonly HashSet<object> _crcVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<string, Entry> _crcSnap = new Dictionary<string, Entry>(StringComparer.Ordinal);
        // Subtree paths the rail declares PER-PEER (see IsPeerLocal). Recorded by the walk where it drops the
        // element; excluded from the CRC because both peers legitimately hold different ones, so hashing them
        // would report permanent false divergence. BELT ONLY since law 58: the element is dropped at its
        // container, so nothing under these paths is emitted in the first place.
        private static readonly HashSet<string> _peerLocalPaths = new HashSet<string>(StringComparer.Ordinal);

        private static List<KeyValuePair<string, object>> _cycleRoots;
        private static int _cycleNext;
        private static int _cycleFrames;
        private static double _cycleWalkMs;
        private static double _maxSliceMs;

        // The forced-emit scope THIS cycle carries, snapshotted at BeginCycle. The dict CENSUS is
        // walk-ORDER sensitive (ForcedNow is asked during the walk), so a prefix armed halfway through a
        // cycle would silently miss every root already passed. Snapshotting is what lets a late force stay
        // armed for its OWN next cycle instead of abandoning and restarting this one — see HostTick.
        private static readonly List<string> _cycleForcePrefixes = new List<string>();
        private static bool _cycleForceFull;

        // ─── Lifecycle (driven by SyncEngine) ──────────────────────────────

        public static void Reset()
        {
            ResetForReloadBoundary();
            Seq.Reset();
            _kindIds.Clear(); _kinds.Clear();
            _rootTouchedSeq.Clear(); // the seq stream restarts, so recorded touch-seqs would over-gate
            _reportWritten = false;
        }

        /// <summary>Reload boundary: drop snapshot + baseline (post-reload state reaches clients via the
        /// save transfer); seq streams PERSIST (rca-3 contract) so later deltas keep applying.</summary>
        public static void ResetForReloadBoundary()
        {
            AbandonCycle();
            LastCycleSeconds = 0f;      // a peer that was HOST before this boundary must not price the next
                                        // latch (as host again, or as the seed of its own client DTO) off a
                                        // walk that belonged to a level it no longer has
            _snapshot = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _sentKinds.Clear();
            _prevRoots.Clear(); _rootsSeeded = false; // re-seed silently on the post-boundary baseline walk
            _baselined = false;
            _forceFull = false;
            _forcePrefixes.Clear();
            _crcHeals.Clear(); // the clients' state was wholesale replaced by the save transfer — re-arm the heal
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

        /// <summary>Whole-segment path-prefix match ("U#3" never matches "U#30") — shared by the forced
        /// re-emit scope and the CRC's per-peer exclusion.</summary>
        private static bool PrefixMatch(IEnumerable<string> prefixes, string path)
        {
            foreach (var p in prefixes)
                if (PrefixMatchOne(p, path)) return true;
            return false;
        }

        private static bool PrefixMatchOne(string p, string path) =>
            path.Length >= p.Length && string.CompareOrdinal(path, 0, p, 0, p.Length) == 0 &&
            (path.Length == p.Length || path[p.Length] == '.');

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── Law-7 drift backstop: per-root subtree CRC ─────────────────────
        // THE only host↔client reconciliation in the rail. Everything else compares host-NOW against
        // host-BEFORE (DiffAndEmit), so state the host DELETES is invisible by construction: a vanished
        // path emits no entry and no tombstone (only dict SUBKEYS are tombstoned), and the baseline walk
        // emits nothing at all. Result today: a completed mission, a scrapped item or a dead entity's
        // subtree lives on the client forever with NO log line anywhere. This makes it loud.

        /// <summary>CRC32 of ONE root's subtree, hashed from the entries of the SAME canonical walk that
        /// produces the wire (<see cref="VisitEntity"/> — a second "CRC walk" would drift from the rail it
        /// is meant to police, so there is none; the walk's scratch is already parameterized, only its three
        /// tick-state side effects need the <c>_crcWalk</c> guard). Run by BOTH peers, so it must be
        /// symmetric: fresh scratch each call, because a standalone root walk sees cross-root aliases that
        /// the periodic cycle's SHARED visited set would have credited to whichever root came first.</summary>
        internal static uint RootCrc(string rootKey, object rootObj)
        {
            if (rootObj == null || (rootObj is UnityEngine.Object uo && uo == null)) return 0u;
            // Scratch is REUSED and cleared, not reallocated — "fresh" means EMPTY, which is what the
            // cross-root-alias argument above actually needs. The periodic walk made the same call
            // (double-buffered _ordered/_visited/_snapshotBack) after a fresh set of three per tick at 23k
            // entries turned out to be megabytes of garbage for nothing; this one runs on the CLIENT, once
            // a second, over roots up to the same size. Not reentrant, and never was: both drivers
            // (ClientCrcTick, HandleCrcReport) run on the game loop and VisitEntity never calls back in.
            _crcOrdered.Clear();
            _crcVisited.Clear();
            _crcSnap.Clear();
            _peerLocalPaths.Clear();
            _crcWalk = true;
            try { VisitEntity(rootKey, rootObj, _crcVisited, _crcOrdered, _crcSnap, 0); }
            finally { _crcWalk = false; }
            return CrcOfEntries(_crcOrdered, _peerLocalPaths);
        }

        /// <summary>The hash itself, split out so the harness can assert what it must detect (L25): entries
        /// in walk order (canonical by law 6), each as its own SnapKey + length-delimited value bytes, so a
        /// REMOVED entry, a changed value and a reorder all change the CRC.</summary>
        internal static uint CrcOfEntries(List<Entry> ordered, ICollection<string> peerLocalPaths)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                foreach (var e in ordered)
                {
                    if (peerLocalPaths != null && peerLocalPaths.Count > 0 && PrefixMatch(peerLocalPaths, e.Path)) continue;
                    w.Write(e.Key ?? "");
                    w.Write((ushort)(e.Value?.Length ?? 0));
                    if (e.Value != null) w.Write(e.Value);
                }
                return Multiplayer.Util.Crc32.Compute(ms.ToArray());
            }
        }

        /// <summary>Host half: a client reported the CRC of ONE of its root subtrees, taken at the seq it had
        /// applied. QUIESCENCE gate first — if the host emitted anything under that root after the client's
        /// seq, the client is merely behind and a mismatch means nothing (comparing anyway would flag every
        /// busy root and re-emit it forever). On a quiescent root a mismatch IS divergence: nothing on the
        /// host will ever emit a change for it, so the client stays wrong until something re-states the
        /// truth — which is exactly <see cref="ForceReemit"/> (values + dict censuses). ONE heal attempt per
        /// episode: if the next quiescent report still differs, re-emitting provably cannot fix it (a removed
        /// path or a structural entity the rail does not mirror — there is nothing left to re-state), so say
        /// so and stop. A matching report clears the counter and re-arms the heal.</summary>
        internal static void HandleCrcReport(ulong peerId, string rootKey, uint clientCrc, uint clientSeq)
        {
            if (string.IsNullOrEmpty(rootKey)) return;
            var geo = GeoLevel();
            if (geo == null) return;
            string tag = peerId + "|" + rootKey;
            var mine = IdentityResolver.Resolve(geo, rootKey, null);
            if (mine == null)
            {
                // The root is gone on the host but still live on the client: a destroy the structural layer
                // does not mirror (StructuralPrefixes). No value re-emit can delete it — only naming it helps.
                if (_structuralSkipsLogged.Add("crc-orphan:" + tag))
                    Debug.LogError("[Multiplayer][rail] CRC backstop: peer " + peerId + " still holds root '" + rootKey +
                                   "' which the host no longer has — destroy not mirrored, that client is diverged");
                return;
            }
            if (_rootTouchedSeq.TryGetValue(rootKey, out var touched) && touched > clientSeq) return; // behind, not diverged
            uint hostCrc = RootCrc(rootKey, mine);
            if (hostCrc == clientCrc) { _crcHeals.Remove(tag); return; }
            _crcHeals.TryGetValue(tag, out var heals);
            _crcHeals[tag] = heals + 1;
            if (heals == 0)
            {
                Debug.LogError("[Multiplayer][rail] CRC backstop: root '" + rootKey + "' DIVERGED on peer " + peerId +
                               " (host " + hostCrc.ToString("X8") + " != client " + clientCrc.ToString("X8") +
                               " at quiescent seq " + clientSeq + ") — forcing a re-emit of the subtree");
                ForceReemit(rootKey);
            }
            else if (heals == 1)
                Debug.LogError("[Multiplayer][rail] CRC backstop: root '" + rootKey + "' STILL diverged on peer " + peerId +
                               " after a forced re-emit — the client holds state the host cannot re-state (a removed " +
                               "path, or a structural entity the rail does not mirror). No further re-emits for this root.");
        }

        /// <summary>Record which root an emitted entry belongs to, at the seq it rode — the quiescence gate's
        /// only input. Root key = the path up to the first segment separator (root keys never contain '.').</summary>
        private static void TouchRoot(string path, uint seq)
        {
            if (string.IsNullOrEmpty(path)) return;
            int dot = path.IndexOf('.');
            _rootTouchedSeq[dot < 0 ? path : path.Substring(0, dot)] = seq;
        }

        /// <summary>The quiescence ledger, read-only — RailCheck L40 asserts that the residual drop path
        /// records its root here, since a drop that leaves the root looking quiescent makes the CRC
        /// backstop hash a host truth the wire never carried.</summary>
        internal static uint RootTouchedAt(string rootKey) =>
            _rootTouchedSeq.TryGetValue(rootKey, out var s) ? s : 0u;

        // ─── Host tick: walk → diff → emit ─────────────────────────────────

        /// <summary>Ship host state as soon as the walk can carry it: start the sliced cycle NOW instead of
        /// waiting out the rest of the 0.5 s poll. That is ALL it does, and the "that is all" is the fix.
        ///
        /// It used to run one SINGLE-SHOT MONOLITHIC tick — the whole graph read in ONE frame — after
        /// abandoning any cycle in progress. Measured on the user's own host log (2026-07-30, 275 ticks):
        /// walk p50=40 ms / p90=60 ms / max=95 ms, and the Unity frame counter in the same lines reads
        /// 60 fps across the periodic sliced cycles but 10-37 fps across exactly the windows bounded by
        /// forced ticks (110 of 275 inter-tick gaps were under 0.45 s, i.e. forced). The callers are not
        /// rare: <see cref="FlushOnHostGesture"/> sits at the top of <c>IntentRail.ShouldRunNative</c>,
        /// which every capture seam calls on EVERY invocation — and an equip/augment screen re-flushes
        /// <c>GeoCharacter.SetItems</c> several times per click (EquipSync's own repeat-guard note: "6-7
        /// frames in a row after one drag"). So the host paid 6-7 whole-graph walks per drag. That is the
        /// ~3x host framerate loss, and why it showed on the soldier and augmentation screens first.
        ///
        /// Now a flush can only make the cycle START earlier, never make a frame longer: all walk work
        /// stays capped at <see cref="SliceBudgetMs"/> per frame, and N gestures inside one cycle coalesce
        /// into ONE walk by construction (a cycle already running simply keeps running). Nothing is
        /// skipped and no tick rate was lowered — the walk, its order and its emit are byte-identical.
        /// COST, stated: a forced delta ships at cycle completion rather than in the same frame. That is the
        /// latency the user measured on inventory (~¼-⅓ s), so the upgrade path this comment reserved is now
        /// TAKEN: the flag also marks the cycle URGENT, and an urgent cycle walks at
        /// <see cref="UrgentSliceBudgetMs"/> per frame (~100 ms end to end) unless the local user has
        /// uncommitted input in flight. Still not the same frame — the whole point is that no walk owns one.
        ///
        /// No guards needed: <see cref="HostTick"/> still returns on non-host / no session / no geoscape,
        /// so this can be called from any seam that knows the host just changed something the client
        /// must not wait for.</summary>
        public static void FlushNow() { _nextTickAt = 0f; _cycleUrgent = true; }

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

        // ─── N7: the def-aliasing falsifier — DELETED with the list it measured ─────
        // It existed to answer ONE question: may RailMeta's type-name LocalizedTextBind refusal stay a
        // stopgap, or must it be replaced by the general reference-identity law? Both branches of its own
        // exit criterion are now moot: the refusal is gone, a bind rides as LeafKind.TextBind (a leaf apply
        // REPLACES the reference and cannot write into a def-shared instance), and DefOwnership is the
        // reference-identity index the criterion asked for. Counting aliased binds would change nothing.

        public static void HostTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost || !engine.IsActiveSession) return;
            float now = Time.realtimeSinceStartup;
            // A force NEVER abandons a cycle in progress any more. It cannot: abandoning is what let a
            // gesture storm cost one whole-graph walk per gesture, and it also had a silent starvation
            // mode (forces arriving faster than a cycle completes ⇒ every cycle restarted ⇒ nothing ever
            // shipped, with no log line). The scope a force needs from root 0 is the dict census, and that
            // is preserved instead by snapshotting the scope at BeginCycle: a force armed mid-cycle is not
            // in this cycle's snapshot, so it survives in _forcePrefixes/_forceFull and gets its own cycle
            // on the very next frame (FlushNow already zeroed _nextTickAt).
            if (_cycleRoots == null && now < _nextTickAt) return;
            var geo = GeoLevel();
            if (geo == null) { AbandonCycle(); _nextTickAt = now + TickInterval; return; }
            ArmChangeDrivenFlush(geo.Timing);

            try
            {
                if (_cycleRoots != null) { RunSlice(engine); return; }
                _nextTickAt = now + TickInterval;
                BeginCycle(geo);
                RunSlice(engine);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][rail] DiffEngine tick failed: " + ex);
                AbandonCycle();
                // The forced scope needs nothing re-armed here: DiffAndEmit never ran, so it never
                // consumed _forceFull / _forcePrefixes and the next cycle picks the same scope back up.
                // Backoff, never latch-off: FlushNow here (_nextTickAt=0) retried —
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
            _walkRoots.Clear();
            _peerLocalPaths.Clear();
        }

        /// <summary>Cycle start: SNAPSHOT the root list — the only live-graph enumeration that would
        /// otherwise span frames. Every deeper enumeration happens inside one slice (VisitEntity finishes
        /// its whole root synchronously), so no enumerator over live game state survives a frame.</summary>
        private static void BeginCycle(GeoLevelController geo)
        {
            BeginWalkState();
            // Freeze the forced-emit scope for the whole cycle (see _cycleForcePrefixes): the census is
            // asked DURING the walk, so the scope must not change under it.
            _cycleForcePrefixes.Clear();
            _cycleForcePrefixes.AddRange(_forcePrefixes);
            _cycleForceFull = _forceFull;
            _cycleRoots = new List<KeyValuePair<string, object>>();
            foreach (var r in IdentityResolver.Roots(geo)) _cycleRoots.Add(r);
            _cycleNext = 0; _cycleFrames = 0; _cycleWalkMs = 0; _maxSliceMs = 0;
            _cycleStartedAt = Time.realtimeSinceStartup;
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
            // The urgency gate (the upgrade path FlushNow reserved). An urgent cycle is one a GESTURE asked
            // for, and a gesture's delta is what the other peers are waiting on — so it may spend the larger
            // budget. It may NOT while the local user has uncommitted input in flight: the low floor exists
            // for exactly that drag, and raising it there would cost frame time during the interaction it
            // was bought to protect. Asked of input state, not of screens (OpenUiRepaint.LocalInputInFlight).
            double budget = _cycleUrgent && !OpenUiRepaint.LocalInputInFlight() ? UrgentSliceBudgetMs : SliceBudgetMs;
            var sw = Stopwatch.StartNew();
            while (_cycleNext < _cycleRoots.Count)
            {
                var root = _cycleRoots[_cycleNext++];
                VisitRoot(root.Key, root.Value);
                if (sw.Elapsed.TotalMilliseconds >= budget) break;
            }
            _cycleFrames++;
            double ms = sw.Elapsed.TotalMilliseconds;
            _cycleWalkMs += ms;
            if (ms > _maxSliceMs) _maxSliceMs = ms;
            if (_cycleNext < _cycleRoots.Count) return;
            int roots = _cycleRoots.Count;
            _cycleRoots = null;
            _cycleUrgent = false; // this cycle carried whatever the flush asked for
            LastCycleSeconds = Time.realtimeSinceStartup - _cycleStartedAt;
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

        // The single-shot monolithic Tick() that used to live here is DELETED, not disabled. It was the
        // ONE way a whole-graph walk could land inside a single frame, and every forced path took it —
        // which is the measured 34-95 ms host stall (see FlushNow). There is now exactly one walk driver,
        // RunSlice, and it is budget-gated; RailCheck L50 asserts that structurally.

        private static void DiffAndEmit(NetworkEngine engine, long walkMs, double maxSliceMs, int frames, int roots)
        {
            var sw = Stopwatch.StartNew();
            var ordered = _ordered;
            var newSnap = _snapshotBack;

            // Diff: changed/new pairs in walk order (canonical), then subKey deletions. An unchanged field
            // kept the previous tick's array (RailMeta.EncodeFieldValue with prev), so BytesEqual's
            // ReferenceEquals fast path settles it without a byte compare.
            var changed = new List<Entry>();
            bool anyForced = _cycleForcePrefixes.Count > 0;
            foreach (var e in ordered)
                if (_cycleForceFull || !_snapshot.TryGetValue(e.Key, out var old) || !RailMeta.BytesEqual(old.Value, e.Value) ||
                    (anyForced && PrefixMatch(_cycleForcePrefixes, e.Path)))
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
                // (and could hit a different entity that took over the old path). An entity whose ONLY
                // covered fields are dicts emits NO entries once they empty, so livePaths alone would
                // suppress its tombstones forever — MOD-state roots ARE that shape (dict-only stores).
                // The _walkRoots arm is scoped to those ("M#") on purpose: _walkRoots is filled BEFORE
                // VisitEntity, which has whole-entity bails (depth :549, entity cap :550, def-owned :555,
                // no fields :559) — for a GAME root a bail would tombstone every dict subkey under it and
                // wipe live client dicts, and the deterministic entity-cap bail would do it permanently.
                if (!livePaths.Contains(kv.Value.Path) &&
                    !(kv.Value.Path.StartsWith("M#", StringComparison.Ordinal) && _walkRoots.ContainsKey(kv.Value.Path)))
                    continue;
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
            // Consume only what THIS cycle carried: a full resend armed mid-cycle shipped nothing and must
            // survive into the cycle that will actually honour it.
            bool wasForceFull = _cycleForceFull;
            if (_cycleForceFull) _forceFull = false;

            if (!_reportWritten) { WriteCoverageReport(ordered.Count); _reportWritten = true; }

            // The line the stutter hunt reads: did the periodic walk stay inside its per-frame budget.
            if (MpDiag.On)
                Debug.Log("[MP][rail] cycle: frames=" + frames + " walk=" + walkMs + "ms maxSlice=" +
                          maxSliceMs.ToString("F1", CultureInfo.InvariantCulture) + "ms roots=" + roots +
                          " changed=" + changed.Count);

            if (!_baselined && !wasForceFull)
            {
                _baselined = true;
                SeedRoots(); // baseline root set — clients got these entities via the save transfer (law 1)
                Debug.Log("[Multiplayer][rail] DiffEngine BASELINE: entities=" + _entityCounts.Values.Sum() +
                          " fields=" + ordered.Count + " walk=" + walkMs + "ms (no emit — clients share the save)");
                // A ForceReemit that raced the baseline shipped NOTHING — keep its prefixes armed and
                // re-run forced, instead of silently dropping the convergence re-emit.
                if (_forcePrefixes.Count > 0) FlushNow();
                return;
            }
            _baselined = true;
            // Consumed: the emit below carries the forced scope (censuses + values). Only the prefixes THIS
            // cycle walked with are retired — one armed mid-cycle never got its census and stays armed.
            foreach (var p in _cycleForcePrefixes) _forcePrefixes.Remove(p);

            // LOUD zero-match (the swallow class this rail keeps re-learning): a forced scope that matches
            // no covered path re-emits NOTHING, so whatever armed it — a reject's convergence, a CRC heal —
            // never converges anybody, and the ONLY evidence used to be the absence of a delta. EventSync
            // shipped "ES.EncounterRecords#<id>", the element-path form the walk emits ONLY for
            // EntityCollection fields, and every rejected event answer died there in silence. One scan of
            // this cycle's entries per armed prefix, at reject/heal rate.
            if (anyForced)
                foreach (var p in _cycleForcePrefixes)
                {
                    bool hit = false;
                    for (int i = 0; i < ordered.Count && !hit; i++) hit = PrefixMatchOne(p, ordered[i].Path);
                    if (!hit)
                        Debug.LogError("[Multiplayer][rail] ForceReemit scope '" + p + "' matched ZERO covered paths — " +
                                       "nothing was re-emitted and whatever armed it will NOT converge. A scope is whole " +
                                       "path SEGMENTS from a root key (\"S#12\", \"ES\"); the element form " +
                                       "\"<path>.<Field>#<key>\" exists only for EntityCollection fields.");
                }

            int packets = 0, bytes = 0;
            // Structural create/destroy FIRST: the same batch's value entries for a just-created root
            // (and refs to it) then resolve on the client in seq order.
            EmitStructural(engine, ref packets, ref bytes);
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

        /// <summary>One declared root: structural scratch + the walk. Shared by the sliced cycle and the
        /// monolithic tick so both carry the same guards.
        ///
        /// The "already visited" line is a SILENT-SWALLOW counter, not diagnostics: an earlier root's
        /// closure can reach a later root's instance, and <see cref="VisitEntity"/>'s reference `visited`
        /// set then returns with NO entries, NO tombstone and NO incident — the earlier path owns every
        /// field and this root's own paths never appear on the wire. Deterministic (the root order is
        /// fixed, IdentityResolver.RootKinds) but invisible, so name it once per key. RailCheck L28 is the
        /// static belt over the same property.</summary>
        private static void VisitRoot(string key, object value)
        {
            if (value is UnityEngine.Object uo && uo == null) return; // destroyed root = ABSENT (structural)
            WalkRoot(key, value);
            if (value != null && _visited.Contains(value) && _dupRootLogged.Add(key))
                Debug.LogWarning("[Multiplayer][rail] root '" + key + "' (" + value.GetType().Name +
                                 ") was already reached from an EARLIER root — its own subtree ships under that path, nothing under '" +
                                 key + "'. Reorder IdentityResolver.RootKinds if this root should own it.");
            VisitEntity(key, value, _visited, _ordered, _snapshotBack, 0);
        }

        private static readonly HashSet<string> _dupRootLogged = new HashSet<string>(StringComparer.Ordinal);

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
                    {
                        if (val == null) break;   // absent from _walkRoots ⇒ the set-diff reads it as destroyed
                        var descendPath = path + "." + f.Name;
                        // Structural set-diff for a nullable Descend field (see StructuralDescendTypes).
                        // !_crcWalk for the same reason as the element arm: the CRC walk must ship nothing
                        // and touch no tick state.
                        if (!_crcWalk && StructuralDescendTypes.Contains(f.ValueType)) _walkRoots[descendPath] = val;
                        VisitEntity(descendPath, val, visited, ordered, snap, depth + 1);
                        break;
                    }
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
                            // PEER-LOCAL ELEMENTS ARE DROPPED HERE (law 58) — before the element exists for
                            // this container at all, so it contributes neither a key to the order vector below
                            // nor any entry of its own. _peerLocalPaths still records the subtree so the CRC
                            // belt holds for anything that reaches the object by another path.
                            if (IsPeerLocal(e))
                            {
                                _peerLocalPaths.Add(path + "." + f.Name + "#" + k);
                                if (_structuralSkipsLogged.Add("peer-local-optout"))
                                    Debug.Log("[Multiplayer][rail] peer-local elements opted out (corridors: derived, " +
                                              "locally-minted ids — logged once)");
                                continue;
                            }
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
                            // stays silent on idle ticks. Empty/singleton sequences ship too: the vector is
                            // also the MEMBERSHIP carrier for alias collections (GenericApplier), so a
                            // 2→1 or 1→0 transition must still produce an entry.
                            if (!f.Unordered)
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
                        foreach (var (key, e) in elems) // already sorted ordinal above; peer-locals already gone
                        {
                            var childPath = path + "." + f.Name + "#" + key;
                            if (!_crcWalk && StructuralElemTypes.Contains(e.GetType()))
                                _walkRoots[childPath] = e; // structural element set-diff (create/destroy)
                            VisitEntity(childPath, e, visited, ordered, snap, depth + 1);
                        }
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
            !_crcWalk && (_cycleForceFull || (_cycleForcePrefixes.Count > 0 && PrefixMatch(_cycleForcePrefixes, path)));

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
            // TEMP diag (reassign retest): every CHANGED TacUnits list leaving the host (an unchanged
            // encode returns the previous tick's array by reference). Pull after the in-game retest.
            if (!_crcWalk && f.Name == "TacUnits" && val is IList tacDiag &&
                (!_snapshot.TryGetValue(key, out var tacPrev) || !ReferenceEquals(tacPrev.Value, enc)))
            {
                var ids = new StringBuilder();
                foreach (var u in tacDiag) ids.Append(IdentityResolver.RootRef(u) ?? "?").Append(' ');
                Debug.Log("[MP][diag] TacUnits HOST-ENCODE " + path + " count=" + tacDiag.Count +
                          " [" + ids.ToString().TrimEnd() + "]");
            }
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

        /// <summary>Structural-scratch write for one walked root AND the ROOT-key collision detector.
        /// Element keys have had a duplicate check since day one (VisitEntity's keyless abort), root keys
        /// had none: two entities minted the same root key, the second one's whole subtree hit the
        /// "first deterministic path wins" dedup in <see cref="Add"/>/<see cref="AddEncoded"/> and vanished
        /// with no incident, no log, nothing in rail-coverage.txt — the class "this entity does not exist
        /// for the rail" was the one exclusion class the coverage guarantee could not see (how 17 of 27
        /// GeoVehicles stayed unsynced behind faction-local VehicleIDs). Same Incident channel as the
        /// element exclusions, so a clash is a report line, not a silent drop.</summary>
        internal static void WalkRoot(string key, object value)
        {
            if (_walkRoots.TryGetValue(key, out var first) && !ReferenceEquals(first, value))
                Incident(value?.GetType() ?? typeof(object), "(root)",
                         "duplicate ROOT key — a second " + (value?.GetType().Name ?? "null") +
                         " collides with an already-walked " + (first?.GetType().Name ?? "null") +
                         "; its whole subtree is dropped by first-wins dedup (entity invisible to the rail)", key);
            _walkRoots[key] = value;
        }

        /// <summary>This walk's exclusion incidents (the coverage report's "! " lines) — read by the
        /// stage-1 harness to assert the detector above still fires.</summary>
        internal static ICollection<string> WalkIncidents => _walkIncidents;

        private static void Incident(Type t, string field, string reason, string path)
        {
            var line = t.Name + "." + field + ": " + reason + " [" + path + "]";
            if (_walkIncidents.Add(line) && _reportWritten)
                Debug.LogWarning("[Multiplayer][rail] DiffEngine excluded: " + line);
        }

        // ─── Structural emit (root create/destroy — law 3) ─────────────────

        private static void SeedRoots()
        {
            _prevRoots.Clear();
            foreach (var k in _walkRoots.Keys) _prevRoots.Add(k);
            _rootsSeeded = true;
        }

        private static bool StructuralEnabled(string rootKey)
        {
            // An ELEMENT path (contains '.') only ever enters the set through the StructuralElemTypes
            // gate in the EntityCollection arm — enabled by construction. Root keys never contain '.'.
            if (rootKey.IndexOf('.') >= 0) return true;
            for (int i = 0; i < StructuralPrefixes.Length; i++)
                if (rootKey.StartsWith(StructuralPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Root-set diff vs the previous walk → create/destroy packets. A create carries the
        /// entity as a native-Serializer blob (law 6: blobs are licensed ONLY as structural payloads) —
        /// the client reconstructs through the game's own deserialization, PostRead callbacks included.
        /// One packet per item (hires/dismissals are rare, chunking games buy nothing); each packet its
        /// own seq on the ONE ordered stream, emitted BEFORE the value delta of the same walk. A root
        /// whose blob fails to serialize stays OUT of _prevRoots, so the next walk retries it. Non-enabled
        /// kinds log once — the visible opt-out line, same contract as the coverage report.</summary>
        private static void EmitStructural(NetworkEngine engine, ref int packets, ref int bytes)
        {
            if (!_rootsSeeded) { SeedRoots(); return; } // first non-baseline walk after an old-format boundary
            List<string> removed = null;
            foreach (var k in _prevRoots)
                if (!_walkRoots.ContainsKey(k)) (removed = removed ?? new List<string>()).Add(k);
            if (removed != null)
                foreach (var k in removed)
                {
                    _prevRoots.Remove(k);
                    if (!StructuralEnabled(k))
                    {
                        if (_structuralSkipsLogged.Add("d:" + k))
                            Debug.Log("[Multiplayer][rail] structural: destroy of '" + k + "' not enabled — not mirrored");
                        continue;
                    }
                    SendStructural(engine, 2, k, null, ref packets, ref bytes);
                }
            foreach (var kv in _walkRoots)
            {
                if (_prevRoots.Contains(kv.Key)) continue;
                if (!StructuralEnabled(kv.Key))
                {
                    _prevRoots.Add(kv.Key); // remember it either way — the skip line must not repeat per walk
                    if (_structuralSkipsLogged.Add("c:" + kv.Key))
                        Debug.Log("[Multiplayer][rail] structural: create of '" + kv.Key + "' (" + kv.Value.GetType().Name + ") not enabled — not mirrored");
                    continue;
                }
                // Payload arm, in this order — the graph blob is the LAST resort, never the default:
                //   • a MonoBehaviour-bound ACTOR (GeoVehicle) ships its type name + the ComponentSetDef the
                //     game spawns it from, as a DefRef leaf (RailMeta.EncodeActorCreate). A native blob is
                //     forbidden here, not merely avoided: decoding one would re-CREATE the actor and every
                //     actor its members reach (law 3 "never replace a MonoBehaviour-bound instance",
                //     RailCheck L3/L31). Asked as "is this a UnityEngine.Object", so the rule holds for any
                //     future actor root without a second table to keep in sync.
                //   • a Descend field ships its type name + its create params as LEAVES
                //     (RailMeta.EncodeDescendCreate — refs, never a graph).
                //   • everything else (plain serializable classes: GeoCharacter, GeoPhoenixFacility) the
                //     native graph blob, which is exactly where law 6 licenses one.
                byte[] blob;
                switch (PayloadFor(kv.Key, kv.Value.GetType()))
                {
                    case CreatePayload.ActorFrame:   blob = RailMeta.EncodeActorCreate(kv.Value); break;
                    case CreatePayload.DescendFrame: blob = RailMeta.EncodeDescendCreate(kv.Value); break;
                    default: blob = Multiplayer.Rail.SerializerRoundtrip.SerializeGraph(new[] { kv.Value }, quiet: true); break;
                }
                if (blob == null || blob.Length == 0)
                {
                    Debug.LogError("[Multiplayer][rail] structural: payload for '" + kv.Key + "' (" +
                                   kv.Value.GetType().Name + ") failed — retrying next walk");
                    continue; // NOT added to _prevRoots → re-detected next cycle
                }
                SendStructural(engine, 1, kv.Key, blob, ref packets, ref bytes);
                _prevRoots.Add(kv.Key);
            }
        }

        private static void SendStructural(NetworkEngine engine, byte op, string rootKey, byte[] blob, ref int packets, ref int bytes)
        {
            try
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(MsgStructural);
                    uint seq = Seq.Next(SurfaceIds.GeoRail);
                    w.Write(seq);
                    TouchRoot(rootKey, seq); // a create/destroy makes the root non-quiescent too
                    w.Write(op);          // 1 = create, 2 = destroy
                    w.Write(rootKey);
                    w.Write(blob?.Length ?? 0);
                    if (blob != null) w.Write(blob);
                    var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoRail, SyncKind.StateDelta, ms.ToArray());
                    engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                    packets++;
                    bytes += (int)ms.Length;
                    Debug.Log("[Multiplayer][rail] structural " + (op == 1 ? "create" : "destroy") + " '" + rootKey +
                              "' sent (" + (blob?.Length ?? 0) + "B blob)");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] structural emit '" + rootKey + "' failed: " + ex.Message); }
        }

        // ─── Wire emit (chunked; each packet its own seq on one ordered stream) ───

        /// <summary>Split every entry whose value outgrew the envelope cap into cap-sized FRAGMENTS, in
        /// place of the batch's original entry and in its exact position — the client reassembles them
        /// before the apply (<c>GenericApplier.Reassemble</c>), so nothing downstream of the wire knows a
        /// value was ever split. PURE and internal, because it is the whole of RailCheck L40: the alternative
        /// it replaces was <c>continue</c>, i.e. the batch quietly shipping fewer entries than it was handed.
        ///
        /// Not a raised cap and not a special case: the cap is a TRANSPORT bound (u16 length inside a u16
        /// envelope) and every keyless <see cref="FieldClass.EntityList"/> rides one canonical blob by
        /// construction, so any of them can reach it as the campaign grows. Fragmenting at the envelope
        /// covers all of them at once, keyable or not.</summary>
        internal static List<Entry> FragmentForWire(List<Entry> changed)
        {
            int oversized = 0;
            foreach (var e in changed) if (e.Value != null && e.Value.Length > MaxValueBytes) oversized++;
            if (oversized == 0) return changed; // the normal batch: same list, no allocation
            var wire = new List<Entry>(changed.Count + oversized);
            int chunk = MaxValueBytes - RailMeta.FragmentHeaderBytes;
            foreach (var e in changed)
            {
                if (e.Value == null || e.Value.Length <= MaxValueBytes) { wire.Add(e); continue; }
                for (int off = 0; off < e.Value.Length; off += chunk)
                {
                    var slice = e;
                    slice.Value = RailMeta.EncodeFragment(e.Value, off, Math.Min(chunk, e.Value.Length - off));
                    wire.Add(slice);
                }
            }
            return wire;
        }

        /// <summary>The residual drop path, and the ONE place it may happen. LOUD EVERY TIME (not
        /// once-per-incident: a value the client never receives is wrong on every tick, so it must read as
        /// wrong on every tick) and it TOUCHES the root, so the law-7 CRC backstop treats the subtree as
        /// non-quiescent instead of hashing a truth the wire never carried. Reachable only if a single
        /// FRAGMENT ever exceeds the cap — which <see cref="FragmentForWire"/> makes arithmetically
        /// impossible — so this is the belt, kept because a silent drop is the bug class this rail
        /// exists to kill. RailCheck L40 drives it.</summary>
        internal static void NoteUndeliverable(Entry e, uint seq)
        {
            string kind = e.KindId < _kinds.Count ? _kinds[e.KindId].Name : "kind " + e.KindId;
            Debug.LogError("[Multiplayer][rail] DiffEngine: UNDELIVERABLE " + kind + ".(fieldIdx " + e.FieldIdx +
                           ") at " + e.Path + " — value " + (e.Value?.Length ?? 0) + "B exceeds the " + MaxValueBytes +
                           "B envelope cap even after fragmentation; the client's copy of this field is STALE");
            TouchRoot(e.Path, seq); // never quiescent while we are failing to ship it (CRC backstop input)
        }

        private static void Emit(NetworkEngine engine, List<Entry> changed, ref int packets, ref int bytes)
        {
            changed = FragmentForWire(changed);
            int i = 0;
            while (i < changed.Count)
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(MsgDelta);
                    uint seq = Seq.Next(SurfaceIds.GeoRail);
                    w.Write(seq);

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
                        if (e.Value.Length > MaxValueBytes) { NoteUndeliverable(e, seq); continue; }
                        w.Write(e.KindId);
                        w.Write(e.Path);
                        w.Write(e.FieldIdx);
                        w.Write(e.SubKey);
                        w.Write((ushort)e.Value.Length);
                        w.Write(e.Value);
                        TouchRoot(e.Path, seq); // quiescence input for the CRC backstop
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
                sb.AppendLine("roots: T (level clock) | F#<factionDefGuid> | S#<siteId> | U#<tacUnitId> | V#<vehicleId>@<ownerFactionDefGuid> | ES (event system) | MG (mission generator) | MK (marketplace) | M#<name> (registered mod-state roots)");
                sb.AppendLine("total snapshot fields: " + totalFields);
                // Root CENSUS: how many DISTINCT root keys the walk actually addressed, per kind. Instance
                // counts below say how many objects were visited; this says how many the rail can NAME —
                // the two disagreeing is the collision signature (27 GeoVehicle instances vs 10 "V#" keys
                // was faction-local VehicleIDs colliding, before the owner qualifier).
                var rootKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var k in _walkRoots.Keys)
                {
                    int h = k.IndexOf('#');
                    var kind = h < 0 ? k : k.Substring(0, h + 1);
                    rootKinds.TryGetValue(kind, out var n);
                    rootKinds[kind] = n + 1;
                }
                sb.AppendLine("distinct root keys: " + _walkRoots.Count + "  (" +
                              string.Join(" ", rootKinds.Select(kv => kv.Key + "=" + kv.Value)) + ")");
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
