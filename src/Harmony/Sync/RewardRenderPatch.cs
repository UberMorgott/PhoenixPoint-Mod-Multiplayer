using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
using UnityEngine;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// CLIENT-only deterministic reward-card render hook. The host's geoscape-event RESULT card draws the
    /// structured reward delta lines via the native <c>UIModuleSiteEncounters.ShowReward</c>; the client mirrors
    /// them by replaying the host-formatted lines through the SAME module's native <c>AddRewardText</c> — NEVER
    /// by re-applying the reward (the host already applied it; the totals ride the state channels).
    ///
    /// The lines must be appended AFTER the result page's text container is (re)built — the native
    /// <c>UIModuleSiteEncounters.ShowEncounter(GeoscapeEvent)</c> (decompile
    /// <c>...View.ViewModules\UIModuleSiteEncounters.cs:194</c>) clears + repopulates that container. So we
    /// Postfix exactly that method: when it finishes building OUR synthetic result page, we append the reward
    /// lines onto <c>__instance</c> (guaranteed the correct, freshly-built module). This replaces the earlier
    /// frame-delay heuristic with an event-driven trigger — renders onto exactly the right page, exactly once.
    ///
    /// Correlation: the synthetic result <c>GeoscapeEvent</c> carries EventID="" (it must, so it is never
    /// re-broadcast/re-keyed), so it cannot be matched by id. Instead <see cref="RewardDisplayReflection"/> arms
    /// a one-shot slot keyed to the exact synthetic event INSTANCE (set in <c>SyncEngine.OnEventDismiss</c>
    /// right before <c>ShowResult</c>); the Postfix consumes it by REFERENCE IDENTITY. The original
    /// host-broadcast choice dialog (also a <c>ShowEncounter</c> call) never matches → its render never fires;
    /// consumption is one-shot → no double-render; a stale armed reference can never match a later event.
    ///
    /// Client-only + best-effort try/catch — never throws into game code (mirrors <c>EventRaisedDisplayPatch</c>).
    /// </summary>
    [HarmonyPatch]
    public static class RewardRenderPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var moduleT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var evtT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            if (moduleT == null || evtT == null) return false;
            _target = AccessTools.Method(moduleT, "ShowEncounter", new[] { evtT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the UIModuleSiteEncounters that just built the page; geoEvent = the event it built.
        public static void Postfix(object __instance, object geoEvent)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return; // client only

                // Consume ONLY if geoEvent is our armed synthetic result event (reference identity). Returns null
                // (no-op) for the original choice dialog or any unrelated event.
                var reward = RewardDisplayReflection.TryConsume(geoEvent);
                if (reward == null) return;

                Debug.Log("[Multipleer] RewardRenderPatch: synthetic result page built → rendering reward lines");
                RewardDisplayReflection.Render(GeoRuntime.Instance, __instance, reward);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] RewardRenderPatch.Postfix failed: " + ex.Message); }
        }
    }
}
