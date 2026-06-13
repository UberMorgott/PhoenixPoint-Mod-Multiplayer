using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using Multipleer.Network.MessageLayer;

namespace Multipleer.Harmony
{
    // Client intercept of the speed funnel UIModuleTimeControl.SelectTimePreset(int). Client -> encode a
    // SetTimeState{Paused=current, PresetIndex=arg} + relay + block local write. Host re-applies via the
    // same method (which clamps the index), so we send the raw requested index. Mirrors the pause patch.
    [HarmonyPatch]
    public static class TimeSpeedInterceptPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
            if (t == null) return false;
            _target = AccessTools.Method(t, "SelectTimePreset", new[] { typeof(int) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance, int presetIndex)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (CommandRelay.IsApplying) return true;
            if (engine.IsHost) return true;

            var msg = new CampaignActionMessage
            {
                ActionId = Guid.NewGuid(),
                ActionType = CampaignActionType.SetTimeState,
                TargetId = "",
                Payload = CommandCodec.EncodeSetTime(new SetTimePayload
                {
                    Paused = TimeBridge.GetCurrentPaused(__instance),
                    PresetIndex = presetIndex
                }),
                Timestamp = DateTime.UtcNow.Ticks
            };
            CommandRelay.Instance?.RelayFromClient(msg);
            return false;  // block the client's local SelectedPresetTime / Scale write
        }
    }
}
