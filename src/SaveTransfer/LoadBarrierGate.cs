using System;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.Params;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// THE SIMULTANEOUS-START BARRIER, ARMED AT THE ONE LEVEL-TRANSITION FUNNEL (law L94).
    ///
    /// THE REPORT (3 instances, 2026-08-04): "whoever finishes loading FIRST gets his window lit up, sees the
    /// game and can already act on it while the others are still loading." Every peer must stay behind the
    /// loading screen until the LAST one is in, then all release together.
    ///
    /// NOTHING NEW IS BUILT HERE — THE BARRIER ALREADY EXISTS AND ALREADY WORKS. The mechanism is
    /// <see cref="CurtainLiftGatePatch"/> parking EVERY native curtain lift on
    /// <c>SaveTransferMath.HoldCurtain</c> (engine active + session started + not yet revealed), each peer
    /// reporting itself in at <c>SaveTransferCoordinator.OnReachedPlaying</c> →
    /// <c>SendLoadComplete</c> → <c>RosterProgressTracker.MarkDone</c>, and the host broadcasting one
    /// <c>RevealAll</c> the instant <c>AllDone(GetRosterSlots())</c> holds. That machinery is live and
    /// in-game-proven on the lobby/save load, the F2 mid-session reload and the geoscape→tactical entry —
    /// each of which arms it (<c>OpenBarrier</c>, <c>OpenTacticalEntryBarrier</c>, <c>OnSaveChunk</c>).
    ///
    /// WHAT WAS MISSING WAS THE ARM, ON EXACTLY ONE BOUNDARY. <c>_revealed</c> is a LATCH: once a reveal
    /// happens it stays true until something re-arms it. The tactical→geoscape RETURN carries NO save
    /// transfer (the client rides the native mission end — <c>TacticalTurnSync</c>'s <c>GoToGeoscape</c> →
    /// <c>PhoenixGame.FinishLevel</c> → geoscape load), so NOTHING re-armed it: <c>HoldCurtain</c> read
    /// <c>revealed:true</c> and every peer lifted its own curtain the moment its OWN load finished, while
    /// <c>_reachedPlaying</c> — also still latched — made <c>OnReachedPlaying</c> early-return so no peer
    /// even reported in. First one loaded plays alone; that is the report, verbatim.
    /// <c>SaveTransferCoordinator.OpenReturnBarrier</c> was written for precisely this and shipped with the
    /// comment "DEAD until the MISSION-END arc — no caller yet". The mission-end arc landed; the caller
    /// never did. This file IS that caller.
    ///
    /// THE SEAM IS THE FUNNEL, NOT THE TRANSITION (universal-first, and the reason this is ONE patch and not
    /// one per boundary). <c>PhoenixGame.FinishLevel</c>:262 is where EVERY level change in the game passes —
    /// the host's tactical launch (<c>GeoLevelController.LaunchTacticalGameCrt</c>:1466), our client entry
    /// (<c>SaveTransferCoordinator.EnterLevel</c>), the F2 reload and quit paths
    /// (<c>FinishLevelAndLoadGame</c>:268 / <c>FinishLevelAndGoToLobby</c>:284) and the post-mission return
    /// (<c>TacticalView.GoToGeoscape</c>:1114). <c>LevelTeardown</c> already picked this method for the same
    /// reason one law earlier. Arming HERE means any future load boundary is covered the day it is added,
    /// with no second barrier to keep in sync.
    ///
    /// IT IS SAFE ON THE BOUNDARIES THAT ALREADY ARM, BY CONSTRUCTION AND NOT BY A CALL-SITE LIST:
    /// <c>OpenReturnBarrier</c> self-guards on <c>if (!_revealed || _barrierOpen) return;</c>, so on every
    /// path that armed itself (all of which cleared <c>_revealed</c> BEFORE reaching FinishLevel) this is a
    /// no-op. It fires on exactly the boundaries nobody else covers. Ordering is safe for the same reason
    /// <c>GeoTeardownResetGate</c> sits here: <c>FinishLevel</c> is ASYNCHRONOUS (it stores
    /// <c>_levelResult</c> and pulses a monitor), so the arm lands well before the new level's curtain can
    /// reach Loaded→Playing — the same ordering <c>OpenTacticalEntryBarrier</c> calls "ordering-critical".
    ///
    /// A QUIT IS NOT A LOAD BOUNDARY. <c>FinishLevelAndGoToLobby</c>/<c>AndQuitGame</c> pass a
    /// <c>QuitGameResult</c>: the peer is leaving for the main menu and no co-op level is being loaded on
    /// the other side, so arming there would hold a curtain over a lobby waiting for peers who are not
    /// loading anything. Excluded explicitly; a null result (no next level at all — <c>UIStateInitial</c>:73,
    /// <c>IntroLevelController</c>:108) likewise.
    ///
    /// THE WAIT IS UNBOUNDED AND EVERY OPENER IS AN EVENT, NEVER A DEADLINE (user ruling 2026-08-05). If a
    /// peer is slow, everybody waits; if a peer never reports, everybody keeps waiting. The two clocks that
    /// used to end the wait — the host's 60 s liveness give-up and each peer's own self-reveal — were both
    /// ways for one player's screen to come down while the others were still loading, which is the entire
    /// report, so they are gone. What opens the barrier: (1) <c>AllDone(GetRosterSlots())</c>, the normal
    /// simultaneous release; (2) a peer that DROPS leaves the roster, so the expected set SHRINKS and (1)
    /// holds on the very next frame — automatic, reactive, no click and no timer; (3) session teardown, where
    /// <c>HoldCurtain</c> returns false the moment the engine goes inactive. None of the three removes
    /// anybody from the session (law L84) — they leave the BARRIER, not the game.
    /// </summary>
    [HarmonyPatch(typeof(PhoenixGame), nameof(PhoenixGame.FinishLevel))]
    internal static class LoadBarrierGate
    {
        /// <summary>What this funnel is looking at. Three kinds, because the prefix owes each a different
        /// answer and the third one did not exist until 2026-08-08.</summary>
        internal enum Boundary
        {
            /// <summary>Leaving for the menu, or no next level at all — nobody is loading anything.</summary>
            NotALoad,
            /// <summary>The same level, torn down and reloaded (<c>PhoenixGame.cs</c>:574-580).</summary>
            Restart,
            /// <summary>An ordinary level change: launch, entry, F2 reload, post-mission return.</summary>
            Ordinary,
        }

        /// <summary>The prefix's verdict as a PURE function, so RailCheck can EXECUTE it (law L328) instead of
        /// asserting that some method was called. A restart is distinguishable from every other level change
        /// ONLY by this result type — there is no <c>RestartMission()</c> anywhere in the engine — so this
        /// three-way answer IS the fix, and a law that cannot run it is a law that cannot see it disappear.</summary>
        internal static Boundary Classify(ILevelParams result) =>
            result == null || result is QuitGameResult ? Boundary.NotALoad
          : result is RestartGameResult ? Boundary.Restart
          : Boundary.Ordinary;

        // Harmony binds injected params BY NAME to the original (PhoenixGame.cs:262
        // `FinishLevel(ILevelParams result = null)`).
        // Returns false to SKIP the original — the one case is a client's own restart (see below). Every
        // other prefix on this method is side-effect-free and therefore still runs (Harmony calls those
        // unconditionally); GeoTeardownResetGate is the only one, and it self-returns when the peer holds no
        // GeoLevelController — which a restart never does, the native Restart button exists in TACTICAL only
        // (UIStateTacticalOptions:45 `restartEnabed: true`, UIStateGeoscapeOptions:34 `restartEnabed: false`).
        private static bool Prefix(ILevelParams result)
        {
            try
            {
                var boundary = Classify(result);
                // Not a load boundary: leaving for the menu, or no next level at all.
                if (boundary == Boundary.NotALoad) return true;

                // A RESTART IS THE ONE LEVEL CHANGE NOBODY ANNOUNCED (law L328). There is no
                // PhoenixGame.RestartMission() to patch — a restart is distinguishable ONLY by this result
                // type (PhoenixGame.cs:574-580 loops a RestartGameResult back into RunGameLevel), which is
                // why the discrimination belongs here, in the method that already discriminates by type.
                // Until this arm landed, `RestartGameResult` appeared nowhere in the mod: whoever pressed
                // Restart tore down and reloaded ALONE, and on 2026-08-08 that was the CLIENT — it reloaded
                // while the host stayed pinned at 68 %, after which the two peers were in different tactical
                // levels with different key maps and every actor key on the wire named a different board.
                // The HOST's restart is broadcast so every peer follows without anybody pressing anything;
                // the CLIENT's becomes an ask and is BLOCKED here.
                //
                // RestartTrace is INSTRUMENTATION ONLY and gates nothing. It sits here because this is the
                // one place in the mod that knows a restart is beginning; everything downstream only writes
                // into an already-open trace, so no other seam can emit outside one.
                if (boundary == Boundary.Restart) RestartTrace.Enter(result);
                if (boundary == Boundary.Restart && !TacticalTurnSync.OnLocalRestart()) return false;

                var coord = NetworkEngine.Instance?.SaveTransfer;
                if (coord == null)
                {
                    RestartTrace.Note("no SaveTransfer coordinator on this peer — the load barrier is NOT " +
                                      "armed for this restart (solo, or the session is already down).");
                    return true;
                }
                // Self-guarded: a no-op unless this boundary is one nobody else armed (see class doc). A
                // restart that DOES run reaches it, and should: every peer is about to load the same map, so
                // they wait behind the curtain for each other exactly as they do on every other boundary.
                coord.OpenReturnBarrier();
                RestartTrace.Note("load barrier armed (OpenReturnBarrier) — the curtain now stays down until " +
                                  "every live peer reports its own load done.");
            }
            catch (Exception e)
            {
                RestartTrace.Note("LoadBarrierGate.Prefix THREW — the native FinishLevel still runs, but " +
                                  "nothing after the throw ran: " + e.Message);
                // Never throw into FinishLevel — an escaping exception here kills the level-switch coroutine
                // outright ("Broken coroutine call chain"), which is law L70's blocker one level down.
                Debug.LogError("[Multiplayer] LoadBarrierGate.Prefix failed: " + e.Message);
            }
            return true;
        }
    }
}
