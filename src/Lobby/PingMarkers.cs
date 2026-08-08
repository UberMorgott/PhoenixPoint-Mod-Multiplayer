using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Base;
using Base.Core;
using Base.Eventus;
using Base.UI.MessageBox.PromptControllers;
using Base.Utils;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;
using PhoenixPoint.Geoscape.Entities;
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
    /// NATIVE VISUALS ONLY, AND THE MOD OWNS EVERY INSTANCE. Geoscape: the game's own
    /// <c>SitePointOfInterest</c> marker prefab, taken from <c>GeoscapeGlobeMarkers.Binds</c> via
    /// <c>GetMarkerPrefab</c>. Tactical: the <c>LocatedBeaconPrefab</c> shaft (<c>TacticalView.cs:83</c>)
    /// the game already raises over a heard-but-unseen actor, i.e. the native "something is HERE" idiom.
    /// AND A NATIVE SOUND — a silent marker is easy to miss, so every shown ping also plays the game's own
    /// modal-appears cue through the game's own audio path; see <see cref="Cue"/>.
    ///
    /// WHY NOT <c>GeoscapeGlobeMarkers.AddMarker</c>, WHICH THIS USED TO CALL AND WHICH DREW NOTHING (fixed
    /// 2026-08-08, law L182). <c>AddMarker</c> has two branches and only ONE of them activates the marker:
    /// the POOLED branch calls <c>gameObject.SetActive(true)</c> (GeoscapeGlobeMarkers.cs:72 point, :101
    /// actor), the FRESH-INSTANTIATE branch (:62-66) does not. <c>SitePointOfInterest</c> has no pooled
    /// instance sitting in <c>MarkersContainer</c> when a ping lands, so every ping took the fresh branch
    /// and got back a marker that was never switched on. <c>AddMarker</c> RETURNED NORMALLY — which is why
    /// the cue, which fires downstream of it in <see cref="Track"/>, was audible over an empty globe. The
    /// game itself never hits that hole: it is the only caller and it explicitly activates the prefabs it
    /// instantiates by hand (<c>AncientSiteProbeAbilityView.cs:88 SetActive(true)</c>,
    /// <c>UIStateVehicleSelected.cs:728</c>, <c>TacticalActorViewBase.cs:449</c>).
    ///
    /// So the marker is instantiated here, by the game's OWN free-standing recipe — the one
    /// <c>AncientSiteProbeAbilityView</c> uses for its probe preview, verbatim: instantiate the prefab
    /// under a geoscape container, <c>SetActive(true)</c>, then place it by ROTATION ALONE,
    /// <c>transform.rotation = Quaternion.Euler(SceneReferences.GetSphericalCoordinates(worldPos))</c>
    /// (AncientSiteProbeAbilityView.cs:87-89). Rotation alone is not a shortcut: every globe object is a
    /// CENTRE-pivot whose art hangs off a child at globe radius (<c>GeoActor.PivotTransform</c> +
    /// <c>Surface = PivotTransform.Find("GlobeOffset")</c>, GeoAncientSiteProbe.cs:44), which is also why
    /// <c>GlobeMarker.SetWorldPosition</c> (GlobeMarker.cs:70-74) writes a rotation and never a position.
    /// Owning the instance is what buys the two things the pool could not give: the expiry is ours (one
    /// <see cref="Expire"/> for both screens) and so is the COLOUR (see <see cref="Tint"/>).
    ///
    /// THE OFF-SCREEN ARROW IS OURS, and the recon's <c>UIObjectTracker.KeepOnScreen</c> route is NOT
    /// usable: <c>UIObjectTrackersController.LateUpdate</c>'s <c>shouldUpdateTracker</c>
    /// (<c>UIObjectTrackersController.cs:204-218</c>) frustum-culls a tracker and calls
    /// <c>EnableVisuals(false)</c> on it — a tracked point that is off screen is HIDDEN, which is precisely
    /// the case the arrow exists for. So the clamp is not native after all, and OnGUI draws it here.
    /// ponytail: ~40 lines of IMGUI; if it ever needs to sit under the game's canvas, port it then.
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
        }

        // ─── lifecycle ───────────────────────────────────────────────────────

        private void Awake() => _instance = this;

        private void OnDestroy()
        {
            foreach (var p in _live) if (p.Beacon != null) Destroy(p.Beacon);
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
                var pick = view.SelectAtCursor();          // pure query — selects nothing
                if (pick.Actor != null)
                {
                    var re = IdentityResolver.RootRef(pick.Actor);
                    if (re != null) return Encode(SceneGeo, KindObject, Vector3.zero, re, 0);
                }
                if (float.IsNaN(pick.GlobePos.x)) return null;
                var root = GlobeRoot(geo);
                if (root == null) return null;
                // Globe-LOCAL, never world: GeoscapeYRotation spins under the camera, so a world point
                // means a different place on every peer (GeoSceneReferences.cs:100-109 is the game's own
                // proof — it inverse-transforms through that root to get lat/long).
                return Encode(SceneGeo, KindPoint, root.InverseTransformPoint(pick.GlobePos), null, 0);
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
            var markers = geo?.View?.Markers;
            var root = GlobeRoot(geo);
            if (markers == null || root == null || refs?.MarkersContainer == null)
            {
                // Not an error — a peer on another screen legitimately has no globe to draw on — but it is
                // the difference between "he is in a battle" and "the ping broke", so it says which.
                Debug.LogWarning("[Multiplayer] geoscape ping DROPPED — no live globe to draw it on (level=" +
                                 (geo == null ? "not geoscape" : "geoscape") + ", view=" +
                                 (geo?.View == null ? "none" : "live") + ", markers=" +
                                 (markers == null ? "none" : "live") + ", globe root=" +
                                 (root == null ? "none" : "live") + ", container=" +
                                 (refs?.MarkersContainer == null ? "none" : "live") + "). The sender still " +
                                 "saw his own marker.");
                return;
            }

            // The game's own POI marker, taken from the game's own bind table. If the type is unbound there
            // is no native widget for this and we do NOT draw a substitute — a hand-rolled overlay is
            // exactly what "native UI first" forbids, and a named log line is worth more than a stray quad.
            var prefab = markers.GetMarkerPrefab(GlobeMarkerType.SitePointOfInterest);
            if (prefab == null)
            {
                Debug.LogWarning("[Multiplayer] geoscape ping DROPPED — GeoscapeGlobeMarkers.Binds names no " +
                                 "prefab for SitePointOfInterest, so the game ships no widget to show. Nothing " +
                                 "is drawn on purpose; the fix is a different bound GlobeMarkerType, not an " +
                                 "overlay of ours.");
                return;
            }

            Transform follow = null;
            Transform parent;
            if (kind == KindObject)
            {
                var actor = IdentityResolver.Resolve(geo, entityRef, null) as GeoActor;
                if (actor == null)
                {
                    Debug.LogWarning("[Multiplayer] ping names '" + entityRef + "', which does not resolve on " +
                                     "this peer — nothing to point at.");
                    return;
                }
                // Under the actor's own PIVOT, at local zero — the same frame AddMarker(actor, …) parents
                // into (GeoscapeGlobeMarkers.cs:92/:99). The pivot is at the globe centre and already
                // carries the actor's lat/long as its rotation, so the marker needs no placement of its
                // own and follows a moving aircraft for free.
                parent = actor.transform;
                follow = actor.transform;
            }
            else
            {
                parent = refs.MarkersContainer;
            }

            var go = Instantiate(prefab, parent);
            if (kind == KindPoint)
                go.transform.rotation = Quaternion.Euler(refs.GetSphericalCoordinates(root.TransformPoint(local)));
            // THE LINE AddMarker's fresh-instantiate branch is missing, and the whole reason nothing was
            // ever visible. The game writes it by hand at every one of its own instantiate sites.
            go.SetActive(true);
            Tint(go, mine);
            _instance.Track(new Live { Geo = true, Follow = follow, Local = local, Beacon = go });
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
                // Parented to the actor, exactly as RefreshLocatedBeacon:439-449 does it, so the shaft
                // travels with him. Deliberately NOT IHighlightable.Highlight: that is a shared boolean
                // with a global shader colour (HighlightControllerComponent.cs:146-149,169), so a ping on
                // an actor the local player already has highlighted is swallowed on the way in and the
                // expiry then clears a highlight the game still wants — silently.
                var beacon = Instantiate(prefab, actor.transform);
                beacon.transform.ResetTransform();
                // SetActive, exactly as RefreshLocatedBeacon:449 does on the line after its own
                // Instantiate+ResetTransform pair. The geoscape half proved what happens when an
                // instantiated native prefab is assumed to arrive switched on.
                beacon.SetActive(true);
                Tint(beacon, mine);
                _instance.Track(new Live { Follow = actor.transform, Beacon = beacon });
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
            _instance.Track(new Live { Local = pos, Beacon = stand });
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
                block.SetColor(ColorId, colour);
                block.SetColor(TintColorId, colour);
                r.SetPropertyBlock(block);
                var mat = r.sharedMaterial;
                if (seen != null)
                    seen.Add(r.gameObject.name + "<" + (mat == null || mat.shader == null ? "no-shader" : mat.shader.name) + ">");
            }
            if (seen == null) return;
            _loggedTint = true;
            Debug.Log("[Multiplayer] ping marker tinted " + (mine ? "OWN green" : "PEER blue") +
                      " over _Color/_TintColor on " + seen.Count + " renderer(s): " +
                      string.Join(", ", seen.ToArray()) + " (logged once per run). A count of 0, or a marker " +
                      "still drawing in its stock colour, means the visual is not a Renderer or does not " +
                      "declare either property — the shader names above are what to write instead.");
        }

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
        private static bool _loggedTint;

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
                _live.RemoveAt(i);
            }
        }

        // ─── the off-screen arrow ────────────────────────────────────────────

        private void OnGUI()
        {
            if (_live.Count == 0 || Event.current.type != EventType.Repaint) return;
            var cam = MainCamera.Instance;
            if (cam == null) return;

            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var half = Mathf.Max(12f, Screen.height * 0.02f);
            var limitX = centre.x - half - 8f;
            var limitY = centre.y - half - 8f;
            var old = GUI.matrix;
            var oldColor = GUI.color;

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
                var left = p.Until - Time.unscaledTime;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(left / FadeSeconds));
                GUIUtility.RotateAroundPivot(Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg, at);
                GUI.DrawTexture(new Rect(at.x - half, at.y - half, half * 2f, half * 2f), Arrow());
                GUI.matrix = old;
            }

            GUI.color = oldColor;
            GUI.matrix = old;
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

        private static Texture2D Arrow()
        {
            if (_arrow != null) return _arrow;
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[n * n];
            var solid = new Color32(255, 208, 64, 255);
            var clear = new Color32(0, 0, 0, 0);
            for (var y = 0; y < n; y++)
            {
                // Apex at the texture's TOP row, which GUI.DrawTexture draws at the rect's top — so the
                // untransformed glyph points "up" and the rotation below is the only aiming step.
                var width = (n - 1 - y) * 0.4f;
                for (var x = 0; x < n; x++)
                    px[y * n + x] = Mathf.Abs(x - (n - 1) * 0.5f) <= width ? solid : clear;
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
}
