using System.Collections.Generic;
using Base.Core;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
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
                // NATIVE-ENTRY EXPERIMENT (law L103, off by default): a client STILL never generates a
                // battle. What passes here is only its replay of the host's already-generated
                // TacticalGameParams through the game's own launch — NativeTacticalEntry.Replaying is set
                // for exactly the synchronous span of that one call, so a genuine self-launch is still
                // blocked on the very next line.
                if (NativeTacticalEntry.Enabled && NativeTacticalEntry.Replaying)
                {
                    coord.ArmSelfLoadBarrier("tac-entry, native local build");
                    return true;
                }
                Debug.LogWarning("[Multiplayer][tac] client LaunchTacticalGame BLOCKED — a client enters the " +
                                 "battle from the host's mid-tactical save transfer, never by self-launching.");
                return false;
            }

            coord.OpenTacticalEntryBarrier();
            // Native mode ships no save, so nothing else opens the LOADED barrier or starts the host's
            // reveal aggregation (HostTacticalEntryTransferCrt's OpenBarrier is the save path's job). Arm the
            // same self-load barrier the tac→geo return uses — every peer is loading its own level here too.
            if (NativeTacticalEntry.Enabled) coord.ArmSelfLoadBarrier("tac-entry, native local build");
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

            // NATIVE-ENTRY EXPERIMENT (law L103, off by default): in native mode every peer built this
            // battle from the host's shipped TacticalGameParams, so there is nothing to transfer — UNLESS a
            // peer said it could not (NativeTacticalEntry.FallbackArmed), which is precisely what this path
            // is the answer to. Read HERE and not at launch, because the request can arrive at any point
            // during the deploy-ready wait above.
            if (NativeTacticalEntry.Enabled && !NativeTacticalEntry.FallbackArmed)
            {
                Debug.Log("[Multiplayer][tac] native entry: every peer built the battle locally — no save " +
                          "transfer. (Flip NativeTacticalEntry.Enabled to false to restore the save path.)");
                yield break;
            }

            // Never silent: a refused start strands every peer behind the reveal-hold armed at launch, so
            // the abort route (0x47 → client curtain lift) is the ONLY correct answer to "false".
            if (!coord.HostBeginTacticalEntryTransfer())
                coord.AbortTacticalEntryTransfer("HostBeginTacticalEntryTransfer refused to start (see the block reason logged above)");
        }
    }

    /// <summary>
    /// CLIENT turn control, arm 1 — end-turn (A1 blocked it outright; A2 turns it into an INTENT).
    /// <c>TacticalFaction.RequestEndTurn</c> (TacticalFaction.cs:382) is the ONE thing that lets
    /// <c>PlayTurnCrt</c>'s input-wait loop finish (TacticalFaction.cs:478 tests <c>_endTurnRequested</c>),
    /// which makes it the model funnel this family captures — block-first through the ONE posture
    /// (<see cref="IntentRail.ShouldRunNative"/>): host and solo run native; a client's own click is
    /// converted into <see cref="SurfaceIds.TacTurnIntent"/> and never writes the local flag, so the turn
    /// ends on every peer at the same moment or on none. The client's turn DOES still end here — but only
    /// when <c>TacticalTurnSync.ClientTick</c> calls this same native method inside a
    /// <see cref="SyncApplyScope"/> after the host announced the handoff (that is the branch
    /// <c>ShouldRunNative</c> returns true for).
    /// A1's containment note still holds and is why <c>NextTurnCrt</c> is untouched: the client runs the
    /// real turn-START (vision recompute, SetViewerTacticalFaction, actor StartTurn) for every faction, so
    /// it sees the battlefield as the host does. Blocking <c>NextTurnCrt</c> would skip that and leave
    /// permanent fog.
    /// </summary>
    [HarmonyPatch(typeof(TacticalFaction), "RequestEndTurn")]
    internal static class ClientEndTurnGate
    {
        private static bool Prefix(TacticalFaction __instance)
        {
            if (IntentRail.ShouldRunNative()) return true;
            string guid = __instance?.TacticalFactionDef?.Guid;
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError("[Multiplayer][tac] client end-turn DROPPED — faction has no def guid to name in the " +
                               "intent, so the host cannot arbitrate it. The turn stays open.");
                return false;
            }
            IntentRail.Send(SurfaceIds.TacTurnIntent, TacticalTurnSync.OpEndTurn,
                            "end-turn " + __instance.TacticalFactionDef.name, w => w.Write(guid));
            return false;
        }
    }

    /// <summary>
    /// CLIENT turn control, arm 2 — the AI turn. Independent funnel from arm 1 on purpose: an AI faction's
    /// turn ends when <c>AIUpdateCrt</c> RETURNS (TacticalFaction.cs:443), not on <c>_endTurnRequested</c>.
    /// The client must never run enemy AI (the aliens would march across a battlefield the host never moved
    /// them on), but A1's instant-return empty coroutine made the client RACE: every AI turn completed in a
    /// frame, so the client ran ahead of the host into the next player turn and its turn counter drifted.
    /// A2 replaces it with a HOLD on the same predicate the player-faction release uses — the client stands
    /// frozen in the AI faction's turn for exactly as long as the host plays it.
    /// </summary>
    [HarmonyPatch(typeof(TacticalFaction), "AIUpdateCrt")]
    internal static class ClientAiGate
    {
        private static bool Prefix(TacticalFaction __instance, ref IEnumerator<NextUpdate> __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            Debug.LogWarning("[Multiplayer][tac] client AI turn SUPPRESSED — enemy actions are host-only; holding " +
                             "until the host hands the turn on.");
            __result = HoldUntilHostHandsOn(__instance);
            return false;
        }

        // ~60 s at 60 fps. Not a deadline — a long alien turn is normal — but a hold nobody can see is the
        // silent-swallow class this project keeps paying for, so it says so periodically and keeps waiting.
        private const int HoldWarnFrames = 3600;

        private static IEnumerator<NextUpdate> HoldUntilHostHandsOn(TacticalFaction faction)
        {
            string name = faction?.TacticalFactionDef?.name ?? "<unnamed faction>";
            int frames = 0;
            while (!TacticalTurnSync.HostHasLeft(faction))
            {
                if (++frames % HoldWarnFrames == 0)
                    Debug.LogWarning("[Multiplayer][tac] still holding in '" + name + "'s turn after " +
                                     (frames / 60) + "s — the host has announced no handoff yet.");
                yield return NextUpdate.NextFrame;
            }
        }
    }
}
