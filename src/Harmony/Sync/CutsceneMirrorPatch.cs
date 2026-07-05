using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Mirrors GEOSCAPE narrative CUTSCENES from the authoritative host to every client. Patches the single native
    /// chokepoint <c>GeoscapeView.ToCutsceneState(VideoPlaybackSourceDef cutsceneDef, int priority)</c>
    /// (GeoscapeView.cs:672) through which ALL geoscape cutscenes flow — exploration/event reward outcomes
    /// (<c>GeoFactionReward.Apply</c> :264), research-complete (:2114), the marketplace. Reward application is
    /// host-authoritative and the client geoscape sim is frozen + events suppressed, so the client never runs the
    /// reward apply → the story cutscene used to be host-only video. This Postfix, on the HOST, broadcasts a
    /// <see cref="PlayCutsceneAction"/> carrying the def guid + priority; the client resolves the SAME
    /// <c>VideoPlaybackSourceDef</c> and drives its own <c>ToCutsceneState</c> (see <see cref="CutsceneReflection"/>).
    ///
    /// Single-chokepoint design (smallest native mechanism): one patch mirrors every geoscape cutscene rather than
    /// hooking each producer (reward / research / marketplace). No double-play — the host plays natively and only
    /// broadcasts; the client has no local cutscene originator (its mirrored modal DialogCallbacks are nulled) and
    /// plays solely from the mirror. Guards live in the pure <see cref="CutsceneBroadcastDecision"/>: host + active
    /// session only; the <c>SyncApplyScope.IsApplying</c> skip applies ONLY on the non-host (client mirror-driven
    /// replay never re-broadcasts) — on the HOST a cutscene fired synchronously INSIDE a relayed action apply
    /// (relayed explore → instant SiteExplored → reward → ToCutsceneState, all under SyncApplyScope.Enter,
    /// SyncEngine.cs:208) MUST still broadcast. Reflective target (Prepare returns false → PatchAll skips) so an
    /// engine rename never bombs. Best-effort.
    /// </summary>
    [HarmonyPatch]
    public static class CutsceneMirrorPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var viewT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var videoDefT = AccessTools.TypeByName("Base.UI.VideoPlayback.VideoPlaybackSourceDef");
            if (viewT == null || videoDefT == null) return false;
            _target = AccessTools.Method(viewT, "ToCutsceneState", new[] { videoDefT, typeof(int) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = VideoPlaybackSourceDef cutsceneDef (typed object to avoid a hard game ref); __1 = int priority.
        public static void Postfix(object __0, int __1)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                // Pure decision (unit-tested): host+active only; IsApplying suppresses ONLY the non-host (client
                // mirror replay). A host cutscene inside a relayed action apply (IsApplying true) still broadcasts.
                if (!CutsceneBroadcastDecision.ShouldBroadcast(
                        engine != null && engine.IsHost,
                        engine != null && engine.IsActiveSession,
                        SyncApplyScope.IsApplying)) return;
                if (__0 == null) return;

                string guid = DefReflection.GetGuid(__0);
                if (string.IsNullOrEmpty(guid)) return;   // can't identify the def on the wire → skip

                engine.Sync?.BroadcastHostAction(new PlayCutsceneAction(guid, __1));
                Debug.Log("[Multiplayer][geo] host cutscene → broadcast mirror guid=" + guid + " priority=" + __1);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CutsceneMirrorPatch postfix failed: " + ex.Message); }
        }
    }
}
