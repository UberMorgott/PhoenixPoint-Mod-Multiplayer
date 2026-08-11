using System;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// FIVE SECONDS BEFORE THE GEOSCAPE COMES BACK. Owner request 2026-08-10: when a mission ends and the
    /// peer is about to be taken back to the geoscape, a countdown strip appears at the TOP of the screen
    /// and ticks 5 → 0; the return happens when it reaches zero.
    ///
    /// THE SEAM IS THE GAME'S OWN AND THERE IS EXACTLY ONE: <c>TacticalView.GoToGeoscape</c>
    /// (TacticalView.cs:1112) is the private callback every end-of-battle route hands to the summary
    /// screen — <c>GetLevelFinishedViewState</c>:1109 (<c>UIStateBattleSummary</c>), :1105
    /// (<c>UIStateTacticalCutscene</c>) and the <c>battle_summary</c> console command :1200 — and its whole
    /// body is <c>PhoenixGame.FinishLevel</c>. Holding it is therefore holding the return itself, with no
    /// second path to forget and nothing torn down early. Same shape as
    /// <c>DeployCountdown.Gate</c> holds a launch, in the other direction.
    ///
    /// NO WIRE, NO QUORUM (P13). The clock is peer-LOCAL and starts on THIS peer's own click, because the
    /// return already is: each peer runs its own <c>GoToGeoscape</c> whenever it dismisses its own summary
    /// screen (measured 2026-08-11 — the two clients reached the geoscape at 475.1 s / 476.7 s and the HOST
    /// only at 483.6 s). Nothing here reads another peer, nothing waits on a human, and reaching zero
    /// requires nobody to press anything. A peer that alt-tabs returns the moment its own Update resumes;
    /// a peer that never left the geoscape never calls <c>GoToGeoscape</c> and so never arms this at all.
    ///
    /// NO NEW WIDGET (native-UI-first). The strip is the mod's EXISTING top-of-screen countdown plate,
    /// <see cref="Multiplayer.UI.CountdownPanel"/> — the same skinned plate the deployment drop and the
    /// lobby start already share, anchored under the top edge (CountdownPanel.cs:83-87) and skinned from
    /// the captured native theme. It only grows a third caption. The CANCEL button is HIDDEN for this one:
    /// there is nothing to veto — the battle is over and the geoscape is where the game goes next.
    ///
    /// REPAINT SEAM: <c>MultiplayerUI.Update</c>:1977 (<c>_countdownPanel?.Sync()</c>), unconditional and
    /// every frame in every scene, which is what makes the number tick on an already-open screen.
    /// </summary>
    internal static class ReturnCountdown
    {
        /// <summary>Five, matching <c>DeployCountdown.CountdownSeconds</c> and
        /// <c>LobbyCountdown.CountdownSeconds</c> — one number for every countdown in the mod.</summary>
        internal const int CountdownSeconds = 5;

        private static readonly System.Reflection.MethodInfo GoToGeoscapeMethod =
            AccessTools.Method(typeof(TacticalView), "GoToGeoscape");     // TacticalView.cs:1112

        /// <summary>Peer-local deadline (realtime), 0 = nothing running.</summary>
        private static float _zeroAt;

        /// <summary>The view whose return is being held. Unity-null once the level is gone, which is one of
        /// the two ways <see cref="Tick"/> gives up.</summary>
        private static TacticalView _view;

        /// <summary>Set across every invoke the MOD makes through <c>TacticalTurnSync.InvokeNativeLeave</c>
        /// — our own release at zero, and the host executing an accepted peer ask — so the prefix lets those
        /// through. <c>ApplyLeave</c> is the third such invoke and rides <c>SyncApplyScope</c> instead,
        /// because L64 requires it to call the native handle directly.</summary>
        internal static bool ModDriving;

        /// <summary>Session/level teardown — a live count must not survive into the next battle.</summary>
        internal static void Reset() { _zeroAt = 0f; _view = null; ModDriving = false; }

        /// <summary>THE HOLD JUST SWALLOWED THIS CALL — read by <c>TacLeaveBattleCapture</c>, which must not
        /// announce a leave that has not happened. Exactly mirrors the arms on which
        /// <see cref="ReturnHoldPatch"/> returns false: every one of them has set the deadline first, and
        /// <see cref="Tick"/> clears it BEFORE it re-invokes, so the release is not "holding".</summary>
        internal static bool Holding => _zeroAt > 0f;

        /// <summary>What the strip shows on THIS peer. Holds at 1 rather than reaching 0 by itself: zero is
        /// the frame the return actually fires, the same rule <c>LobbyCountdown.DisplaySecondsLeft</c> uses.</summary>
        internal static int DisplaySecondsLeft()
        {
            if (_zeroAt <= 0f) return 0;
            int left = Mathf.CeilToInt(_zeroAt - Time.realtimeSinceStartup);
            return left < 1 ? 1 : left;
        }

        /// <summary>THE HOLD. Returns false to swallow the native return while the strip counts.
        ///
        /// ORDER IS DECLARED, NOT HOPED FOR. The mod puts a SECOND prefix on this very method —
        /// <c>TacLeaveBattleCapture</c>, which latches <c>TacticalTurnSync.LeftBattle</c> and announces the
        /// leave to every peer — and a prefix returning false cancels the ones behind it. Unordered,
        /// whether the leave went out at the CLICK or five seconds later at the RELEASE came down to
        /// registration order. <c>Priority.First</c> settles it.
        ///
        /// THE PRIORITY IS ONLY HALF OF IT, and Harmony does NOT do the cancelling. In HarmonyLib 2.2.0.0 a
        /// false prefix skips only the prefixes that can affect the original — ones returning bool or taking
        /// a ref/out the body reads; a <c>void</c> prefix is emitted unguarded and runs at any priority
        /// (probed against the ModSDK's own 0Harmony.dll, 2026-08-11, after a first attempt at this shipped
        /// as a runtime no-op). The capture is <c>void</c> and L64 requires it to stay that way, so it reads
        /// <see cref="Holding"/> instead. The priority is still load-bearing: this hold must ARM before the
        /// capture asks.
        ///
        /// THE HOLD MUST WIN, so the announcement happens at the release: this hold can be ABANDONED
        /// (<see cref="Tick"/> gives up when the session dies, the level goes away, or the method stops
        /// resolving) and a leave announced at the click would then have carried every other peer out of a
        /// battle this one never left — the exact stranding <c>TacLeaveBattleCapture</c> exists to prevent.
        /// A leave is real when it happens, not when it is scheduled. The release re-invokes through this
        /// same chain with <see cref="ModDriving"/> set, so the capture runs then, exactly once.
        ///
        /// IT HOLDS A HUMAN'S CLICK AND NOTHING ELSE. The strip's whole premise is that the clock is
        /// peer-LOCAL and starts on THIS peer's own click; the mod's own three invocations of this funnel
        /// are not clicks and must go through untouched:
        ///   • the release (<see cref="ModDriving"/>) — obviously, it IS the expiry;
        ///   • <c>TacticalTurnSync.HandleLeaveBattle</c>, the host executing a peer's accepted ask. Held, it
        ///     returned false to a caller that then logged "ACCEPTED — running the host's own GoToGeoscape"
        ///     over nothing, and any throw from the real body surfaced 5 s later inside <see cref="Tick"/>,
        ///     on a stack where <c>IntentRail.HandleInbound</c>'s catch — the asking peer's only reject —
        ///     is long gone. The asking peer has ALREADY spent its own five seconds; the host adding five
        ///     more just delays every other peer behind it.
        ///   • <c>TacticalTurnSync.ApplyLeave</c> (<see cref="SyncApplyScope"/>), a peer being carried out
        ///     by the host. Held, the capture ran at the RELEASE instead — outside the apply scope that was
        ///     the only thing suppressing it — and the carried peer sent a leave ask straight back to the
        ///     host: the direct echo loop law 8 exists to forbid. A peer that never clicked has nothing to
        ///     count down anyway.</summary>
        [HarmonyPatch(typeof(TacticalView), "GoToGeoscape")]
        internal static class ReturnHoldPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(TacticalView __instance)
            {
                try
                {
                    // The mod driving this funnel itself, not a human clicking Continue — see the summary.
                    if (ModDriving || SyncApplyScope.Active) return true;
                    var engine = NetworkEngine.Instance;
                    // Solo is untouched vanilla: a single player has nobody to be told about, and five
                    // seconds of waiting is not a thing to add to a game that never had it.
                    if (engine == null || !engine.IsActiveSession) return true;
                    if (GoToGeoscapeMethod == null)
                    {
                        // NEVER SILENT (P1), and never at the cost of the return: a resolve failure means
                        // nothing could re-invoke this, so the strip is skipped and the game goes back now.
                        Debug.LogError("[MP][return] TacticalView.GoToGeoscape did not resolve — no countdown " +
                                       "strip for this return; going back to the geoscape immediately.");
                        return true;
                    }
                    if (_zeroAt > 0f) return false;                // already counting; eat the re-click

                    _view = __instance;
                    _zeroAt = Time.realtimeSinceStartup + CountdownSeconds;
                    Debug.Log("[MP][return] mission over — holding this peer's own return to the geoscape for " +
                              CountdownSeconds + " s and showing the countdown strip. LOCAL clock, no wire: " +
                              "every peer counts its own from its own summary screen, nobody waits on anybody " +
                              "(no quorum), and it expires by itself.");
                    return false;
                }
                catch (Exception ex)
                {
                    // A throw here must never strand a player in a finished battle.
                    Reset();
                    Debug.LogError("[MP][return] arming the pre-geoscape countdown failed — returning now: " + ex);
                    return true;
                }
            }
        }

        /// <summary>Driven from <c>MultiplayerUI.Update</c>, next to the strip's own repaint — the one loop
        /// that runs in every scene including the tactical one.</summary>
        internal static void Tick()
        {
            if (_zeroAt <= 0f) return;
            try
            {
                var engine = NetworkEngine.Instance;
                // The level went away under us (host left, session torn down, level reloaded). Nothing to
                // release into; just stop showing a number.
                if (engine == null || !engine.IsActiveSession || _view == null || GoToGeoscapeMethod == null)
                { Reset(); return; }
                if (Time.realtimeSinceStartup < _zeroAt) return;

                var view = _view;
                _zeroAt = 0f;
                _view = null;
                Debug.Log("[MP][return] countdown reached zero — running the game's own " +
                          "TacticalView.GoToGeoscape (PhoenixGame.FinishLevel) exactly as the summary screen " +
                          "would have five seconds ago.");
                // Through TacticalTurnSync's invoker, not our own MethodInfo: this call is what fires
                // TacLeaveBattleCapture (the hold above deliberately runs first, so the capture only ever
                // fires here), and if the native body throws the leave latch has to come back off. It owns
                // ModDriving, which is what gets this re-invoke past the hold.
                TacticalTurnSync.InvokeNativeLeave(view);
            }
            catch (Exception ex)
            {
                Reset();
                Debug.LogError("[MP][return] releasing the pre-geoscape countdown failed — this peer is still " +
                               "on the summary screen and its Continue button still works: " + ex);
            }
        }
    }
}
