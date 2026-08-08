using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Base;
using Base.Cameras;
using Base.Core;
using Base.Defs;
using Base.Eventus;
using Base.UI.MessageBox.PromptControllers;
using Base.Utils;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Geoscape.Cameras;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Interception;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Multiplayer.UI
{
    /// <summary>
    /// PING MARKERS — "LOOK HERE", AND NOTHING ELSE.
    ///
    /// One hotkey drops a marker every peer sees, on the geoscape and in tactical (deployment included,
    /// because the hotkey is POLLED and therefore view-state-agnostic). Cursor over an object → an OBJECT
    /// ping: a stable id travels and the marker hangs off the object, so it follows a moving aircraft or
    /// soldier for free. Cursor over empty ground → a POINT ping: a coordinate travels.
    ///
    /// ON THE GLOBE THERE IS NO POINT PING — IT IS REFUSED (2026-08-08). Empty ocean is not a thing to point
    /// at: a ring drawn on open water names nothing, so the geoscape half pings only what the GAME'S OWN
    /// picking finds under the cursor (<c>Map.PickObjectOnPosition</c>, reached through
    /// <c>SelectAtCursor</c>) and otherwise sends NOTHING and plays the game's own refusal click at the
    /// presser — see <see cref="Refuse"/>. The battlefield keeps its point ping, where a bare tile IS the
    /// thing being pointed at ("go here"). <see cref="ShowGeo"/> still understands an arriving globe point
    /// ping; only this build's send side stopped producing one.
    ///
    /// AND IT NEVER POINTS AT SOMETHING THE VIEWER HAS NOT FOUND (2026-08-09, law L344). The globe's own
    /// picking answers for unrevealed sites as readily as revealed ones, which made the ping key a map
    /// scanner and published every find to every peer. Both doors now ask the game's own per-faction reveal
    /// flag about their OWN viewer — <see cref="Capture"/> before it sends, <see cref="ShowGeo"/> before it
    /// draws — and the two answers are allowed to differ. See <see cref="Known"/>.
    ///
    /// PRESENTATION, NOT STATE (P4c, law L158). Nothing here is domain state: no <c>[SerializeMember]</c>
    /// leaf, no surface id, no <c>TimeAnchor</c>, no diff rail. A lost ping is not a problem and there is
    /// deliberately no exactly-once guarantee — the packet is fire-and-forget and the marker expires by
    /// itself.
    ///
    /// IT MOVES NOBODY (law L160, extending L97). A ping enters no view state, moves no camera and changes
    /// no selection on the peer that receives it. That is why this class instantiates markers directly and
    /// never touches <c>SetSelectedActor</c> / <c>ChaseTarget</c> / the state stack. The game's own
    /// <c>SelectAtCursor</c> is used for PICKING only — despite the name it selects nothing, it is a pure
    /// raycast query (<c>GeoscapeView.cs:970-1006</c>, <c>TacticalView.cs:700-780</c>).
    ///
    /// THE WIRE: a top-level <see cref="PacketType.PingMarker"/>, like <c>Heartbeat</c> and unlike every
    /// rail surface. It never enters <c>SurfaceRouter</c>, so it needs no surface id and no band — which
    /// matters, because the geoscape band 0xA0-0xBF is full of tombstones and law L62 hard-requires the
    /// name prefix to match the band. One packet type serves both screens. Client → host → everyone else,
    /// exactly like <c>ChatMessage</c>; the sender shows its own ping locally the same frame it sends.
    ///
    /// NATIVE VISUALS ONLY, AND THE MOD OWNS EVERY INSTANCE. Geoscape: the RADIATING WAVES of the game's own
    /// haven-defence marker, <c>GeoSiteVisualsDefs.Instance.HavenDefenseVisualsPrefab</c> — its rings are
    /// switched off, see <see cref="WavesOnly"/>. Tactical: the
    /// <c>LocatedBeaconPrefab</c> shaft (<c>TacticalView.cs:83</c>) the game already raises over a
    /// heard-but-unseen actor, i.e. the native "something is HERE" idiom. AND A NATIVE SOUND — a silent
    /// marker is easy to miss, so every shown ping also plays the game's own modal-appears cue through the
    /// game's own audio path; see <see cref="Cue"/>.
    ///
    /// WHY THE GLOBE MARKER WAS INVISIBLE FOR ITS WHOLE LIFE, and it was never the ACTIVATION (2026-08-08,
    /// second attempt — the first, law L182, added the missing <c>SetActive(true)</c> and changed nothing on
    /// screen). IT WAS BURIED AT THE CENTRE OF THE EARTH. Every globe object is a CENTRE-pivot:
    /// <c>GeoActor.PivotTransform => transform</c> and <c>WorldPosition => Surface.position</c>
    /// (GeoActor.cs:23,25), with <c>Surface = PivotTransform.Find("GlobeOffset")</c> (GeoVehicle.cs:315) the
    /// only child that is out at globe radius. An object ping parented its marker to <c>actor.transform</c>
    /// — the PIVOT, at (0,0,0) — and a point ping placed its marker by ROTATION ALONE, which likewise leaves
    /// the object sitting at its parent's origin. Both recipes only ever work if the marker prefab carries
    /// the ~6.4-unit radius offset in its own local transform, and the <c>Binds</c> prefab does not: the
    /// game never exercises that path, because <c>ReadMarkersCache</c> (GeoscapeGlobeMarkers.cs:38-52)
    /// pre-loads POOLED scene instances that keep their authored local offset across
    /// <c>SetParent(…, worldPositionStays: false)</c> (:71,:99). THE LOG PROVED IT before a line was
    /// changed: <c>ping arrow CLICKED — centring … on the pinged object at (0.0, 0.0, 0.0)</c>, i.e. the
    /// globe centre, against <c>(-6.0, 2.0, 1.0)</c> for a point ping on the surface.
    ///
    /// SO PLACEMENT IS EXPLICIT NOW AND OWES A PREFAB NOTHING. An object ping parents to
    /// <c>GeoActor.Surface</c> — the transform the game itself hangs world visuals off
    /// (<c>Instantiate(VehicleDef.ExplorationVisualsPrefab, CurrentSite.Surface)</c>, GeoVehicle.cs:454) —
    /// and an arriving point ping gets a carrier with BOTH halves written, <c>position</c> = the surface
    /// point and <c>rotation</c> = <c>Quaternion.Euler(GetSphericalCoordinates(pos))</c>, which is the same
    /// orientation <c>Surface</c> inherits from its pivot. Owning the instance is what buys the rest: the
    /// expiry is ours (one <see cref="Expire"/> for both screens) and so is the COLOUR (see
    /// <see cref="Tint"/> and <see cref="WavesOnly"/>).
    ///
    /// THE OFF-SCREEN ARROW IS OURS, and the recon's <c>UIObjectTracker.KeepOnScreen</c> route is NOT
    /// usable: <c>UIObjectTrackersController.LateUpdate</c>'s <c>shouldUpdateTracker</c>
    /// (<c>UIObjectTrackersController.cs:204-218</c>) frustum-culls a tracker and calls
    /// <c>EnableVisuals(false)</c> on it — a tracked point that is off screen is HIDDEN, which is precisely
    /// the case the arrow exists for. So the clamp is not native after all, and OnGUI draws it here.
    /// ponytail: ~40 lines of IMGUI; if it ever needs to sit under the game's canvas, port it then.
    /// IT NOW TAKES A CLICK — see <see cref="Focus"/> — and the click is blocked from reaching the world by
    /// the GAME'S OWN cursor-over-UI guard rather than by anything of ours; see
    /// <see cref="PingArrowIsGuiInBattle"/>.
    ///
    /// A TACTICAL OBJECT PING PAINTS THE MODEL AND PLANTS NO SHAFT (2026-08-08). The whole actor blinks in
    /// the ping's colour — green mine, blue somebody else's — through the game's own whole-body repaint. Two
    /// earlier readings of this got the mechanism wrong and are recorded in <see cref="Repaint"/>, which is
    /// the only place that matters: it is NOT <c>Highlight(_, _, ability: true)</c> (a latched bool the
    /// game's own ability targeting rides, so a ping would be swallowed and its expiry would clear a paint
    /// the game still wants), and it is NOT <c>AbilityHighlightShader</c> (one fixed scene asset, carries no
    /// colour). It is <c>SetSpecialShader</c> — the unlatched per-actor slot a stance and a holographic
    /// status already use — driven with <c>BodypartHighlightShader</c>, the one look the game does colour.
    /// The beacon shaft stays as the FALLBACK, for a pinged thing that is not <c>IHighlightable</c>.
    /// </summary>
    public sealed class PingMarkers : MonoBehaviour
    {
        /// <summary>Marker lifetime, and now the ONLY one: every marker on both screens is this class's own
        /// instance, so <see cref="Expire"/> is the single deadline for the marker, the beacon and the
        /// arrow. Nothing is handed to the geoscape's pooled expiry timer any more.</summary>
        public const float LifetimeSeconds = 5f;

        /// <summary>MY OWN PING vs SOMEBODY ELSE'S — the owner's ask, in the two colours he named. Applied
        /// per-instance in <see cref="Tint"/>, so it costs the game's shared marker prefab nothing.</summary>
        private static readonly Color Own = new Color(0.20f, 0.90f, 0.35f, 1f);
        private static readonly Color Peer = new Color(0.25f, 0.55f, 1f, 1f);

        /// <summary>The arrow fades over its last second. The native markers animate themselves; only what
        /// this class draws is faded here.</summary>
        private const float FadeSeconds = 1f;

        /// <summary>HOW BIG THE ARROW IS, as its half-extent in pixels — <c>max(30, height/20)</c>, which is
        /// 2.5x the <c>max(12, height/50)</c> it shipped at. It was too small to notice, which for the one
        /// widget whose entire job is "somebody is pointing at something you cannot see" is the whole feature
        /// missing. Named constants and not literals because L252 reads them: a shrink back has to be a
        /// deliberate edit to a number a law is watching, not a tweak inside a draw call.</summary>
        internal const float ArrowHalfMin = 30f;
        internal const float ArrowHalfFraction = 0.05f;

        private const byte SceneGeo = 0;
        private const byte SceneTac = 1;
        private const byte KindPoint = 0;
        private const byte KindObject = 1;

        private static PingMarkers _instance;
        private static Texture2D _arrow;

        private readonly List<Live> _live = new List<Live>();

        /// <summary>One shown ping. <see cref="Follow"/> non-null = object ping (the marker is parented to
        /// it and so is the arrow's target); otherwise <see cref="Local"/> is the point — globe-LOCAL on
        /// the geoscape, world in tactical.</summary>
        private sealed class Live
        {
            public float Until;
            public bool Geo;
            public Transform Follow;
            public Vector3 Local;
            /// <summary>The marker this class instantiated and must destroy — the geoscape POI marker or
            /// the tactical beacon shaft. Never a pooled object: nothing here is the game's to reclaim.</summary>
            public GameObject Beacon;
            /// <summary>Whose ping this is, from the VIEWER's side — true only on the peer that pressed the
            /// key. The marker's tint is written once at birth, but the arrow is redrawn every frame, so it
            /// needs the answer kept.</summary>
            public bool Mine;
            /// <summary>TACTICAL OBJECT PING ONLY: the pinged model, repainted whole. Null on every other
            /// shape, and null on an actor whose model could not be painted (then <see cref="Beacon"/> is
            /// the shaft, as before). <see cref="PriorShader"/> is what the actor's special-shader channel
            /// held before us — a stance or a holographic status owns that channel too, so the expiry
            /// hands it back rather than clearing to nothing.</summary>
            public IHighlightable Paint;
            public Shader PriorShader;
            public bool PaintOn;
        }

        // ─── lifecycle ───────────────────────────────────────────────────────

        private void Awake() => _instance = this;

        private void OnDestroy()
        {
            foreach (var p in _live)
            {
                if (p.Beacon != null) Destroy(p.Beacon);
                if (p.Paint != null) Repaint(p, false);
            }
            _live.Clear();
            // ReferenceEquals, not == (L113): this is "am I the registered instance", a reference question.
            // Unity's == answers "is the native half alive", which is false for a component being destroyed —
            // so it would leave the static pointing at a corpse.
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        // ─── send ────────────────────────────────────────────────────────────

        private void Update()
        {
            Expire();
            Blink();

            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;

            var cfg = MultiplayerMain.Instance?.Config;
            if (cfg == null || cfg.PingMarkerKey == KeyCode.None) return;
            // A focused input field owns the keyboard. Without this, typing the ping key into the chat box
            // also drops a ping on whatever the cursor happens to be over.
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null) return;
            if (!Input.GetKeyDown(cfg.PingMarkerKey)) return;

            var payload = Capture();
            if (payload == null) return;

            // Local first: the sender never receives its own packet (host) or waits a round trip (client).
            // ShowLocal, not Show — the two entry points ARE the peer distinction. Nothing on the wire says
            // who sent a ping, and nothing needs to: the only peer that ever reaches this line is the one
            // whose key was pressed, so "mine" is answered by WHICH DOOR the payload came through. That
            // keeps the packet byte-identical to what every build already speaks.
            ShowLocal(payload);
            var msg = new NetworkMessage(PacketType.PingMarker, payload);
            if (engine.IsHost) engine.BroadcastToAll(msg);
            else engine.SendToHost(msg);
        }

        /// <summary>What the cursor is over, as a wire payload — or null when there is nothing to ping
        /// (cursor off the globe, over GUI, no level).</summary>
        private static byte[] Capture()
        {
            var geo = GeoLevel();
            if (geo != null)
            {
                var view = geo.View;
                if (view == null) return null;
                // THE GAME'S OWN HIT TEST, not one of ours: SelectAtCursor raycasts the Globe layer and
                // then hands the surface point to Map.PickObjectOnPosition (GeoscapeView.cs:986-996), which
                // casts back down through the site/vehicle PICKING colliders (GeoMap.cs:806-815). So "is
                // there a point of interest here" is answered by the same colliders a normal click uses,
                // with the same tolerance.
                var pick = view.SelectAtCursor();          // pure query — selects nothing
                // AND IT MUST ALREADY BE KNOWN TO THE PRESSER — see Known. An unrevealed site is treated
                // exactly like empty ocean, refusal cue included, so the refusal itself leaks nothing.
                var seen = pick.Actor != null && Known(geo, pick.Actor);
                var re = seen ? IdentityResolver.RootRef(pick.Actor) : null;
                if (re != null) return Encode(SceneGeo, KindObject, Vector3.zero, re, 0);
                Refuse(view);
                return null;
            }

            var tlc = Tlc();
            if (tlc?.View == null) return null;
            var hit = tlc.View.SelectAtCursor();            // pure query — selects nothing
            if (hit.Actor != null)
            {
                var key = TacticalActorKey.Of(hit.Actor);
                if (key != 0) return Encode(SceneTac, KindObject, Vector3.zero, null, key);
            }
            // ActionGridPos, not RawHit.point: it is the SNAPPED grid cell the game's own actions address
            // (TacticalMap.SnapXYZ), so both peers name the same tile, and it is NaN exactly when the
            // cursor found no floor — nothing to point at.
            return float.IsNaN(hit.ActionGridPos.x)
                ? null
                : Encode(SceneTac, KindPoint, hit.ActionGridPos, null, 0);
        }

        /// <summary>
        /// A PING NEVER TELLS A PEER SOMETHING HIS OWN GLOBE HAS NOT TOLD HIM. Asked on BOTH sides and
        /// always about the LOCAL viewer: at <see cref="Capture"/>, so a presser cannot ping what he has not
        /// found, and again at <see cref="ShowGeo"/>, so an arriving ping cannot draw a ring on a site the
        /// RECEIVER has not found. The two answers legitimately differ — one player has scanned a region the
        /// other has not — and the receiving arm is the one that matters: without it, a host who has
        /// explored ahead reveals every site he pings to everybody.
        ///
        /// WHY IT WAS NEEDED AT ALL. <c>GeoscapeView.SelectAtCursor</c>'s picking branch is
        /// <c>GeoMap.PickObjectOnPosition</c> (GeoMap.cs:806-815), a bare <c>Physics.Raycast</c> on
        /// <c>UnityLayers.Picking</c> that returns <c>hitInfo.transform.GetComponentInParent&lt;GeoActor&gt;()</c>
        /// with NO visibility filter — so an unrevealed site answers the cursor exactly like a revealed one
        /// and a player could sweep the globe with the ping key and read off where the map's contents are.
        /// (The sibling <c>selectWithProximation</c> branch does filter, GeoscapeView.cs:985, which is what
        /// says the missing filter here is an omission and not a design.)
        ///
        /// THE PREDICATE IS THE GAME'S OWN, not one of ours. A site answers
        /// <c>GeoSite.GetVisible(GeoFaction)</c> (GeoSite.cs:387-390) — the per-faction flag the game's own
        /// <c>GeoSite.Selectable</c> gates on with this exact viewer (<c>GetVisible(GeoLevel.ViewerFaction)</c>,
        /// GeoSite.cs:179-186). A craft answers <c>GeoVehicle.IsVisible</c> (GeoVehicle.cs:174-187), whose
        /// own writer is the per-viewer <c>RefreshVisibility</c> (:606-613).
        ///
        /// EVERY OTHER <c>GeoActor</c> PASSES, and the list is short and closed: <c>GeoScanner</c> and
        /// <c>GeoAncientSiteProbe</c> are the viewer's own deployables, and a <c>GeoBehemothActor</c> hides by
        /// switching its <c>VisualsRoot</c> off (:584, :609) — which takes its picking collider with it, so an
        /// unseen one is not pickable in the first place. None of the three carries a per-faction secret this
        /// could read, so there is nothing to ask them.
        /// </summary>
        private static bool Known(GeoLevelController geo, GeoActor actor)
        {
            var site = actor as GeoSite;
            if (site != null) return site.GetVisible(geo.ViewerFaction);
            var vehicle = actor as GeoVehicle;
            return vehicle == null || vehicle.IsVisible;
        }

        /// <summary>
        /// PINGING OPEN OCEAN IS REFUSED, AND THE PRESSER HEARS WHY. Nothing is sent, nothing is drawn, and
        /// the only peer that learns anything is the one who pressed the key — a refusal is not state and
        /// has no business on the wire.
        ///
        /// THE SOUND IS THE GAME'S OWN "you clicked something you cannot use":
        /// <c>InterceptionGameSoundDef.DisabledEquipmentClick</c>, the Wwise event the game posts in place
        /// of its normal click when the equipment under the cursor is disabled
        /// (<c>InterceptionGameSound.cs:34-43</c> — the two lines below are those lines). Posted through
        /// <c>AK.Wwise.Event.Post</c> exactly as the game posts it, so it rides the player's own volume
        /// sliders like every other cue. It is the only NAMED refusal cue in the build: a sweep of every
        /// <c>AK.Wwise.Event</c> and <c>UIEventDef</c> field in Assembly-CSharp turns up no error/invalid/
        /// denied event anywhere else, so this is reuse rather than a nearest guess.
        ///
        /// THE GATE IS THE GAME'S TOO. <c>SelectAtCursor</c> reports "nothing" for three different reasons —
        /// empty ocean, cursor off the globe entirely, and cursor over a panel — and only the first two are
        /// a missed ping. <c>IsCursorOverGui</c> (GeoscapeView.cs:905, the same method the globe's own
        /// picking and camera drag gate on) separates them, so typing over a panel does not beep.
        /// </summary>
        private static void Refuse(GeoscapeView view)
        {
            if (view.IsCursorOverGui()) return;
            var host = _instance == null ? null : _instance.gameObject;
            if (host == null) return;

            var def = GameUtl.GameComponent<DefRepository>()?.GetAllDefs<InterceptionGameSoundDef>()
                             .FirstOrDefault();
            var cue = def?.DisabledEquipmentClick;
            if (cue != null && cue.IsValid()) { cue.Post(host); return; }

            if (_loggedRefusal) return;
            _loggedRefusal = true;
            Debug.LogWarning("[Multiplayer] geoscape ping REFUSED (nothing under the cursor) but SILENTLY — " +
                             (def == null
                                 ? "the def repository names no InterceptionGameSoundDef"
                                 : "InterceptionGameSoundDef.DisabledEquipmentClick is not a valid Wwise " +
                                   "event in this build") +
                             ", so there is no native refusal cue to post. The refusal itself is correct — " +
                             "no packet was sent and no marker drawn — only the sound is missing (logged " +
                             "once per run).");
        }

        private static bool _loggedRefusal;

        private static byte[] Encode(byte scene, byte kind, Vector3 pos, string entityRef, int actorKey)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(scene);
                w.Write(kind);
                if (kind == KindObject)
                {
                    if (scene == SceneGeo) w.Write(entityRef ?? "");
                    else w.Write(actorKey);
                }
                else { w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); }
                return ms.ToArray();
            }
        }

        // ─── receive ─────────────────────────────────────────────────────────

        /// <summary>THE ARRIVAL SEAM, wired to <see cref="PacketType.PingMarker"/> in
        /// <c>NetworkEngine.RouteMessage</c>. Runs inside the engine's packet drain, which runs inside the
        /// game's own Update — hence the catch (L158 arm (c)): a throw here would unwind into native code
        /// and cost the session a marker.
        ///
        /// REACTIVITY: there is no "repaint the open screen" step and none is needed. The marker is
        /// instantiated straight into the LIVE view the frame the packet is drained, so it appears on an
        /// already-open geoscape or battle with no re-entry, no state change and no reload.</summary>
        public static void Show(byte[] payload)
        {
            try { ShowCore(payload, false); }
            catch (Exception e) { Debug.LogWarning("[Multiplayer] ping marker not shown: " + e.Message); }
        }

        /// <summary>THE SENDER'S OWN DOOR, called from <see cref="Update"/> and from nowhere else. Same
        /// work as <see cref="Show"/>, one flag apart — see the note at the call site for why the peer
        /// distinction lives in the entry point rather than on the wire.</summary>
        public static void ShowLocal(byte[] payload)
        {
            try { ShowCore(payload, true); }
            catch (Exception e) { Debug.LogWarning("[Multiplayer] own ping marker not shown: " + e.Message); }
        }

        private static void ShowCore(byte[] payload, bool mine)
        {
            // BOTH OF THESE USED TO RETURN IN SILENCE, and that is how a feature whose every visible half
            // was broken still produced no line to read. Silent swallow is this repo's dominant bug class;
            // a ping is cheap, so it can afford to say why it vanished.
            if (_instance == null)
            {
                Debug.LogWarning("[Multiplayer] ping DROPPED — PingMarkers has no live instance, so there is " +
                                 "nothing holding the marker list. The component is created with the mod's UI " +
                                 "host, so this is a packet that arrived before that host existed or after it " +
                                 "was torn down. Nothing else is affected.");
                return;
            }
            if (payload == null || payload.Length < 2)
            {
                Debug.LogWarning("[Multiplayer] ping DROPPED — payload is " +
                                 (payload == null ? "null" : payload.Length + " byte(s)") + " and the header " +
                                 "alone is 2 (scene, kind). Either the datagram was truncated or the sender " +
                                 "speaks a different build's ping format.");
                return;
            }

            byte scene, kind;
            var pos = Vector3.zero;
            string entityRef = null;
            var actorKey = 0;
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                scene = r.ReadByte();
                kind = r.ReadByte();
                if (kind == KindObject)
                {
                    if (scene == SceneGeo) entityRef = r.ReadString();
                    else actorKey = r.ReadInt32();
                }
                else pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }

            if (scene == SceneGeo) ShowGeo(kind, pos, entityRef, mine);
            else ShowTac(kind, pos, actorKey, mine);
        }

        private static void ShowGeo(byte kind, Vector3 local, string entityRef, bool mine)
        {
            var geo = GeoLevel();
            var refs = geo?.SceneReferences;
            var root = GlobeRoot(geo);
            // The game's own haven-defence ring. GeoSiteVisualsDefs is a def, not a scene object, so unlike
            // the marker bind table it cannot go missing with a view — but it is still read rather than
            // assumed, and a missing prefab draws NO substitute: a hand-rolled overlay is exactly what
            // "native UI first" forbids, and a named log line is worth more than a stray quad.
            var prefab = GeoSiteVisualsDefs.Instance?.HavenDefenseVisualsPrefab;
            if (root == null || refs?.MarkersContainer == null || prefab == null)
            {
                // Not an error — a peer on another screen legitimately has no globe to draw on — but it is
                // the difference between "he is in a battle" and "the ping broke", so it says which.
                Debug.LogWarning("[Multiplayer] geoscape ping DROPPED — no live globe to draw it on (level=" +
                                 (geo == null ? "not geoscape" : "geoscape") + ", view=" +
                                 (geo?.View == null ? "none" : "live") + ", globe root=" +
                                 (root == null ? "none" : "live") + ", container=" +
                                 (refs?.MarkersContainer == null ? "none" : "live") + ", ring prefab=" +
                                 (prefab == null ? "none" : "live") + "). The sender still saw his own marker.");
                return;
            }

            Transform follow = null;
            Transform host;
            if (kind == KindObject)
            {
                var actor = IdentityResolver.Resolve(geo, entityRef, null) as GeoActor;
                // THE RECEIVER'S OWN ANSWER, not the sender's — see Known. A peer who has not found this
                // site does not learn of it from somebody else's key press.
                if (actor != null && !Known(geo, actor))
                {
                    Debug.Log("[Multiplayer] geoscape ping on '" + entityRef + "' NOT DRAWN — that site is " +
                              "not revealed to this peer's faction (GeoSite.GetVisible is false), and a ping " +
                              "is not allowed to be the thing that reveals it. Working as intended: the " +
                              "sender can see it, this peer cannot, and the two globes legitimately differ.");
                    return;
                }
                var surface = actor?.Surface;
                if (surface == null)
                {
                    Debug.LogWarning("[Multiplayer] ping names '" + entityRef + "', which does not resolve to " +
                                     "a geoscape actor with a globe surface on this peer — nothing to point at.");
                    return;
                }
                // SURFACE, NOT transform. The actor's own transform is the centre-pivot at (0,0,0); Surface
                // is the "GlobeOffset" child out at globe radius (GeoVehicle.cs:315) and is where the game
                // itself hangs world visuals (GeoVehicle.cs:454). It carries the actor's lat/long as its
                // inherited rotation, so the ring needs no placement of its own and follows a moving
                // aircraft for free — and the off-screen arrow, which reads Follow.position, finally gets a
                // point on the globe instead of the middle of the earth.
                host = surface;
                follow = surface;
            }
            else
            {
                // A globe point ping is no longer SENT (see Refuse) but is still shown if one arrives.
                // BOTH halves are written: rotation alone leaves the object at its parent's origin, which
                // is the globe centre, which is why nothing was ever visible here.
                var world = root.TransformPoint(local);
                var carrier = new GameObject("MpPingRing");
                carrier.transform.SetParent(refs.MarkersContainer, false);
                carrier.transform.position = world;
                carrier.transform.rotation = Quaternion.Euler(refs.GetSphericalCoordinates(world));
                host = carrier.transform;
            }

            var ring = Instantiate(prefab, host);
            ring.gameObject.SetActive(true);
            Tint(ring.gameObject, mine);
            WavesOnly(ring.gameObject);
            _instance.Track(new Live
            {
                Geo = true,
                Follow = follow,
                Local = local,
                // The carrier owns the ring, so destroying the carrier destroys both; an object ping has no
                // carrier and the ring is what expires.
                Beacon = kind == KindObject ? ring.gameObject : host.gameObject,
                Mine = mine,
            });
        }

        private static void ShowTac(byte kind, Vector3 pos, int actorKey, bool mine)
        {
            var tlc = Tlc();
            var view = tlc?.View;
            var prefab = view?.LocatedBeaconPrefab;
            if (prefab == null || view.Markers == null) return;

            if (kind == KindObject)
            {
                string why;
                var actor = TacticalActorKey.Resolve(tlc, actorKey, out why);
                if (actor == null)
                {
                    Debug.LogWarning("[Multiplayer] ping names actor key " + actorKey + ", unresolved: " + why);
                    return;
                }
                // AN ACTOR PING PAINTS THE MODEL, IT DOES NOT PLANT A SHAFT — see Repaint. The shaft is
                // the fallback for an actor the paint cannot reach.
                var painted = new Live { Follow = actor.transform, Mine = mine, Paint = actor as IHighlightable };
                if (painted.Paint != null && LightingSettingsCharacters.Instance?.BodypartHighlightShader != null)
                {
                    painted.PriorShader = PriorShaderOf(actor);
                    painted.PaintOn = true;
                    Repaint(painted, true);
                    _instance.Track(painted);
                    return;
                }
                Debug.LogWarning("[Multiplayer] ping on actor key " + actorKey + " falls back to the beacon shaft — " +
                                 (painted.Paint == null
                                     ? actor.GetType().Name + " is not IHighlightable, so it owns no body materials to repaint."
                                     : "LightingSettingsCharacters names no BodypartHighlightShader in this scene.") +
                                 " The ping is still shown; only the full-body colour is missing.");

                // Parented to the actor, exactly as RefreshLocatedBeacon:439-449 does it, so the shaft
                // travels with him.
                var beacon = Instantiate(prefab, actor.transform);
                beacon.transform.ResetTransform();
                // SetActive, exactly as RefreshLocatedBeacon:449 does on the line after its own
                // Instantiate+ResetTransform pair. The geoscape half proved what happens when an
                // instantiated native prefab is assumed to arrive switched on.
                beacon.SetActive(true);
                Tint(beacon, mine);
                _instance.Track(new Live { Follow = actor.transform, Beacon = beacon, Mine = mine });
                return;
            }

            // Free-standing shaft under the special-markers root, as OverwatchStatus.cs:102 parents its
            // cones. Not a GroundMarker: the only group ClearGroundMarkers() spares is Tutorial, and
            // borrowing it would put our marker in the same bag as a running tutorial's.
            var stand = Instantiate(prefab, view.Markers.SpecialMarkersRoot);
            stand.transform.ResetTransform();
            stand.transform.position = pos;
            stand.SetActive(true);
            Tint(stand, mine);
            _instance.Track(new Live { Local = pos, Beacon = stand, Mine = mine });
        }

        private void Track(Live p)
        {
            p.Until = Time.unscaledTime + LifetimeSeconds;
            _live.Add(p);
            Cue();
        }

        // ─── the coloured half ───────────────────────────────────────────────

        /// <summary>
        /// GREEN IS MINE, BLUE IS SOMEBODY ELSE'S — the owner's ask, on every surface the ping has
        /// (geoscape, battle, and deployment, which is the battle screen).
        ///
        /// COLOUR IS NOT SETTABLE ON THE NATIVE WIDGET ITSELF: <c>GlobeMarker</c> exposes <c>Type</c>,
        /// <c>Animator</c> and <c>Range</c> and nothing else (GlobeMarker.cs:13-48), and the marker the
        /// game hands out through <c>AddMarker</c> is POOLED — it is handed back and re-used, so a tint
        /// written onto it would leak into the game's own next point-of-interest. The tint is possible only
        /// BECAUSE this class stopped borrowing from the pool and instantiates its own copy, which nothing
        /// else will ever see (the same reason <c>UIStateVehicleSelected.cs:714</c> instantiates its
        /// selection marker from the prefab rather than asking the pool for one).
        ///
        /// <c>MaterialPropertyBlock</c>, never <c>renderer.material</c>: the block writes per-renderer
        /// without instancing a material — so there is no clone to leak when the marker is destroyed — and,
        /// the point here, a property the shader does not declare is silently IGNORED rather than logged as
        /// an error. That is what makes it safe to offer both of the two names Unity's own shaders use for
        /// a tint (<c>_Color</c> on the standard/UI family, <c>_TintColor</c> on the particle/additive one)
        /// without knowing which family an asset-only prefab was authored against.
        /// ponytail: if the marker still draws in its stock colour, the one-shot log below names the shader
        /// per renderer — that is the property to add, and it is one line.
        /// </summary>
        private static void Tint(GameObject go, bool mine)
        {
            var colour = mine ? Own : Peer;
            var block = new MaterialPropertyBlock();
            List<string> seen = _loggedTint ? null : new List<string>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                r.GetPropertyBlock(block);
                block.SetColor("_Color", colour);
                block.SetColor("_TintColor", colour);
                r.SetPropertyBlock(block);
                var mat = r.sharedMaterial;
                if (seen != null)
                    seen.Add(r.gameObject.name + "<" + (mat == null || mat.shader == null ? "no-shader" : mat.shader.name) + ">");
            }

            // THE WAVES, KEPT AND MADE ENDLESS — the second half of the owner's 2026-08-09 ask, and the one
            // the renderer loop above cannot do on its own. The prefab's radiating pulse is a ParticleSystem,
            // authored for a haven defence that lasts a whole mission, and a burst that plays once leaves a
            // 5-second marker sitting still for four of those seconds. `loop` is what runs it for the
            // marker's whole life; Destroy at Expire is what ends it.
            //
            // AND `startColor` IS WHERE A PARTICLE'S COLOUR ACTUALLY LIVES. A property block reaches a
            // ParticleSystemRenderer's MATERIAL, but the per-particle colour the system multiplies over it
            // is the module's, which is why the waves stayed stock red through the tint above.
            var puffs = 0;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                var main = ps.main;                      // a handle back into the system, not a copy
                main.loop = true;
                main.startColor = colour;
                ps.Play(true);
                puffs++;
            }

            if (seen == null) return;
            _loggedTint = true;
            Debug.Log("[Multiplayer] ping marker tinted " + (mine ? "OWN green" : "PEER blue") +
                      " over _Color/_TintColor on " + seen.Count + " renderer(s): " +
                      string.Join(", ", seen.ToArray()) + ", and over startColor on " + puffs +
                      " looping particle system(s) (logged once per run). A count of 0, or a marker " +
                      "still drawing in its stock colour, means the visual is not a Renderer or does not " +
                      "declare either property — the shader names above are what to write instead. A " +
                      "particle count of 0 means the radiating waves are NOT particles in this build and " +
                      "whatever animates them still owns their colour and their one-shot life.");
        }

        /// <summary>THE SHADER IDS ARE NOT CACHED, AND MUST NOT BE. They used to be two
        /// <c>static readonly int</c>s initialised with <c>Shader.PropertyToID</c>, which put a UNITY ECALL
        /// INTO THIS TYPE'S STATIC CONSTRUCTOR — so merely READING any static field of this class ran engine
        /// code. RailCheck reflects over <c>Own</c>/<c>Peer</c> (law L251) outside a Unity runtime and the
        /// whole harness aborted on the type initializer: 1 law crashed, 179 of 180 ran, no verdict. The
        /// name-taking <c>SetColor</c> overload interns the same id internally and a ping is a handful per
        /// minute, so the cache bought nothing and cost a harness.</summary>
        private static bool _loggedTint;

        /// <summary>
        /// THE RING IS GONE, THE WAVES STAY (2026-08-09, owner). Several points of interest already wear a
        /// ring of their own in vanilla — this IS the haven-defence ring prefab — so a ping laid over one of
        /// them stacked ring on ring and read as garbage. What names the spot is the motion, not the outline.
        ///
        /// THE PREFAB'S PARTS ARE MEASURED, NOT GUESSED. <see cref="Tint"/>'s one-shot log named all five
        /// renderers in a live 2026-08-08 session: <c>Progress</c>&lt;Unlit/Circular Progress Cutout&gt; (the
        /// fill), <c>Outline</c>, <c>Pulse1</c>, <c>Pulse2</c> and <c>OngoingBattle_Swords</c> (all four
        /// &lt;Unlit/Colored, Animated Mask&gt;). The waves are <c>Pulse1</c>/<c>Pulse2</c>: separate
        /// renderers on separate objects, animated by their own shader, so switching the rest off cannot
        /// take them with it. The swords icon goes too — a ping is not an ongoing battle.
        ///
        /// <c>renderer.enabled</c>, NOT <c>SetActive(false)</c>: deactivating an object takes its CHILDREN
        /// down with it, and nothing here proves the pulses are not parented under the ring. Disabling one
        /// renderer stops exactly one mesh drawing and touches nothing below it.
        ///
        /// FAILS SAFE. If a build renames the pulses, this finds no wave and hides NOTHING — the marker
        /// draws as it did before, which is a ring too many rather than a ping nobody can see.
        /// </summary>
        private static void WavesOnly(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            int waves = 0;
            foreach (var r in rs) if (r != null && IsWave(r)) waves++;
            if (waves == 0)
            {
                Debug.LogWarning("[Multiplayer] ping marker keeps its ring — no renderer named Pulse* under " +
                                 "the haven-defence prefab, so hiding the rest would leave nothing at all " +
                                 "to see. The tint log names the renderers this build actually has.");
                return;
            }
            foreach (var r in rs) if (r != null && !IsWave(r)) r.enabled = false;
        }

        private static bool IsWave(Renderer r) => r.gameObject.name.StartsWith("Pulse", StringComparison.Ordinal);

        /// <summary>
        /// THE PINGED ACTOR, PAINTED WHOLE AND BLINKING — green mine, blue somebody else's, the same two
        /// colours the marker wears. This is the game's own full-body repaint, reached through the game's
        /// own free channel.
        ///
        /// THE CHANNEL IS <c>SetSpecialShader</c>, NOT <c>Highlight</c>, AND THAT IS THE WHOLE TRICK.
        /// The white fill an ability's target wears is <c>Highlight(_, _, ability: true)</c>, and that entry
        /// is unusable here: it opens with <c>if (_isHighlighted == highlight) return false;</c> over ONE
        /// bool per body part (HighlightControllerComponent.cs:146-149) that the game's own ability
        /// targeting rides (UIStateShoot.cs:1445 sets it, :1463 clears it) — so a ping on an actor the local
        /// player is already aiming at would be swallowed, and our expiry would clear a paint the game still
        /// wants. <c>SetSpecialShader</c> is a SEPARATE, unlatched, per-actor slot on the same component
        /// (:269-293): the game itself parks a whole-body look there for a stance and for a holographic
        /// status (StanceStatus.cs:41,56; HolographicStatus.cs:30,38), sets it and hands it back with null.
        /// It composes correctly with the ability paint too — <c>SelectShader</c> takes the highlight branch
        /// while a highlight is on (:177-187) and falls back to <c>_specialMaterials</c> when it is not
        /// (:192-195), so neither of us can lose the other's visual.
        ///
        /// THE SHADER IS <c>BodypartHighlightShader</c>, NOT <c>AbilityHighlightShader</c>, BECAUSE ONLY ONE
        /// OF THEM TAKES A COLOUR. The ability look is one fixed scene asset with no colour input anywhere
        /// on its branch; the bodypart-highlight look reads <c>BodypartHighlightColor</c>, which is exactly
        /// the colour the game writes at HighlightControllerComponent.cs:169 to say friendly-or-enemy. So
        /// green-mine / blue-yours is expressible there and nowhere else.
        ///
        /// THE BLINK IS THE CHANNEL ITSELF, TOGGLED — special shader on, special shader back to whatever was
        /// there before, 4 Hz off <c>unscaledTime</c> so it keeps blinking while the game sits paused
        /// mid-turn. No coroutine and no material of ours: the component already owns both material sets and
        /// swaps between them.
        /// ponytail: <c>BodypartHighlightColor</c> is a GLOBAL shader colour, so two live pings of different
        /// colours, or a hover-highlight landing mid-ping, share one value and the last writer wins for a
        /// frame or two — re-asserted on every blink tick, which is why it settles. Per-renderer property
        /// blocks would fix it; do that if anyone ever notices.
        /// </summary>
        private static void Repaint(Live p, bool on)
        {
            // The actor can die, ragdoll or unload while its ping is still live. Unity-null on the followed
            // transform is the cheap answer; the catch covers the rest, because a throw here would unwind
            // through Update into native code (L158 arm (c)).
            if (p.Follow == null) return;
            try
            {
                if (on) Shader.SetGlobalColor("BodypartHighlightColor", p.Mine ? Own : Peer);
                p.Paint.SetSpecialShader(on ? LightingSettingsCharacters.Instance?.BodypartHighlightShader : p.PriorShader);
            }
            catch (Exception e) { Debug.LogWarning("[Multiplayer] ping paint not applied: " + e.Message); }
        }

        /// <summary>What the actor's special-shader slot held before the ping took it. Read by reflection
        /// because <c>TacticalActor._specialShader</c> (TacticalActor.cs:111) has no getter — and read
        /// LAZILY, inside a method, never into a static field: this type's static initializer must stay free
        /// of engine and game reflection or RailCheck's out-of-Unity pass over <see cref="Own"/> aborts on
        /// the type initializer (see the note on <see cref="_loggedTint"/>).</summary>
        private static Shader PriorShaderOf(object actor)
        {
            try { return AccessTools.Field(typeof(TacticalActor), "_specialShader")?.GetValue(actor) as Shader; }
            catch { return null; }
        }

        private void Blink()
        {
            var on = ((int)(Time.unscaledTime * 4f) & 1) == 0;
            foreach (var p in _live)
            {
                if (p.Paint == null || p.PaintOn == on) continue;
                p.PaintOn = on;
                Repaint(p, on);
            }
        }

        // ─── the audible half ────────────────────────────────────────────────

        /// <summary>
        /// THE NATIVE CUE. A marker that only appears is easy to miss on a busy map, so every ping that is
        /// actually SHOWN also sounds — and because <see cref="Track"/> is the one funnel all four shapes
        /// (geo point, geo object, tac point, tac object) go through, the cue lands exactly when a marker
        /// does: on every peer that received the packet, on the sender too (it calls <see cref="Show"/>
        /// locally, PingMarkers.cs:129), and never on a ping that resolved to nothing.
        ///
        /// THE SOUND IS THE GAME'S OWN "A WINDOW JUST APPEARED" EVENT, borrowed rather than authored:
        /// <c>MessageBoxPromptController.WindowShowEvent</c>, the UIEventDef the game plays through this
        /// exact call when a modal opens (<c>MessageBoxPromptController.cs:38,69</c> — the line below is
        /// that line). It is the native attention cue for "look at this", it ships no asset of ours, and
        /// going through <c>EventusManager</c> → <c>AudioManager.PlayEvent</c> (AudioManager.cs:130-148)
        /// is what makes it respect the player's own volume: those sliders are Wwise RTPCs the mixer
        /// applies globally (AudioManager.cs:186-190), so a sound that skipped this path would skip them.
        ///
        /// The def is a SCENE object, not a static, so it is looked up rather than assumed and re-looked-up
        /// whenever the cached controller dies with its scene. A ping is a handful per minute, so the scan
        /// costs nothing worth caching harder.
        /// ponytail: if a player ever wants it off, the game's UI volume slider already turns it off.
        /// </summary>
        private static void Cue()
        {
            if (_prompt == null)
                _prompt = Resources.FindObjectsOfTypeAll<MessageBoxPromptController>()
                                   .FirstOrDefault(c => c != null && c.WindowShowEvent != null);
            if (_prompt == null) return;
            GameUtl.GameComponent<EventusManager>()?.PlayEventDirect(_prompt.WindowShowEvent, _prompt.gameObject);
        }

        private static MessageBoxPromptController _prompt;

        private void Expire()
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < _live[i].Until) continue;
                if (_live[i].Beacon != null) Destroy(_live[i].Beacon);
                // Hand the special-shader slot back to whoever held it (a stance, a holographic status, or
                // nobody) — never blanket-clear it to null.
                if (_live[i].Paint != null) Repaint(_live[i], false);
                _live.RemoveAt(i);
            }
        }

        // ─── the off-screen arrow ────────────────────────────────────────────

        /// <summary>Is the cursor sitting on an arrow this frame. Read by the two patches below and by
        /// nothing else — it is what lets the GAME'S OWN guard treat the arrow as the UI it is.</summary>
        internal static bool CursorOverArrow { get; private set; }

        private void OnGUI()
        {
            // Two events, one geometry pass. IMGUI calls OnGUI several times a frame with different events;
            // the arrow's placement is a handful of floats, so it is cheaper to recompute than to cache and
            // risk a click tested against last frame's rect.
            var ev = Event.current;
            var repaint = ev.type == EventType.Repaint;
            var click = ev.type == EventType.MouseDown && ev.button == 0;
            if (!repaint && !click) return;

            var cam = MainCamera.Instance;
            if (_live.Count == 0 || cam == null)
            {
                if (repaint) CursorOverArrow = false;
                return;
            }

            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var half = Mathf.Max(ArrowHalfMin, Screen.height * ArrowHalfFraction);
            var limitX = centre.x - half - 8f;
            var limitY = centre.y - half - 8f;
            var old = GUI.matrix;
            var oldColor = GUI.color;
            var over = false;

            foreach (var p in _live)
            {
                var world = WorldOf(p);
                if (!world.HasValue) continue;

                var sp = cam.WorldToScreenPoint(world.Value);
                var behind = sp.z <= 0f;
                if (behind) { sp.x = Screen.width - sp.x; sp.y = Screen.height - sp.y; }
                // GUI space is y-down.
                var d = new Vector2(sp.x, Screen.height - sp.y) - centre;

                var scale = Mathf.Min(limitX / Mathf.Max(Mathf.Abs(d.x), 0.001f),
                                      limitY / Mathf.Max(Mathf.Abs(d.y), 0.001f));
                if (!behind && scale >= 1f && Faces(p, cam, world.Value)) continue;  // visible: the marker speaks

                var at = centre + d * Mathf.Min(scale, 1f);
                // The unrotated square the glyph is drawn inside. Hit-testing it rather than the triangle is
                // deliberate: a 60px arrow is already a small target and the corners cost nothing.
                var rect = new Rect(at.x - half, at.y - half, half * 2f, half * 2f);
                if (rect.Contains(ev.mousePosition))
                {
                    over = true;
                    if (click) { Focus(p); break; }
                }
                if (!repaint) continue;

                var left = p.Until - Time.unscaledTime;
                var colour = p.Mine ? Own : Peer;
                colour.a = Mathf.Clamp01(left / FadeSeconds);
                GUI.color = colour;
                GUIUtility.RotateAroundPivot(Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg, at);
                GUI.DrawTexture(rect, Arrow());
                GUI.matrix = old;
            }

            if (repaint) CursorOverArrow = over;
            GUI.color = oldColor;
            GUI.matrix = old;
        }

        /// <summary>
        /// THE CLICK — "take me to what he is pointing at", and the ONE place in this class allowed to move
        /// a camera. Law L160 bans a camera move on the ARRIVING side and still does; this is the opposite
        /// side of the same rule. The watcher pressed his own mouse button on a widget that exists only to
        /// say "there is something off screen": nothing here is remote-driven, and a peer who ignores the
        /// arrow keeps his camera exactly where he left it. That is also why nothing STICKS — see below.
        ///
        /// BOTH RECIPES ARE THE GAME'S OWN, copied off the call the game makes for the same intent:
        ///  · battle — <c>CameraDirector.Hint(CameraHint.ChaseTarget, new CameraChaseParams { … })</c>, which
        ///    is <c>TacticalActorViewBase.DoCameraChaseParam</c> (TacticalActorViewBase.cs:491-501) — the
        ///    method behind <c>DoCameraChase()</c>, i.e. what a portrait click already does
        ///    (<c>UIStateCharacterSelected.cs:249</c>). <c>EaseInOut</c> and <c>Instant = false</c> are its
        ///    values, so the pan is the game's own smooth one, not a lerp of ours.
        ///  · globe — <c>Hint(CameraDirectorHint.GeoscapeFocus, new GeoCamDirectorParams { … })</c> with
        ///    <c>InstantChase = false</c>, verbatim <c>GeoscapeView.ChaseTarget</c> (GeoscapeView.cs:1109-1113).
        ///
        /// <c>ChaseTransform</c> IS DELIBERATELY LEFT NULL, and <c>GeoscapeView.ChaseTarget</c> deliberately
        /// not called. Both are the same refusal: a chase that names a TRANSFORM never ends by itself
        /// (<c>PlanarScrollCamera</c>:747 can only self-end a chase whose <c>ChaseTransform</c> is null, and
        /// :838 refuses to stop a locked one), and <c>ChaseTarget</c> additionally installs the globe's sticky
        /// snap (<c>_currentChaseTarget</c>, GeoscapeView.cs:1104-1106). Either would leave the watcher's
        /// camera glued to somebody else's soldier — a camera that stops obeying its player, which is the
        /// exact defect the camera policy's closing note records from a live 3-peer session. A position is a
        /// one-shot: it arrives and it is over.
        ///
        /// NEITHER HINT IS FILTERED BY THIS MOD. The tactical one rides <c>CameraDirector.Hint(CameraHint,
        /// object)</c>:167, an unrelated overload from the patched <c>Hint(CameraDirectorHint,
        /// CameraDirectorParams)</c>:123; the geoscape one rides the patched overload but
        /// <c>TacticalCameraPolicy</c> narrows to the four action hints and lets <c>Geoscape*</c> through
        /// untouched (its own note, TacticalCameraPolicy.cs:46).
        /// </summary>
        private static void Focus(Live p)
        {
            var world = WorldOf(p);
            if (!world.HasValue)
            {
                Debug.LogWarning("[Multiplayer] ping arrow clicked but NOT followed — the ping has no world " +
                                 "position on this peer any more (the object it named is gone, or the screen " +
                                 "it was drawn for has been unloaded under it). The arrow will expire on its " +
                                 "own; nothing else is affected.");
                return;
            }

            if (p.Geo)
            {
                var director = GeoLevel()?.View?.CameraDirector;
                if (director == null)
                {
                    Debug.LogWarning("[Multiplayer] ping arrow clicked but NOT followed — the geoscape view " +
                                     "has no CameraDirector, so there is nothing to hint. This is the globe " +
                                     "being torn down mid-click.");
                    return;
                }
                director.Hint(CameraDirectorHint.GeoscapeFocus, new GeoCamDirectorParams
                {
                    TargetPosition = world.Value,
                    Unmanaged = true,
                    InstantChase = false,
                });
            }
            else
            {
                var director = Tlc()?.View?.CameraDirector;
                if (director == null)
                {
                    Debug.LogWarning("[Multiplayer] ping arrow clicked but NOT followed — the tactical view " +
                                     "has no CameraDirector, so there is nothing to hint. This is the battle " +
                                     "being torn down mid-click.");
                    return;
                }
                director.Hint(CameraHint.ChaseTarget, new CameraChaseParams
                {
                    ChaseVector = world.Value,
                    ChaseCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f),
                });
            }

            Debug.Log("[Multiplayer] ping arrow CLICKED — centring this peer's own camera on the " +
                      (p.Follow != null ? "pinged object" : "pinged point") + " at " + world.Value + " on the " +
                      (p.Geo ? "geoscape" : "battlefield") + " (" + (p.Mine ? "own" : "peer") + " ping). " +
                      "Nobody else's camera moves.");
        }

        /// <summary>The ping's world position this frame, or null when its screen is not the live one (a
        /// battle started, the geoscape unloaded) or the object it followed is gone.</summary>
        private static Vector3? WorldOf(Live p)
        {
            if (p.Follow != null) return p.Follow.position;
            if (!p.Geo) return Tlc() == null ? (Vector3?)null : p.Local;
            var root = GlobeRoot(GeoLevel());
            return root == null ? (Vector3?)null : root.TransformPoint(p.Local);
        }

        /// <summary>Geoscape only: a point on the FAR side of the globe projects to an on-screen pixel and
        /// is still invisible, so "inside the viewport" is not the whole visibility question there. The
        /// globe's own centre answers it with one dot product.</summary>
        private static bool Faces(Live p, Camera cam, Vector3 world)
        {
            if (!p.Geo) return true;
            var root = GlobeRoot(GeoLevel());
            if (root == null) return true;
            var centre = root.position;
            return Vector3.Dot(world - centre, cam.transform.position - centre) > 0f;
        }

        /// <summary>
        /// THE GLYPH, BUILT ONCE AND CACHED — and built BIG, because the aliasing was in the SOURCE and no
        /// draw-side setting could have fixed it.
        ///
        /// At 32 px this texture was drawn at <c>half*2</c> = 144 px on a 1440p screen (see
        /// <see cref="ArrowHalfMin"/>/<see cref="ArrowHalfFraction"/>), a 4.5x UPSCALE, and every edge pixel
        /// was fully opaque or fully clear — a binary in/out test leaves bilinear filtering nothing but 0s
        /// and 255s to interpolate between, so the 1-px steps survived at full size and
        /// <c>GUIUtility.RotateAroundPivot</c> turned them into visible diagonal stairs.
        ///
        /// Two changes, both on the source: 256 px, so the same draw is a DOWNSCALE (which bilinear handles
        /// cleanly at any resolution the game runs at), and a COVERAGE RAMP into the alpha instead of the
        /// in/out test — how much of the pixel the triangle's edge actually covers, which is the
        /// anti-aliasing the rotation then resamples. One ~256 KB allocation for the whole run; the cache
        /// above is what keeps it out of the per-frame draw.
        /// </summary>
        private static Texture2D Arrow()
        {
            if (_arrow != null) return _arrow;
            const int n = 256;
            var tex = new Texture2D(n, n, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[n * n];
            // WHITE, not the amber it used to be: the glyph is now tinted per ping through GUI.color, and
            // GUI.color MULTIPLIES. Amber times the peer blue is olive, not blue — a white glyph is the only
            // one that renders the two owner colours as themselves. Only the ALPHA carries the shape.
            for (var y = 0; y < n; y++)
            {
                // Apex at the texture's TOP row, which GUI.DrawTexture draws at the rect's top — so the
                // untransformed glyph points "up" and the rotation below is the only aiming step.
                var width = (n - 1 - y) * 0.4f;
                for (var x = 0; x < n; x++)
                {
                    // 1 well inside the triangle, 0 well outside, and the fraction the edge covers between —
                    // the +0.5f is the pixel's own half-width, so the ramp is centred on the true edge.
                    var a = Mathf.Clamp01(width - Mathf.Abs(x - (n - 1) * 0.5f) + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            _arrow = tex;
            return tex;
        }

        // ─── level accessors (same shape as TacticalDamageSync.Tlc) ──────────

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        private static TacticalLevelController Tlc()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<TacticalLevelController>();
        }

        private static Transform GlobeRoot(GeoLevelController geo) => geo?.SceneReferences?.GeoscapeYRotation;
    }

    /// <summary>
    /// THE ARROW IS UI, AND THE GAME HAS TO KNOW IT — battle half.
    ///
    /// An IMGUI rect is invisible to <c>EventSystem.IsPointerOverGameObject</c>, because it is not a
    /// GameObject. Without this the click that follows a ping ALSO reaches the world underneath it: the
    /// tactical states take their world clicks in <c>TacticalViewState.Update</c> (:47-70) behind exactly one
    /// gate, <c>if (!Context.View.IsCursorOverGUI())</c>, so a click on an arrow at the screen edge would
    /// also order the selected soldier to walk there. Losing a move because you looked at a ping is a worse
    /// bug than the one the arrow fixes.
    ///
    /// SO THE GAME'S OWN GATE IS TOLD THE TRUTH rather than a gate of ours being invented: the arrow IS
    /// cursor-over-GUI while the cursor is on it, and every caller of that method — states, cameras, the
    /// square-selection drag — inherits the answer for free. <c>Event.current.Use()</c> would NOT have done
    /// it: the game reads the mouse through Rewired, which never sees an IMGUI event as consumed.
    ///
    /// ONE FRAME OF LAG, ON PURPOSE. <c>CursorOverArrow</c> is written in OnGUI, which runs after Update, so
    /// the gate reads a flag computed at the end of the previous frame. Hovering precedes clicking by many
    /// frames in every real input, and the alternative — guessing this component's Update order against the
    /// view's — is the kind of ordering assumption that breaks silently.
    /// </summary>
    [HarmonyPatch(typeof(TacticalView), nameof(TacticalView.IsCursorOverGUI))]
    internal static class PingArrowIsGuiInBattle
    {
        private static void Postfix(ref bool __result)
        {
            if (PingMarkers.CursorOverArrow) __result = true;
        }
    }

    /// <summary>The globe half of <see cref="PingArrowIsGuiInBattle"/>, over the geoscape's own spelling of
    /// the same method (<c>GeoscapeView.IsCursorOverGui</c>, GeoscapeView.cs:905 — lower-case "ui", a
    /// different member from the tactical one, not an override). It gates the globe's own picking
    /// (<c>SelectActorAtCursor</c>:909-915) and the geoscape camera's drag/edge-scroll
    /// (<c>GeoscapeCamera.cs:187,268-286</c>), so without it a click on an arrow both follows the ping AND
    /// starts spinning the globe under it.</summary>
    [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.IsCursorOverGui))]
    internal static class PingArrowIsGuiOnTheGlobe
    {
        private static void Postfix(ref bool __result)
        {
            if (PingMarkers.CursorOverArrow) __result = true;
        }
    }
}
