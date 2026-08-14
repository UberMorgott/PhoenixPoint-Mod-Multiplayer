using System.Collections.Generic;
using Base.Core;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Tactical.Entities.Abilities;
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
        /// <summary>ONE SESSION ENTRY, ONE LOAD (law L122) — armed HERE, at the geo→tac transition, and
        /// consumed by <see cref="TacDeployReadyCapture"/>. Same rule as 858eee3: arm at the transition,
        /// guard on the arm.
        ///
        /// <c>TacDeployReadyCapture</c> fires on ANY host tactical level reaching <c>Playing</c>, and
        /// "Playing" cannot tell "I launched this battle from the geoscape" (nobody else has it — ship it)
        /// from "I LOADED this battle from a blob every peer already consumed" (everybody has it — shipping
        /// it again reloads them). Loading straight into a battle from a save did exactly the second thing:
        /// the first transfer was correct and simultaneous (995,978 B, <c>reveal released: AllDone
        /// (loadedClients=2)</c>) and then a second ~996 KB blob went out on top of it, reloading only the
        /// CLIENTS while the host stayed in the level it was already in. Only the geo→tac seam knows the
        /// difference, and it is this method.</summary>
        // OWNED BY THE BARRIER, NOT BY THIS PATCH (2026-08-05). Both ends are the reveal hold's:
        // SaveTransferCoordinator.OpenTacticalEntryBarrier arms it, PerformDeferredLift /
        // AbortTacticalEntryTransfer expire it — so ONE reveal ends ONE entry no matter how the launch died.
        // It used to be armed here and cleared ONLY by a consume, which meant a launch that never reached
        // tactical Playing (aborted deployment, refused launch, failed level load) left the arm set for the
        // whole process lifetime, and the next battle LOADED FROM A SAVE consumed it: the double transfer
        // this law exists to stop, one battle later.
        private static bool _entryLaunched;

        internal static void ArmSessionEntry() => _entryLaunched = true;

        /// <summary>The arm expires with the reveal that ended its entry — an arm nobody consumed must not
        /// outlive the barrier that raised it.</summary>
        internal static void DisarmSessionEntry() => _entryLaunched = false;

        /// <summary>One-shot BY CONSTRUCTION: it clears. A guard that only reads would pass again on the
        /// next <c>Playing</c> — which, for a level nobody re-entered, is the same double transfer one beat
        /// later.</summary>
        internal static bool ConsumeSessionEntry()
        {
            bool armed = _entryLaunched;
            _entryLaunched = false;
            return armed;
        }

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
                MpLog.LogWarning("[Multiplayer][tac] client LaunchTacticalGame BLOCKED — a client enters the " +
                                 "battle from the host's mid-tactical save transfer, never by self-launching.");
                return false;
            }

            // THIS is the entry. Everything TacDeployReadyCapture is allowed to ship hangs off this one line
            // having run (law L122) — a battle reached any other way is a battle every peer already holds.
            // The arm rides INSIDE the barrier now (see _entryLaunched): same moment, same host branch, but
            // with an expiry, because the barrier is the only thing that knows when the entry ended.
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
        // static readonly, not const: see HoldCeilingFrames — L91's bounded-hold arm asserts the ldsfld, and
        // an inlined const is a magic number by the time the IL exists.
        private static readonly int CaptureReadyMaxFrames = 600;

        private static void Postfix(TacticalLevelController __instance, Level.State state)
        {
            if (state != Level.State.Playing) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;

            // LAW L122 — A BATTLE EVERYONE LOADED FROM ONE BLOB IS NOT AN ENTRY. `Playing` says a tactical
            // level exists on the host; it does not say this peer is the one that CREATED it. Loading a save
            // that is already a battle takes the ordinary F2 save-transfer path (HostSerializeAndSendCrt) —
            // every peer consumed that blob and entered the same level — and then arrived here, where the
            // unguarded capture wrote a SECOND ~996 KB mid-tactical save and reloaded the clients out of the
            // battle they had just loaded into, while the host stayed put.
            if (!TacLaunchGate.ConsumeSessionEntry())
            {
                MpLog.Log("[Multiplayer][tac] host tactical level Playing, but this peer did not LAUNCH this " +
                          "battle — it came from a save blob every peer already loaded (no geo→tac transition " +
                          "ran). No second transfer: one session entry, one load.");
                return;
            }

            MpLog.Log("[Multiplayer][tac] host tactical level Playing — waiting for deploy-ready (HasAnyTurnStarted).");
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
                MpLog.LogError("[Multiplayer][tac] deploy-ready gate TIMED OUT after " + CaptureReadyMaxFrames +
                               " frames — HasAnyTurnStarted never set. Capturing anyway; the client may " +
                               "receive a half-initialised battle.");
            else
                MpLog.Log("[Multiplayer][tac] deploy-ready after " + frames + " frame(s) → mid-tactical save transfer.");

            // NATIVE-ENTRY EXPERIMENT (law L103, off by default): in native mode every peer built this
            // battle from the host's shipped TacticalGameParams, so there is nothing to transfer — UNLESS a
            // peer said it could not (NativeTacticalEntry.FallbackArmed), which is precisely what this path
            // is the answer to. Read HERE and not at launch, because the request can arrive at any point
            // during the deploy-ready wait above.
            if (NativeTacticalEntry.Enabled && !NativeTacticalEntry.FallbackArmed)
            {
                MpLog.Log("[Multiplayer][tac] native entry: every peer built the battle locally — no save " +
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
        /// <summary>The (faction, turn) whose end has already been asked for. A faction's turn can be ended
        /// exactly ONCE, so that pair — and not the nonce — is what makes a second click a repeat: the logs
        /// show 62 and 53 intents from one impatient player, each with a genuinely distinct nonce, so
        /// <c>IntentDedup</c> is right to let them all through and the debounce belongs HERE, on the thing the
        /// game itself cannot do twice. Nothing clears it: the turn advancing changes the number.</summary>
        private static string _asked;

        private static bool Prefix(TacticalFaction __instance)
        {
            if (IntentRail.ShouldRunNative()) return true;
            string guid = __instance?.TacticalFactionDef?.Guid;
            if (string.IsNullOrEmpty(guid))
            {
                MpLog.LogError("[Multiplayer][tac] client end-turn DROPPED — faction has no def guid to name in the " +
                               "intent, so the host cannot arbitrate it. The turn stays open.");
                return false;
            }
            string once = guid + "#" + __instance.TurnNumber;
            if (once == _asked) return false;      // the same turn's end, asked for again; nothing new to send
            _asked = once;
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
            MpLog.LogWarning("[Multiplayer][tac] client AI turn SUPPRESSED — enemy actions are host-only; holding " +
                             "until the host hands the turn on.");
            __result = HoldUntilHostHandsOn(__instance);
            return false;
        }

        // ~60 s at 60 fps. Not a deadline — a long alien turn is normal — but a hold nobody can see is the
        // silent-swallow class this project keeps paying for, so it says so periodically.
        // static readonly, not const: a const is inlined into the state machine's IL and becomes
        // indistinguishable from a typed digit, so L91's bounded-hold arm could not see it (the same
        // reasoning that made NetworkEngine.MaxPlayers a field).
        private static readonly int HoldWarnFrames = 3600;

        /// <summary>~3 minutes at 60 fps. THE CEILING LAW L91 DEMANDS: this hold used to have none, by an
        /// explicit design note, and it was itself a peer waiting on another peer — the shape the prime rule
        /// forbids. 3 minutes is far past any real alien turn (the pathological 60 s+ hold in the 2026-08-05
        /// log came from the host's hint popup, which <see cref="HintWaitGate"/> now removes as a cause), so
        /// reaching this is a broken stream, not a slow one.</summary>
        private static readonly int HoldCeilingFrames = 10800;

        /// <summary>ON EXPIRY THE CLIENT RELEASES — and that is safe because releasing is NOT running blind.
        /// Returning from <c>AIUpdateCrt</c> ends only THIS faction's turn locally; the client's turn machine
        /// then enters the next faction and meets THE SAME hold again, so it can walk forward at most as far
        /// as the host's last announced cursor and stops dead there (that is the documented
        /// <c>ClientAdoptTurn</c> "catching up — a faction the host skipped" path). If the host has announced
        /// nothing at all, every faction re-arms a fresh 3-minute hold, so a peer that has genuinely lost the
        /// stream crawls one faction per ceiling with an ERROR per step rather than freezing — loud and
        /// recoverable beats silent and permanent. The one thing never done here is advancing the client's
        /// model on its own guess: no actor is moved, no turn is ended for anyone else, and the host's next
        /// cursor message re-anchors the peer.</summary>
        private static IEnumerator<NextUpdate> HoldUntilHostHandsOn(TacticalFaction faction)
        {
            string name = faction?.TacticalFactionDef?.name ?? "<unnamed faction>";
            int frames = 0;
            while (!TacticalTurnSync.HostHasLeft(faction))
            {
                if (++frames >= HoldCeilingFrames)
                {
                    MpLog.LogError("[Multiplayer][tac] turn hold CEILING reached in '" + name + "'s turn after " +
                                   (frames / 60) + "s — the host has announced no handoff. Releasing rather " +
                                   "than waiting forever (law L91): this peer moves to the next faction, where " +
                                   "the same hold re-engages, so it advances no further than the host's last " +
                                   "announced cursor.");
                    yield break;
                }
                if (frames % HoldWarnFrames == 0)
                    MpLog.LogWarning("[Multiplayer][tac] still holding in '" + name + "'s turn after " +
                                     (frames / 60) + "s — the host has announced no handoff yet.");
                yield return NextUpdate.NextFrame;
            }
        }
    }

    /// <summary>
    /// CLIENT turn control, arm 3 — THE OTHER DOOR INTO ENEMY AI (law L123, 2026-08-05).
    ///
    /// <see cref="ClientAiGate"/> above blocks <c>TacticalFaction.AIUpdateCrt</c>, which was believed to be
    /// how AI decisions are reached. It is one of TWO. <c>TacticalLevelController.
    /// ExecuteQueuedAbilitiesSequence</c>:1226-1232 runs panic → AI-evaluation → hurt-reaction, and it has a
    /// SECOND caller that no gate touches: <c>ExecuteQueuedAbilitiesEffect.OnApply</c>:22 — an ordinary
    /// authored EFFECT, started straight on <c>TacticalLevelController.Timing</c>, outside any turn
    /// coroutine. Its <c>ExecuteAIEvaluationAbilities</c>:1234-1264 then executes an
    /// <c>AIEvaluationAbility</c> on every <c>CurrentFaction</c> actor carrying an
    /// <c>AIEvaluationStatus</c> — on a client, in the alien turn, that is the aliens.
    ///
    /// That is the leak the live log shows verbatim: <c>this CLIENT activated 'Move_AbilityDef' on
    /// Fishman_17 … the client ran enemy AI of its own</c>, 10 ms BEFORE the host's own mirror of a different
    /// move, 200 ms after a <c>Panic_AbilityDef … ExecuteQueuedAbilitiesSequence:1225</c> line, and a full
    /// 8 s before <c>client AI turn SUPPRESSED</c> ever appeared. The peers then disagreed about where a
    /// cloaked enemy was, and every shot the client aimed at the one only IT could see was refused.
    ///
    /// NARROWED TO THE AI ARM ON PURPOSE. Panic and hurt-reaction are REACTIONS to something that already
    /// happened and ride the ordinary mirror; AI evaluation is a DECISION, and law 5 puts every decision on
    /// the host. Blocking the whole sequence would take the first two with it. Returning an empty coroutine
    /// (rather than <see cref="ClientAiGate"/>'s hold) is right here because this is not a turn boundary:
    /// nothing downstream is waiting on it, and the host's own run of the same evaluation arrives as
    /// ordinary 0x82 mirrors.
    /// </summary>
    [HarmonyPatch(typeof(TacticalLevelController), "ExecuteAIEvaluationAbilities")]
    internal static class ClientAiEvaluationGate
    {
        private static bool Prefix(ref IEnumerator<NextUpdate> __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            MpLog.LogWarning("[Multiplayer][tac] client AI EVALUATION suppressed — reached outside the turn " +
                             "coroutine (ExecuteQueuedAbilitiesEffect → ExecuteQueuedAbilitiesSequence), which " +
                             "ClientAiGate does not cover. An AI decision is the host's; its result arrives on " +
                             "0x82 like every other action.");
            __result = Nothing();
            return false;
        }

        private static IEnumerator<NextUpdate> Nothing() { yield break; }
    }

    /// <summary>
    /// CLIENT turn control, arm 4 — THE SEAM. Arms 2 and 3 are DOORS; this one is where the doors meet.
    ///
    /// <see cref="ClientAiEvaluationGate"/> covers ONE caller of <c>AIEvaluationAbility</c>:
    /// <c>TacticalLevelController.ExecuteAIEvaluationAbilities</c>:1257, which reaches it as
    /// <c>ability.Execute(null)</c>. That is not the only caller. <c>TacticalAbility.Execute</c>:1158-1160
    /// is a two-liner over <c>Activate(parameter)</c>, and <c>AIEvaluationStatus.OnApply</c>:17 calls that
    /// SAME <c>Activate()</c> DIRECTLY whenever <c>AIEvaluationStatusDef.ExecuteActionsOnApply</c> is set —
    /// no coroutine, no turn machinery, nothing arm 3 can see. So a status merely MIRRORED onto a client
    /// walked straight into <c>AIEvaluationAbility.ExecuteAIEvaluation</c>:32 →
    /// <c>TacticalFaction.EvaluateAiActionsAsync</c>:668 → <c>AIFaction.EvaluateActionsAsync</c>:109, and
    /// the client decided for itself — law 5's exact prohibition, reached without tripping a single gate.
    ///
    /// GATED AT THE SEAM ON PURPOSE. <c>Activate</c> is the one method BOTH doors pass through, so this
    /// closes the class instead of the instance; a gate per known call path is precisely how the previous
    /// hole opened. (<c>AIFaction.EvaluateActionsAsync</c> is the seam one level deeper and would also
    /// catch arm 2's <c>AIUpdateCrt</c>:593 — but suppressing it there leaves the caller reading a STALE
    /// <c>EvaluationResult</c>, since nulling it is the first thing the real method does. Blocking at
    /// <c>Activate</c> means nothing downstream ever reads that field.)
    ///
    /// LOUD BY CONSTRUCTION — that is the whole design, not a log-level preference. Arm 3 already stops the
    /// ordinary path UPSTREAM of here, so on a client this prefix is unreachable via any door we know
    /// about. Every line it prints is therefore a caller nobody had enumerated. A backstop that says
    /// nothing when it catches something is the silent-swallow class this project keeps paying for.
    /// </summary>
    [HarmonyPatch(typeof(AIEvaluationAbility), "Activate", new[] { typeof(object) })]
    internal static class ClientAiEvaluationSeamGate
    {
        private static bool Prefix(AIEvaluationAbility __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            MpLog.LogError("[Multiplayer][tac] client AI evaluation BACKSTOP caught '" +
                           (__instance?.TacticalActor?.name ?? "<unknown actor>") + "' — AIEvaluationAbility." +
                           "Activate was reached by a caller NO gate covers, so name it and close it: the " +
                           "known door (ExecuteAIEvaluationAbilities) is suppressed upstream by " +
                           "ClientAiEvaluationGate, and the other one on record is AIEvaluationStatus.OnApply " +
                           "under ExecuteActionsOnApply. Suppressed either way — an AI decision is the host's " +
                           "and its result arrives on 0x82 like every other action.");
            return false;
        }
    }
}
