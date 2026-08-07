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
    /// NATIVE VISUALS ONLY. Geoscape: <c>GeoscapeGlobeMarkers.AddMarker</c> (:54 point, :82 actor) with the
    /// game's OWN 5 s expiry timer — no timer of ours. Tactical: the <c>LocatedBeaconPrefab</c> shaft
    /// (<c>TacticalView.cs:83</c>) the game already raises over a heard-but-unseen actor, i.e. the native
    /// "something is HERE" idiom, instantiated exactly as <c>TacticalActorViewBase.RefreshLocatedBeacon</c>
    /// :439-449 does it. AND A NATIVE SOUND — a silent marker is easy to miss, so every shown ping also
    /// plays the game's own modal-appears cue through the game's own audio path; see <see cref="Cue"/>.
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
        /// <summary>Marker lifetime. Passed verbatim to the geoscape's own expiry timer, and the deadline
        /// for the beacon and the arrow this class owns.</summary>
        public const float LifetimeSeconds = 5f;

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
            /// <summary>The beacon shaft this class owns and must destroy (tactical only; the geoscape
            /// marker is pooled and expired by the game).</summary>
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
            Show(payload);
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
            try { ShowCore(payload); }
            catch (Exception e) { Debug.LogWarning("[Multiplayer] ping marker not shown: " + e.Message); }
        }

        private static void ShowCore(byte[] payload)
        {
            if (_instance == null || payload == null || payload.Length < 2) return;

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

            if (scene == SceneGeo) ShowGeo(kind, pos, entityRef);
            else ShowTac(kind, pos, actorKey);
        }

        private static void ShowGeo(byte kind, Vector3 local, string entityRef)
        {
            var geo = GeoLevel();
            var markers = geo?.View?.Markers;
            var root = GlobeRoot(geo);
            if (markers == null || root == null) return;

            if (kind == KindObject)
            {
                var actor = IdentityResolver.Resolve(geo, entityRef, null) as GeoActor;
                if (actor == null)
                {
                    Debug.LogWarning("[Multiplayer] ping names '" + entityRef + "', which does not resolve on " +
                                     "this peer — nothing to point at.");
                    return;
                }
                markers.AddMarker(actor, GlobeMarkerType.SitePointOfInterest, LifetimeSeconds);
                _instance.Track(new Live { Geo = true, Follow = actor.transform });
                return;
            }

            markers.AddMarker(root.TransformPoint(local), GlobeMarkerType.SitePointOfInterest, LifetimeSeconds);
            _instance.Track(new Live { Geo = true, Local = local });
        }

        private static void ShowTac(byte kind, Vector3 pos, int actorKey)
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
                _instance.Track(new Live { Follow = actor.transform, Beacon = beacon });
                return;
            }

            // Free-standing shaft under the special-markers root, as OverwatchStatus.cs:102 parents its
            // cones. Not a GroundMarker: the only group ClearGroundMarkers() spares is Tutorial, and
            // borrowing it would put our marker in the same bag as a running tutorial's.
            var stand = Instantiate(prefab, view.Markers.SpecialMarkersRoot);
            stand.transform.ResetTransform();
            stand.transform.position = pos;
            _instance.Track(new Live { Local = pos, Beacon = stand });
        }

        private void Track(Live p)
        {
            p.Until = Time.unscaledTime + LifetimeSeconds;
            _live.Add(p);
            Cue();
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
