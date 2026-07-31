using System.Collections.Generic;
using Base.Core;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// TACTICAL ARC A1 — put both peers into the SAME battle, client as a pure spectator.
    ///
    /// ENTRY MECHANISM = the game's NATIVE save-loader, not a bespoke snapshot surface. Law 1 says a
    /// join into a level rides the save transfer ("battle-tested"), never a full snapshot pushed through
    /// the delta path — and a geo→tac transition IS a join into a new level. The whole host+client path
    /// already exists in <see cref="SaveTransferCoordinator"/> (write a mid-tactical save → chunked
    /// SendBlob → client ReadMetaData/level-params/scene-binding → LOADED barrier → BEGIN → FinishLevel →
    /// synchronized reveal), and <c>PrepareEntryFromBlobCrt</c> is already tactical-aware (it lifts the
    /// embedded "Geoscape" section out of a tactical save so the post-mission return can work). What was
    /// missing was ONLY the three call sites below. ZERO new wire surfaces: the 0x80-0x9F tactical band
    /// stays entirely free for the live move/combat surfaces of the later arcs.
    ///
    /// v1 evidence, reconciled: v1 shipped BOTH mechanisms (UseSaveTransferEntry=true AND a 493 KB
    /// `tac.deploy` snapshot). The snapshot is the half that deserialized EMPTY on a real client, and the
    /// fix was to stop consuming it (`alreadyLoaded:true`) precisely because the SAVE had already built
    /// the level. So the post-mortem is evidence AGAINST the snapshot surface, not for it.
    ///
    /// A1 does NOT include: intents, turn control, end-turn, damage, movement, inventory, spawn/despawn,
    /// mission end. The client is contained (not commanded) by the two spectator gates at the bottom.
    /// </summary>
    [HarmonyPatch(typeof(GeoLevelController), "LaunchTacticalGame")]
    internal static class TacLaunchGate
    {
        // Intent-capture/sim-gating seam (law 4a/4b), host+client halves of ONE decision:
        //  • HOST: arm the synchronized-reveal hold BEFORE the tactical level can reach Loaded→Playing.
        //    Ordering is the whole point — OpenTacticalEntryBarrier resets _revealed=false, and if that
        //    lands after the transition CurtainShowPatch.Prefix lets the native auto-lift through and the
        //    host reveals the battle alone.
        //  • CLIENT: never self-launch. The client's battle is BUILT from the host's bytes; a self-launch
        //    would generate its own map/deployment and the two peers would be in different battles.
        private static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true; // solo: native
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return true;    // connected but not in a co-op game

            if (!engine.IsHost)
            {
                Debug.LogWarning("[Multiplayer][tac] client LaunchTacticalGame BLOCKED — a client enters the " +
                                 "battle from the host's mid-tactical save transfer, never by self-launching.");
                return false;
            }

            coord.OpenTacticalEntryBarrier();
            return true;
        }
    }

    /// <summary>
    /// HOST deploy-ready → ship the battle. <c>TacticalLevelController.OnLevelStateChanged</c> is the
    /// game's own level-state listener (TacticalLevelController.cs:419); Playing means the level exists,
    /// NOT that it is playable — <c>OnLevelStart</c> is only queued there. The real "capture now" edge is
    /// <c>HasAnyTurnStarted</c>, which <c>PlayTurnCrt</c> flips through its turnStartAction only after
    /// every StartTurn plus the map-update / nav-obstacle / queued-ability / situation-cache waits
    /// (TacticalFaction.cs:398-441 → TacticalLevelController.cs:713-716). v1's proven gate; capturing
    /// earlier ships a half-built battle.
    /// </summary>
    [HarmonyPatch(typeof(TacticalLevelController), "OnLevelStateChanged")]
    internal static class TacDeployReadyCapture
    {
        // ~10 s at 60 fps. A budget, not a deadline: the capture happens either way, but a timeout is a
        // LOUD error — a silently-early capture is exactly the failure this arc must not have.
        private const int CaptureReadyMaxFrames = 600;

        private static void Postfix(TacticalLevelController __instance, Level.State state)
        {
            if (state != Level.State.Playing) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;

            Debug.Log("[Multiplayer][tac] host tactical level Playing — waiting for deploy-ready (HasAnyTurnStarted).");
            __instance.Timing.Start(CaptureWhenPlayableCrt(__instance, coord));
        }

        private static IEnumerator<NextUpdate> CaptureWhenPlayableCrt(TacticalLevelController tlc, SaveTransferCoordinator coord)
        {
            int frames = 0;
            while (!tlc.HasAnyTurnStarted && frames < CaptureReadyMaxFrames)
            {
                frames++;
                yield return NextUpdate.NextFrame;
            }

            if (!tlc.HasAnyTurnStarted)
                Debug.LogError("[Multiplayer][tac] deploy-ready gate TIMED OUT after " + CaptureReadyMaxFrames +
                               " frames — HasAnyTurnStarted never set. Capturing anyway; the client may " +
                               "receive a half-initialised battle.");
            else
                Debug.Log("[Multiplayer][tac] deploy-ready after " + frames + " frame(s) → mid-tactical save transfer.");

            // Never silent: a refused start strands every peer behind the reveal-hold armed at launch, so
            // the abort route (0x47 → client curtain lift) is the ONLY correct answer to "false".
            if (!coord.HostBeginTacticalEntryTransfer())
                coord.AbortTacticalEntryTransfer("HostBeginTacticalEntryTransfer refused to start (see the block reason logged above)");
        }
    }

    /// <summary>
    /// CLIENT spectator containment, arm 1 — the turn loop. <c>TacticalFaction.RequestEndTurn</c>
    /// (TacticalFaction.cs:382) is the ONE thing that lets <c>PlayTurnCrt</c>'s input-wait loop finish
    /// (TacticalFaction.cs:478 tests <c>_endTurnRequested</c>), so blocking it parks the client inside the
    /// player faction's turn forever. That is deliberately the LIGHTEST possible containment: the client
    /// still runs the native turn-START (vision recompute, SetViewerTacticalFaction, actor StartTurn), so
    /// it sees the battlefield exactly as the host does — it simply can never advance past it and can
    /// therefore never reach an AI faction's turn. Blocking <c>NextTurnCrt</c> instead would have skipped
    /// that setup and left the client in permanent fog.
    /// </summary>
    [HarmonyPatch(typeof(TacticalFaction), "RequestEndTurn")]
    internal static class ClientEndTurnGate
    {
        private static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            Debug.LogWarning("[Multiplayer][tac] client end-turn BLOCKED — arc A1 has no turn control; the " +
                             "client is a spectator parked in the host's current turn.");
            return false;
        }
    }

    /// <summary>
    /// CLIENT spectator containment, arm 2 — the AI. Independent trigger from arm 1 on purpose: anything
    /// that ends the client's turn some other way (a scripted faction switch, a game-over branch) would
    /// otherwise hand the client's own AI a full turn and march the aliens across a battlefield the host
    /// never moved them on. The AI coroutine is replaced with an empty one, so <c>PlayTurnCrt</c>'s
    /// <c>Timing.Current.Call(AIUpdateCrt())</c> completes immediately instead of hanging.
    /// </summary>
    [HarmonyPatch(typeof(TacticalFaction), "AIUpdateCrt")]
    internal static class ClientAiGate
    {
        private static bool Prefix(ref IEnumerator<NextUpdate> __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            Debug.LogWarning("[Multiplayer][tac] client AI turn SUPPRESSED — enemy actions are host-only.");
            __result = NoAi();
            return false;
        }

        private static IEnumerator<NextUpdate> NoAi() { yield break; }
    }
}
