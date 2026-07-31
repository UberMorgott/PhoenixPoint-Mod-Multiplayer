using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Base.Core;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Levels.Missions;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// TACTICAL ARC A2 — the turn cursor, end-turn and mission end. A1 put both peers into the same battle
    /// and parked the client as a spectator; A2 makes that battle PLAYABLE from start to finish by the host,
    /// with the client following the host's turn cursor and BOTH peers landing back on the geoscape.
    ///
    /// THE ONE IDEA: the client's NATIVE turn machine keeps running — it is only PACED by the host.
    /// <c>TacticalLevelController.NextTurnCrt</c> is NOT suppressed (v1 suppressed it and had to re-drive
    /// <c>PlayTurnCrt</c> by hand, re-implementing the faction cursor, the turn counter and a hydrate-race
    /// stash). Every faction's turn on the client still runs the real turn-START (vision recompute,
    /// <c>SetViewerTacticalFaction</c>, actor/effect StartTurn), which is exactly what A1 refused to skip.
    /// The client is held inside whatever faction turn it is playing until the host announces it has left
    /// that faction, and there are only two things to hold:
    ///   • a PLAYER faction's turn ends only on <c>_endTurnRequested</c> (TacticalFaction.cs:478) — so the
    ///     client's turn ends when <see cref="ClientTick"/> calls the native <c>RequestEndTurn</c> inside a
    ///     <see cref="SyncApplyScope"/>, and never otherwise;
    ///   • an AI faction's turn ends when <c>AIUpdateCrt</c> returns — so the client's AI coroutine
    ///     (<c>ClientAiGate</c>, A1) no longer returns instantly but waits on <see cref="HostHasLeft"/>.
    /// Everything else — advancing <c>_currentFactionIndex</c>, incrementing <c>TurnNumber</c>, skipping a
    /// wiped faction — stays the game's own code on both peers. The mirror therefore mostly VERIFIES
    /// (<see cref="ClientVerifyTurn"/> shouts on turn drift) instead of writing, which is why it needs no
    /// reflection into a single private field.
    ///
    /// SELF-HEALING, deliberately: if the host SKIPS a faction the client would still play (host wiped it,
    /// and A2 does not replicate death yet), the client lands on a faction the host already left — the very
    /// same predicate releases it immediately, so it spins forward one faction per frame until it catches up.
    ///
    /// END-TURN rides the EXISTING generic intent seam, no bespoke channel: the capture seam asks
    /// <see cref="IntentRail.ShouldRunNative"/> (block-first), the client emits <c>[nonce][op][factionGuid]</c>
    /// on <see cref="SurfaceIds.TacTurnIntent"/>, and <see cref="IntentRail"/> owns nonce/dedup/dispatch/
    /// reject+nudge. The host validates against its OWN cursor and runs the same native
    /// <c>RequestEndTurn</c> its own button runs; the result reaches everyone as an ordinary turn message.
    ///
    /// MISSION END: the host's native <c>TacticalLevelController.GameOver</c> is broadcast on the SAME
    /// surface (so it can never be overtaken by a stale turn message), the client sets its player faction's
    /// outcome to the host's and calls the NATIVE <c>GameOver()</c> — the game's own view machine then tears
    /// the battle down into its own battle-summary → geoscape flow. Geoscape CONSEQUENCES need no tactical
    /// message: they are save-graph state the geoscape rail already ships, and the client's local
    /// <c>GeoMission.Complete</c> is blocked (<c>ClientMissionResultGate</c>) so it never computes its own.
    /// </summary>
    public static class TacticalTurnSync
    {
        // Wire ops on SurfaceIds.TacTurn (host→all) and SurfaceIds.TacTurnIntent (client→host).
        private const byte OpTurn = 1;
        private const byte OpEnd = 2;
        internal const byte OpEndTurn = 1;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>CLIENT: the host's turn cursor as last announced. Null = the host has not announced a
        /// handoff yet in this battle (the entry turn — the save already put both peers on the same faction).</summary>
        internal static string HostFactionGuid;
        /// <summary>CLIENT: the host's PRE-increment TurnNumber for <see cref="HostFactionGuid"/>, i.e. the
        /// exact value the client's own faction must be holding when it enters that same turn.</summary>
        internal static int HostTurnNumber;
        /// <summary>CLIENT: the host has ended the mission. Releases every hold — a peer must never be able
        /// to sit in a turn hold for a battle that is already over (that is A1's stranding hole).</summary>
        internal static bool HostMissionOver;

        /// <summary>Per-BATTLE state, dropped at tactical teardown (and at session teardown). Not doing this
        /// would carry <see cref="HostMissionOver"/> into the next battle and end it on frame one.</summary>
        internal static void Reset()
        {
            Seq.Reset();
            HostFactionGuid = null;
            HostTurnNumber = 0;
            HostMissionOver = false;
        }

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler> { [OpEndTurn] = HandleEndTurn };
            IntentRail.Register(SurfaceIds.TacTurnIntent, "tac-turn", ops);
        }

        private static TacticalLevelController Tlc()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<TacticalLevelController>();
        }

        private static string Guid(TacticalFaction f) => f?.TacticalFactionDef?.Guid;
        private static string Name(TacticalFaction f) => f?.TacticalFactionDef?.name ?? "<null>";

        // ─── HOST: broadcast the cursor ────────────────────────────────────

        /// <summary>The host's authoritative turn edge. <c>TacMission.OnNewTurn(prev, next)</c> is called
        /// exactly once per faction-turn-start by <c>NextTurnCrt</c> (TacticalLevelController.cs:716), right
        /// before <c>PlayTurnCrt</c> — so <c>next.TurnNumber</c> is still the PRE-increment value on BOTH
        /// peers at this point (PlayTurnCrt does the +1, TacticalFaction.cs:390), which is what makes the
        /// client's comparison an equality and not an off-by-one guess.</summary>
        internal static void HostBroadcastTurn(TacticalFaction next)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;

            string guid = Guid(next);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError("[Multiplayer][tac] turn broadcast SKIPPED — faction '" + Name(next) + "' has no def " +
                               "guid, so no client can recognise this handoff and every one of them stays frozen in " +
                               "its current turn.");
                return;
            }
            Send(SurfaceIds.TacTurn, OpTurn, "turn '" + Name(next) + "' #" + (next.TurnNumber + 1),
                 w => { w.Write(guid); w.Write(next.TurnNumber); });
        }

        /// <summary>The host's authoritative mission end. One byte of outcome: the Player participant's
        /// <c>TacFactionState</c>, which is the whole input the native battle-summary/cutscene branch reads
        /// (TacticalView.GetLevelFinishedViewState:1071-1084). Everything else the client would need is
        /// either local presentation or geoscape state the value rail already ships.</summary>
        internal static void HostBroadcastEnd(TacticalLevelController tlc)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;

            var player = tlc?.Factions?.FirstOrDefault(f => f.ParticipantKind == TacMissionParticipant.Player);
            if (player == null)
                Debug.LogError("[Multiplayer][tac] mission-end broadcast has NO player faction to read the outcome " +
                               "from — clients will still be torn down, but their battle summary will show a loss.");
            byte state = (byte)(player?.State ?? TacFactionState.None);
            Send(SurfaceIds.TacTurn, OpEnd, "mission END outcome=" + (TacFactionState)state, w => w.Write(state));
        }

        private static void Send(byte surfaceId, byte op, string what, Action<BinaryWriter> writeBody)
        {
            var engine = NetworkEngine.Instance;
            try
            {
                uint seq = Seq.Next(surfaceId);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    w.Write(op);
                    writeBody(w);
                    inner = ms.ToArray();
                }
                var env = SyncProtocol.EncodeEnvelope(surfaceId, SyncKind.StateDelta, inner);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[Multiplayer][tac] HOST " + what + " seq=" + seq);
            }
            catch (Exception ex)
            {
                // A dropped turn message parks every client in its current turn; a dropped mission end
                // STRANDS them in a battle that no longer exists. Never silent.
                Debug.LogError("[Multiplayer][tac] HOST " + what + " broadcast FAILED — clients will not follow " +
                               "this handoff: " + ex);
            }
        }

        // ─── CLIENT: apply / verify ────────────────────────────────────────

        /// <summary>Consumes <see cref="SurfaceIds.TacTurn"/> only; every other surface (including this
        /// family's own 0x81 intent, which <see cref="IntentRail"/> owns) falls through untouched.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacTurn) return false;
            if (engine == null || engine.IsHost) return true; // the host never mirrors its own cursor
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    byte op = r.ReadByte();
                    if (!Seq.ShouldApply(SurfaceIds.TacTurn, seq)) return true; // stale re-delivery (law 7)
                    if (op == OpTurn) ApplyTurn(r.ReadString(), r.ReadInt32());
                    else if (op == OpEnd) ApplyEnd((TacFactionState)r.ReadByte());
                    else
                    {
                        Debug.LogError("[Multiplayer][tac] unknown host→all tactical op " + op + " (seq=" + seq +
                                       ") — this peer cannot follow the battle any further.");
                        return true;
                    }
                    Seq.Mark(SurfaceIds.TacTurn, seq);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] turn/end inbound FAILED — this peer may now be stranded in " +
                               "tactical: " + ex);
            }
            return true;
        }

        /// <summary>Recording the cursor is the WHOLE apply. The release is a standing condition evaluated
        /// by <see cref="ClientTick"/>/<see cref="HostHasLeft"/>, never a one-shot done here: a one-shot
        /// that landed in the window between <c>NextTurnCrt</c>'s handoff and <c>PlayTurnCrt</c>'s
        /// <c>_endTurnRequested = false</c> (its first line) would be silently erased and park the client
        /// forever.</summary>
        private static void ApplyTurn(string factionGuid, int turnNumber)
        {
            HostFactionGuid = factionGuid;
            HostTurnNumber = turnNumber;
            Debug.Log("[Multiplayer][tac] CLIENT host cursor → faction " + factionGuid + " turn " + (turnNumber + 1));
        }

        private static void ApplyEnd(TacFactionState playerState)
        {
            HostMissionOver = true; // releases every turn hold even if the teardown below cannot run
            var tlc = Tlc();
            if (tlc == null)
            {
                Debug.LogError("[Multiplayer][tac] host mission END arrived with no live tactical level on this " +
                               "peer — nothing to tear down (already left the battle?).");
                return;
            }
            if (tlc.IsGameOver) return; // idempotent: the native flow is already running

            var player = tlc.Factions?.FirstOrDefault(f => f.ParticipantKind == TacMissionParticipant.Player);
            if (player == null)
                Debug.LogError("[Multiplayer][tac] host mission END: this peer has no Player-participant faction, " +
                               "so the authoritative outcome cannot be stamped — the summary will show a loss.");
            else
                player.State = playerState; // the ONE authoritative outcome bit; the client computes none of it

            // The NATIVE game-over, inside an apply scope so ClientGameOverGate lets it through and the
            // broadcast postfix stays host-only (law 8). From here the game's own machinery runs: GameOverEvent
            // → TacticalView.OnGameOver → battle summary → GoToGeoscape → FinishLevel → LoadCurrentGeoscape.
            using (SyncApplyScope.Enter()) tlc.GameOver();
            Debug.Log("[Multiplayer][tac] CLIENT applied host mission END outcome=" + playerState +
                      " — native teardown → battle summary → geoscape.");
        }

        /// <summary>The client entered a faction's turn: the mirror's VERIFY half. Silent when the two peers
        /// agree, which is the normal case — a warning here is the catch-up spin, an error is real drift.</summary>
        internal static void ClientVerifyTurn(TacticalFaction next)
        {
            if (HostFactionGuid == null) return; // entry turn: the save put both peers on the same faction
            string guid = Guid(next);
            if (guid != HostFactionGuid)
            {
                Debug.LogWarning("[Multiplayer][tac] client entered '" + Name(next) + "'s turn while the host is on " +
                                 HostFactionGuid + " — catching up (a faction the host skipped); the same hold " +
                                 "releases it again this frame.");
                return;
            }
            if (next.TurnNumber != HostTurnNumber)
                Debug.LogError("[Multiplayer][tac] TURN DRIFT: this peer is starting turn " + (next.TurnNumber + 1) +
                               " for '" + Name(next) + "' but the host announced turn " + (HostTurnNumber + 1) +
                               " — the peers are no longer on the same turn.");
        }

        // ─── CLIENT: the ONE hold predicate ────────────────────────────────

        /// <summary>Has the host left <paramref name="faction"/>'s turn? The single condition both client
        /// holds wait on. A null cursor means the host has announced nothing yet in this battle, which is
        /// NOT permission to advance — the client stays where the entry save put it.</summary>
        internal static bool HostHasLeft(TacticalFaction faction)
        {
            if (HostMissionOver) return true;
            var guid = Guid(faction);
            return HostFactionGuid != null && guid != null && HostFactionGuid != guid;
        }

        /// <summary>Standing release for a PLAYER faction's turn on the client (driven from
        /// <c>SyncEngine.Tick</c>, every frame). <c>RequestEndTurn</c> only sets a bool, so re-running it
        /// while the turn winds down is free; going through the native method (inside an apply scope, so the
        /// capture seam lets it run — law 8) is what keeps this from being a second, divergent way to end a
        /// turn. AI factions are held by <c>ClientAiGate</c> instead — same predicate, different funnel.</summary>
        internal static void ClientTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
            var tlc = Tlc();
            if (tlc == null || tlc.IsGameOver) return;
            var cur = tlc.CurrentFaction;
            if (cur == null || !cur.IsControlledByPlayer || !cur.IsPlayingTurn) return;
            if (!HostHasLeft(cur)) return;
            using (SyncApplyScope.Enter()) cur.RequestEndTurn();
        }

        // ─── HOST: the end-turn intent ─────────────────────────────────────

        /// <summary>The whole acceptance decision as a PURE function of the HOST's own turn cursor: null =
        /// accept, otherwise the human reason. Pure so the race it arbitrates is testable headless
        /// (RailCheck L63) — v1's turn arbiter was in-game-only and "any client may end any turn" went
        /// unnoticed. A reason is never blank: a silently eaten end-turn click is exactly this family's
        /// bug class.</summary>
        internal static string Validate(string wantedGuid, string currentGuid, bool currentIsPlayerControlled,
                                        bool currentIsPlayingTurn)
        {
            if (string.IsNullOrEmpty(wantedGuid)) return "end-turn intent carried no faction guid";
            if (currentGuid == null) return "no faction is playing a turn on the host right now";
            if (wantedGuid != currentGuid)
                return "faction " + wantedGuid + " is not the host's current faction " + currentGuid +
                       " — a peer cannot end a turn it does not own";
            if (!currentIsPlayerControlled)
                return "faction " + currentGuid + " is AI-controlled — only the shared player faction's turn " +
                       "can be ended by a peer";
            if (!currentIsPlayingTurn)
                return "faction " + currentGuid + " is not playing its turn yet — the host is mid-handoff";
            return null;
        }

        private static void HandleEndTurn(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string wanted = r.ReadString();
            var cur = Tlc()?.CurrentFaction;
            string why = Validate(wanted, Guid(cur), cur != null && cur.IsControlledByPlayer,
                                  cur != null && cur.IsPlayingTurn);
            if (why != null)
            {
                // No path prefix: nothing on the GEOSCAPE rail is touched by a tactical reject, and the
                // reject nudge is what repaints the gesturing client's own screen.
                IntentRail.Reject(SurfaceIds.TacTurnIntent, senderPeerId, why);
                return;
            }
            cur.RequestEndTurn(); // the same native call the host's own end-turn button makes
            Debug.Log("[Multiplayer][tac] HOST end-turn intent from peer=" + senderPeerId + " ACCEPTED for '" +
                      Name(cur) + "' turn " + cur.TurnNumber);
        }
    }

    /// <summary>
    /// The turn edge, both halves. Host broadcasts the cursor; client verifies its own against it. One patch
    /// because it is one edge: <c>TacMission.OnNewTurn</c> is called once per faction-turn-start by
    /// <c>NextTurnCrt</c> on EVERY peer running the native turn machine (TacticalLevelController.cs:716).
    /// </summary>
    [HarmonyPatch(typeof(TacMission), "OnNewTurn")]
    internal static class TacNewTurnHook
    {
        private static void Postfix(TacticalFaction nextFaction)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            // A3b: the FIRST turn edge is the one moment every peer is provably looking at the same board —
            // the client's battle was just built from the host's mid-tactical save, so nobody has moved yet.
            // TacticalActorKey.BuildBattleKeys is a hard one-shot for exactly that reason (see its doc): after
            // this, aliens move on the host and never on a client (A2's ClientAiGate), so a later rebuild would
            // give the two peers different maps and point every alien key at the wrong monster.
            TacticalActorKey.BuildBattleKeys(nextFaction == null ? null : nextFaction.TacticalLevel);
            if (engine.IsHost) TacticalTurnSync.HostBroadcastTurn(nextFaction);
            else TacticalTurnSync.ClientVerifyTurn(nextFaction);
        }
    }

    /// <summary>
    /// MISSION END, both halves on the one native funnel. PREFIX (sim gating, law 4b): a client must never
    /// declare the battle over on its own — its <c>GameOverCondition</c> evaluates win/lose against a mirror
    /// whose actors A2 does not yet replicate, so it would end the battle on a state the host never reached.
    /// Block-first through the ONE posture (<see cref="IntentRail.ShouldRunNative"/>): host and solo run
    /// native, and the client runs it only from inside <c>ApplyEnd</c>'s apply scope. POSTFIX (host only):
    /// ship the outcome. A Harmony postfix still runs when a prefix skipped the original, which is exactly
    /// what is wanted — the blocked client must not broadcast anything, and the host gate inside does that.
    /// </summary>
    [HarmonyPatch(typeof(TacticalLevelController), "GameOver")]
    internal static class ClientGameOverGate
    {
        private static bool Prefix()
        {
            if (IntentRail.ShouldRunNative()) return true;
            Debug.LogWarning("[Multiplayer][tac] client-local GameOver BLOCKED — mission end is the host's call and " +
                             "arrives on the turn surface; a client deciding it alone would end the battle on state " +
                             "the host never reached.");
            return false;
        }

        private static void Postfix(TacticalLevelController __instance) => TacticalTurnSync.HostBroadcastEnd(__instance);
    }

    /// <summary>
    /// TACTICAL TEARDOWN, on host AND client (<c>OnLevelStateChanged</c> reaches <c>OnLevelEnd</c> for any
    /// transition out of Playing — TacticalLevelController.cs:432-436). Two jobs, one edge:
    ///   • drop this battle's mirror state, so the next battle does not inherit a set mission-over flag;
    ///   • arm <see cref="SaveTransferCoordinator.OpenReturnBarrier"/> — the tac→geo return has NO save
    ///     transfer (each peer rides the native mission end to its own geoscape load), so nothing else
    ///     re-arms the synchronized reveal and the first peer to finish loading would lift its curtain while
    ///     the others were still loading. This was A1's known dead code: the method existed with no caller.
    /// </summary>
    [HarmonyPatch(typeof(TacticalLevelController), "OnLevelStateChanged")]
    internal static class TacLevelEndBarrier
    {
        private static void Postfix(Level.State prevState, Level.State state)
        {
            // The native condition, verbatim: the switch RETURNS for Loading and Playing, so OnLevelEnd runs
            // only on a transition out of Playing into anything else (TacticalLevelController.cs:421-436).
            if (state == Level.State.Loading || state == Level.State.Playing) return;
            if (prevState != Level.State.Playing) return;
            TacticalTurnSync.Reset();
            TacticalCommandSync.Reset();   // A3a: 0x82 seq + pending settles must not survive into the next battle
            TacticalDamageSync.Reset();    // A3b: 0x84 seq + the gap cursor + any leaked mirror-apply depth
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            engine.SaveTransfer?.OpenReturnBarrier();
        }
    }

    /// <summary>
    /// The client must not compute its OWN geoscape outcome. <c>GeoMission.Complete(TacMissionResult)</c>
    /// (GeoMission.cs:267) is the one funnel that applies a battle to the campaign — casualties, XP, rewards,
    /// faction effects, site destruction — and <c>GeoLevelController</c> calls it on every peer with the
    /// result that peer's own <c>TacticalView.GoToGeoscape</c> built out of its own (unreplicated) actors.
    /// Blocked block-first: the host applies the real one, and its consequences reach the client as ordinary
    /// save-graph state on the value rail — no tactical message carries them.
    /// </summary>
    [HarmonyPatch(typeof(PhoenixPoint.Geoscape.Entities.GeoMission), "Complete")]
    internal static class ClientMissionResultGate
    {
        private static bool Prefix()
        {
            if (IntentRail.ShouldRunNative()) return true;
            Debug.LogWarning("[Multiplayer][tac] client-local GeoMission.Complete BLOCKED — the mission outcome is " +
                             "the host's, and its geoscape consequences arrive on the value rail as ordinary state.");
            return false;
        }
    }

    /// <summary>
    /// DIAGNOSTIC ONLY — NO BEHAVIOUR. On maps with a special squad the HOST gets a camera move onto the
    /// elite unit plus a left-side info panel before the combat UI, and clients jump straight to the ready
    /// UI. That panel is the NATIVE ContextHelp hint system, not a cutscene and not a TFTV widget:
    /// <c>TacContextHelpManager</c>:118 subscribes <c>ActorSawOtherFactionActorEvent</c> → <c>OnActorSeen</c>
    /// :199 → <c>EventTypeTriggered(HintTrigger.ActorSeen, …)</c>:205, and the panel + camera move are
    /// <c>UIStateTacticalContextHelp.EnterState</c>:75 → <c>TryFocusCameraOnContext</c>:98-110 →
    /// <c>DoCameraChase()</c>. TFTV only registers <c>ContextHelpHintDef</c>s on that trigger
    /// (<c>TFTVHints.cs</c>:400-410).
    ///
    /// THE HYPOTHESIS THIS LINE EXISTS TO KILL OR CONFIRM: the mission-start replay lives in
    /// <c>TacContextHelpManager.OnStartTurn</c>:244-262, gated <c>nextFaction == _tacLevel.FirstFaction &amp;&amp;
    /// nextFaction.TurnNumber == 1</c>, and it is what walks <c>Vision.KnownActors</c> re-firing
    /// <c>OnActorSeen</c> for everything already visible. The host crosses that edge natively; the client's
    /// battle is BUILT FROM A MID-TACTICAL SAVE that is already past it, so the replay loop may never run.
    ///
    /// WHY THE PREFIX SITS ON THAT EXACT METHOD rather than on <c>NewTurnEvent</c>: the gate reads
    /// POST-increment <c>TurnNumber</c> (<c>PlayTurnCrt</c> does the +1 before raising the event, whereas
    /// <c>TacMission.OnNewTurn</c> at TacticalLevelController.cs:712 still sees the pre-increment value), so
    /// logging anywhere else would print a different number than the gate tests. Patching the gate's own
    /// method also proves whether it RUNS on the client at all. Both members read here are public —
    /// <c>TacticalLevelController.FirstFaction</c>:187 and <c>TacticalFactionVision.KnownActors</c>:115 — so
    /// no reflection is needed to read them; <c>AccessTools</c> is used only because <c>OnStartTurn</c>
    /// itself is private. NO FIX IS ATTEMPTED IN THIS BATCH.
    /// </summary>
    [HarmonyPatch]
    internal static class ContextHelpTurnEdgeProbe
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(PhoenixPoint.Tactical.ContextHelp.TacContextHelpManager), "OnStartTurn",
                               new[] { typeof(TacticalFaction), typeof(TacticalFaction) });

        private static void Prefix(TacticalFaction nextFaction)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            var tlc = nextFaction == null ? null : nextFaction.TacticalLevel;
            bool isFirst = tlc != null && nextFaction == tlc.FirstFaction;
            int turn = nextFaction == null ? -1 : nextFaction.TurnNumber;
            int known = (nextFaction == null || nextFaction.Vision == null)
                            ? -1 : nextFaction.Vision.KnownActors.Count;
            Debug.Log("[Multiplayer][tac] contexthelp turn-edge: host=" + engine.IsHost +
                      " faction='" + (nextFaction == null ? "<null>" : nextFaction.TacticalFactionDef?.name) +
                      "' isFirstFaction=" + isFirst + " turnNumber=" + turn + " knownActors=" + known +
                      " → missionStartReplay=" + (isFirst && turn == 1));
        }
    }
}
