using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Client-only apply of a host clock snapshot (0x34, subtype TimingState). Forces the local clock to
    // host via Timing.ProcessInstanceData (TimeBridge.ApplyTimeState) -> displayed Now/Scale/Paused match
    // host, frame-drift corrected. Host ignores (it owns the clock). ProcessInstanceData fires no events
    // and reschedules nothing, so no re-intercept and no SetGamePauseState TimeLimit guard.
    public static class ClientTimeMirror
    {
        public static void Apply(TimeStatePayload payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            try { TimeBridge.ApplyTimeState(payload); }
            catch (System.Exception ex) { Debug.LogError($"[Multipleer] ClientTimeMirror apply failed: {ex}"); }
        }
    }
}
