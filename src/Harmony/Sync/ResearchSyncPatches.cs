using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
using Multipleer.Network.Sync.State;
using UnityEngine;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// Relay interceptor for research START: <c>Research.AddResearchToQueue(ResearchElement)</c>
    /// (Research.cs:370). Client clicks → relay to host + block local; host → broadcast + run.
    /// Engine-driven replay (SyncApplyScope) and single-player play pass straight through.
    /// </summary>
    [HarmonyPatch]
    public static class AddResearchToQueuePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
            var elem = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");
            if (t == null || elem == null) return false;
            _target = AccessTools.Method(t, "AddResearchToQueue", new[] { elem });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // research = the ResearchElement being queued.
        // __state carries the host action to broadcast AFTER the original succeeds (Postfix).
        public static bool Prefix(object research, out ISyncedAction __state)
        {
            __state = null;
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!PermissionGate.Check(ActionCategory.Research))
            {
                PermissionGate.Notify(ActionCategory.Research);
                return false;
            }

            try
            {
                string id = ResearchReflection.GetId(research);
                if (string.IsNullOrEmpty(id)) return true; // can't identify → fail open to vanilla
                var action = new StartResearchAction(id);
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost) { __state = action; return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] AddResearchToQueuePatch failed: " + ex.Message);
                return true;
            }
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed start.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try
            {
                var sync = NetworkEngine.Instance?.Sync;
                sync?.BroadcastHostAction(__state);
                // A host-local add of a NON-current queue item does not fire OnResearchStarted (StartedResearch
                // runs only when research == Current), so the faction-event ch2-dirty subscription misses it.
                // And StartResearchAction is now IHostOnlyApply → the client no longer applies the broadcast,
                // so the ResearchChannel echo (ch2) is the ONLY path that reaches the client. Mark ch2 dirty so
                // every host add echoes via the channel regardless of whether it became Current.
                sync?.MarkChannelDirty(2);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] AddResearchToQueuePatch postfix broadcast failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Relay interceptor for research COMPLETION: <c>Research.CompleteResearch(ResearchElement)</c>
    /// (Research.cs:576). Host → broadcast completion + run original; client → suppress local
    /// self-completion (host's broadcast re-completes it under SyncApplyScope).
    /// </summary>
    [HarmonyPatch]
    public static class CompleteResearchPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
            var elem = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");
            if (t == null || elem == null) return false;
            _target = AccessTools.Method(t, "CompleteResearch", new[] { elem });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __state carries the completion action snapshotted in Prefix; broadcast in Postfix only if the
        // original CompleteResearch returns normally (a thrown original skips the Postfix → no false echo).
        public static bool Prefix(object research, out ISyncedAction __state)
        {
            __state = null;
            if (SyncApplyScope.IsApplying) return true; // engine-driven replay → let it complete
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!engine.IsHost) return false; // client: host drives completion; suppress self-completion

            try
            {
                string id = ResearchReflection.GetId(research);
                if (!string.IsNullOrEmpty(id))
                    __state = new ResearchCompletedAction(id);   // broadcast on success in Postfix
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] CompleteResearchPatch failed: " + ex.Message);
            }
            return true; // host runs the real completion
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed completion.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multipleer] CompleteResearchPatch postfix broadcast failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Relay interceptor for research CANCEL / remove-from-queue: <c>Research.Cancel(ResearchElement)</c>
    /// (Research.cs:461 — the exact method the UI cancel button calls, UIModuleResearch.cs:435). The
    /// client is frozen, so its local cancel must reach the host. Client → relay
    /// <see cref="CancelResearchAction"/> + block local; host → run original AND mark the research
    /// channel dirty (no faction-level cancel event exists, so the channel echo is what propagates the
    /// cancel to every peer). Engine-driven apply (SyncApplyScope) and single-player pass straight through.
    /// </summary>
    [HarmonyPatch]
    public static class CancelResearchPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
            var elem = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");
            if (t == null || elem == null) return false;
            _target = AccessTools.Method(t, "Cancel", new[] { elem });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // research = the ResearchElement being cancelled / removed from queue.
        public static bool Prefix(object research)
        {
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!PermissionGate.Check(ActionCategory.Research))
            {
                PermissionGate.Notify(ActionCategory.Research);
                return false;
            }

            try
            {
                string id = ResearchStateReflection.GetId(research);
                if (engine.IsHost)
                {
                    // Host runs the real cancel; mark the channel dirty so the next Tick echoes the
                    // new authoritative queue (there is no faction-level cancel event to do it for us).
                    engine.Sync?.MarkChannelDirty(2);
                    return true;
                }
                if (string.IsNullOrEmpty(id)) return false; // client can't identify → still suppress (frozen)
                engine.Sync.SendActionRequest(new CancelResearchAction(id));
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] CancelResearchPatch failed: " + ex.Message);
                return true;
            }
        }
    }
}
