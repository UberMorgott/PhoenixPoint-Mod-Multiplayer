using System;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Network;
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
    /// SELF-RELEASING, WHICH IS A FIRST-CLASS REQUIREMENT AND NOT A NICETY (user's law: at any moment ANY
    /// player must be able to play EVERYTHING — 49 of 50 AFK, the one active player still finishes the
    /// game). A barrier that waits on a peer that will never report is a permanent block, so the release
    /// this re-enters has three independent openers, all pre-existing: (1) a peer that drops is removed from
    /// the roster before <c>AllDone(GetRosterSlots())</c> is next evaluated, so the expected set SHRINKS and
    /// the barrier opens with whoever is left — the normal case, and it is immediate; (2) the host's LIVENESS
    /// release (<c>NoLiveLoaderLeft</c>): a peer that shows no progress AND says nothing for
    /// <c>BarrierLivenessGraceMs</c> (60 s) stops being waited for and <c>RevealAll</c> goes out without it,
    /// while a peer that is merely SLOW is waited for indefinitely, by design; (3) each peer's own self-reveal
    /// once the HOST has been silent for the same grace, for a host that died holding the announcement.
    /// Teardown is a fourth: <c>HoldCurtain</c> returns false the moment the engine goes inactive. None of the
    /// four removes anybody from the session (law L84) — they leave the BARRIER, not the game.
    /// </summary>
    [HarmonyPatch(typeof(PhoenixGame), nameof(PhoenixGame.FinishLevel))]
    internal static class LoadBarrierGate
    {
        // Harmony binds injected params BY NAME to the original (PhoenixGame.cs:262
        // `FinishLevel(ILevelParams result = null)`).
        private static void Prefix(ILevelParams result)
        {
            try
            {
                // Not a load boundary: leaving for the menu, or no next level at all.
                if (result == null || result is QuitGameResult) return;

                var coord = NetworkEngine.Instance?.SaveTransfer;
                if (coord == null) return;
                // Self-guarded: a no-op unless this boundary is one nobody else armed (see class doc).
                coord.OpenReturnBarrier();
            }
            catch (Exception e)
            {
                // Never throw into FinishLevel — an escaping exception here kills the level-switch coroutine
                // outright ("Broken coroutine call chain"), which is law L70's blocker one level down.
                Debug.LogError("[Multiplayer] LoadBarrierGate.Prefix failed: " + e.Message);
            }
        }
    }
}
