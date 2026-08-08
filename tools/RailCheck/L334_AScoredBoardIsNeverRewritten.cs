using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L334 — ONCE A MISSION IS SCORED, NOTHING REWRITES THE BOARD IT WAS SCORED FROM.
    ///
    /// THE BUG (live 2026-08-08, the 3-vs-2 evacuation count and the XP that followed it). L329 made the
    /// host's mission-end sweep the last correction before the verdict. It was not the last WRITE, and the
    /// cross-peer trace names the one that came after it (client clock = host + 02:02:31.7):
    ///   client:2335 20:25:29.376  status set of Soldier_1 reconciled ... applied [0@Evacuated_StatusDef||]
    ///   client:2337 20:25:29.642  CLIENT applied host mission END outcome=Won
    ///   client:2338 20:25:29.796  CLIENT tac-cmd command Soldier_1 ExitMission_AbilityDef nonce=131
    ///   client:2339 20:25:29.892  reject nudge — "it is not that faction's turn on the host"
    ///   host:4270   18:22:58.047  HOST tac-cmd REJECT peer=1 — same refusal
    ///   client:2340 20:25:29.892  status set ... applied [] removed [0@Evacuated_StatusDef||]   ← the strip
    /// That last line is the final <c>Evacuated</c> line in the entire client log. The client's own
    /// <c>EvacuateFactionObjective</c> then counted one evacuee fewer than the host's, and since every peer
    /// computes its own XP from its own objective state (L329), the two summaries disagreed.
    ///
    /// THE ROOT IS WHAT A "ROLLBACK" ACTUALLY IS, not the status def it happened to remove. A reject's
    /// rollback is <c>HostSettle(actor, forced: true)</c> — a FULL-STATE snapshot (position, AP, WP, the whole
    /// status set, traits, selected weapon) read from the host AT THAT INSTANT. It does not undo the refused
    /// command's effect; it overwrites the actor with whatever the host can see. Before the mission end that
    /// is the correction the reject path exists for. AFTER it, the host is looking at a torn-down board while
    /// the peers have already counted theirs, so the same snapshot becomes a rewrite of a finished result.
    /// Guarding <c>Evacuated_StatusDef</c> would have fixed this ONE symptom and left every other trailing
    /// settle — the end-of-action rider, a queued settle, the next reject — doing the same thing.
    ///
    /// THE OTHER HALF IS CHEAPER AND UPSTREAM: the client still held a live ExitMission button 154 ms after
    /// applying the mission end, and pressing it is what produced the reject in the first place. A command
    /// sent after the verdict cannot be honoured by anybody — the only thing it can produce is a refusal, and
    /// the refusal is what does the damage. So the capture seam stops relaying on BOTH roles once that peer
    /// has passed the mission end.
    ///
    /// WHY NOT <c>tlc.IsGameOver</c>, which is the obvious flag: it is ALREADY TRUE when
    /// <c>HostBroadcastEnd</c> runs — that method is a POSTFIX on <c>TacticalLevelController.GameOver</c> —
    /// so a guard reading it would silence L329's mission-end sweep, the very correction that makes the
    /// scored board the host's. The latch is therefore set AFTER the sweep and after the announcement.
    ///
    /// THE ARMS:
    ///   (a) EXECUTED, all four corners of <c>TacticalTurnSync.BattleAlreadyScored</c>: false only while
    ///       BOTH flags are clear, true as soon as either is set. The two are role-exclusive (the host sets
    ///       <c>MissionEndSent</c> when it announces, a client sets <c>HostMissionOver</c> when it applies),
    ///       so the OR is what makes one predicate cover both roles. Non-vacuous both ways — a predicate
    ///       stuck false guards nothing, one stuck true kills the live battle.
    ///   (b) THE SETTLE CHOKE POINT: <c>TacticalCommandSync.HostSettle</c> must consult
    ///       <c>MissionEndSent</c>. Every settle in the mod goes through it — the reject rollback, the
    ///       end-of-action rider, the turn-edge sweep — so one guard there covers the class, and the reported
    ///       defect is exactly what its absence produces.
    ///   (c) THE RELAY CHOKE POINT: <c>TacticalCommandSync.OnAbilityActivated</c> must consult
    ///       <c>BattleAlreadyScored</c>, so no peer emits a command for a battle that is already counted.
    ///   (d) THE LATCH IS ACTUALLY ARMED: <c>TacticalTurnSync.HostBroadcastEnd</c> must reference
    ///       <c>MissionEndSent</c>. Without the write, (a)-(c) guard a flag nobody ever sets and every arm
    ///       above is green over the shipped bug.
    ///   (e) AND DROPPED PER BATTLE: <c>TacticalTurnSync.Reset</c> must reference it too — a latch carried
    ///       into the next battle mutes every settle in it from frame one.
    ///
    /// Falsify: delete the <c>MissionEndSent</c> guard from <c>HostSettle</c> (the defect verbatim) → (b)
    /// red. Delete the <c>BattleAlreadyScored</c> guard from <c>OnAbilityActivated</c> → (c) red. Make
    /// <c>BattleAlreadyScored</c> read one flag instead of both → (a) red on the role it dropped. Delete the
    /// latch write from <c>HostBroadcastEnd</c> or from <c>Reset</c> → (d)/(e) red.
    /// </summary>
    internal static class L334_AScoredBoardIsNeverRewritten
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(IntentRail).Assembly;
            var turnSync = mod.GetType("Multiplayer.Tactical.TacticalTurnSync");
            var cmdSync = mod.GetType("Multiplayer.Tactical.TacticalCommandSync");

            var sent = turnSync?.GetField("MissionEndSent", All);
            var over = turnSync?.GetField("HostMissionOver", All);
            var scored = turnSync?.GetProperty("BattleAlreadyScored", All);
            var scoredGet = scored?.GetGetMethod(true);
            var broadcastEnd = turnSync?.GetMethod("HostBroadcastEnd", All);
            var reset = turnSync?.GetMethod("Reset", All);
            var hostSettle = cmdSync?.GetMethod("HostSettle", All);
            var onActivated = cmdSync?.GetMethod("OnAbilityActivated", All);

            if (sent == null || over == null || scoredGet == null || broadcastEnd == null || reset == null ||
                hostSettle == null || onActivated == null)
            {
                yield return "L334 premise-changed: the scored-board family no longer resolves " +
                             "(TacticalTurnSync.MissionEndSent/HostMissionOver/BattleAlreadyScored/" +
                             "HostBroadcastEnd/Reset, TacticalCommandSync.HostSettle/OnAbilityActivated). " +
                             "Every arm below would pass vacuously, so 'a scored board is never rewritten' is " +
                             "UNCHECKED rather than satisfied";
                yield break;
            }

            // ═══ (a) THE PREDICATE, EXECUTED ON ALL FOUR CORNERS ═══
            // The live values are restored afterwards: this law runs inside the same process as every other,
            // and leaving a mission-end latch armed would be a false red somewhere downstream.
            object wasSent = sent.GetValue(null), wasOver = over.GetValue(null);
            var rows = new List<string>();
            try
            {
                foreach (var c in new[]
                {
                    new { S = false, O = false, Want = false, What = "mid-battle, neither role has passed the end" },
                    new { S = true,  O = false, Want = true,  What = "the HOST has announced the mission end" },
                    new { S = false, O = true,  Want = true,  What = "a CLIENT has applied the host's mission end" },
                    new { S = true,  O = true,  Want = true,  What = "both flags set" },
                })
                {
                    sent.SetValue(null, c.S);
                    over.SetValue(null, c.O);
                    bool got;
                    try { got = (bool)scoredGet.Invoke(null, null); }
                    catch (Exception ex)
                    {
                        rows.Add("L334 scored-predicate-threw: BattleAlreadyScored threw '" +
                                 (ex.InnerException ?? ex).Message + "' with MissionEndSent=" + c.S +
                                 " HostMissionOver=" + c.O);
                        continue;
                    }
                    if (got != c.Want)
                        rows.Add("L334 scored-predicate-wrong: BattleAlreadyScored answers " + got + " when " +
                                 c.What + " (MissionEndSent=" + c.S + ", HostMissionOver=" + c.O + "), expected " +
                                 c.Want + ". The two flags are role-exclusive — the host sets one when it " +
                                 "announces, a client the other when it applies — so a predicate reading only " +
                                 "one leaves that role still relaying commands into a battle everyone has " +
                                 "already counted, and one stuck true kills the live battle instead");
                }
            }
            finally
            {
                sent.SetValue(null, wasSent);
                over.SetValue(null, wasOver);
            }
            foreach (var r in rows) yield return r;

            // ═══ (b) THE SETTLE CHOKE POINT ═══
            if (!Program.ReadsField(hostSettle, sent))
                yield return "L334 scored-board-still-settled: TacticalCommandSync.HostSettle no longer " +
                             "consults MissionEndSent. A settle is a FULL-STATE snapshot, not an undo of the " +
                             "refused command, so one taken after the mission end overwrites an actor on peers " +
                             "that have already scored — live 2026-08-08 a reject's rollback settle stripped " +
                             "Evacuated_StatusDef straight back off a soldier the client had just evacuated " +
                             "(3 evacuated on the host, 2 on the client, XP diverged with them). Every settle " +
                             "in the mod passes through this method, which is why the guard is here and not on " +
                             "the reject path that happened to be the one caught";
            // ═══ (c) THE RELAY CHOKE POINT ═══
            if (!Reaches(onActivated, scoredGet, mod))
                yield return "L334 command-relayed-after-the-verdict: TacticalCommandSync.OnAbilityActivated " +
                             "no longer consults BattleAlreadyScored, so a peer still ships commands for a " +
                             "battle that is already counted. Nobody can honour one — the only thing it can " +
                             "produce is a refusal, and it was a refusal (nonce=131, 154 ms after the client " +
                             "applied the mission end) whose rollback did the damage above";
            // ═══ (d) THE LATCH IS ARMED, ═══
            if (!Program.ReadsField(broadcastEnd, sent))
                yield return "L334 latch-never-armed: TacticalTurnSync.HostBroadcastEnd does not touch " +
                             "MissionEndSent, so the flag every guard above reads is never set and all of them " +
                             "are green over the shipped bug. It must be set AFTER the mission-end sweep and " +
                             "after the Send — before the sweep it would silence the sweep itself (L329), " +
                             "since HostSettle now reads it";
            // ═══ (e) AND DROPPED PER BATTLE ═══
            if (!Program.ReadsField(reset, sent))
                yield return "L334 latch-outlives-its-battle: TacticalTurnSync.Reset does not clear " +
                             "MissionEndSent. Carried into the next mission it withholds every settle in it " +
                             "from frame one — the whole battle uncorrected, and silently, which is worse than " +
                             "the defect this law closes";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null && Program.Callees(from, asm).Any(c => Same(c, target));
    }
}
