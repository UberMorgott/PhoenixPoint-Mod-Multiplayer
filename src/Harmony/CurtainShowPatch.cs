using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.UI;
using UnityEngine;

namespace Multipleer.Harmony
{
    /// <summary>
    /// SHOW seam: after the native curtain drops for a level load (OnLevelStateChanged →
    /// newState == Level.State.Loading), bring up the co-op load overlay. Only acts during an
    /// active co-op session with a pending barrier. Type is resolved dynamically (the mod
    /// assembly does not reference LevelSwitchCurtainController).
    /// </summary>
    [HarmonyPatch]
    public static class CurtainShowPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
            if (_targetType == null) return false;
            _targetMethod = AccessTools.Method(_targetType, "OnLevelStateChanged");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        // Signature: OnLevelStateChanged(Level level, Level.State prevState, Level.State newState).
        // newState is an enum; compare by its integer/string name to avoid a hard type ref.
        public static void Postfix(object newState)
        {
            try
            {
                if (newState == null || newState.ToString() != "Loading") return;

                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return;
                var coord = engine.SaveTransfer;
                if (coord == null || !coord.TransferActive) return;

                MultiplayerUI.Instance?.ShowLoadOverlay();
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] CurtainShowPatch failed: " + e.Message);
            }
        }
    }
}
