using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// HOST-side display-order stamp source for the Batch-3 P4 unified sequencer — a pure-observe Postfix on
    /// the ONE native funnel every queued geoscape display passes through:
    /// <c>GeoscapeViewSwitchQuery.QueryStateSwitch(GeoscapeViewStateSwitchRequest)</c> (decompile :75 — sorted
    /// insert before the first strictly-lower priority, FIFO among equals; ProcessQueriedStateSwitch pops
    /// one-at-a-time). The spec mandates stamping "at the moment the host's own QueryStateSwitch fires": this
    /// postfix allocates the next monotonic <c>displaySeq</c> and records it with the request's REAL
    /// <c>Priority</c> + pushed state type into the one-slot <see cref="DisplayStamp"/>. The per-rail broadcast
    /// patches (event raise / report modal / cutscene), which run LATER IN THE SAME call stack, consume the
    /// stamp type-matched — so the wire carries the exact seq/priority the host's native queue used, including
    /// the dynamic event priorities (TriggeredByEvent 10 / plain 0 / completed-upgrade 15) no re-computation
    /// could reproduce. Stamps for NON-mirrored pushes (deploy prompt, replenish, …) are simply overwritten by
    /// the next push — never mis-consumed (type match + consume-once). Host + active session + gate only;
    /// never mutates the request (S1 host-transparency kept). Reflective target; best-effort.
    /// </summary>
    [HarmonyPatch]
    public static class ViewSwitchQueryStampPatch
    {
        private static MethodBase _target;
        private static FieldInfo _priorityField;   // GeoscapeViewStateSwitchRequest.Priority (public readonly)
        private static FieldInfo _stateField;      // GeoscapeViewStateSwitchRequest.State (public readonly)

        public static bool Prepare()
        {
            var queryT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeViewSwitchQuery");
            var requestT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeViewStateSwitchRequest");
            if (queryT == null || requestT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): (GeoscapeViewStateSwitchRequest).
            _target = AccessTools.Method(queryT, "QueryStateSwitch", new[] { requestT });
            _priorityField = AccessTools.Field(requestT, "Priority");
            _stateField = AccessTools.Field(requestT, "State");
            return _target != null && _priorityField != null && _stateField != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = the GeoscapeViewStateSwitchRequest just inserted into the native queue.
        public static void Postfix(object __0)
        {
            try
            {
                if (!DisplaySequencerGate.Enabled) return;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                if (__0 == null) return;
                var state = _stateField.GetValue(__0);
                int priority = Convert.ToInt32(_priorityField.GetValue(__0));
                DisplayStamp.Record(state != null ? state.GetType().Name : "", DisplaySequence.NextSeq(), priority);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ViewSwitchQueryStampPatch failed: " + ex.Message); }
        }
    }
}
