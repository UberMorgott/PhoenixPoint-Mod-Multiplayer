using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
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
                string id = ResearchReflection.GetId(research);
                if (string.IsNullOrEmpty(id)) return true; // can't identify → fail open to vanilla
                var action = new StartResearchAction(id);
                if (engine.IsHost) { engine.Sync.BroadcastHostAction(action); return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] AddResearchToQueuePatch failed: " + ex.Message);
                return true;
            }
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

        public static bool Prefix(object research)
        {
            if (SyncApplyScope.IsApplying) return true; // engine-driven replay → let it complete
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!engine.IsHost) return false; // client: host drives completion; suppress self-completion

            try
            {
                string id = ResearchReflection.GetId(research);
                if (!string.IsNullOrEmpty(id))
                    engine.Sync.BroadcastHostAction(new ResearchCompletedAction(id));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] CompleteResearchPatch failed: " + ex.Message);
            }
            return true; // host runs the real completion
        }
    }
}
