using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewControllers.Modal;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11 presentation for the geoscape QUEUED-WINDOW family — the second window kind on the rail, after
    /// <see cref="EventPopup"/>'s 0xB6 event picker, and the one <see cref="GeoWindowCoverage"/> carried as
    /// its biggest declared Gap. The host ships ONE PRESENTATION PAYLOAD per raise (surface
    /// <see cref="SurfaceIds.GeoModalRaise"/> 0xB7) and the client rebuilds the REAL native window from it.
    ///
    /// NOT ONLY MODALS SINCE 2026-08-05. The surface carries a <see cref="StateKind"/> byte, so the SAME
    /// payload — seq, priority, data shape, refs — raises any DECLARED queued <c>GeoscapeViewState</c>, not
    /// just <c>UIStateGeoModal</c>. That is the whole generic arm: <c>UIStateAssetDeployment</c> ("where does
    /// this newly manufactured vehicle / recruited soldier go") was outside 0xB7 entirely because it is not a
    /// modal, and being outside meant it reached ONE screen and held that peer's window queue until that
    /// peer clicked (law 91). It now rides the seq stream, the park queue, the refusal contract, the coverage
    /// gate and the priority ordering that already existed — the only per-kind code is one
    /// <see cref="Describe"/> case, one <see cref="BuildData"/> case and one <c>new</c> in
    /// <see cref="RaiseMirrored"/>. A raise is host→all only; the ANSWER (which base) rides back as
    /// <see cref="WindowQueueSync"/>'s 0xB9 deploy op, because placing an asset is host-authoritative.
    ///
    /// WHY A PAYLOAD FAMILY AND NOT THE QUEUE REQUEST. <c>GeoscapeViewStateSwitchRequest</c> carries a live
    /// <c>IState</c> and nothing else, and the state behind it — <c>UIStateGeoModal</c> — is built from a
    /// <c>DialogCallback</c> CLOSURE over the raiser's locals plus an <c>object modalData</c> whose class
    /// differs per <c>ModalType</c> (UIStateGeoModal.cs:65, GeoscapeView.cs:848/:867). Neither is in the save
    /// graph, so law 2 addressing cannot reach them; each kind needs its own description, exactly as the
    /// 0xB6 raise needs its own.
    ///
    /// WHAT MAKES A MODAL MIRRORABLE — the ONE rule, and it is the game's own code that decides it, not this
    /// file: a modal mirrors when its host-side <c>DialogCallback</c> does NOTHING AUTHORITATIVE. Read
    /// <c>GeoscapeView.ModalResultCallback</c>:798 and the set falls out: a mission BRIEF resolves to
    /// <c>LaunchMission</c> / <c>mission.Cancel()</c>, an ability confirmation to <c>GeoAbility.Activate</c>,
    /// a soldier join to <c>reward.Apply</c> — those are host-authoritative decisions and their windows are
    /// declared, not shipped (<see cref="GeoWindowCoverage.DeclaredModals"/> holds all 43 <c>ModalType</c>
    /// members with a reviewed reason each, and RailCheck L49 fails the build for a new one). What is LEFT is
    /// the campaign-progress notification: research complete, shared-research diplomacy, base activated —
    /// windows whose only button is an acknowledgement, raised by a HOST-side faction event the client's own
    /// gated sim never fires.
    ///
    /// THE CLIENT'S COPY IS NON-AUTHORITATIVE BY CONSTRUCTION, and that is the whole safety argument:
    /// <see cref="RaiseMirrored"/> passes <c>dialogHandler: null</c>. Every button on a native modal funnels
    /// through <c>UIModal.Invoke</c> → <c>UIStateGeoModal.FinishDialog</c>:82 → <c>_dialogHandler?.Invoke</c>,
    /// and the Esc/close tail at ExitState:121 is guarded the same way — so with a null handler NO button on
    /// a mirrored modal can run game logic, whatever the prefab wires up. It is also never marked
    /// <c>Persistent</c>: a persistent modal is SAVE-RESTORED through
    /// <c>UIStateGeoModal.RestoreContext.RegenerateState</c>:36, which rebuilds it with the game's OWN
    /// <c>level.View.ModalResultCallback</c> closure — i.e. a reload would hand a client the authoritative
    /// buttons this file just took away. RailCheck L49 asserts both from IL.
    ///
    /// NO DISMISS MESSAGE, ON PURPOSE. A mirrored modal is informational and carries no shared resolution, so
    /// there is no race to arbitrate and no reason for one peer's OK to close the other's window: each peer
    /// dismisses its own copy at its own pace. (That is also the 0xB6 lesson "never hard-dismiss an open
    /// window when the peer can still act", applied by not creating the problem.) A future ACTIONABLE modal
    /// would need the missing half — an intent for its buttons and a host→all hide keyed on
    /// <c>GeoscapeView.ModalClosed</c>:793 — and that is exactly why the brief family is still declared.
    /// </summary>
    internal static class GeoModalMirror
    {
        internal static DurableCarrierLease BindQueuedCarrier(DurableInboxStore store,
            OccurrenceId occurrence, Action<TerminalReason> silentRemove) =>
            DurableCarrierLease.Bind(store, occurrence, DurableCarrierClass.ModQueued, silentRemove);
        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        private static readonly System.Reflection.FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");   // GeoscapeView.cs:138 (game typo)

        private static readonly ModalParkQueue Park = new ModalParkQueue();

        /// <summary>FULL session teardown only — the raise seq is a host monotonic stream and a client
        /// last-writer guard, so a mid-session reload must NOT reset it (rca-3 contract, same as
        /// <see cref="EventPopup.Reset"/>). The park queue DOES go: a parked raise names an entity in the
        /// graph that teardown just threw away.</summary>
        public static void Reset() { Seq.Reset(); Park.Clear(); }
        internal static void ClearDurableCarrierState() => Park.Clear();

        // ─── THE PAYLOAD (host→all, surface 0xB7) ──────────────────────────

        /// <summary>WHICH queued <c>GeoscapeViewState</c> the peer builds. The generic axis of this surface:
        /// the payload below describes the DATA, this says what to make with it. A kind is added by declaring
        /// its state <c>Mirrored</c> in <see cref="GeoWindowCoverage.Declared"/>, giving its data object a
        /// <see cref="Describe"/> case and adding its <c>new</c> to <see cref="RaiseMirrored"/> — never by
        /// minting a second raise surface, which is how 0xBA (cutscenes) and this one ended up as two
        /// mechanisms for one job.</summary>
        internal enum StateKind : byte
        {
            /// <summary><c>UIStateGeoModal</c> — 43 <c>ModalType</c>s wearing one state, so this kind and
            /// this kind alone reads <see cref="Raise.ModalType"/>.</summary>
            Modal = 0,
            /// <summary><c>UIStateAssetDeployment</c> — GeoscapeView.PrepareDeployAsset:1308, raised on the
            /// host by a manufacture completion (<c>ItemManufacturing.FinishManufactureItem</c>:498 →
            /// <c>OnManufacture</c>, reached only from <c>Manufacture.Update()</c> inside
            /// <c>LevelHourlyUpdateCrt</c>, which <see cref="ClientSimGate"/> blocks WHOLE on a client) or by
            /// a recruit with nowhere to go (<c>GeoPhoenixFaction.AddRecruit</c>:708). Host-only by
            /// construction, therefore a one-screen window until this arm existed.</summary>
            AssetDeployment = 1,
        }

        /// <summary>How <c>modalData</c> is described on the wire. The shape is derived from the RUNTIME type
        /// of the host's own object, never from a static ModalType→class table: the game's own mapping is a
        /// fallback chain (<c>GetMissionBriefModal</c>:1723 ends in "anything else ⇒ BehemothAttackBrief"), so
        /// a table keyed on the enum would go quietly wrong for a modded mission, while the object in hand
        /// cannot. Adding a shape is the extension point; an object no shape covers is
        /// <see cref="Unsupported"/> and says so out loud rather than shipping half a window.</summary>
        internal enum DataShape : byte
        {
            /// <summary>The raise carried no data at all (GeoPhoenixBaseOutcome — GeoscapeView.cs:1965 passes
            /// null). Nothing to resolve, so nothing can fail to resolve.</summary>
            None = 0,
            /// <summary><c>GeoResearchCompleteData</c>: Ref = the research's own faction, Keys[0] = its
            /// <c>ResearchID</c>, Num = <c>SwitchToResearchState</c>. The client re-fetches the LIVE element
            /// off its own mirrored research container — the renderer walks
            /// <c>UnlocksResearches</c>/<c>ManufactureRewards</c> off it
            /// (GeoReseatchCompleteDataBind.cs:97-125), which a synthesized element could not answer.
            /// NO LIVE PRODUCER since 2026-08-05: <c>ModalType.GeoResearchComplete</c> is declared LocalOnly
            /// (GeoWindowCoverage — the raise raced ahead of the 0xAC deltas and the window arrived twice), so
            /// <see cref="HostBroadcast"/> returns before <see cref="Describe"/> ever sees one. Kept, not
            /// deleted: the wire value stays reserved, and RailCheck L49's shape-derivation and refusal arms
            /// execute this shape by name.</summary>
            ResearchComplete = 1,
            /// <summary><c>DiplomacyResearchRewardData</c>: Ref = <c>Faction</c>, Keys = every
            /// <c>ResearchID</c> in <c>Researches</c>, Num = <c>DiplomacyShareLevel</c>.</summary>
            DiplomacyReward = 2,
            /// <summary>THE GENERIC ARM, and the reason this file stopped growing a case per window: the
            /// modalData IS an entity the rail already names, so there is nothing to describe — Ref = the
            /// rail path (<see cref="EntityRefOf"/>), Keys[0] = the object's class, and the peer hands the
            /// prefab whatever ITS OWN graph resolves that path to. A mission brief's live
            /// <c>GeoMission</c> and a <c>FactionSoldierJoin</c>'s <c>GeoCharacter</c> both ride it with no
            /// type-specific code on either side; the next entity-shaped modal rides it with none
            /// either.</summary>
            EntityRef = 3,
            /// <summary><c>GeoDeployAssetFactionCharacterBind</c> — the asset-deployment prompt's whole data
            /// object (GeoDeployAssetFactionCharacterBind.cs). NOT an <see cref="EntityRef"/>: its entity is
            /// OPTIONAL (a manufactured aircraft is named by a <c>ComponentSetDef</c> and has no
            /// <c>GeoCharacter</c> at all — GeoscapeView.cs:1312 passes character null) and it carries two
            /// defs and two flags beside it. Ref = the asset's root ref or "", Keys = {aircraftDefGuid,
            /// relatedItemDefGuid} with "" for absent, Num = bit 1 Manufactured | bit 2 NotEnoughSpace. The
            /// <c>Faction</c> field never rides: the game's own restore path stamps the LOCAL faction
            /// (UIStateAssetDeployment.RestoreContext:35) and so does the rebuild here.</summary>
            AssetDeploy = 4,
            /// <summary>The host holds an object this file has no description for. Never sent — the host
            /// refuses the raise and logs it, because a modal whose data cannot be rebuilt renders as an empty
            /// prefab full of the designers' baked placeholder text (the 0xB6 half-built-window lesson).</summary>
            Unsupported = 255,
        }

        /// <summary>One modal raise, as the wire carries it. Refs are ordinary rail root refs (law 2 —
        /// <see cref="IdentityResolver.RootRef"/>/<see cref="IdentityResolver.Resolve"/>, the same addressing
        /// the value rail uses); Keys are the game's own stable string ids for what hangs off that root.
        /// No TEXT rides here and that is deliberate: every mirrored modal's renderer paints from the data
        /// object alone (verified: GeoReseatchCompleteDataBind:97 and DiplomacyResearchRewardDataBind:89
        /// touch no GeoLevelController), so the client's OWN defs render in the CLIENT's locale and no
        /// host-resolved string is ever stamped onto shared def state — the failure
        /// <see cref="EventPopup.WithWireTexts"/> exists to work around does not arise here at all.</summary>
        internal struct Raise
        {
            /// <summary>Which view state this becomes on the peer. Default 0 = <see cref="StateKind.Modal"/>,
            /// so every existing raiser keeps its meaning without saying so.</summary>
            public StateKind Kind;
            /// <summary>The <c>ModalType</c> as its integer value — mod parity (law 10) makes the enum
            /// identical on every peer, and shipping the int keeps a value the local build does not know
            /// decodable and reportable instead of an exception.</summary>
            public int ModalType;
            public DataShape Shape;
            public string Ref;
            public string[] Keys;
            public int Num;
            /// <summary>The host's OWN queue priority, passed verbatim from the native call
            /// (GeoscapeView.cs:848/:867 hand it to <c>GeoscapeViewStateSwitchRequest</c>). Windows are shown
            /// one at a time out of a PRIORITY-ORDERED queue — <c>QueryStateSwitch</c>:77-82 inserts before
            /// the first LOWER-priority entry — and the raisers really do differ (research complete 99,
            /// diplomacy 100, base activated 0), so mirroring at a fixed 0 would put two peers on different
            /// windows the moment two are pending. Same reason <see cref="EventPopup.Raise.Priority"/>
            /// exists.</summary>
            public int Priority;
            public string DurableTrigger;
            public string DurableSubject;
        }

        /// <summary>[seq:u32][kind:u8][modalType:i32][shape:u8][ref][n:u16][key × n][num:i32][priority:i32].
        /// Pure both ways so RailCheck L49/L106/L109 can round-trip it headless — a wire that drops a field
        /// silently is a window rendered against the wrong faction or shown in the wrong order, and neither
        /// shows up in a log.</summary>
        internal static byte[] Encode(uint seq, Raise p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write((byte)p.Kind);
                w.Write(p.ModalType);
                w.Write((byte)p.Shape);
                w.Write(p.Ref ?? "");
                var keys = p.Keys ?? new string[0];
                w.Write((ushort)keys.Length);
                foreach (var k in keys) w.Write(k ?? "");
                w.Write(p.Num);
                w.Write(p.Priority);
                w.Write(p.DurableTrigger ?? "");
                w.Write(p.DurableSubject ?? "");
                return ms.ToArray();
            }
        }

        internal static Raise Decode(byte[] payload, out uint seq)
        {
            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                seq = r.ReadUInt32();
                var p = new Raise
                {
                    Kind = (StateKind)r.ReadByte(),
                    ModalType = r.ReadInt32(),
                    Shape = (DataShape)r.ReadByte(),
                    Ref = WireString.ReadKey(r),
                };
                var keys = new string[r.ReadUInt16()];
                for (int i = 0; i < keys.Length; i++) keys[i] = WireString.ReadKey(r);
                p.Keys = keys;
                p.Num = r.ReadInt32();
                p.Priority = r.ReadInt32();
                p.DurableTrigger = WireString.ReadKey(r);
                p.DurableSubject = WireString.ReadKey(r);
                return p;
            }
        }

        // ─── HOST: describe the modal at the one place it is opened ────────

        /// <summary>Describe the host's live <c>modalData</c> for the wire. Returns
        /// <see cref="DataShape.Unsupported"/> for anything no shape covers — the caller then declines the
        /// raise loudly instead of shipping a modal the client would render empty.</summary>
        internal static Raise Describe(object modalData)
        {
            switch (modalData)
            {
                case null:
                    return new Raise { Shape = DataShape.None, Ref = "", Keys = new string[0] };
                case GeoResearchCompleteData d:
                    var el = d.ResearchElement;
                    return new Raise
                    {
                        Shape = DataShape.ResearchComplete,
                        Ref = IdentityResolver.RootRef(el?.Faction) ?? "",
                        Keys = new[] { el?.ResearchID ?? "" },
                        Num = d.SwitchToResearchState ? 1 : 0,
                    };
                case DiplomacyResearchRewardData d:
                    return new Raise
                    {
                        Shape = DataShape.DiplomacyReward,
                        Ref = IdentityResolver.RootRef(d.Faction) ?? "",
                        Keys = (d.Researches ?? Enumerable.Empty<ResearchElement>())
                               .Select(r => r?.ResearchID ?? "").ToArray(),
                        Num = d.DiplomacyShareLevel,
                    };
                case GeoDeployAssetFactionCharacterBind b:
                    // No "unnameable asset" arm here on purpose: Asset is a GeoCharacter and RootRef always
                    // names one (IdentityResolver.cs:145). The real hazard — a freshly minted character the
                    // level has not registered — is caught by HostBroadcast resolving the name BACK on the
                    // host's own graph, which is where every shape's naming is checked.
                    return new Raise
                    {
                        Shape = DataShape.AssetDeploy,
                        Ref = IdentityResolver.RootRef(b.Asset) ?? "",
                        Keys = new[] { b.Aircraft?.Guid ?? "", b.RelatedItemDef?.Guid ?? "" },
                        Num = (b.Manufactured ? 1 : 0) | (b.NotEnoughSpace ? 2 : 0),
                    };
                default:
                    var named = EntityRefOf(modalData);
                    return named == null
                        ? new Raise { Shape = DataShape.Unsupported, Ref = "", Keys = new string[0] }
                        // The CLASS rides along because a path cannot carry it and the prefab's data-bind
                        // casts on it: it is the one thing the peer must check before handing the object over.
                        : new Raise
                        {
                            Shape = DataShape.EntityRef,
                            Ref = named,
                            Keys = new[] { modalData.GetType().FullName },
                        };
            }
        }

        /// <summary>The rail path that NAMES a modal's data object, or null when the rail cannot name it at
        /// all. Two rungs, both of them the rail's OWN law-2 grammar and neither of them about windows:
        ///   1. a ROOT entity names itself — <see cref="IdentityResolver.RootRef"/> covers
        ///      GeoSite/GeoVehicle/GeoCharacter/GeoFaction/GeoHavenZone, which is the whole of what a
        ///      <c>FactionSoldierJoin</c> needs (its modalData is the offered <c>GeoCharacter</c>, and
        ///      <c>CreateCharacterFromDescriptor</c>:1597 has already put it in <c>_tacUnits</c> under a
        ///      minted id, so it is a root the walk structurally creates on the peer).
        ///   2. a SUB-entity is named by the PATH to the slot its owner holds it in — the same shape
        ///      <c>IdentityResolver.HavenZoneRef</c> uses, and for a <c>GeoMission</c> that slot is the
        ///      Descend field the VALUE rail already ships it under: <c>S#&lt;id&gt;.SerializationData
        ///      .ActiveMission</c> (GenericApplier.cs:251, docs/rail-baseline.txt GeoSite twin table
        ///      "+ Descend ActiveMission"). So a mission brief resolves to the peer's own structurally
        ///      mirrored mission and never to a wire reference the host may already have cancelled — the
        ///      same rule <see cref="MissionSync"/> follows for the launch intent.
        /// A name that cannot be resolved BACK to its object is not a name; <see cref="HostBroadcast"/>
        /// checks that on the host's own graph rather than trusting either rung.</summary>
        internal static string EntityRefOf(object modalData)
        {
            var root = IdentityResolver.RootRef(modalData);
            if (root != null) return root;
            if (modalData is PhoenixPoint.Geoscape.Entities.GeoMission m)
            {
                var owner = IdentityResolver.RootRef(m.Site);
                return owner == null ? null : owner + MissionSlotPath;
            }
            return null;
        }

        /// <summary>The owner-slot suffix of rung 2 above, as one constant: the rail emits this exact path
        /// for the mission's structural create, so a peer resolving it walks the SAME member.</summary>
        internal const string MissionSlotPath = ".SerializationData.ActiveMission";

        /// <summary>PURE: does this payload name a rail ENTITY the peer must already hold before the window
        /// can be built? The park queue and the host's own name-check both ask THIS, so a new shape opts into
        /// both by answering here — <see cref="DataShape.AssetDeploy"/>'s ref is OPTIONAL (an aircraft has no
        /// GeoCharacter), which is why the question is "is there a name" and not "is the shape EntityRef".</summary>
        internal static bool NamesEntity(Raise p) =>
            !string.IsNullOrEmpty(p.Ref) &&
            (p.Shape == DataShape.EntityRef || p.Shape == DataShape.AssetDeploy);

        /// <summary>The object a payload's <c>Ref</c> NAMES: the data object itself for
        /// <see cref="DataShape.EntityRef"/>, the asset inside the bind for
        /// <see cref="DataShape.AssetDeploy"/>. <see cref="HostBroadcast"/> resolves the derived name back to
        /// exactly this on the HOST's graph before anything ships.</summary>
        private static object NamedObject(object data) =>
            data is GeoDeployAssetFactionCharacterBind b ? b.Asset : data;

        /// <summary>The view-state type a non-modal kind builds — the type <see cref="GeoWindowCoverage"/>
        /// declares it under, so one table decides "does the other peer get this window" for modals and
        /// non-modals alike.</summary>
        private static Type StateTypeOf(StateKind kind) =>
            kind == StateKind.AssetDeployment ? typeof(UIStateAssetDeployment) : typeof(UIStateGeoModal);

        /// <summary>How a window's kind reads in a log line: the ModalType for the modal family (43 windows
        /// wear one state, so the state's name would say nothing), the StateKind otherwise.</summary>
        internal static string NameOf(StateKind kind, ModalType modalType) =>
            kind == StateKind.Modal ? modalType.ToString() : kind.ToString();

        /// <summary>THE NON-MODAL ARM'S HOST ENTRY, called from the coverage gate — i.e. from
        /// <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>, the ONE queue every pushed window passes through.
        /// A state that got there really was queued by the game (the <c>forceOnTop</c>/<c>replaceTop</c>
        /// local-navigation branches never reach it) and carries its own priority and its own live data, so
        /// there is nothing per-raiser to hook: adding a kind is one <c>case</c> here. Modals keep their own
        /// two openers because <see cref="GeoWindowCoverage.AnnounceModal"/> must see the un-queued ones too.</summary>
        internal static void HostBroadcastQueued(GeoscapeViewStateSwitchRequest request)
        {
            switch (request?.State)
            {
                case UIStateAssetDeployment s:
                    HostBroadcast(StateKind.AssetDeployment, ModalType.None, s.DeployBind, request.Priority);
                    break;
            }
        }

        /// <summary>Host-side broadcast of ONE window, called from the postfixes on the two native modal
        /// openers and from <see cref="HostBroadcastQueued"/>. Never throws into game code: a raise this
        /// fails on is a window the client does not get, and it says so.</summary>
        internal static void HostBroadcast(StateKind kind, ModalType modalType, object modalData, int priority)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (SyncApplyScope.Active) return;   // law 8: an apply that reaches the view never re-broadcasts
            var rule = kind == StateKind.Modal
                ? GeoWindowCoverage.RuleForModal(modalType)
                : GeoWindowCoverage.RuleFor(StateTypeOf(kind));
            if (rule == null || rule.Sync != WindowSync.Mirrored) return;  // declared: the gate already announced it
            string name = NameOf(kind, modalType);
            try
            {
                var p = Describe(modalData);
                p.Kind = kind;
                p.ModalType = (int)modalType;
                p.Priority = priority;
                var durable = DurableWindowRegistry.LastCapturedPriorityOccurrence;
                if (durable.HasValue && durable.Value.EventId == "Modal:" + modalType)
                {
                    p.DurableTrigger = durable.Value.TriggerId;
                    p.DurableSubject = durable.Value.SubjectIds.FirstOrDefault() ?? "";
                }
                DurableWindowRegistry.LastCapturedPriorityOccurrence = null;
                if (p.Shape == DataShape.Unsupported)
                {
                    Debug.LogError("[MP][modals] '" + name + "' is DECLARED Mirrored but its data is a " +
                                   (modalData == null ? "null" : modalData.GetType().FullName) + ", which " +
                                   "GeoModalMirror.Describe has no shape for — the client gets NO window. Add the " +
                                   "shape, or move the declaration to LocalOnly/Gap with the reason");
                    return;
                }
                // AssetDeploy is exempt: its entity is optional by construction (a manufactured aircraft is
                // named by a def, not by a GeoCharacter), and Describe already refused the case that matters
                // — an asset that exists and cannot be named.
                if (p.Shape != DataShape.None && p.Shape != DataShape.AssetDeploy && string.IsNullOrEmpty(p.Ref))
                {
                    Debug.LogError("[MP][modals] '" + name + "' NOT mirrored — its data has no rail root ref on " +
                                   "the host (shape=" + p.Shape + "), so the client would have nothing to resolve it " +
                                   "against and would render the prefab's placeholder text");
                    return;
                }
                // A NAME MUST NAME ITS OBJECT — checked on the HOST's own graph, where the object certainly
                // is. A derivation can produce a syntactically perfect key that means nothing: an entity the
                // level has not registered yet, or an id that means "nobody" (a fresh GeoTacUnitId is 0, and
                // "U#0" resolves to no unit — or to somebody else's). Refusing here is the difference between
                // a window that does not appear and a window built over the wrong soldier.
                if (NamesEntity(p) &&
                    !ReferenceEquals(IdentityResolver.Resolve(GeoLevel(), p.Ref, null), NamedObject(modalData)))
                {
                    Debug.LogError("[MP][modals] '" + name + "' NOT mirrored — the rail named its " +
                                   modalData.GetType().Name + " '" + p.Ref + "', but that path does not " +
                                   "resolve back to that very object on the HOST's own graph, so it names " +
                                   "nothing (or something else) on a peer. The entity is not on the rail yet");
                    return;
                }
                uint seq = Seq.Next(SurfaceIds.GeoModalRaise);
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoModalRaise, SyncKind.StateDelta, Encode(seq, p));
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][modals] HOST raised '" + name + "' seq=" + seq + " kind=" + p.Kind +
                          " shape=" + p.Shape + " ref=" + (p.Ref == "" ? "none" : p.Ref) +
                          " keys=" + p.Keys.Length + " num=" + p.Num + " priority=" + p.Priority);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][modals] HOST raise broadcast FAILED for '" + name + "' — no peer will see " +
                               "this window: " + ex);
            }
        }

        // ─── CLIENT: rebuild the data and push the NATIVE window ───────────

        /// <summary>THE validity decision, PURE so RailCheck L49 can falsify it headless: may this payload
        /// become a window on this peer? Null = yes. A ref or a key the host SHIPPED but this peer cannot
        /// resolve is the killer: the modal prefab's data-bind casts <c>modal.Data</c> and dereferences it
        /// unguarded (GeoReseatchCompleteDataBind.cs:97 reads <c>ResearchElement.GetLocalizedName()</c>
        /// straight off), the throw lands INSIDE <c>UIStateGeoModal.EnterState</c>, and what stays on screen
        /// is a half-built prefab over the designers' baked placeholder text — the exact 0xB6 failure mode,
        /// with the same "logged as a success" property. <see cref="DataShape.None"/> never refuses: a
        /// data-less modal legitimately has nothing to resolve and the host rendered it that way too.</summary>
        internal static string DataRefusal(DataShape shape, bool rootResolved, int keysWanted, int keysResolved)
        {
            if (shape == DataShape.None) return null;
            if (shape == DataShape.EntityRef)
            {
                // Same three questions as below, asked of one object instead of a root plus its ids —
                // spelled out rather than folded in, because the REASONS differ and a refusal nobody can
                // read is the silent swallow with extra steps.
                if (!rootResolved)
                    return "the host named an entity this peer's own graph cannot resolve — either the rail " +
                           "has not created it here yet or it never will, and a modal whose data is null has " +
                           "its cast dereferenced unguarded inside EnterState";
                if (keysWanted <= 0)
                    return "the host shipped no class for the entity it named, so nothing here can tell " +
                           "whether this peer resolved the same KIND of object — and the prefab's data-bind " +
                           "casts before it reads";
                if (keysResolved != keysWanted)
                    return "the shipped path resolves to a DIFFERENT class on this peer than the host " +
                           "described (" + keysResolved + " of " + keysWanted + " class keys matched; nothing " +
                           "here tested the peers' def sets, so this is not a parity finding) — the data-bind's " +
                           "cast throws inside EnterState, which is the same half-built window by another route";
                return null;
            }
            if (shape == DataShape.AssetDeploy)
            {
                // rootResolved is VACUOUSLY true when the host named no asset (the manufactured-aircraft
                // case), so the only question left is whether everything it DID name exists here.
                if (!rootResolved)
                    return "the host named a newly created asset this peer's own graph cannot resolve — the " +
                           "deploy screen reads bind.Asset.TemplateDef and bind.Asset.Progression unguarded " +
                           "(UIModuleGeoAssetDeployment.ShowDeployDialog:105/:122), so a null there throws " +
                           "inside EnterState and leaves a window nobody can answer";
                if (keysResolved != keysWanted)
                    return "only " + keysResolved + " of " + keysWanted + " defs the host named resolve on this " +
                           "peer (mod parity: law 10 should have blocked the join) — the prompt would offer a " +
                           "different asset than the host is actually deploying";
                return null;
            }
            if (!rootResolved)
                return "the host raised it for a faction/root this peer cannot resolve — the rebuilt modal data " +
                       "would be null and the prefab's data-bind casts and dereferences it unguarded inside " +
                       "EnterState, leaving a half-built window over placeholder text";
            if (keysWanted <= 0)
                return "the host shipped no ids for a " + shape + " payload, which needs at least one — a modal " +
                       "rendered off an empty element list is an empty prefab";
            if (keysResolved != keysWanted)
                return "only " + keysResolved + " of " + keysWanted + " shipped ids resolve on this peer (mod " +
                       "parity: law 10 should have blocked the join) — a modal missing part of what the host " +
                       "showed is a different window, not the same one";
            return null;
        }

        /// <summary>The <see cref="DataShape.EntityRef"/> arm's peer-side half, PURE so RailCheck L106 can
        /// execute the whole describe→encode→decode→rebuild round trip headless.
        /// <paramref name="resolved"/> is what THIS peer's graph made of the shipped path (see
        /// <see cref="BuildData"/>); the object is returned as-is, because for this shape the modalData IS
        /// the entity. The CLASS check is not ceremony: the path grammar is type-blind, so a ref that lands
        /// on a different kind of object here than it named on the host is a cast that throws inside
        /// <c>UIStateGeoModal.EnterState</c> — the half-built-prefab failure, and it would be logged as a
        /// successful raise.</summary>
        internal static object EntityData(Raise p, object resolved, out string refusal)
        {
            var want = p.Keys != null && p.Keys.Length > 0 ? p.Keys[0] : null;
            bool sameClass = resolved != null && !string.IsNullOrEmpty(want) &&
                             resolved.GetType().FullName == want;
            refusal = DataRefusal(DataShape.EntityRef, resolved != null,
                                  string.IsNullOrEmpty(want) ? 0 : 1, sameClass ? 1 : 0);
            return refusal == null ? resolved : null;
        }

        /// <summary>Returns true when the surface was consumed. Client-only: the host never applies its own
        /// raise (it already showed the window natively).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoModalRaise) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                var p = Decode(payload, out uint seq);
                // A re-delivered raise is a SECOND window, not a stale value — the strictly-greater guard is
                // what makes this surface idempotent (law 7), and it is marked only after the window is up.
                if (!Seq.ShouldApply(SurfaceIds.GeoModalRaise, seq)) return true;
                // STRICT FIFO, and the queue is checked FIRST: once one raise is waiting for its entity,
                // every later raise waits behind it. The game's own queue is priority-ordered but keeps
                // ARRIVAL order between equal priorities (QueryStateSwitch:77-82 inserts before the first
                // LOWER entry), so releasing a parked raise after a later one that resolved immediately
                // would put this peer's windows in a sequence the host never showed.
                if (Park.Count > 0 || NeedsPark(p))
                {
                    var evicted = Park.Park(seq, p);
                    if (evicted != null) Debug.LogError("[MP][modals] " + evicted);
                    Debug.Log("[MP][modals] raise of '" + NameOf(p.Kind, (ModalType)p.ModalType) + "' seq=" + seq +
                              " PARKED — waiting for " +
                              (SwitchQuery() == null ? "a live GeoscapeView on this peer"
                                                     : "'" + p.Ref + "' to reach this peer's graph") +
                              " (queue=" + Park.Count + ")");
                    return true;
                }
                if (RaiseMirrored(p, seq)) Seq.Mark(SurfaceIds.GeoModalRaise, seq);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] GeoModalMirror inbound failed: " + ex); }
            return true;
        }

        internal static bool RestoreSuspended(InboxWindowCheckpoint checkpoint, Func<bool> exactRebuild)
        {
            if (checkpoint == null || exactRebuild == null) return false;
            return exactRebuild();
        }

        internal static InboxWindowCheckpoint CaptureCheckpoint(UIStateGeoModal state)
        {
            if (state == null) return null;
            var query = SwitchQuery(); int priority = WindowOrder.CurrentRequest(query)?.Priority ?? 0;
            string identity = state.ModalData is GeoMission mission
                ? DurableWindowRegistry.StableMissionSubject(mission) : "";
            return new InboxWindowCheckpoint("modal", ((int)state.ModalType).ToString(), "open", "", "", "",
                priority, identity);
        }

        internal static GeoscapeViewStateSwitchRequest ReconstructCarrier(OccurrenceId occurrence,
            InboxWindowCheckpoint checkpoint)
        {
            int raw;
            if (checkpoint == null || checkpoint.ContentPhase != "modal" ||
                !int.TryParse(checkpoint.Selection, out raw) || !Enum.IsDefined(typeof(ModalType), raw)) return null;
            var geo = GeoLevel(); if (geo == null) return null;
            var mission = geo.Map?.ActiveSites?.Select(x => x.ActiveMission).FirstOrDefault(x => x != null &&
                occurrence.SubjectIds.Contains(DurableWindowRegistry.StableMissionSubject(x)));
            if (mission == null) return null;
            var modal = (ModalType)raw;
            DialogCallback handler = result => geo.View.ModalResultCallback(modal, result, mission);
            if (!string.IsNullOrEmpty(checkpoint.NativeDataIdentity) &&
                checkpoint.NativeDataIdentity != DurableWindowRegistry.StableMissionSubject(mission)) return null;
            var request = new GeoscapeViewStateSwitchRequest(new UIStateGeoModal(modal, handler, mission), checkpoint.NativePriority)
                { PauseGame = true };
            WindowOrder.BindDurable(request, occurrence); return request;
        }

        /// <summary>THE DEFERRAL PREDICATE, and the whole reason <see cref="ModalParkQueue"/> exists: an
        /// <see cref="DataShape.EntityRef"/> raise routinely arrives BEFORE the entity it names. The host
        /// broadcasts inside the <c>OpenModal</c> postfix — SYNCHRONOUSLY, in the same call stack that just
        /// minted the object (HavenMissionUtil.cs:94 creates the GeoCharacter, :59 opens the modal) — while
        /// the structural create for that object is a VALUE-RAIL packet the diff cycle only emits on a later
        /// frame (<c>DiffEngine.HostTick</c>, TickInterval 0.1 s). So the inversion is not a race the
        /// transport could lose: 0xB7 is ALWAYS on the wire first, and refusing it there is a reward window
        /// the player never sees (law 91). Generic on purpose — every present and future EntityRef window
        /// rides this, there is no soldier-join arm anywhere.
        /// SECOND EARLY CONDITION, same queue, since 2026-08-08: this peer has no live GeoscapeView at all
        /// (tactical mission, or the mid-load on the way back from one). That used to be a hard DROP in
        /// <see cref="RaiseMirrored"/> and it lost real player decisions — two 'AssetDeployment' prompts in one
        /// session, gone with no way to get them back, because the raise landed while the returning peer's
        /// geoscape was still loading. It is the SAME situation the queue already handles — "not yet" — and it
        /// self-releases on the SAME event, an applied rail batch found with the view up: the view arrives by
        /// itself when the level finishes loading, so no player has to act and nobody waits on anybody
        /// (law 91 — never a quorum). Only kinds <see cref="NoViewRefusal"/> clears start that wait; the rest
        /// keep the loud drop, with that same function's reason on it.</summary>
        private static bool NeedsPark(Raise p)
        {
            if (!string.IsNullOrEmpty(p.DurableTrigger))
            {
                var store = DurableInboxSession.ActiveStore;
                if (!DurableWindowRegistry.MatchPriorityOccurrence(store, "Modal:" + (ModalType)p.ModalType,
                    p.DurableTrigger, p.DurableSubject).HasValue)
                    return true;
            }
            if (SwitchQuery() == null) return NoViewRefusal(p) == null;   // no screen yet — wait for one, if it may still be shown
            if (!NamesEntity(p)) return false;   // only a NAME can be early; None/research resolve or never will
            var geo = GeoLevel();
            return geo != null && IdentityResolver.Resolve(geo, p.Ref, null) == null;
        }

        /// <summary>THE NO-VIEW VERDICT, PURE so RailCheck L337 executes it headless, and written as a REASON
        /// STRING for the same discipline <see cref="DataRefusal"/> follows: null = "wait, this window may
        /// still be shown"; non-null = "it will never be shown, and here is the sentence that says so".
        /// <see cref="NeedsPark"/> and <see cref="RaiseMirrored"/> both read THIS, so the thing that decides
        /// to wait and the thing that decides to drop can never disagree, and neither exit is ever silent.
        ///
        /// The split is by what the window IS, not by its data shape:
        ///   • <see cref="StateKind.AssetDeployment"/> — REPLAYABLE (null). It is a standing PROMPT, not a
        ///     notice: the asset is manufactured/recruited and sits UNPLACED until somebody answers, the
        ///     answer rides back as <see cref="WindowQueueSync"/>'s 0xB9 deploy op, and all three native
        ///     raise sites are an ACQUISITION completing (GeoPhoenixFaction.cs:708, VehicleItemDef.cs:47,
        ///     GroundVehicleItemDef.cs:48 → GeoscapeView.PrepareDeployAsset:1308). Nothing re-asks. Dropping
        ///     it destroys a player decision, which is exactly what happened twice in one session.
        ///   • <see cref="StateKind.Modal"/> — NOT replayable. Every ModalType declared Mirrored is an
        ///     acknowledgement-only PRESENTATION of a moment (base activated, research shared, a mission
        ///     brief, a soldier-join offer), and the client's copy is non-authoritative BY CONSTRUCTION —
        ///     <c>dialogHandler: null</c>, so no button on it runs game logic and no decision rides on it.
        ///     Replaying one after a mission shows a brief for a mission the host may already have launched
        ///     or cancelled: a WRONG window, which is worse than a missing one. Same verdict the repaint side
        ///     already reached for one-shot presentations ("a one-shot presentation has no repaint, and
        ///     Exit+Enter would replay it").</summary>
        internal static string NoViewRefusal(Raise p)
        {
            if (p.Kind == StateKind.AssetDeployment) return null;
            return "raise of '" + NameOf(p.Kind, (ModalType)p.ModalType) + "' DROPPED — this peer has no live " +
                   "GeoscapeView to show it in, and this window is a one-shot PRESENTATION that is deliberately " +
                   "NOT replayed late: its copy here carries no player decision (dialogHandler is null, so no " +
                   "button on it runs anything), and showing it once this peer is back would describe a moment " +
                   "that has passed. A window that DOES carry a decision parks and waits for the view instead";
        }

        /// <summary>Called once per applied GeoRail batch (<c>GenericApplier.HandleInbound</c>) — the one
        /// moment this peer's graph can have GAINED the entity a parked raise names (law 3: only a
        /// structural apply creates identity, and it rides that same surface). The pump is ALSO what
        /// advances the bounded expiry, so no parked raise can sit forever without saying so.</summary>
        public static void PumpParked()
        {
            // No live view = nothing to raise INTO, and the wait does not COUNT: the expiry below is there to
            // catch an entity that never arrives, not to punish a peer for being in a tactical mission. The
            // bound on THIS wait is the other kind the class doc names — the queue cap, which evicts loudly —
            // plus Reset() at session teardown; and the release event (the view) arrives on its own.
            if (Park.Count == 0 || SwitchQuery() == null) return;
            Park.Pump(
                p => !NeedsPark(p),
                (p, s) => { if (RaiseMirrored(p, s)) Seq.Mark(SurfaceIds.GeoModalRaise, s); },
                (p, s, waited) => Debug.LogError(
                    "[MP][modals] parked raise of '" + NameOf(p.Kind, (ModalType)p.ModalType) + "' seq=" + s + " EXPIRED after " +
                    waited + " rail batches — '" + p.Ref + "' never reached this peer's graph, so the window is " +
                    "DROPPED and this player never sees it. The entity is not on the rail (no structural create " +
                    "kind for it), or its create failed earlier and said so"));
        }

        /// <summary>Rebuild the host's modal here and push the NATIVE view state through the game's own switch
        /// query — the same call <c>GeoscapeView.OpenModal</c>:876 makes, with the host's own priority so this
        /// peer's queue orders it exactly as the host's did. Returns false when nothing was raised (and always
        /// says why).</summary>
        private static bool RaiseMirrored(Raise p, uint seq)
        {
            var modalType = (ModalType)p.ModalType;
            string name = NameOf(p.Kind, modalType);
            var geo = GeoLevel();
            var q = SwitchQuery();
            if (q == null)
            {
                // Reachable only by a kind NoViewRefusal() refuses — a replayable one was parked by NeedsPark
                // and the pump only runs with the view up, so it never gets here. A DELIBERATE drop with a
                // stated reason, not the old "no view, tough luck". The ?? arm is not decoration: if the two
                // ever disagreed, the window would vanish with no line at all, and that is this repo's
                // dominant bug class.
                Debug.LogWarning("[MP][modals] " + (NoViewRefusal(p) ??
                    "raise of '" + name + "' DROPPED although it is replayable — NeedsPark parked nothing and " +
                    "the view is gone here, so the two disagreed or the view died between them"));
                return false;
            }
            var rule = p.Kind == StateKind.Modal
                ? GeoWindowCoverage.RuleForModal(modalType)
                : GeoWindowCoverage.RuleFor(StateTypeOf(p.Kind));
            if (rule == null || rule.Sync != WindowSync.Mirrored)
            {
                // Both peers run the same DLL, so this means the SENDER's table and ours disagree — a mod/
                // build mismatch law 10 should have caught. Refuse rather than open a window nobody reviewed.
                Debug.LogError("[MP][modals] raise of '" + name + "' REFUSED — this peer's " +
                               "GeoWindowCoverage does not declare it Mirrored (" +
                               (rule == null ? "undeclared" : rule.Sync.ToString()) + "), so the peers are not " +
                               "running the same coverage table");
                return false;
            }

            var data = BuildData(geo, p, out string refusal);
            if (refusal != null)
            {
                Debug.LogError("[MP][modals] raise of '" + name + "' REFUSED — " + refusal);
                return false;
            }

            GeoscapeViewState state;
            if (p.Kind == StateKind.AssetDeployment)
            {
                // The prompt's data IS its constructor argument, so a null bind is not a blank window but an
                // NRE on the first line of EnterState. Refused rather than queued: a queued state that throws
                // holds this peer's queue slot forever, which is the wedge this arm exists to end.
                var bind = data as GeoDeployAssetFactionCharacterBind;
                if (bind == null)
                {
                    Debug.LogError("[MP][modals] raise of '" + name + "' REFUSED — shape " + p.Shape + " rebuilt " +
                                   "no GeoDeployAssetFactionCharacterBind, and UIStateAssetDeployment reads it " +
                                   "unguarded in EnterState:61");
                    return false;
                }
                // NON-AUTHORITATIVE the same way a mirrored modal is, by a different mechanism: this copy's
                // only button funnels into DeployAtSite:69, which WindowQueueSync's capture blocks on a client
                // and converts into the 0xB9 deploy intent — the host runs the one DeployAsset there is.
                state = new UIStateAssetDeployment(bind);
            }
            else
            {
                // dialogHandler: null is THE safety property of this family (see the class doc) — with it, every
                // button funnels into UIStateGeoModal.FinishDialog:82 -> `_dialogHandler?.Invoke` and does nothing
                // but close this peer's own copy. Persistent stays FALSE (never set): a persistent modal is
                // save-restored with the game's own authoritative ModalResultCallback closure (RestoreContext:36).
                //
                // EXCEPT FOR THE PER-PEER ANSWER CLASS (GeoWindowCoverage.IsPerPeerAnswer), where a null handler
                // is the DEFECT: a mission brief's Confirm has to reach this peer's own ToDeploymentState, and
                // with no handler the copy could only emit 0xB9 — which made the HOST answer the brief for the
                // clicking peer and, on a Cancel, delete the mission for everybody. The closure below is
                // VERBATIM the game's own (UIStateGeoModal.RestoreContext.RegenerateState:36-39), and it is safe
                // here because both of its arms are already gated: Confirm -> GeoscapeView.LaunchMission:1043 is
                // pure view (its SkipDeploymentScreen arm's mission.Launch:1046 is captured block-first as
                // 0xB8), and Cancel/Close is refused by MissionSync.PerPeerModalAnswer + MissionCancelGate.
                DialogCallback handler = null;
                if (GeoWindowCoverage.IsPerPeerAnswer(modalType, data))
                {
                    var lvl = geo;
                    var mt = modalType;
                    var md = data;
                    handler = res => lvl.View.ModalResultCallback(mt, res, md);
                }
                state = new UIStateGeoModal(modalType, handler, data);
            }
            var request = new GeoscapeViewStateSwitchRequest(state, p.Priority)
            // The GAME'S OWN flag, true as on the host: a mirrored modal must pause THIS peer, which is what
            // ProcessQueriedStateSwitch:67-70 -> RequestGamePause:1269 does. It used to be false on the theory
            // that pause arrives via the rail — it does not when the host is already paused (change-gated
            // Timing.Paused setter → no delta), which is how a client kept running under an open window.
            // A ONE-SHOT pause, not a hold: any peer resumes unconditionally, first-to-act-wins.
            { PauseGame = true };
            q.QueryStateSwitch(request);
            if (!string.IsNullOrEmpty(p.DurableTrigger))
            {
                var occurrence = DurableWindowRegistry.MatchPriorityOccurrence(DurableInboxSession.ActiveStore,
                    "Modal:" + (ModalType)p.ModalType, p.DurableTrigger, p.DurableSubject);
                if (occurrence.HasValue && occurrence.Value.EventId != null)
                    WindowOrder.BindDurable(request, occurrence.Value);
            }
            Debug.Log("[MP][modals] raised '" + name + "' seq=" + seq + " kind=" + p.Kind + " shape=" + p.Shape +
                      " priority=" + p.Priority + " data=" + (data == null ? "none" : data.GetType().Name));
            return true;
        }

        /// <summary>Resolve the shipped refs against THIS peer's live graph and rebuild the concrete
        /// <c>modalData</c> object the prefab's data-bind expects. Every element comes out of the client's own
        /// mirrored containers, so the window renders this peer's defs in this peer's locale.</summary>
        private static object BuildData(GeoLevelController geo, Raise p, out string refusal)
        {
            refusal = null;
            if (p.Shape == DataShape.None) return null;
            // THE GENERIC ARM. The wire carried a NAME, so the object comes out of THIS peer's own mirrored
            // graph — never off the payload, which holds no entity to be tempted by. The modalData for this
            // shape IS the entity, so the prefab binds against the peer's own live GeoMission/GeoCharacter
            // and renders this peer's state in this peer's locale.
            if (p.Shape == DataShape.EntityRef)
                return EntityData(p, IdentityResolver.Resolve(geo, p.Ref, null), out refusal);
            if (p.Shape == DataShape.AssetDeploy) return DeployBind(geo, p, out refusal);
            var faction = IdentityResolver.Resolve(geo, p.Ref, null) as GeoFaction;
            var keys = p.Keys ?? new string[0];
            var found = new List<ResearchElement>(keys.Length);
            if (faction?.Research != null)
                foreach (var id in keys)
                {
                    var el = string.IsNullOrEmpty(id) ? null : faction.Research.GetResearchById(id);
                    if (el != null) found.Add(el);
                }
            refusal = DataRefusal(p.Shape, faction != null, keys.Length, found.Count);
            if (refusal != null) return null;
            switch (p.Shape)
            {
                case DataShape.ResearchComplete:
                    return new GeoResearchCompleteData
                    {
                        ResearchElement = found[0],
                        // The host's own flag would send the CLIENT to its research screen when the host asked
                        // for it — a navigation decision belongs to the peer looking at the window, and every
                        // shipped raiser passes false anyway (GeoscapeView.cs:1985).
                        SwitchToResearchState = false,
                    };
                case DataShape.DiplomacyReward:
                    return new DiplomacyResearchRewardData
                    {
                        Faction = faction,
                        Researches = found,
                        DiplomacyShareLevel = p.Num,
                    };
                default:
                    refusal = "shape " + p.Shape + " has no client-side builder — the sending peer knows a shape " +
                              "this build does not, which is a version/mod parity break (law 10)";
                    return null;
            }
        }

        /// <summary>The <see cref="DataShape.AssetDeploy"/> arm's peer-side half: rebuild the prompt's whole
        /// data object out of THIS peer's own graph and THIS peer's own def repository. Nothing is copied off
        /// the wire but addresses — the asset is the peer's own mirrored <c>GeoCharacter</c>, the two defs are
        /// its own, and the faction is stamped locally exactly as the game's own save-restore does
        /// (UIStateAssetDeployment.RestoreContext:35), so the screen renders in this peer's locale.</summary>
        private static object DeployBind(GeoLevelController geo, Raise p, out string refusal)
        {
            var keys = p.Keys ?? new string[0];
            string aircraftGuid = keys.Length > 0 ? keys[0] : "";
            string relatedGuid = keys.Length > 1 ? keys[1] : "";
            var asset = string.IsNullOrEmpty(p.Ref)
                ? null : IdentityResolver.Resolve(geo, p.Ref, null) as GeoCharacter;
            var aircraft = ResolveDef<Base.Core.ComponentSetDef>(aircraftGuid);
            var related = ResolveDef<ItemDef>(relatedGuid);
            int wanted = (aircraftGuid == "" ? 0 : 1) + (relatedGuid == "" ? 0 : 1);
            int got = (aircraft == null ? 0 : 1) + (related == null ? 0 : 1);
            refusal = DataRefusal(DataShape.AssetDeploy,
                                  string.IsNullOrEmpty(p.Ref) || asset != null, wanted, got);
            if (refusal != null) return null;
            return new GeoDeployAssetFactionCharacterBind
            {
                Faction = geo.PhoenixFaction,
                Asset = asset,
                Aircraft = aircraft,
                RelatedItemDef = related,
                Manufactured = (p.Num & 1) != 0,
                NotEnoughSpace = (p.Num & 2) != 0,
            };
        }

        private static T ResolveDef<T>(string guid) where T : BaseDef =>
            string.IsNullOrEmpty(guid) ? null : Base.Core.GameUtl.GameComponent<DefRepository>()?.GetDef(guid) as T;

        private static GeoLevelController GeoLevel()
        {
            try { return GenericApplier.GeoLevel(); }
            catch { return null; }
        }

        /// <summary>THE ONE "is there a live geoscape view" probe, non-null exactly when a queued window can be
        /// pushed — and it is the GAME's own marker, not an invented one: <c>_viewSwichQuery</c> is constructed
        /// next to the states stack it wraps in the view's own init (GeoscapeView.cs:334-335) and is what every
        /// native raiser calls through (:860/:880/:1320). A <c>GeoLevelController</c> can exist while this is
        /// still null — that is precisely the mid-load window a returning peer's raise landed in. One probe,
        /// three callers (<see cref="NeedsPark"/>, <see cref="PumpParked"/>, <see cref="RaiseMirrored"/>), so
        /// "can it be shown" and "must it wait" can never answer differently.</summary>
        private static GeoscapeViewSwitchQuery SwitchQuery()
        {
            var view = GeoLevel()?.View;
            return view == null ? null : SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
        }
    }

    /// <summary>
    /// The deferral for a 0xB7 raise this peer cannot show YET — its entity has not landed here, or (since
    /// 2026-08-08) there is no live GeoscapeView to show it in at all. Both are the same situation, "not yet",
    /// and both release on the same self-arriving event, so they share this one queue rather than growing a
    /// second mechanism. PURE (no Unity, no game types beyond the payload struct) so RailCheck L107/L337 drive
    /// the REAL queue with a fake resolver instead of asserting that some call exists.
    ///
    /// STRICT FIFO. Only the HEAD is ever tested: a raise behind a waiting one is not "ready", it is LATER.
    /// The game shows windows one at a time out of a priority-ordered queue that keeps arrival order between
    /// equal priorities, so releasing out of order would hand this peer a sequence the host never showed.
    /// Cost, stated: one raise whose entity never arrives holds the ones behind it until it EXPIRES.
    ///
    /// BOUNDED TWICE, because the failure this replaces was a silent swallow and an unbounded queue is the
    /// same bug wearing a hat: <see cref="MaxBatches"/> caps how long a raise may wait (counted in APPLIED
    /// rail batches, not wall clock — deterministic, and it is the same clock the entity would arrive on),
    /// and <see cref="MaxParked"/> caps how many may wait at once. Both exits log; neither is silent.
    /// </summary>
    internal sealed class ModalParkQueue
    {
        /// <summary>Applied GeoRail batches a raise may wait. The create it waits for rides the NEXT diff
        /// cycle (DiffEngine TickInterval 0.1 s) and a sliced cycle can emit several packets, so this is
        /// generous by design — a few seconds of live rail traffic, then a loud drop.</summary>
        internal const int MaxBatches = 32;
        /// <summary>Concurrent waiters. The host raises windows one at a time out of its own queue, so more
        /// than a handful pending means they are not arriving at all.</summary>
        internal const int MaxParked = 8;

        private struct Entry { public uint Seq; public GeoModalMirror.Raise P; public int Waited; }
        private readonly List<Entry> _q = new List<Entry>();
        private readonly Dictionary<uint, DurableCarrierLease> _durable =
            new Dictionary<uint, DurableCarrierLease>();

        internal int Count { get { return _q.Count; } }
        internal void Clear()
        { foreach (var lease in _durable.Values.ToArray()) lease.Dispose(); _durable.Clear(); _q.Clear(); }

        /// <summary>Park one raise at the TAIL. Returns null normally, or the reason string for the OLDEST
        /// entry this push evicted — the caller logs it, because a dropped window that says nothing is the
        /// exact bug class this queue exists to close.</summary>
        internal string Park(uint seq, GeoModalMirror.Raise p)
        {
            _q.Add(new Entry { Seq = seq, P = p });
            var store = DurableInboxSession.ActiveStore;
            var occurrence = DurableWindowRegistry.MatchPriorityOccurrence(store,
                "Modal:" + (ModalType)p.ModalType, p.DurableTrigger, p.DurableSubject);
            if (occurrence.HasValue && !_durable.ContainsKey(seq))
            {
                lock (_durable)
                {
                    if (!_durable.ContainsKey(seq))
                    {
                        var lease = GeoModalMirror.BindQueuedCarrier(store, occurrence.Value, _ =>
                        { lock (_durable) { _q.RemoveAll(x => x.Seq == seq); _durable.Remove(seq); } });
                        if (!lease.IsFinished) _durable.Add(seq, lease);
                    }
                }
            }
            if (_q.Count <= MaxParked) return null;
            var lost = _q[0];
            _q.RemoveAt(0);
            DurableCarrierLease evictedLease;
            if (_durable.TryGetValue(lost.Seq, out evictedLease))
            { _durable.Remove(lost.Seq); evictedLease.Dispose(); }
            return "parked-raise queue is FULL (" + MaxParked + ") — the oldest waiting raise of '" +
                   GeoModalMirror.NameOf(lost.P.Kind, (ModalType)lost.P.ModalType) + "' seq=" + lost.Seq +
                   " ('" + lost.P.Ref + "') is DROPPED unshown. Either the entities named by 0xB7 are not " +
                   "reaching this peer at all, or this peer has been away from its geoscape long enough to " +
                   "stack up more windows than the queue holds";
        }

        /// <summary>One batch of progress. <paramref name="resolved"/> asks whether the head's entity is
        /// here NOW; <paramref name="show"/> raises it; <paramref name="expire"/> is the loud drop. A head
        /// that is still waiting stops the walk (FIFO) after costing itself one batch; an EXPIRED head is
        /// removed and the next one gets its turn in this same pump.</summary>
        internal void Pump(Func<GeoModalMirror.Raise, bool> resolved,
                           Action<GeoModalMirror.Raise, uint> show,
                           Action<GeoModalMirror.Raise, uint, int> expire)
        {
            while (_q.Count > 0)
            {
                var head = _q[0];
                if (resolved(head.P)) { _q.RemoveAt(0); Complete(head.Seq); show(head.P, head.Seq); continue; }
                head.Waited++;
                if (head.Waited < MaxBatches) { _q[0] = head; return; }
                _q.RemoveAt(0);
                Complete(head.Seq);
                expire(head.P, head.Seq, head.Waited);
            }
        }

        private void Complete(uint seq)
        {
            DurableCarrierLease lease;
            if (!_durable.TryGetValue(seq, out lease)) return;
            _durable.Remove(seq); lease.Dispose();
        }
    }

    /// <summary>
    /// HOST capture seam (law 4a) for the modal family, half one of two: <c>GeoscapeView.OpenModal</c>:867.
    /// A POSTFIX, so the host's own window is queued exactly as it always was and the mirror is a pure
    /// addition.
    ///
    /// The <c>forceOnTop</c>/<c>replaceTop</c> branches are DELIBERATELY not mirrored, and it is the same
    /// partition <see cref="GeoWindowCoverage"/> already draws: those two push straight onto
    /// <c>_statesStack</c> (:871/:876) instead of the queue, which is what the game does for the LOCAL player
    /// navigating their own geoscape — the soldier-edit pickers (UIStateEditSoldier.cs:670/:707) and the demo
    /// end screen. A window that never reaches <c>QueryStateSwitch</c> is not a window the game is
    /// interrupting the player with.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), "OpenModal", new[]
    {
        typeof(ModalType), typeof(DialogCallback), typeof(object), typeof(int), typeof(bool), typeof(bool),
    })]
    internal static class GeoModalOpenMirror
    {
        private static void Postfix(ModalType modalType, object modalData, int priority,
                                    bool forceOnTop, bool replaceTop)
        {
            if (forceOnTop || replaceTop) return;   // local navigation, not a pushed window (see the class doc)
            GeoWindowCoverage.AnnounceModal(modalType);
            GeoModalMirror.HostBroadcast(GeoModalMirror.StateKind.Modal, modalType, modalData, priority);
        }
    }

    /// <summary>
    /// HOST capture seam (law 4a), half two: <c>GeoscapeView.OpenModalPersistent</c>:848 — the other of the
    /// only two methods in the shipped game that construct a <c>UIStateGeoModal</c> (the third construction,
    /// <c>RestoreContext.RegenerateState</c>:36, is the save-restore path and re-raises a window this peer
    /// already had). It always queues, so there is no local-navigation branch to skip here.
    ///
    /// Nothing declared Mirrored comes through this opener today — <see cref="GeoModalMirror.HostBroadcast"/>
    /// declines every LocalOnly/Gap kind — but the ANNOUNCE must run for both, or a new modal kind would be
    /// invisible on whichever opener it happened to pick.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), "OpenModalPersistent", new[] { typeof(ModalType), typeof(object), typeof(int) })]
    internal static class GeoModalOpenPersistentMirror
    {
        private static void Postfix(ModalType modalType, object modalData, int priority)
        {
            DurableWindowRegistry.CaptureModal(modalType, modalData);
            GeoWindowCoverage.AnnounceModal(modalType);
            GeoModalMirror.HostBroadcast(GeoModalMirror.StateKind.Modal, modalType, modalData, priority);
        }
    }
}
