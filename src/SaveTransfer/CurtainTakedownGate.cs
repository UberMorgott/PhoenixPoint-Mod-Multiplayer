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
    /// THE LOADING SCREEN IS THREE GAMEOBJECTS AND THE BARRIER ONLY EVER HELD ONE OF THEM.
    ///
    /// THE REPORT, five or six "fixes" deep (user, 2026-08-05): three peers enter a mission, ONE peer's
    /// loading screen disappears and it can already play while the other two are still loading. The freshest
    /// captured run shows the AGGREGATE working perfectly — all three peers released within one hop, host
    /// last — which is why every theory aimed at <c>AllDone</c> / <c>RevealAll</c> / the arm kept missing.
    /// The aggregate was never the hole. COVERAGE was.
    ///
    /// What the player calls "the loading screen" is THREE independent GameObjects with three independent
    /// take-downs, and <see cref="CurtainLiftGatePatch"/> gated exactly one:
    ///   1. the INK CURTAIN — <c>SceneFadeController.LiftCurtain</c>:87, whose sole entry is
    ///      <c>LevelSwitchCurtainController.LiftCurtainCrt</c>:102. GATED (that patch), and it is the one
    ///      every log line in this mod has ever been about.
    ///   2. the "LoadingScreen" CHILD — <c>SceneFadeController.SetLoadingScreenVisible(false)</c>:82, a plain
    ///      <c>SetActive</c>. NOT gated. Reached through <c>LevelSwitchCurtainController</c>:40.
    ///   3. <c>InGameLoadingCurtain.CurtainObject</c> — <c>HideCurtain()</c>:36, another plain
    ///      <c>SetActive</c>, called from <c>PhoenixSaveManager</c>:255/:369/:650/:709,
    ///      <c>GeoLevelController</c>:1241/:1329 and <c>PhoenixGame</c>:383. NOT gated. This is the one
    ///      whose SHAPE matches the report: it is driven per-peer off purely LOCAL state
    ///      (<c>WaitForView</c>:50 spins on <c>CurrentLevel.Playing &amp;&amp; view.ViewInitialized</c>), so
    ///      it comes down when THIS peer finishes and knows nothing about any barrier.
    ///
    /// ONE PREDICATE, ONE SEAM, THREE PATHS — not three bespoke guards, which is how a fourth path gets
    /// added and quietly not gated. <see cref="Hold"/> lives here and every gate asks it; the RailCheck arm
    /// L94 (l) derives the take-down set STRUCTURALLY off the shipped assembly and goes red on any writer
    /// no patch of ours covers, so the enumeration cannot silently grow a hole again.
    ///
    /// SINGLE-PLAYER AND BOOT ARE UNTOUCHED BY CONSTRUCTION, not by a call-site list: <see cref="Hold"/> is
    /// false unless a co-op session is live, started and un-revealed. <c>PhoenixGame</c>:416/:736 (BootCrt,
    /// FirstRunCrt) and :383 run before any session exists, so they pass straight through.
    ///
    /// PARKING IS NOT SUPPRESSING. Paths 2 and 3 are void methods with no coroutine to wrap, so a blocked
    /// call must be RE-ISSUED or the screen never comes down at all — the permanent hang the standing
    /// mandate forbids. Each parked take-down starts one coroutine on the GAME's Timing (not the network
    /// engine's Update, which stops at teardown) that re-invokes it the frame <see cref="Hold"/> goes false.
    /// Both openers are unconditional: the synchronized reveal, and session teardown.
    /// </summary>
    internal static class CurtainTakedownGate
    {
        // Re-entry latch: a parked take-down is replayed by INVOKING the very method we prefixed, so that
        // one call has to pass. Set and cleared inside a single synchronous replay, never across a yield.
        private static bool _replaying;

        /// <summary>
        /// THE hold decision, live per frame, shared by all three take-down paths. Never throws — a gate
        /// exception must never be what strands a peer behind a loading screen.
        /// </summary>
        internal static bool Hold()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                var coord = engine?.SaveTransfer;
                return SaveTransferMath.HoldCurtain(
                    engineActive: engine != null && engine.IsActive,
                    // NOT SessionStarted: the arm has to cover the host's pending new-campaign bootstrap
                    // too, or its fresh geoscape goes interactive before the barrier it will be gated by
                    // even exists (SaveTransferMath.CurtainHoldArmed carries the measured run).
                    sessionStarted: coord != null && coord.CurtainHoldArmed,
                    revealed: coord == null || coord.Revealed);
            }
            catch { return false; }
        }

        /// <summary>
        /// The inputs behind <see cref="Hold"/>, verbatim. THE EVIDENCE LINE: until now a lift that
        /// sailed through the gate was indistinguishable in the logs from no lift at all — CurtainLiftGate's
        /// PARKED/RELEASED pair appears ZERO times in all three captured runs, so five fixes were written
        /// with no way to tell which of "it was not held" and "it never fired" had happened.
        ///
        /// AND IT HAS TO NAME EVERY TERM, NOT THE TERMS IT HAPPENED TO BE BORN WITH (law L173). 17bf9fe
        /// added <c>loadBoundaryAnnounced</c> to <see cref="SaveTransferMath.CurtainHoldArmed"/> and did not
        /// add it here, so this line kept printing <c>holdArmed=</c> followed by a parenthesis accounting
        /// for two of three terms. That is not cosmetic: on a lobby FIRST start and on the new-campaign arm
        /// the announcement is the ONLY term that can be true (<c>_begun</c> is false and the latch is spent
        /// by the time the transfer runs), so the exact boundary the newest fix exists for was the one the
        /// log could say nothing about — and the 2026-08-07 host+client capture could not settle whether the
        /// host's screen had been held. An evidence line that omits an input is worse than none: it reads
        /// like a complete account.
        /// </summary>
        internal static string State()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                var coord = engine?.SaveTransfer;
                return "engineActive=" + (engine != null && engine.IsActive) +
                       " holdArmed=" + (coord != null && coord.CurtainHoldArmed) +
                       " (sessionStarted=" + (coord != null && coord.SessionStarted) +
                       " newCampaignPending=" + (coord != null && coord.NewCampaignPending) +
                       " loadBoundaryAnnounced=" + (coord != null && coord.LoadBoundaryAnnounced) + ")" +
                       " revealed=" + (coord == null || coord.Revealed);
            }
            catch (Exception e) { return "gate state unreadable: " + e.Message; }
        }

        /// <summary>
        /// Park a take-down that arrived while the barrier holds; true means the caller must SKIP it. The
        /// replay coroutine re-issues it the frame the gate opens, so a parked screen always comes down.
        /// FAILS OPEN on purpose: with no Timing to drive the replay we do not park at all, because an
        /// un-replayable park is exactly the permanent hang this gate exists inside a mandate against.
        /// </summary>
        internal static bool Park(string what, Action replay)
        {
            if (_replaying || !Hold()) return false;
            var timing = GameUtl.GameComponent<TimeSource>()?.Timing;
            if (timing == null) return false;
            MpLog.Log("[Multiplayer] loading-screen take-down PARKED (" + what + ") — holding for all-players reveal");
            timing.Start(ReplayWhenOpen(what, replay));
            return true;
        }

        private static IEnumerator<NextUpdate> ReplayWhenOpen(string what, Action replay)
        {
            while (Hold()) yield return NextUpdate.NextFrame;
            MpLog.Log("[Multiplayer] loading-screen take-down RELEASED (" + what + ") — reveal/teardown opened the gate");
            _replaying = true;
            try { replay(); }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] take-down replay failed (" + what + "): " + e.Message);
            }
            _replaying = false;
        }
    }

    /// <summary>
    /// TAKE-DOWN PATH 2 — the "LoadingScreen" child GameObject. <c>SceneFadeController</c>:82 is the
    /// chokepoint (<c>LevelSwitchCurtainController</c>:40 forwards to it and Awake:37 seeds it), so gating
    /// here covers both without a second guard. Only the HIDE direction is gated; showing it is never
    /// something a barrier should block.
    /// </summary>
    [HarmonyPatch]
    public static class LoadingScreenVisibleGatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("Base.Utils.SceneFadeController");
            if (t == null) return false;
            _target = AccessTools.Method(t, "SetLoadingScreenVisible", new[] { typeof(bool) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Harmony binds `visible` BY NAME to the original (SceneFadeController.cs:82).
        public static bool Prefix(object __instance, bool visible)
        {
            try
            {
                if (visible) return true;
                var self = __instance;
                return !CurtainTakedownGate.Park("SceneFadeController.SetLoadingScreenVisible(false)",
                                                 () => _target.Invoke(self, new object[] { false }));
            }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] LoadingScreenVisibleGatePatch failed: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// TAKE-DOWN PATH 3 — <c>InGameLoadingCurtain.CurtainObject</c>. The per-peer one: its own
    /// <c>WaitForView</c>:50 spins on LOCAL <c>Playing + ViewInitialized</c> and nothing about it can see a
    /// barrier, so before this patch the first peer whose view initialised took its own screen down and
    /// started playing. Gating <c>HideCurtain</c> holds BOTH halves — the GameObject and the
    /// <c>CloseCurtain</c> callback the self-drive is paired with — so the pairing stays intact and simply
    /// completes at the reveal.
    /// </summary>
    [HarmonyPatch]
    public static class InGameCurtainHideGatePatch
    {
        private static MethodBase _target;
        private static FieldInfo _curtainObject;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("Base.UI.InGameLoadingScreen.InGameLoadingCurtain");
            if (t == null) return false;
            _target = AccessTools.Method(t, "HideCurtain", new Type[0]);
            _curtainObject = AccessTools.Field(t, "CurtainObject");
            return _target != null && _curtainObject != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance)
        {
            try
            {
                // ONLY A VISIBLE CURTAIN IS A TAKE-DOWN. HideCurtain also runs on paths that never showed it
                // — AutosaveGame(showCurtain:false), the save/load error prompt — where it is a no-op on the
                // GameObject but still fires CloseCurtain → UIStateSystemCanvas.SwitchToPreviousState.
                // Parking one of those would defer a UI-state pop across the whole barrier and then pop it
                // out of a state that is no longer the one which pushed it.
                var self = __instance;
                var obj = _curtainObject.GetValue(self) as GameObject;
                if (obj == null || !obj.activeSelf) return true;
                return !CurtainTakedownGate.Park("InGameLoadingCurtain.HideCurtain",
                                                 () => _target.Invoke(self, null));
            }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] InGameCurtainHideGatePatch failed: " + e.Message);
                return true;
            }
        }
    }
}
