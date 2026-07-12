using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.UI;
using UnityEngine;

namespace Multiplayer.Harmony
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
                Debug.Log("[Multiplayer] curtain Loaded→Playing: SUPPRESS auto-lift (co-op hold)");
                return false; // skip native LiftCurtain; deferred lift happens on RevealAll
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] CurtainShowPatch.Prefix failed: " + e.Message); return true; }
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
                        // P0 new-campaign bootstrap: this peer reached a PLAYABLE frame. No-op unless
                        // the HOST armed the bootstrap at the native new-game confirm; on the first
                        // playable GEOSCAPE frame it autosaves + re-runs the existing chunked transfer
                        // (single consumption point — see SaveTransferCoordinator.OnNewCampaignPlayableFrame).
                        engine.SaveTransfer?.OnNewCampaignPlayableFrame();

                        // Native curtain auto-lifted (LiftCurtain on Loaded→Playing). In a co-op
                        // session HOLD our own synced overlay until the RevealAll second barrier;
                        // outside co-op, drop it immediately (unchanged behaviour).
                        var playingCoord = engine.SaveTransfer;
                        if (playingCoord != null && playingCoord.SessionStarted)
                        {
                            playingCoord.OnReachedPlaying();           // co-op: hold until RevealAll

                            // Save-loaded tactical entry (geo→tac save-transfer) restores ALREADY in Playing, so
                            // native TacticalLevelController.OnLevelStateChanged never fires → TacticalLevelStateChangedPatch
                            // never arms the client mirror (everyone controlled all soldiers). Arm it here via the SAME
                            // path; idempotent + client/host/session/pending-guarded inside → no-op on the direct-launch
                            // path (already armed) and on the host.
                            Multiplayer.Sync.Tactical.TacticalDeploySync.ClientOnLevelReadyFromCurtain();
                            // Only tactical-side clear of the geo→tactical gate that fires on THIS path (the
                            // TacticalLevelStateChangedPatch clear never runs here). Client-only flag; no-op on host.
                            Multiplayer.Network.Sync.State.GeoTransitionGate.InTransition = false;
                        }
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
                    Debug.Log($"[Multiplayer] curtain Loading: TransferActive={coord.TransferActive} " +
                              $"InPhase2={coord.InPhase2} → skip ShowLoadOverlay");
                    return;
                }

                Debug.Log($"[Multiplayer] curtain Loading: TransferActive={coord.TransferActive} " +
                          $"InPhase2={coord.InPhase2} → ShowLoadOverlay");
                MultiplayerUI.Instance?.ShowLoadOverlay();
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] CurtainShowPatch failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// THE all-loaded curtain barrier (CS-style): gate EVERY native curtain lift on the synchronized
    /// reveal. The game lifts the loading curtain through MULTIPLE paths — the auto-lift on
    /// Loaded→Playing (OnLevelStateChanged, suppressed above), but ALSO direct calls that BYPASS that
    /// seam entirely: UIStateSimulation.ExitState (geoscape sim state, decompile UIStateSimulation.cs:36),
    /// UIStateInitView (tactical init, cs:57) and GeoLevelController error paths (cs:1430/1461) all start
    /// LiftCurtainCrt themselves. That is why the loading screen used to close for whoever finished
    /// loading: the bypass lift ran regardless of peers (live RCA 2026-07-11).
    ///
    /// ALL of those routes converge on LevelSwitchCurtainController.LiftCurtainCrt (LiftCurtain() just
    /// Timing.Start()s it; the inner SceneFadeController.LiftCurtain is called ONLY from it — verified
    /// single chokepoint), so ONE Postfix here wraps the returned coroutine in a gate: while a live,
    /// started co-op session has not revealed yet (SaveTransferMath.HoldCurtain, evaluated LIVE each
    /// frame), the lift parks on NextFrame; then the original lift runs unchanged (fade, LoadingText off,
    /// PauseObjectRendering release, OnCurtainLifted). Release paths: RevealAll → Revealed (roster
    /// all-done, which SHRINKS on peer-left → no dumb infinite wait), the 180 s host/self-reveal belts,
    /// and any session teardown (engine inactive) — a parked lift can never hang forever. Non-co-op
    /// lifts pass through untouched.
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
