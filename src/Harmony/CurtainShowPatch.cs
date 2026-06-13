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

        // Suppress the native auto-LiftCurtain on Loaded→Playing during a co-op session so every peer
        // holds at the (still-visible) native loading screen until the host's RevealAll lifts all at once.
        // Returns false ONLY for that exact state pair while held; all other transitions run natively.
        public static bool Prefix(object prevState, object newState)
        {
            try
            {
                if (prevState == null || newState == null) return true;
                if (prevState.ToString() != "Loaded" || newState.ToString() != "Playing") return true;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return true;
                var coord = engine.SaveTransfer;
                if (coord == null || !coord.SessionStarted) return true;   // non-co-op: native lift as normal
                if (coord.Revealed) return true;                            // already revealed: let it lift
                Debug.Log("[Multipleer] curtain Loaded→Playing: SUPPRESS auto-lift (co-op hold)");
                return false; // skip native LiftCurtain; deferred lift happens on RevealAll
            }
            catch (Exception e) { Debug.LogError("[Multipleer] CurtainShowPatch.Prefix failed: " + e.Message); return true; }
        }

        // Signature: OnLevelStateChanged(Level level, Level.State prevState, Level.State newState).
        // Harmony binds injected params BY NAME to the original (verified names: level/prevState/
        // newState, decompile LevelSwitchCurtainController.cs:46); typed as object to avoid hard
        // refs to Level / Level.State. newState is an enum; compare by its string name.
        public static void Postfix(object level, object prevState, object newState)
        {
            try
            {
                if (newState == null) return;
                var state = newState.ToString();
                if (state != "Loading" && state != "Playing" && state != "Loaded") return;

                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return;

                if (state == "Playing" || state == "Loaded")
                {
                    // Load finished / handed off → clear the captured loading Level so the phase-2
                    // pump stops reading it (a null loading level == done, same as LoadingProgress→null).
                    engine.SaveTransfer?.SetLoadingLevel(null);

                    if (state == "Playing")
                    {
                        // Native curtain auto-lifted (LiftCurtain on Loaded→Playing). In a co-op
                        // session HOLD our own synced overlay until the RevealAll second barrier;
                        // outside co-op, drop it immediately (unchanged behaviour).
                        var playingCoord = engine.SaveTransfer;
                        if (playingCoord != null && playingCoord.SessionStarted)
                            playingCoord.OnReachedPlaying();           // co-op: hold until RevealAll
                        else
                            MultiplayerUI.Instance?.HideLoadOverlay();  // non-co-op: unchanged
                    }
                    return;
                }

                // state == "Loading": curtain dropped for the load. Capture the loading Level for the
                // phase-2 pump (GameUtl.CurrentLevel() is null mid-load), then SHOW the overlay.
                var coord = engine.SaveTransfer;
                if (coord == null) return;
                coord.SetLoadingLevel(level);

                // SHOW on a pending transfer (host/lobby) OR while this peer is in phase-2 world-load.
                // The client predicate is FALSE at this instant (rxBytes reset, barrier not pending),
                // so InPhase2 (begun && !loadComplete) is what makes the client show its overlay.
                if (!(coord.TransferActive || coord.InPhase2))
                {
                    Debug.Log($"[Multipleer] curtain Loading: TransferActive={coord.TransferActive} " +
                              $"InPhase2={coord.InPhase2} → skip ShowLoadOverlay");
                    return;
                }

                Debug.Log($"[Multipleer] curtain Loading: TransferActive={coord.TransferActive} " +
                          $"InPhase2={coord.InPhase2} → ShowLoadOverlay");
                MultiplayerUI.Instance?.ShowLoadOverlay();
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] CurtainShowPatch failed: " + e.Message);
            }
        }
    }
}
