using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using Multipleer.Network.MessageLayer;

namespace Multipleer.Harmony
{
    // Client intercept of the pause funnel UIModuleTimeControl.OnPauseTime(bool). Client -> encode a
    // SetTimeState{Paused=arg, PresetIndex=current} + relay to host + block local write (return false).
    // Host / re-entrant apply -> return true (execute the real write). Mirrors StartTravelInterceptPatch.
    [HarmonyPatch]
    public static class TimePauseInterceptPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
            if (t == null) return false;
            _target = AccessTools.Method(t, "OnPauseTime", new[] { typeof(bool) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance, bool pause)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;  // single player
            if (CommandRelay.IsApplying) return true;              // re-entrant apply: execute
            if (engine.IsHost) return true;                        // host-origin: postfix-free path; host clock is authoritative

            var msg = new CampaignActionMessage
            {
                ActionId = Guid.NewGuid(),
                ActionType = CampaignActionType.SetTimeState,
                TargetId = "",
                Payload = CommandCodec.EncodeSetTime(new SetTimePayload
                {
                    Paused = pause,
                    PresetIndex = TimeBridge.GetCurrentPresetIndex(__instance)
                }),
                Timestamp = DateTime.UtcNow.Ticks
            };
            CommandRelay.Instance?.RelayFromClient(msg);
            return false;  // block the client's local _timing.Paused write
        }
    }
}
