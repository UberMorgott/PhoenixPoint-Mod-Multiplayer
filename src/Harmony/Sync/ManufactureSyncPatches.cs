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
        // __state carries the host action to broadcast AFTER the original succeeds (Postfix).
        public static bool Prefix(object item, out ISyncedAction __state)
        {
            __state = null;
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
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost) { __state = action; return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ManufactureItemPatch failed: " + ex.Message);
                return true;
            }
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed queue.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ManufactureItemPatch postfix broadcast failed: " + ex.Message); }
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
        // __state carries the completion action snapshotted in Prefix (the queue index MUST be captured
        // before the original removes the item) and broadcast in Postfix only if the original succeeds.
        public static bool Prefix(object element, out ISyncedAction __state)
        {
            __state = null;
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!engine.IsHost) return false; // client: host drives completion

            try
            {
                string id = ManufactureReflection.GetQueueItemId(element);
                int idx = ManufactureReflection.GetQueueIndex(GeoRuntime.Instance, element);
                if (!string.IsNullOrEmpty(id))
                    __state = new ManufactureCompletedAction(id, idx);   // snapshot pre-removal; broadcast on success
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] FinishManufactureItemPatch failed: " + ex.Message);
            }
            return true;
        }

        // Host-only (via __state) and only on a normal return of the original (Harmony skips Postfix on a
        // thrown original) → broadcast the confirmed completion so clients never hear a completion that
        // failed to actually apply on the authority.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] FinishManufactureItemPatch postfix broadcast failed: " + ex.Message); }
        }
    }
}
