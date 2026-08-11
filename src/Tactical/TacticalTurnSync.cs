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
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Common.Levels.Params;
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
        private const byte OpLeave = 3;
        // 4 host→all is TacticalReadySync.OpReadyTally — it ships on THIS surface (see Send's doc).
        private const byte OpRestart = 5;
        internal const byte OpEndTurn = 1;
        internal const byte OpLeaveBattle = 2;
        // 3 client→host is TacticalReadySync.OpSetReady.
        internal const byte OpRestartMission = 4;

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

        /// <summary>HOST: the mission end has been ANNOUNCED — the mission-end sweep has gone out and OpEnd
        /// behind it, so the host has said its last word about this board (law L334). Deliberately NOT
        /// <c>tlc.IsGameOver</c>: that is already true when <c>HostBroadcastEnd</c> runs (it is a POSTFIX on
        /// <c>TacticalLevelController.GameOver</c>), so a guard reading it would silence the mission-end sweep
        /// itself — the very correction that makes the scored board the host's.</summary>
        internal static bool MissionEndSent;

        /// <summary>THE BATTLE IS SCORED — on EITHER role. Both flags are one-way and role-exclusive
        /// (<see cref="MissionEndSent"/> is written only by the host's announcement, <see cref="HostMissionOver"/>
        /// only by a client applying it), so this is simply "has this peer passed the mission end yet".</summary>
        internal static bool BattleAlreadyScored => MissionEndSent || HostMissionOver;

        /// <summary>Per-BATTLE state, dropped at tactical teardown (and at session teardown). Not doing this
        /// would carry <see cref="HostMissionOver"/> into the next battle and end it on frame one.</summary>
        internal static void Reset()
        {
            Seq.Reset();
            HostFactionGuid = null;
            HostTurnNumber = 0;
            HostMissionOver = false;
            MissionEndSent = false;            // carrying this into the next battle would mute it on frame one
            LeftBattle = false;
            _pendingSweepWhen = null;          // a sweep owed to a battle that is over must not fire in the next
            ClientMissionStartHints.Reset();   // the next battle gets its own mission-start replay
            HintMirror.Reset();                // ditto for the 0x8A mirror: a name held over silences it next time
            TacticalUiRepaint.Reset();         // drop the paint memo: it names an actor of the dead battle
            TacticalReadySync.Reset();         // advisory ready flags + the cloned button's handles
        }

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpEndTurn] = HandleEndTurn,
                [OpLeaveBattle] = HandleLeaveBattle,
                [OpRestartMission] = HandleRestartMission,
                // ADVISORY, gates nothing (RailCheck L119). It rides this family because it is the same
                // question one step earlier: "I am done with this turn" → "end this turn" → "we are done
                // with this battle".
                [TacticalReadySync.OpSetReady] = TacticalReadySync.HandleSetReady,
            };
            IntentRail.Register(SurfaceIds.TacTurnIntent, "tac-turn", ops);

            // ARM THE TURN-EPOCH GATE on the ONE inbound chokepoint. Here rather than in a tactical-init
            // step because this is the method that already declares "the turn cursor is this file's job",
            // and the predicate self-guards (host / no session / no level → false), so a standing
            // assignment is safe and re-arming per battle is a no-op.
            SurfaceRouter.ClientBehindTurnEdge = ClientBehindTurnEdge;
        }

        private static TacticalLevelController Tlc() => TacticalDamageSync.Tlc();

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

            // NO KEYED LIVE ACTOR'S LAST SETTLE MAY BE OLDER THAN THE CURRENT FACTION TURN (law L123) — but
            // NOT STAMPED FROM HERE. See <see cref="HostSweepTick"/> for why this is an arm and not a call.
            _pendingSweepWhen = "turn '" + Name(next) + "' #" + (next.TurnNumber + 1);
            HostSweepTick(engine);
        }

        /// <summary>The turn-edge settle sweep, WAITING FOR THIS HOST'S OWN TURN TO ACTUALLY START.
        ///
        /// THE REPORT (2026-08-05, DLL 964608 B). The player ends a turn, the aliens play, the player's turn
        /// begins — and on every CLIENT the soldiers stand there with the action points they ENDED the
        /// previous turn on. On the host they are full.
        ///
        /// AP RESTORE IS NATIVE AND PER-PEER, and it does run on the client: <c>PlayTurnCrt</c>:422-425 calls
        /// <c>TacticalActor.StartTurn</c> → <c>RestartAbilities</c>:1244 → <c>ActionPoints.SetToMax()</c>.
        /// Nothing replicates it and nothing needs to. What went wrong is the ORDER a value landed in.
        ///
        /// THE SWEEP USED TO BE STAMPED IN NEITHER EPOCH. <c>TacMission.OnNewTurn</c> — where
        /// <see cref="HostBroadcastTurn"/> runs — is raised by <c>NextTurnCrt</c>:716 BEFORE
        /// <c>PlayTurnCrt</c>, so a sweep emitted there reads every actor's AP from BEFORE the host's own
        /// restore. It is not "the host's authority for the new turn"; it is last turn's leftovers wearing the
        /// new turn's timestamp. That was invisible only while clients applied it EARLY — they then restored
        /// AP themselves, afterwards, and the stale number was overwritten. The turn-epoch gate
        /// (<see cref="SurfaceRouter.ClientBehindTurnEdge"/>) inverted exactly that: the sweep is now held
        /// until this peer crosses its own edge, i.e. until AFTER its own restore, and the stale number wins.
        /// Measured, <c>D:\PP-Instance2\Player.log</c>: frame 10059 host cursor → Phoenix turn 3, frame 10061
        /// "Changing turn to \"Phoenix\" | Turn 3" (the client's own restore), frame 10062 six
        /// "CLIENT settled … ap=0" lines, one per soldier.
        ///
        /// THE FIX IS ON THE EMITTER, NOT ON THE GATE. The gate's premise is that the host stamps records in
        /// exactly two epochs; the host was breaking that premise itself, in the one window between announcing
        /// an edge and finishing it. So the sweep waits for the game's OWN "this turn has started" flag —
        /// <c>TacticalFaction.IsPlayingTurn</c> (TacticalFaction.cs:441, set immediately after every actor's
        /// StartTurn) — and then ships post-restore AP, which agrees with the client whether it is applied
        /// before or after the client's own restore. The race is not narrowed; it stops existing.
        ///
        /// BOUNDED BY CONSTRUCTION, so a pending sweep can never sit forever: <c>OnNewTurn</c> is only raised
        /// for a faction with alive or undeployed actors (<c>NextTurnCrt</c>:698), and that faction always
        /// goes on to <c>PlayTurnCrt</c> — <c>IsPlayingTurn</c> always becomes true. A later announcement
        /// overwrites the pending one rather than queueing, so at most one sweep is ever owed.
        ///
        /// Called eagerly from <see cref="HostBroadcastTurn"/> as well as from <c>SyncEngine.Tick</c>: a turn
        /// announced for a faction that is ALREADY playing sweeps on the spot instead of a frame later. Under
        /// the native ordering above that first evaluation is false, and the tick is what fires it.</summary>
        private static string _pendingSweepWhen;

        internal static void HostSweepTick(NetworkEngine engine)
        {
            if (_pendingSweepWhen == null) return;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) { _pendingSweepWhen = null; return; }
            var cur = Tlc()?.CurrentFaction;
            if (cur == null || !cur.IsPlayingTurn) return;
            string when = _pendingSweepWhen;
            _pendingSweepWhen = null;
            TacticalCommandSync.HostSettleAllLive(when);
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
            // THE LAST BOARD EVERY PEER SCORES MUST BE THE HOST'S (law L329). Every peer computes its own
            // objectives and its own XP off its own actors — RescueSoldiersFactionObjective.EvaluateObjective
            // :37-72 counts what IT can see, and TacticalFaction.GiveExperienceForObjectives:207-294 is run
            // LOCALLY on each peer from LOCAL objective state. The turn-edge sweep (HostSweepTick) is what
            // normally makes those boards agree, and a mission that ends on the same turn its last unit
            // evacuates never reaches another turn edge: nothing heals a divergence introduced during that
            // final turn. Live 2026-08-08: 3 evacuated on the host, 2 on the client, XP diverged with it.
            // So the sweep runs HERE, immediately before the outcome goes out, on the same ordered stream —
            // the settles are on the wire ahead of OpEnd, which is ahead of the native GameOver the clients
            // run from ApplyEnd.
            TacticalCommandSync.HostSettleAllLive("mission end");
            byte state = (byte)(player?.State ?? TacFactionState.None);
            Send(SurfaceIds.TacTurn, OpEnd, "mission END outcome=" + (TacFactionState)state, w => w.Write(state));
            // AND THE LAST WORD IS THE ONE ABOVE (law L334). ORDER-CRITICAL, both halves: the latch is set
            // AFTER the sweep (setting it first would silence the sweep, since HostSettle now reads it) and
            // AFTER the Send (so nothing can slip between the correction and the verdict it belongs to).
            MissionEndSent = true;
        }

        /// <summary>The host is LEAVING the finished battle — carry every other peer out with it. Sent from
        /// the ONE point the host's own native exit passes (<see cref="OnLocalLeaveBattle"/>), so the host's
        /// own Continue click and a client's accepted <see cref="OpLeaveBattle"/> ask emit the SAME message:
        /// <see cref="HandleLeaveBattle"/> reaches <c>GoToGeoscape</c> by INVOKING it, which re-enters that
        /// prefix. Rides 0x80 next to <c>OpEnd</c> for the reason that surface exists at all — one ordered
        /// stream is what makes it impossible for the leave to overtake the mission end it follows.</summary>
        private static void HostBroadcastLeave()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;
            Send(SurfaceIds.TacTurn, OpLeave, "battle LEFT — every peer follows", w => { });
        }

        /// <summary>THE HOST IS RESTARTING THE MISSION — carry every peer through the SAME reload (law L328).
        ///
        /// The native restart is a level change like any other and the game gives it no method of its own:
        /// <c>UIModulePauseScreen.OnRestartConfirmed</c>:203-211 calls <c>PhoenixGame.FinishLevel(new
        /// RestartGameResult(GameUtl.CurrentLevel().LevelParams))</c> and <c>TacticalGameCrt</c>:574-580 loops
        /// that result back into <c>RunGameLevel</c> — a full teardown and reload of the same map. So the
        /// only thing that distinguishes a restart is the RESULT TYPE at the funnel this mod already patches
        /// (<see cref="Multiplayer.Harmony.LoadBarrierGate"/>), and until this arm landed nothing anywhere in
        /// the mod mentioned <c>RestartGameResult</c> at all: the presser reloaded ALONE. Live 2026-08-08 —
        /// the client pressed restart, loaded a fresh level by itself, and the host sat pinned at 68 % while
        /// the two peers fought different tactical levels with different key maps.
        ///
        /// Rides 0x80 next to <c>OpEnd</c>/<c>OpLeave</c> for the same reason they do: one ordered stream in
        /// which a restart cannot overtake, or be overtaken by, the turn edge or the mission end.
        ///
        /// This is NOT a quorum and cannot become one — nobody waits for anybody to press anything. One
        /// player restarts and every peer follows, exactly like one player's Continue takes everyone out of
        /// a finished battle. The simultaneous-start barrier that follows waits only on LOADS.</summary>
        private static void HostBroadcastRestart()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;
            Send(SurfaceIds.TacTurn, OpRestart, "mission RESTART — every peer reloads the same level", w => { });
        }

        /// <summary>internal, not private: <see cref="TacticalReadySync"/> ships its advisory tally as op 4
        /// of THIS surface, and a second emitter with its own SurfaceSeq would break the one property 0x80
        /// exists for — a single ordered stream in which nothing can overtake the turn edge.</summary>
        internal static void Send(byte surfaceId, byte op, string what, Action<BinaryWriter> writeBody)
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
                    // A NEW BATTLE'S STREAM STARTS AT 1 AGAIN, and this surface is the one that gets a
                    // message AFTER the receiver has already reset (HostBroadcastLeave exists to reach a
                    // peer that has not left yet), so the old cursor can outlive the reset that dropped
                    // it. SurfaceSeq.IsStreamRestart is what lets that stream back in; say so, because a
                    // 0x80 message dropped here is a peer stranded in a battle nobody can end for it.
                    uint cursor = Seq.LastApplied(SurfaceIds.TacTurn);
                    if (SurfaceSeq.IsStreamRestart(seq, cursor))
                        Debug.LogWarning("[Multiplayer][tac] host turn stream RESTARTED at seq=1 while this " +
                                         "peer's cursor was " + cursor + " — a trailing message of the previous " +
                                         "battle re-armed it after the teardown reset. Rewinding and applying; " +
                                         "without this every turn edge, mission end and leave of THIS battle is " +
                                         "dropped in silence.");
                    if (!Seq.ShouldApply(SurfaceIds.TacTurn, seq))
                    {
                        // AND A REFUSAL IS NEVER SILENT ON THIS SURFACE. The restart rule above covers the
                        // cause we found; this covers the CLASS. 0x80 carries a handful of messages per
                        // battle — turn edges, the mission end, the leave — so logging every refusal costs
                        // nothing, and the alternative is what happened on 2026-08-07: a whole battle in
                        // which not one of them applied, with nothing in any log to say so.
                        Debug.LogWarning("[Multiplayer][tac] host turn/end/leave message seq=" + seq + " op=" + op +
                                         " REFUSED as stale (cursor " + cursor + "). If this repeats for a whole " +
                                         "battle this peer will never be told the mission ended or that the host " +
                                         "left, and the host will wait on a reveal barrier it can never open.");
                        return true; // stale re-delivery (law 7)
                    }
                    if (op == OpTurn) ApplyTurn(WireString.ReadKey(r), r.ReadInt32());
                    else if (op == OpEnd) ApplyEnd((TacFactionState)r.ReadByte());
                    else if (op == OpLeave) ApplyLeave();
                    else if (op == OpRestart) ApplyRestart();
                    else if (op == TacticalReadySync.OpReadyTally)
                        TacticalReadySync.ApplyTally(r.ReadInt32(), r.ReadInt32());
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

        /// <summary>The host restarted the mission — this peer runs the SAME native restart, on its own level
        /// params, so both sides re-enter <c>TacticalGameCrt</c>'s reload loop together and the
        /// simultaneous-start barrier (armed for this boundary by <c>LoadBarrierGate</c>, which sees this very
        /// <c>FinishLevel</c>) holds every curtain down until the last peer is in.
        ///
        /// It is the pause screen's own two lines verbatim (<c>UIModulePauseScreen.OnRestartConfirmed</c>
        /// :209-211), inside a <see cref="SyncApplyScope"/> so this peer's own <c>FinishLevel</c> capture lets
        /// it through instead of bouncing the ask back to the host as a second restart (law 8).</summary>
        private static void ApplyRestart()
        {
            // Opens the trace on a FOLLOWER (this runs BEFORE its own FinishLevel reaches LoadBarrierGate),
            // and continues an already-open one on the client whose own press was blocked into an ask —
            // which is what makes the elapsed on that peer the full press → host → reload round trip.
            RestartTrace.Mark("the host's RESTART announcement arrived — this peer is about to run its own " +
                              "FinishLevel(RestartGameResult) inside a SyncApplyScope.");
            var level = GameUtl.CurrentLevel();
            var game = GameUtl.GameComponent<PhoenixGame>();
            if (level == null || game == null)
            {
                RestartTrace.Note("...and there is NO live level/PhoenixGame here, so nothing reloads on this " +
                                  "peer. From here on it is in a different level from the host.");
                Debug.LogError("[Multiplayer][tac] host mission RESTART arrived with no live level on this peer " +
                               "— nothing to reload here, so this peer and the host are now in different levels " +
                               "and every actor key they exchange names a different board.");
                return;
            }
            using (SyncApplyScope.Enter()) game.FinishLevel(new RestartGameResult(level.LevelParams));
            Debug.Log("[Multiplayer][tac] CLIENT applied host mission RESTART — reloading this level through " +
                      "the game's own restart loop; the curtain stays down until every peer is back in.");
        }

        /// <summary>SOMEBODY pressed Continue and the host left the battle — so does this peer. Without it,
        /// only the clicking peer and the host returned and everyone else sat on their own battle summary
        /// until a human clicked it there too (live 2026-08-01: peer=1 left at 18:23:23, peer=2 was still in
        /// tactical 28 s later and MISSED both post-mission event windows — <c>EventPopup</c> could only drop
        /// a raise a peer has no geoscape to show it in). The mission is over and its outcome is already the
        /// host's, so there is nothing left for a lingering peer to decide; a battle summary is not a
        /// decision, it is a page.
        ///
        /// Runs the peer's OWN native exit, the same private <c>GoToGeoscape</c> its own Continue button
        /// runs — nothing about the result crosses (law 5) and this peer's <c>GeoMission.Complete</c> stays
        /// gated. Inside a <see cref="SyncApplyScope"/> so the capture prefix does not send the ask back to
        /// the host as a second leave (law 8, direct echo loop). A peer that already clicked Continue itself
        /// (<see cref="LeftBattle"/>) or has no level any more is a NO-OP, not an error: that is the ordinary
        /// race of peers clicking at their own pace, and it is what makes the op idempotent (law 7).
        ///
        /// COST, stated plainly: a mission with a win/lose cutscene exits BattleSummary → cutscene →
        /// GoToGeoscape (<c>GetLevelFinishedViewState</c>:1105-1109), and a peer carried out this way skips
        /// its own cinematic. That is the price of "one peer's Continue takes everyone", which is what the
        /// user asked for.</summary>
        private static void ApplyLeave()
        {
            var tlc = Tlc();
            if (tlc == null || LeftBattle)
            {
                Debug.Log("[Multiplayer][tac] CLIENT host-left-battle: nothing to do — this peer " +
                          (LeftBattle ? "already left on its own click" : "holds no tactical level"));
                return;
            }
            if (GoToGeoscapeMethod == null)
            {
                Debug.LogError("[Multiplayer][tac] CLIENT host-left-battle CANNOT run — TacticalView.GoToGeoscape " +
                               "did not resolve, so this peer stays stranded on its battle summary while every " +
                               "other peer is back on the geoscape.");
                return;
            }
            // Direct, NOT through InvokeNativeLeave: L64's leave-apply-hand-rolled arm reads THIS method's IL
            // for the native handle and does not follow a helper. The SyncApplyScope therefore does double
            // duty — it suppresses the capture's echo (law 8) AND is what tells ReturnCountdown's hold that
            // this is the mod carrying the peer out, not the peer clicking Continue, so the exit runs HERE
            // and now rather than five seconds later outside this scope, where the capture would have sent
            // a leave ask straight back to the host. No latch rollback is needed: a peer being carried out
            // has broadcast nothing to retract and its own Continue still works.
            using (SyncApplyScope.Enter()) GoToGeoscapeMethod.Invoke(tlc.View, null);
            Debug.Log("[Multiplayer][tac] CLIENT host-left-battle APPLIED — running this peer's own " +
                      "GoToGeoscape → FinishLevel → geoscape.");
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

        /// <summary>THE TURN-EPOCH GATE's predicate (law L96) — armed onto
        /// <see cref="SurfaceRouter.ClientBehindTurnEdge"/> in <see cref="RegisterIntents"/>, which is where
        /// the full reasoning lives. TRUE means: the host has announced a faction this peer is not standing
        /// on yet, so every record arriving right now was stamped on the far side of an edge this peer has
        /// not crossed. Deliberately NOT <see cref="HostHasLeft"/> with the current faction passed in —
        /// that one returns TRUE on <see cref="HostMissionOver"/> because its job is to release holds, and
        /// releasing a hold is the opposite of what a battle-over peer should do to its inbox.</summary>
        internal static bool ClientBehindTurnEdge()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return false;
            if (HostFactionGuid == null || HostMissionOver) return false;
            var guid = Guid(Tlc()?.CurrentFaction);
            return guid != null && guid != HostFactionGuid;
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
            string wanted = WireString.ReadKey(r);
            var cur = Tlc()?.CurrentFaction;
            string why = Validate(wanted, Guid(cur), cur != null && cur.IsControlledByPlayer,
                                  cur != null && cur.IsPlayingTurn);
            if (why != null)
            {
                // No path prefix: nothing on the GEOSCAPE rail is touched by a tactical reject, and the
                // reject nudge is what repaints the gesturing client's own screen.
                // NOTIFY: every reason Validate can return is about TURN OWNERSHIP — a thing only co-op has.
                // The End Turn button is not greyed for a peer whose turn it is not, so a refusal here is a
                // click that does nothing at all, with no vanilla surface anywhere that would explain it.
                IntentRail.Reject(SurfaceIds.TacTurnIntent, senderPeerId, why, notify: true);
                return;
            }
            cur.RequestEndTurn(); // the same native call the host's own end-turn button makes
            Debug.Log("[Multiplayer][tac] HOST end-turn intent from peer=" + senderPeerId + " ACCEPTED for '" +
                      Name(cur) + "' turn " + cur.TurnNumber);
        }

        /// <summary>
        /// THE COURTESY PRESS THE TABLE NO LONGER HAS TO MAKE — everybody said "done", so the host runs the
        /// end-turn itself (opt-out, <c>MultiplayerConfig.AutoEndTurnWhenAllReady</c>). Called from
        /// <see cref="TacticalReadySync.HostBroadcastTally"/> on the EDGE into all-ready, once per round.
        ///
        /// ADDITIVE, NEVER A PRECONDITION. Nothing here touches <see cref="Validate"/>, the button, or any
        /// hold: End Turn stays pressable by anyone at any instant regardless of the tally, so a table of
        /// AFK players is still driven to the end of the campaign by one person pressing it — this method
        /// simply never fires for them, because a tally that never fills never reaches its edge.
        ///
        /// HOST ONLY, AND THAT IS WHAT KEEPS IT ONE TURN. Three peers each ending their own turn locally
        /// would be three divergent turn machines; the host runs the SAME native <c>RequestEndTurn</c> its
        /// own button and <see cref="HandleEndTurn"/> run, and the result reaches every peer as the ordinary
        /// 0x80 turn message the clients are already paced by.
        ///
        /// The acceptance rule is <see cref="Validate"/>'s, reused verbatim against the host's own cursor —
        /// a second notion of "is this turn endable" is exactly how the two paths would drift apart.
        /// </summary>
        internal static void HostAutoEndTurn()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var cfg = MultiplayerMain.Instance?.Config;
            if (cfg == null || !cfg.AutoEndTurnWhenAllReady) return;
            var cur = Tlc()?.CurrentFaction;
            string guid = Guid(cur);
            string why = Validate(guid, guid, cur != null && cur.IsControlledByPlayer,
                                  cur != null && cur.IsPlayingTurn);
            if (why != null)
            {
                // NOT AN ERROR — mid-handoff or an AI faction's turn simply is not ours to end. Logged
                // because a convenience that silently does nothing is this repo's dominant bug shape, and
                // "everyone was ready and the turn did not end" needs a witness other than a player's memory.
                Debug.Log("[Multiplayer][tac] auto end-turn on all-ready SKIPPED — " + why +
                          ". Nothing is blocked: End Turn is pressable by anyone at any moment.");
                return;
            }
            cur.RequestEndTurn();
            Debug.Log("[Multiplayer][tac] auto end-turn: every seated peer is ready, so the HOST ended '" +
                      Name(cur) + "' turn " + cur.TurnNumber + " (setting AutoEndTurnWhenAllReady). " +
                      "Convenience only — the button was never gated on this.");
        }

        // ─── THE LEAVE-BATTLE INTENT: peer autonomy over the mission END ────

        /// <summary>The host's OWN <c>TacticalView.GoToGeoscape</c> — the callback the Continue button on the
        /// battle summary invokes (<c>GetLevelFinishedViewState</c>:1109 hands it to
        /// <c>UIStateBattleSummary</c>, whose <c>ExitTactical</c>:46-49 is what the button runs). It is
        /// private, which is the only reason <c>AccessTools</c> appears here; everything else is public.</summary>
        private static readonly System.Reflection.MethodInfo GoToGeoscapeMethod =
            AccessTools.Method(typeof(PhoenixPoint.Tactical.View.TacticalView), "GoToGeoscape");

        /// <summary>This peer has already started leaving the battle. On the HOST it is the idempotence guard
        /// (law 7): <c>GoToGeoscape</c> → <c>FinishLevel</c> is what eventually reaches
        /// <c>GeoLevelController</c>'s <c>_missionToComplete.Complete()</c>, so two peers both clicking
        /// Continue must not run it twice. Per-BATTLE — dropped by <see cref="Reset"/> at teardown, after
        /// which <see cref="Tlc"/> is null and the validator refuses on that instead.</summary>
        internal static bool LeftBattle;

        /// <summary>THE ONE WAY THIS MOD RUNS THE NATIVE EXIT — and the only place the latch can be dropped
        /// again. <see cref="LeftBattle"/> is set by the PREFIX (<see cref="OnLocalLeaveBattle"/>), which by
        /// construction cannot see whether the body it fronts actually completed. So when the body throws the
        /// peer half-left: on the host the announcement is already out (see below), the level never leaves
        /// Playing, and <see cref="TacLevelEndBarrier"/> — the ONLY thing that clears the latch on a normal
        /// arrival (TacticalLevelController.OnLevelStateChanged, out of Playing → <see cref="Reset"/>) — never
        /// runs. The host then sits in a battle it told everyone it left, with <see cref="ValidateLeave"/>'s
        /// <c>alreadyLeaving</c> arm refusing every remaining peer's retry: nobody can get it out.
        ///
        /// THE BROADCAST CANNOT BE TAKEN BACK and this does not pretend otherwise — the peers already ran
        /// their own FinishLevel and are loading the geoscape; there is no un-leave and inventing one would
        /// be a second, divergent way to move a peer between levels. What IS recoverable is the LATCH, so
        /// that is exactly what rolls back: a retry (this peer's own Continue, or a remaining peer's ask)
        /// gets a host that will honour it instead of a permanently refused one. Loud, because a session
        /// that reached here is already split.
        ///
        /// Rethrows: the caller's existing error path is unchanged (the host dispatch catches at
        /// IntentRail.HandleInbound and turns it into a reject the asking peer can read).</summary>
        internal static void InvokeNativeLeave(PhoenixPoint.Tactical.View.TacticalView view)
        {
            // The MOD is driving this funnel, not a human clicking Continue, so the five-second return
            // strip must let it straight through — see ReturnCountdown.ReturnHoldPatch for why each of the
            // mod's own invocations is exempt.
            ReturnCountdown.ModDriving = true;
            try { GoToGeoscapeMethod.Invoke(view, null); }
            catch
            {
                LeftBattle = false;
                Debug.LogError("[Multiplayer][tac] the native GoToGeoscape THREW after this peer had already " +
                               "latched the leave — dropping the latch so a retry is possible. If this is the " +
                               "host, the leave announcement is already out and every other peer is on its way " +
                               "to the geoscape while this one is still in the battle: press Continue again.");
                throw;
            }
            finally { ReturnCountdown.ModDriving = false; }
        }

        /// <summary>May a remaining peer's Continue click end the battle FOR THE HOST? PURE — plain facts off
        /// the HOST's own level only (law 3), so the arbitration is falsifiable headless (RailCheck L64):
        /// "any peer may end any battle" is the same hole the turn arbiter above exists to close.
        /// null = accept, otherwise the human reason.</summary>
        internal static string ValidateLeave(bool hasLevel, bool isGameOver, bool isFinalMission, bool alreadyLeaving)
        {
            if (!hasLevel)
                return "this host holds no tactical level — the battle was already left";
            if (alreadyLeaving)
                return "this host is already leaving the battle — a second FinishLevel would apply the mission " +
                       "outcome twice";
            if (!isGameOver)
                return "the battle is NOT over on the host — leaving now would abandon a mission that is still " +
                       "being fought and complete it on an outcome nobody reached";
            if (isFinalMission)
                return "the FINAL mission exits into the game summary, not the geoscape " +
                       "(TacticalView.GetLevelFinishedViewState:1093-1099 branches to GoToGameSummary) — " +
                       "GoToGeoscape would finish the level with the wrong result";
            return null;
        }

        private static void HandleLeaveBattle(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            var tlc = Tlc();
            bool over = tlc != null && tlc.IsGameOver;
            string why = ValidateLeave(tlc != null, over,
                                       tlc != null && tlc.TacMission != null && tlc.TacMission.IsFinalMission,
                                       LeftBattle);
            if (why != null)
            {
                // A LIVE, UNFINISHED battle is the ONE arm where the sender ran ahead of host state, and only
                // that is worth a reject + its forced re-emit. "No level any more" and "already leaving" are
                // the ORDINARY race of every peer clicking Continue at its own pace — rejecting those would
                // fire a full-graph resend on the exact frame the host is loading its geoscape.
                // NOTIFY: the peer clicked Continue on the battle summary and stays sitting in a battle the
                // host says is still being fought. The button is live and did nothing — pure mod protocol,
                // and the only remaining arm here is exactly that one (the ordinary races log instead).
                if (tlc != null && !over)
                    IntentRail.Reject(SurfaceIds.TacTurnIntent, senderPeerId, why, notify: true);
                else Debug.Log("[Multiplayer][tac] leave-battle from peer=" + senderPeerId + " nonce=" + nonce +
                               " did NOT apply — " + why);
                return;
            }
            if (GoToGeoscapeMethod == null)
            {
                Debug.LogError("[Multiplayer][tac] leave-battle from peer=" + senderPeerId + " CANNOT run — " +
                               "TacticalView.GoToGeoscape did not resolve, so an idle host still holds every peer " +
                               "in a finished battle and the whole session's rail stays silent.");
                return;
            }
            // The host executes the native exit ITSELF and stays the sole authority for the outcome (law 3 /
            // law 5): GoToGeoscape builds the TacticalGameResult from the HOST's own actors and FinishLevel
            // carries it into GeoMission.Complete. The client contributed the ask, nothing else — its own
            // local Complete is still blocked by ClientMissionResultGate.
            InvokeNativeLeave(tlc.View);
            Debug.Log("[Multiplayer][tac] HOST leave-battle intent from peer=" + senderPeerId + " nonce=" + nonce +
                      " ACCEPTED — running the host's own GoToGeoscape → FinishLevel → geoscape.");
        }

        /// <summary>Every peer's Continue click, on the one native funnel the button reaches. NON-BLOCKING on
        /// purpose: leaving one's OWN finished battle is presentation plus a local geoscape load, and the
        /// campaign write it would otherwise cause is already gated (<see cref="ClientMissionResultGate"/>) —
        /// the block-first law governs STATE mutations and there is none here. What crosses is the ASK.</summary>
        internal static void OnLocalLeaveBattle()
        {
            bool first = !LeftBattle;
            LeftBattle = true;
            if (!IntentRail.ShouldRunNative())
            {
                IntentRail.Send(SurfaceIds.TacTurnIntent, OpLeaveBattle, "leave battle");
                return;
            }
            // Host, solo, or inside an apply — this peer really is leaving now. THE HOST tells everyone
            // else, once: HandleLeaveBattle reaches GoToGeoscape by invoking it, so an accepted client ask
            // arrives here too and the broadcast has exactly one emission point. HostBroadcastLeave is
            // self-gated on IsHost, so a client re-entering from ApplyLeave's own apply scope sends nothing.
            if (first) HostBroadcastLeave();
        }

        /// <summary>Pure, so RailCheck can execute it (law L328). A restart needs a live, unfinished mission
        /// to restart — everything else is a press the host cannot honour and must say so about.</summary>
        internal static string ValidateRestart(bool hasLevel, bool isGameOver)
        {
            if (!hasLevel)
                return "this host holds no tactical level, so there is no mission to restart";
            if (isGameOver)
                return "this battle is already over — a finished mission is LEFT, not restarted";
            return null;
        }

        /// <summary>A client pressed Restart. The host runs the restart ITSELF (law 3): its own
        /// <c>FinishLevel</c> passes <see cref="OnLocalRestart"/>, which is the ONE emission point for
        /// <see cref="HostBroadcastRestart"/> — so a client's ask and the host's own press take exactly the
        /// same road and every peer reloads once. The asking client already blocked its own restart and is
        /// still standing in the battle; if the host refuses, the reject tells it why instead of leaving a
        /// button that visibly did nothing.</summary>
        private static void HandleRestartMission(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            var tlc = Tlc();
            string why = ValidateRestart(tlc != null, tlc != null && tlc.IsGameOver);
            if (why == null)
            {
                var level = GameUtl.CurrentLevel();
                var game = GameUtl.GameComponent<PhoenixGame>();
                if (level == null || game == null) why = "this host has no PhoenixGame/level to restart through";
            }
            RestartTrace.Mark("HOST received a restart ask from peer=" + senderPeerId + " nonce=" + nonce +
                              " — verdict: " + (why ?? "ACCEPTED, running the host's own restart"));
            if (why != null)
            {
                // QUIETLY (law L123 arm g). Both refusal arms mean the host's battle is already over or gone,
                // and in both the asking peer is being carried out of it by a message already in flight
                // (OpEnd → ApplyEnd, OpLeave → ApplyLeave). A modal there pops over a teardown; it is not the
                // player's only word, so this stays a reject + a log line.
                IntentRail.Reject(SurfaceIds.TacTurnIntent, senderPeerId, why);
                return;
            }
            GameUtl.GameComponent<PhoenixGame>().FinishLevel(new RestartGameResult(GameUtl.CurrentLevel().LevelParams));
            Debug.Log("[Multiplayer][tac] HOST restart-mission intent from peer=" + senderPeerId + " nonce=" + nonce +
                      " ACCEPTED — running the host's own restart; its FinishLevel carries every peer with it.");
        }

        /// <summary>THE RESTART CAPTURE (law L328), called from the ONE funnel every level change passes
        /// (<see cref="Multiplayer.Harmony.LoadBarrierGate"/> on <c>PhoenixGame.FinishLevel</c>) the moment the
        /// result is a <c>RestartGameResult</c>. Returns TRUE to let the native restart run, FALSE to block it.
        ///
        /// A restart is a full teardown and reload of the map: it is a STATE change of the harshest kind, so
        /// it is block-first like every other one. On a client the press becomes an ask and NOTHING happens
        /// locally — self-restarting is what put the two peers in different tactical levels with different key
        /// maps on 2026-08-08. On the host (or solo, or inside an apply) the native restart runs and every
        /// other peer is told to run its own; <see cref="HostBroadcastRestart"/> self-gates on host+session, so
        /// a client re-entering here from <see cref="ApplyRestart"/>'s apply scope sends nothing back.</summary>
        internal static bool OnLocalRestart()
        {
            if (!IntentRail.ShouldRunNative())
            {
                RestartTrace.Note("OnLocalRestart: this peer may NOT run the native restart — the press " +
                                  "becomes an ask on the tactical intent surface and NOTHING is torn down " +
                                  "here. The trace stays open and continues when the host's answer arrives.");
                IntentRail.Send(SurfaceIds.TacTurnIntent, OpRestartMission, "restart mission");
                Debug.Log("[Multiplayer][tac] LOCAL mission restart BLOCKED on this client and sent to the host " +
                          "as an ask — a peer that reloads the map on its own is fighting a different battle " +
                          "from everyone else. This screen stays in the mission until the host restarts it.");
                return false;
            }
            HostBroadcastRestart();
            RestartTrace.Note("OnLocalRestart: the native restart is allowed to run here, and every other " +
                              "peer has been told to run its own (HostBroadcastRestart self-gates on " +
                              "host+session, so a follower re-entering from ApplyRestart sends nothing).");
            return true;
        }
    }

    /// <summary>
    /// PEER AUTONOMY OVER THE MISSION END (user mandate: "if the host is AFK for an hour the remaining players
    /// must still do EVERYTHING; if one player is left, they can play for everyone").
    ///
    /// THE BLOCK, from the game's own code. A finished battle leaves the tactical level through exactly one
    /// door: <c>TacticalView.GetLevelFinishedViewState</c>:1109 hands <c>GoToGeoscape</c> to
    /// <c>UIStateBattleSummary</c>, whose Continue button runs it (:46-49) → <c>PhoenixGame.FinishLevel</c>:262
    /// → the geoscape load → <c>GeoLevelController</c>:703 <c>_missionToComplete.Complete()</c>. Un-clicked ON
    /// THE HOST, none of that happens: the outcome is never applied and the host stays inside the tactical
    /// level, so <c>DiffEngine.HostTick</c> finds no <c>GeoLevelController</c> and EVERY peer's rail goes
    /// silent. One idle human ends the session for everyone — the same failure shape
    /// <see cref="Multiplayer.Network.Sync.WindowQueueSync"/> closed for the geoscape window queue, one level up.
    ///
    /// THE SEAM IS THAT SAME DOOR, on every peer, as a NON-BLOCKING prefix (law 4a intent capture): the
    /// clicking peer keeps leaving its own level natively — that is presentation, and its
    /// <c>GeoMission.Complete</c> is already gated — while the ask crosses as op 2 on the EXISTING 0x81
    /// tactical intent family. No new surface: this is the same client→host tactical family the end-turn
    /// intent rides, and it is the same question one step further on ("I am done with this turn" → "we are
    /// done with this battle").
    ///
    /// THE HOST RUNS THE NATIVE PATH ITSELF and stays the ONE authority for the outcome (law 5): it invokes
    /// its OWN <c>GoToGeoscape</c>, which reads the host's own <c>ViewerFaction</c> and
    /// <c>TacticalLevel.GetMissionResult()</c>. Nothing about the result crosses the wire in either direction;
    /// its geoscape consequences reach the clients as ordinary value-rail state, exactly as they do when the
    /// host clicks the button itself. That normal case is untouched — the prefix only records and returns.
    ///
    /// IT ASKS THE HOLD, IT DOES NOT RELY ON BEING SKIPPED (2026-08-11).
    /// <c>ReturnCountdown.ReturnHoldPatch</c> prefixes this same method at <c>Priority.First</c> and returns
    /// FALSE while its five-second strip runs, and this capture must not announce behind it: the leave is
    /// announced when the return HAPPENS, not when it is asked for, because that hold can be abandoned and
    /// an announcement at the click would have carried every other peer out of a battle this peer never
    /// left. Harmony WILL NOT do that skipping for us. In HarmonyLib 2.2.0.0 — the ModSDK's own
    /// 0Harmony.dll — a false prefix cancels only the prefixes that can affect the original (returning
    /// bool, or taking a ref/out the body reads); a <c>void</c> prefix is emitted outside that guard and
    /// runs at any priority, which a probe against that exact dll confirmed after a first attempt at this
    /// shipped as a runtime no-op. Making it <c>bool</c> is not the answer either — L64's
    /// <c>leave-capture-blocks</c> arm forbids that return type outright, because a capture that CAN block
    /// this funnel would strand the clicking peer on its own summary screen.
    ///
    /// So the condition is read, not inherited: <see cref="ReturnCountdown.Holding"/> is true exactly when
    /// the hold just swallowed this call. That is also why the priority still matters — the hold has to arm
    /// before this runs. Any third prefix on <c>TacticalView.GoToGeoscape</c> must declare its own priority
    /// relative to these two.
    /// </summary>
    [HarmonyPatch(typeof(PhoenixPoint.Tactical.View.TacticalView), "GoToGeoscape")]
    internal static class TacLeaveBattleCapture
    {
        private static void Prefix()
        {
            if (ReturnCountdown.Holding) return;   // swallowed by the strip — announce at the release
            TacticalTurnSync.OnLocalLeaveBattle();
        }
    }

    /// <summary>
    /// A TURN DOES NOT END OVER AN ORDER THAT HAS NOT BEEN PLAYED YET (law L104).
    ///
    /// THE NATIVE GATE, USED AS-IS. <c>TacticalFaction.PlayTurnCrt</c>:471-483 is the player-turn input loop,
    /// and it does NOT act on <c>_endTurnRequested</c> while
    /// <c>TacticalView.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate</c>:867-874 is true — which is true for
    /// as long as any actor has an ability whose <c>ShouldForceViewInWaitingState</c> is set
    /// (<c>TacticalActor.ShouldViewWaitForMe</c>:1342-1360, fed by <c>StartPlayingAction</c>:1015-1018). So
    /// EVERY peer, host and client alike, already refuses to end a turn on top of a running animation, and
    /// <see cref="TacticalTurnSync.HandleEndTurn"/> handing a peer's ask to the very same
    /// <c>RequestEndTurn</c>:382-386 (which only sets a bool) keeps it that way. Nothing here re-implements
    /// that; this is the one thing the game cannot see.
    ///
    /// THE HOLE IT CLOSES. An order the host has ACCEPTED but is deliberately HOLDING — a peer's follow-up
    /// that arrived while that soldier was still finishing that same peer's previous order
    /// (<c>TacticalCommandSync.BusyWithOwnOrder</c>, the melee case law 5 spells out) — is not an executing
    /// ability anywhere, so the native gate reads the board as idle in the window between the previous order
    /// ending and <c>HostTick</c> releasing the held one. End the turn there and the held order is released
    /// into a faction that is no longer playing, where <c>Validate</c> refuses it: the acting peer sees its
    /// click evaporate and its soldier snap back on the forced settle.
    ///
    /// BOUNDED BY CONSTRUCTION, which is the only reason a turn may be held at all: <c>HostTick</c> refuses a
    /// hold older than <c>DeferCeilingSeconds</c> (10 s) OUT LOUD and drops it, so <c>HasHeldOrders</c> cannot
    /// stay true. Client-side and solo this is inert — <c>_deferred</c> is only ever written by the host's
    /// intent handler and is cleared for a non-host on the first <c>HostTick</c>.
    ///
    /// The QUEUED half of the same question — a mirror this peer enqueued behind a running action
    /// (<c>ApplyActivate</c>'s "MIRROR QUEUED" line) — is deliberately NOT gated here. Such a mirror always
    /// sits behind an ability that IS executing, so the native gate already covers it; and a
    /// <c>NotStarted</c> action is removed by <c>ActionComponent.PlayActionAfterCurrent</c>:85-89 WITHOUT
    /// <c>SetState</c>, so its <c>OnCoroutineEnd</c> never runs and any set we tracked it in would leak an
    /// entry that parks the turn forever — a swallowed hold, this repo's dominant bug class.
    /// </summary>
    [HarmonyPatch(typeof(PhoenixPoint.Tactical.View.TacticalView),
                  nameof(PhoenixPoint.Tactical.View.TacticalView.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate))]
    internal static class EndTurnWaitsForHeldOrders
    {
        private static void Postfix(ref bool __result)
        {
            if (__result || !TacticalCommandSync.HasHeldOrders) return;
            __result = true;
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
            // L67e — THE ROSTER OUTCOME, at the one boundary both peers cross. A refused key IS an actor that
            // lives on the host and does not exist here, so a non-empty ledger is a divergence, not a note.
            string diverged = TacticalActorKey.RosterDivergence();
            if (diverged != null)
                Debug.LogError("[Multiplayer][tac] ROSTER DIVERGENCE at the turn edge — this peer is fighting a " +
                               "SMALLER battle than the host. Actor(s) alive on the host will never appear here: " +
                               diverged + ". Every command, hit and settle naming them is refused for the rest of " +
                               "the mission. This is not a lost packet — the spawn record arrived and could not be " +
                               "rebuilt from what it carried.");
            // THE ADVISORY READY RESET, on the game's own round edge (constraint 4: no poll, no timer).
            // Every peer clears its own flag; the host additionally clears every seat and ships 0/M.
            TacticalReadySync.OnNewTurn(nextFaction);
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
    ///   • drop this battle's mirror state, so the next battle does not inherit a set mission-over flag.
    ///
    /// It does NOT arm the reveal barrier. It used to, and that was a SECOND arm for the one concern:
    /// <c>PhoenixGame.FinishLevel</c> (<c>LoadBarrierGate</c>) is the universal funnel every level change
    /// goes through and it runs FIRST on this very path (<c>GoToGeoscape</c> → <c>FinishLevel</c> → the
    /// level leaves Playing → here), so this call was always the no-op half of the pair.
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
            ReturnCountdown.Reset();       // a strip still counting for a battle that is over would then
                                           // fire its release into a dead view and log an error for it
            TacticalCommandSync.Reset();   // A3a: 0x82 seq + pending settles must not survive into the next battle
            TacticalDamageSync.Reset();    // A3b: 0x84 seq + the gap cursor + any leaked mirror-apply depth
        }
    }

    /// <summary>
    /// The client must not compute its OWN geoscape outcome. <c>GeoMission.Complete(TacMissionResult)</c>
    /// (GeoMission.cs:267) is the one funnel that applies a battle to the campaign — casualties, XP, rewards,
    /// faction effects, site destruction — and <c>GeoLevelController</c> calls it on every peer with the
    /// result that peer's own <c>TacticalView.GoToGeoscape</c> built out of its own (unreplicated) actors.
    /// Blocked block-first: the host applies the real one, and its consequences reach the client as ordinary
    /// save-graph state on the value rail — no tactical message carries them.
    ///
    /// BUT THE BLAST RADIUS WAS TOO WIDE, and it cost the client EVERY post-mission screen (user report
    /// 2026-08-01 items 3a+3b). <c>Complete</c>:267-276 does two things a projector must not do — apply the
    /// results and grant the reward — and two that are pure bookkeeping: <c>Result = result</c> on its first
    /// line and <c>IsCompleted = true</c> at :275. Both of the latter are exactly what the arrival branch
    /// <c>UIStateInitial.EnterState</c>:101 tests, and that ONE branch is what raises the outcome modal
    /// (:105-112) AND the resupply screen (:124-127). Blocking the method whole therefore left the branch
    /// permanently false on every client and silently deleted both panels. So the client now takes the GAME'S
    /// OWN half-measure instead — <c>CompleteSilently</c>:284-287, whose entire body is
    /// <c>IsCompleted = true</c> — and gets the host's reward numbers off 0xBB to draw with
    /// (<see cref="MissionOutcomeMirror.StampMirroredOutcome"/>). <c>Result</c> stays UNSET: it is the host's
    /// authoritative mission result (law 3) and the branch's <c>||</c> never needed it.
    /// </summary>
    [HarmonyPatch(typeof(PhoenixPoint.Geoscape.Entities.GeoMission), "Complete")]
    internal static class ClientMissionResultGate
    {
        private static bool Prefix(PhoenixPoint.Geoscape.Entities.GeoMission __instance)
        {
            if (IntentRail.ShouldRunNative()) return true;
            Debug.LogWarning("[Multiplayer][tac] client-local GeoMission.Complete BLOCKED — the mission outcome is " +
                             "the host's, and its geoscape consequences arrive on the value rail as ordinary state.");
            MissionOutcomeMirror.StampMirroredOutcome(__instance);
            return false;
        }
    }

    /// <summary>
    /// THE MISSION-START INTRO, ON EVERY PEER. On maps with a special squad the HOST gets a camera move onto
    /// the elite unit plus a left-side info panel before the combat UI, and clients jumped straight to the
    /// ready UI. That panel is the NATIVE ContextHelp hint system, not a cutscene and not a TFTV widget:
    /// <c>TacContextHelpManager</c>:118 subscribes <c>ActorSawOtherFactionActorEvent</c> → <c>OnActorSeen</c>
    /// :199 → <c>EventTypeTriggered(HintTrigger.ActorSeen, …)</c>:205, and the panel + camera move are
    /// <c>UIStateTacticalContextHelp.EnterState</c>:75 → <c>TryFocusCameraOnContext</c>:98-110 →
    /// <c>DoCameraChase()</c>. TFTV only registers <c>ContextHelpHintDef</c>s on that trigger
    /// (<c>TFTVHints.cs</c>:400-410).
    ///
    /// THE TURN-EDGE REPLAY (below) IS NECESSARY BUT WAS NOT SUFFICIENT — 2026-08-01, measured. The
    /// mission-start replay lives in <c>TacContextHelpManager.OnStartTurn</c>:244-262 behind
    /// <c>nextFaction == _tacLevel.FirstFaction &amp;&amp; nextFaction.TurnNumber == 1</c>, and it is the only
    /// thing that walks <c>Vision.KnownActors</c> re-firing <c>OnActorSeen</c> for everything already visible
    /// at deployment. The host crosses that edge natively; the client's battle is BUILT FROM THE HOST'S
    /// MID-TACTICAL SAVE, captured at <c>HasAnyTurnStarted</c> — i.e. AFTER <c>PlayTurnCrt</c>:390-391 already
    /// did its unconditional <c>TurnNumber = TurnNumber + 1</c> — so the gate is false and the replay never
    /// runs. The pending-hint list does not cross either: <c>ContextHelpManager.RecordInstanceData</c>:93-99
    /// serialises only <c>_shownHints</c>. All of that is true, the postfix shipped, it RAN — and the clients
    /// still got nothing, because the hint it was replaying DOES NOT EXIST on a client. See
    /// <see cref="TftvMissionHints"/> for that half; this class only re-fires the trigger.
    ///
    /// THE FIX = RUN THE NATIVE REPLAY THE GATE SKIPPED, once, on the peers the gate skipped it on.
    /// A POSTFIX (law 4c presentation seam), no wire bytes and no surface — hints are per-peer presentation,
    /// which is why <c>ContextHelpData</c> is already an excluded field on the rail (RailMeta.cs:753).
    ///
    /// THE "NO PEER WAITS ON ANOTHER'S OK" CLAIM THAT USED TO SIT HERE WAS FALSE, and nothing enforced it.
    /// A popup does hold only its own UI stack — but the native ALIEN-TURN coroutine yielded on that stack:
    /// <c>TacticalFaction.AIUpdateCrt</c>:567 and :621 → <c>TacticalView.WaitUntilHintsAreConfirmed</c>. On
    /// 2026-08-05 the host's Umbra hint stopped the alien turn for 125 s on all three peers, and the clients
    /// — which never render an AI turn of their own — sat in <c>ClientAiGate</c>'s hold with no popup to
    /// dismiss. What is enforced NOW, and where: <see cref="HintWaitGate"/> (src\Tactical\TacticalHintGate.cs)
    /// prefixes that one funnel so the coroutine never stops on any peer's popup; <c>ClientAiGate</c>'s hold
    /// carries a named ceiling (<c>HoldCeilingFrames</c>, TacticalEntry.cs) and releases rather than waiting
    /// forever; and law L91 arms (f)/(g) assert both mechanically — every hold coroutine on the rail must
    /// load a named bound, and every native wait funnel the shared turn coroutine reaches through the local
    /// UI state stack must be patched by this mod.
    ///
    /// NOTHING SHOWS IT EXPLICITLY, deliberately. <c>UIStateCharacterSelected.UpdateState</c>:1103-1106 calls
    /// <c>TacticalView.TryShowContextHint</c>:339 EVERY FRAME, so filling <c>_hintsPendingDisplay</c> is the
    /// entire job; the game pops the panel the moment the client is in its ordinary idle state, and a hint
    /// flagged <c>DelayUntilActorSelected</c> still waits exactly as long as it does natively.
    ///
    /// <c>OnActorSeen</c> is called rather than <c>EventTypeTriggered(ActorSeen, …)</c> directly because it
    /// also carries the <c>ItemSeen</c> chain (:207-217) and the <c>IsFromViewerFaction</c> test; it is
    /// private, which is the only reason <c>AccessTools</c> appears. Everything else read here is public.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientMissionStartHints
    {
        private static bool _replayed;

        internal static void Reset() => _replayed = false;

        private static readonly System.Reflection.MethodInfo ActorSeen =
            AccessTools.Method(typeof(PhoenixPoint.Tactical.ContextHelp.TacContextHelpManager), "OnActorSeen",
                               new[] { typeof(PhoenixPoint.Tactical.Entities.TacticalActor),
                                       typeof(PhoenixPoint.Tactical.Entities.TacticalActor) });

        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(PhoenixPoint.Tactical.ContextHelp.TacContextHelpManager), "OnStartTurn",
                               new[] { typeof(TacticalFaction), typeof(TacticalFaction) });

        private static void Postfix(PhoenixPoint.Tactical.ContextHelp.TacContextHelpManager __instance,
                                    TacticalFaction nextFaction)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
            if (_replayed || nextFaction == null || !nextFaction.IsControlledByPlayer) return;
            var tlc = nextFaction.TacticalLevel;
            if (tlc == null || nextFaction.Vision == null) return;
            _replayed = true;

            // A trigger can only register a hint that EXISTS. On a client it does not — rebuild it first.
            TftvMissionHints.RebuildForThisClient(tlc);

            // The native body verbatim (TacContextHelpManager:246-259), minus the turn-number gate this peer
            // can never satisfy. The viewer is the same "first actor of the faction" the game uses — the
            // hints are about what was SEEN, not about who saw it.
            if (ActorSeen == null)
            {
                Debug.LogError("[Multiplayer][tac] mission-start hints SKIPPED — TacContextHelpManager.OnActorSeen " +
                               "did not resolve, so this peer gets no elite/mission intro at all.");
                return;
            }
            __instance.EventTypeTriggered(PhoenixPoint.Common.ContextHelp.HintTrigger.MissionStart, tlc, tlc);
            var viewer = nextFaction.TacticalActors.FirstOrDefault();
            if (viewer == null) return;
            int seen = 0;
            foreach (var known in new List<PhoenixPoint.Tactical.Entities.TacticalActorBase>(nextFaction.Vision.KnownActors.Keys))
            {
                var actor = known as PhoenixPoint.Tactical.Entities.TacticalActor;
                if (actor == null) continue;
                ActorSeen.Invoke(__instance, new object[] { viewer, actor });
                ++seen;
            }
            Debug.Log("[Multiplayer][tac] mission-start context hints replayed for this client over " + seen +
                      " already-visible actor(s) — the host got them at its own turn-1 edge, which a battle " +
                      "resumed from the host's save is always past.");
        }
    }

    /// <summary>
    /// THE ELITE/GANG INTRO PANEL IS HOST-ONLY BECAUSE ITS HINT DEF IS. Measured 2026-08-01: the host's panel
    /// is the native <c>UIStateTacticalContextHelp</c> showing the hint "Sneaky Monkeys" (host Player.log,
    /// frame 8235) — a TFTV **runtime-created** <c>ContextHelpHintDef</c>
    /// (<c>TFTVHints.DynamicallyCreatedHints.CreateNewTacticalHintForHumanEnemies</c>:171-198, trigger
    /// <c>ActorSeen</c>, conditions = the actor's <c>HumanEnemyFaction_&lt;short&gt;_GameTagDef</c> +
    /// <c>HumanEnemy_GameTagDef</c>), minted once per mission with a fresh <c>Guid</c>.
    ///
    /// A SAVE CARRIES DEF REFERENCES, NOT DEFS. The only thing that mints it is
    /// <c>TFTVTactical.OnNewTurn(0)</c> → <c>TFTVHumanEnemies.ImplementHumanEnemies</c> (TFTVTactical.cs:505-508),
    /// and turn-0 is raised from exactly one place: <c>TacticalLevelController.OnLevelStart</c>:674, inside
    /// <c>if (!IsLoadingSavedGame)</c> (:657). NextTurnCrt's other call (:710) is guarded by
    /// <c>turn != CurrentFaction.TurnNumber</c> and can never pass 0. A client's battle IS a loaded save, so
    /// that callback never fires, the def is never created, and no amount of re-firing <c>ActorSeen</c> can
    /// register a hint that does not exist. That — not the turn-number gate — is why the shipped replay
    /// changed nothing.
    ///
    /// WHY THIS IS A REBUILD AND NOT A RELAY. Everything the panel says already crossed on the save:
    /// <c>TFTVTactical.RecordTacticalInstanceData</c>:438-439 writes <c>HumanEnemiesAndTactics</c> (the tactic
    /// roll) and <c>HumanEnemiesGangNames</c>, restored at :300-301. The actors carry their tier/faction tags
    /// and their rolled names in the save too. So the client holds every input; it is missing only the def
    /// built from them. We re-run TFTV's own builder rather than re-implementing its strings, and
    /// <c>RollTactic</c>:404 keeps the host's roll because the key is already in the dict.
    ///
    /// "THE BUILDER MUTATES NOTHING ON A CLIENT" WAS FALSE, AND IT COST A MODAL ERROR DIALOG ON EVERY CLIENT
    /// (measured 2026-08-01 14:50:49, both clients' <c>TFTV.log</c>: "Trying to add tag that is already present
    /// in the list HumanEnemy_GameTagDef" out of <c>GetGangerReady</c> → <c>TFTVLogger.Error</c>:62
    /// <c>ShowSimplePrompt</c>). The tier2/3/4 loops ARE guarded by <c>HasGameTag(humanEnemyTagDef)</c>, but
    /// the LEADER arm (<c>AssignHumanEnemiesTags</c>:754-757) is guarded by
    /// <c>!leader.GameTags.Contains(HumanEnemyTier1GameTag)</c> — a DIFFERENT tag — and the leader is re-picked
    /// as <c>orderedListOfHumanEnemies[0]</c> off the CURRENTLY <c>InPlay</c> squad. Mid-battle that is not the
    /// original tier-1 leader, so an actor that already carries <c>humanEnemyTagDef</c> (but not tier 1) enters
    /// <c>GetGangerReady</c>:658 and its second line, a plain <c>GameTags.Add</c>, throws. It also renames the
    /// actor and re-runs <c>AdjustStatsAndSkills</c> — a silent stat divergence from the host on top of the popup.
    ///
    /// SO THE NON-MUTATION IS ENFORCED, NOT ASSUMED: <c>GetGangerReady</c> is stood down for the duration of the
    /// one rebuild call, exactly like the gang-name prefix below and in the same scoped window. It is the whole
    /// mutating half of the builder — tags, name, stats, healthbar icon — and every one of those already arrived
    /// on the save. What is left is what this class actually wants: pure def construction. Law 4c (presentation
    /// seam), zero wire bytes, no new surface.
    ///
    /// THE ONE THING THE SAVE CANNOT REPRODUCE is the gang NAME: <c>GenerateGangName</c>:423 re-seeds
    /// <c>UnityEngine.Random</c> from <c>Stopwatch.GetTimestamp()</c>, so it is a different name on every peer
    /// and no shared seed can reach it. The host's names DID cross (<c>HumanEnemiesGangNames</c>), so a
    /// scoped prefix hands them back in call order and every peer's panel reads identically. The patch is
    /// installed and removed around the one call — it is never live during normal play, and it is applied
    /// here rather than through <c>TftvLateBinder</c> because this runs mid-battle, long after TFTV loaded.
    ///
    /// No TFTV reference: resolved by reflection, and absent TFTV this is a no-op.
    /// </summary>
    internal static class TftvMissionHints
    {
        private const string HarmonyId = "Morgott.Multiplayer.tftv-gang-name";

        // Non-null ONLY for the duration of the one ImplementHumanEnemies call below.
        private static Queue<string> _hostGangNames;

        internal static void RebuildForThisClient(TacticalLevelController tlc)
        {
            var humanEnemies = AccessTools.TypeByName("TFTV.TFTVHumanEnemies");
            if (humanEnemies == null) return; // no TFTV — nothing mints defs mid-mission

            var implement = AccessTools.Method(humanEnemies, "ImplementHumanEnemies",
                                               new[] { typeof(TacticalLevelController) });
            var generateName = AccessTools.Method(humanEnemies, "GenerateGangName",
                                                  new[] { typeof(TacticalFaction) });
            // Name-only lookup: there is exactly one GetGangerReady and its last parameter is optional, so an
            // exact Type[] match is a needless way to resolve null and silently stop standing it down.
            var gangerReady = AccessTools.Method(humanEnemies, "GetGangerReady");
            if (implement == null || generateName == null || gangerReady == null)
            {
                Debug.LogError("[Multiplayer][tac] TFTV is loaded but its human-enemy hint builder did not " +
                               "resolve (ImplementHumanEnemies/GenerateGangName/GetGangerReady renamed?) — this " +
                               "peer gets no gang/elite intro panel at all.");
                return;
            }

            var names = AccessTools.Field(humanEnemies, "HumanEnemiesGangNames")?.GetValue(null) as List<string>;
            _hostGangNames = new Queue<string>(names ?? new List<string>());
            int carried = _hostGangNames.Count;

            var harmony = new HarmonyLib.Harmony(HarmonyId);
            try
            {
                harmony.Patch(generateName,
                              prefix: new HarmonyMethod(AccessTools.Method(typeof(TftvMissionHints),
                                                                          nameof(HostGangName))));
                harmony.Patch(gangerReady,
                              prefix: new HarmonyMethod(AccessTools.Method(typeof(TftvMissionHints),
                                                                          nameof(SkipMutatingHalf))));
                implement.Invoke(null, new object[] { tlc });
                Debug.Log("[Multiplayer][tac] TFTV mission hint def(s) rebuilt on this client from the host's " +
                          "transferred gang/tactic data (" + carried + " gang name(s) carried) — the intro " +
                          "panel the host got is now registrable here.");
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer][tac] TFTV mission hint rebuild FAILED — no gang/elite intro on " +
                               "this peer: " + e);
            }
            finally
            {
                harmony.UnpatchAll(HarmonyId);
                _hostGangNames = null;
            }
        }

        // Hands back the host's name instead of re-rolling. Empty queue → native behaviour (a re-roll is
        // still better than no panel), which is also what every non-client call gets: the queue is null then.
        private static bool HostGangName(ref string __result)
        {
            if (_hostGangNames == null || _hostGangNames.Count == 0) return true;
            __result = _hostGangNames.Dequeue();
            return false;
        }

        // GetGangerReady is the builder's whole mutating half — tags, actor name, AdjustStatsAndSkills, the
        // healthbar icon. Every one of those crossed on the save, its leader arm re-fires on a re-picked leader
        // and throws (see the class doc), and the def this class came for is built by GenerateHumanEnemyUnit,
        // which does not go through here. Live ONLY inside the one scoped rebuild call.
        private static bool SkipMutatingHalf() => false;
    }
}
