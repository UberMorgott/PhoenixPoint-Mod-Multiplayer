using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Reflection bridge for the GEOSCAPE CUTSCENE mirror. The mod has NO compile-time game references in the sync
    /// layer, so the single native playback entrypoint is resolved by name and cached (bind-once per session).
    ///
    /// Verified against the decompile (2026-07-05):
    ///   • <c>GeoLevelController.View</c> (public field) → <c>GeoscapeView</c>.
    ///   • <c>GeoscapeView.ToCutsceneState(Base.UI.VideoPlayback.VideoPlaybackSourceDef cutsceneDef, int priority)</c>
    ///     (GeoscapeView.cs:672) — THE single geoscape cutscene entry (reward outcome / research-complete /
    ///     marketplace all route through it). It pushes a <c>UIStateGeoCutscene</c> view state (pure UI video
    ///     playback, no sim mutation) → safe to drive on a frozen client.
    ///   • The def identity travels as <c>VideoPlaybackSourceDef.Guid</c> (via <see cref="DefReflection"/>), resolved
    ///     on each peer through the shared <c>DefRepository</c>.
    /// All reflection is null-safe / best-effort — a miss no-ops (never throws into game code).
    /// </summary>
    public static class CutsceneReflection
    {
        private static bool _ready;
        private static Type _videoDefType;      // Base.UI.VideoPlayback.VideoPlaybackSourceDef
        private static FieldInfo _viewField;    // GeoLevelController.View
        private static MethodInfo _toCutscene;  // GeoscapeView.ToCutsceneState(VideoPlaybackSourceDef, int)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geoType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            _videoDefType = AccessTools.TypeByName("Base.UI.VideoPlayback.VideoPlaybackSourceDef");
            if (geoType == null || viewType == null || _videoDefType == null) return;

            _viewField = AccessTools.Field(geoType, "View");
            // EXACT param match (harmony-accesstools-exact-param-match): (VideoPlaybackSourceDef, int).
            _toCutscene = AccessTools.Method(viewType, "ToCutsceneState", new[] { _videoDefType, typeof(int) });

            _ready = _viewField != null && _toCutscene != null;
        }

        /// <summary>Is the def-type binding available? (Used by the host broadcast patch to skip cheaply when the
        /// engine cutscene type is absent.)</summary>
        public static bool IsAvailable(GeoRuntime rt)
        {
            try { Ensure(rt); return _ready; } catch { return false; }
        }

        /// <summary>CLIENT: resolve the <c>VideoPlaybackSourceDef</c> by guid and play it through the vehicle's own
        /// native <c>GeoscapeView.ToCutsceneState(def, priority)</c> — identical playback to the host. No-op on any
        /// miss (unresolved guid / no geoscape view / unbound member).</summary>
        public static void PlayGeoscapeCutscene(GeoRuntime rt, string cutsceneGuid, int priority)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return;
                var def = DefReflection.GetDefByGuid(cutsceneGuid);
                if (def == null || !_videoDefType.IsInstanceOfType(def))
                {
                    Debug.LogWarning("[Multiplayer][geo] cutscene mirror: def " + cutsceneGuid + " unresolved / wrong type (skip)");
                    return;
                }
                var geo = rt?.GeoLevel();
                if (geo == null) return;
                var view = _viewField.GetValue(geo);
                if (view == null) return;

                _toCutscene.Invoke(view, new object[] { def, priority });
                Debug.Log("[Multiplayer][geo] cutscene mirror: played " + cutsceneGuid + " priority=" + priority);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] CutsceneReflection.PlayGeoscapeCutscene failed: " + ex.Message); }
        }
    }
}
