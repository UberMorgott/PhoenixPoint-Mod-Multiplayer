using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// Presentation seam on the native curtain (LevelSwitchCurtainController.OnLevelStateChanged),
    /// ported from the quarry. Drives the SaveTransferCoordinator barrier machinery:
    /// Loading → SetLoadingLevel(level) (phase-2 pump source); Playing/Loaded → SetLoadingLevel(null);
    /// Playing → OnNewCampaignPlayableFrame() (P0 bootstrap consume) + OnReachedPlaying() (hold +
    /// LoadComplete). Overlay visibility is NOT driven here — LoadOverlayController.Update is
    /// state-driven per frame. Type resolved dynamically (no hard ref to the controller).
    /// </summary>
    [HarmonyPatch]
    public static class CurtainShowPatch
    {
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
            if (t == null) return false;
            _targetMethod = AccessTools.Method(t, "OnLevelStateChanged");
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
                Debug.Log("[Multiplayer] curtain Loaded→Playing: SUPPRESS auto-lift (co-op hold)");
                return false; // skip native LiftCurtain; deferred lift happens on RevealAll
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] CurtainShowPatch.Prefix failed: " + e.Message); return true; }
        }

        // Signature: OnLevelStateChanged(Level level, Level.State prevState, Level.State newState).
        // Harmony binds injected params BY NAME to the original (verified names: level/prevState/
        // newState, decompile LevelSwitchCurtainController.cs:46); typed as object to avoid hard
        // refs to Level / Level.State. newState is an enum; compare by its string name.
        public static void Postfix(object level, object newState)
        {
            try
            {
                if (newState == null) return;
                var state = newState.ToString();
                if (state != "Loading" && state != "Playing" && state != "Loaded") return;

                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return;
                var coord = engine.SaveTransfer;
                if (coord == null) return;

                if (state == "Loading")
                {
                    // Curtain dropped for the load: capture the loading Level for the phase-2 pump
                    // (GameUtl.CurrentLevel() is null mid-load). Overlay show is state-driven elsewhere.
                    coord.SetLoadingLevel(level);
                    return;
                }

                // Playing/Loaded: load finished / handed off → clear the captured loading Level so the
                // phase-2 pump stops reading it (null loading level == done).
                coord.SetLoadingLevel(null);

                if (state == "Playing")
                {
                    // P0 new-campaign bootstrap: no-op unless the HOST armed it at the native
                    // new-game confirm; consumed at the first playable GEOSCAPE frame.
                    coord.OnNewCampaignPlayableFrame();

                    if (coord.SessionStarted)
                    {
                        coord.OnReachedPlaying();   // co-op: hold + LoadComplete until RevealAll
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] CurtainShowPatch.Postfix failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// THE all-loaded curtain barrier: gate EVERY native curtain lift on the synchronized reveal.
    /// The game lifts the loading curtain through MULTIPLE paths — the auto-lift on Loaded→Playing
    /// (suppressed above) but ALSO direct LiftCurtainCrt starts that bypass that seam
    /// (UIStateSimulation.ExitState, UIStateInitView, GeoLevelController error paths). All converge
    /// on LevelSwitchCurtainController.LiftCurtainCrt (verified single chokepoint), so ONE Postfix
    /// wraps the returned coroutine: while SaveTransferMath.HoldCurtain (evaluated LIVE each frame)
    /// says hold, the lift parks on NextFrame; then the original lift runs unchanged. Release paths:
    /// RevealAll → Revealed, the reveal fallback belts, and session teardown (engine inactive) — a
    /// parked lift can never hang forever. Non-co-op lifts pass through untouched.
    /// </summary>
    [HarmonyPatch]
    public static class CurtainLiftGatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
            if (t == null) return false;
            _target = AccessTools.Method(t, "LiftCurtainCrt");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(ref IEnumerator<NextUpdate> __result)
        {
            __result = Gated(__result);
        }

        private static IEnumerator<NextUpdate> Gated(IEnumerator<NextUpdate> original)
        {
            if (Hold())
            {
                Debug.Log("[Multiplayer] curtain lift PARKED — holding for all-players reveal");
                while (Hold()) yield return NextUpdate.NextFrame;
                Debug.Log("[Multiplayer] curtain lift RELEASED — reveal/teardown opened the gate");
            }
            while (original.MoveNext()) yield return original.Current;
        }

        // Live per-frame hold decision; never throws (a gate exception must never strand the curtain).
        private static bool Hold()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                var coord = engine?.SaveTransfer;
                return SaveTransferMath.HoldCurtain(
                    engineActive: engine != null && engine.IsActive,
                    sessionStarted: coord != null && coord.SessionStarted,
                    revealed: coord == null || coord.Revealed);
            }
            catch { return false; }
        }
    }
}
