using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.UI;
using UnityEngine;

namespace Multipleer.Harmony
{
    /// <summary>
    /// SHOW + HIDE seam on the native curtain (OnLevelStateChanged). SHOW the co-op load overlay
    /// when newState == Level.State.Loading (curtain drops for the load); HIDE it when
    /// newState == Level.State.Playing (curtain LIFTS — this peer's gameplay actually starts,
    /// decompile LevelSwitchCurtainController.cs:60-62). The overlay therefore stays up THROUGH
    /// the whole phase-2 world-load and drops only at the lift. Only acts during a co-op session.
    /// Type is resolved dynamically (the mod assembly does not reference LevelSwitchCurtainController).
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
                if (newState == null) return;
                var state = newState.ToString();
                if (state != "Loading" && state != "Playing") return;

                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return;

                if (state == "Playing")
                {
                    // Native curtain auto-lifted (LiftCurtain on Loaded→Playing). In a co-op session
                    // HOLD our own synced overlay until the RevealAll second barrier; outside co-op,
                    // drop it immediately (unchanged behaviour).
                    var playingCoord = engine.SaveTransfer;
                    if (playingCoord != null && playingCoord.SessionStarted)
                        playingCoord.OnReachedPlaying();           // co-op: hold until RevealAll
                    else
                        MultiplayerUI.Instance?.HideLoadOverlay();  // non-co-op: unchanged
                    return;
                }

                // state == "Loading": curtain dropped for the load → SHOW (only with a pending transfer).
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
