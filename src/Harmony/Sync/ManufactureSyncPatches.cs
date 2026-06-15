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
    /// Relay interceptor for manufacturing START: <c>ItemManufacturing.ManufactureItem(ManufacturableItem)</c>
    /// (:169). Client → relay + block local; host → broadcast + run.
    /// </summary>
    [HarmonyPatch]
    public static class ManufactureItemPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemManufacturing");
            var mItem = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ManufacturableItem");
            if (t == null || mItem == null) return false;
            _target = AccessTools.Method(t, "ManufactureItem", new[] { mItem });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // item = the ManufacturableItem being queued.
        public static bool Prefix(object item)
        {
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!PermissionGate.Check(ActionCategory.Manufacturing))
            {
                PermissionGate.Notify(ActionCategory.Manufacturing);
                return false;
            }

            try
            {
                string id = ManufactureReflection.GetItemId(item);
                if (string.IsNullOrEmpty(id)) return true;
                var action = new QueueManufactureAction(id);
                if (engine.IsHost) { engine.Sync.BroadcastHostAction(action); return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] ManufactureItemPatch failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Relay interceptor for manufacturing COMPLETION:
    /// <c>ItemManufacturing.FinishManufactureItem(ManufactureQueueItem)</c> (:479, private).
    /// Host → broadcast completion (capturing the queue index before the original removes it) + run;
    /// client → suppress local self-completion (host drives it under SyncApplyScope).
    /// </summary>
    [HarmonyPatch]
    public static class FinishManufactureItemPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemManufacturing");
            if (t == null) return false;
            var queueItem = AccessTools.Inner(t, "ManufactureQueueItem");
            if (queueItem == null) return false;
            _target = AccessTools.Method(t, "FinishManufactureItem", new[] { queueItem });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // element = the ManufactureQueueItem being finished.
        public static bool Prefix(object element)
        {
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!engine.IsHost) return false; // client: host drives completion

            try
            {
                string id = ManufactureReflection.GetQueueItemId(element);
                int idx = ManufactureReflection.GetQueueIndex(GeoRuntime.Instance, element);
                if (!string.IsNullOrEmpty(id))
                    engine.Sync.BroadcastHostAction(new ManufactureCompletedAction(id, idx));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] FinishManufactureItemPatch failed: " + ex.Message);
            }
            return true;
        }
    }
}
