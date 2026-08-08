using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Levels;

namespace RailCheck
{
    /// <summary>
    /// L329 — THE BOARD EVERY PEER SCORES AT MISSION END IS THE HOST'S.
    ///
    /// THE BUG (live 2026-08-08, rescue mission): the host counted 3 soldiers evacuated, the client 2, and
    /// the XP diverged with the count. Nothing about evacuation is replicated and nothing needs to be —
    /// the outcome is one authoritative byte on 0x80. What each peer computes for itself is EVERYTHING
    /// AROUND that byte: <c>RescueSoldiersFactionObjective.EvaluateObjective</c>:37-72 counts the actors
    /// THIS peer can see, and <c>TacticalFaction.GiveExperienceForObjectives</c>:207-294 runs LOCALLY on
    /// every peer over LOCAL objective state. Two boards that disagree therefore score differently, in
    /// silence, and the summary is the first place anyone notices.
    ///
    /// THE ONE MOMENT THAT HAD NO HEALER. Divergence is corrected by the turn-edge sweep —
    /// <c>HostSweepTick</c> → <c>TacticalCommandSync.HostSettleAllLive</c>, law L123's arm — which ships the
    /// host's position/AP/WP/statuses for every keyed live actor once per faction turn. A rescue mission
    /// ENDS ON THE SAME TURN its last unit evacuates: no further turn edge ever arrives, so anything that
    /// drifted during that final turn was still drifted when both peers scored. The sweep therefore has to
    /// run once more, at the end, BEFORE the outcome is announced.
    ///
    /// THE OUTCOME THIS LAW ASSERTS:
    ///   (a) ORDER, WHICH IS THE WHOLE POINT. In <c>HostBroadcastEnd</c> the sweep must appear BEFORE the
    ///       <c>Send</c> that carries <c>OpEnd</c>, walked in IL order. A sweep emitted after the
    ///       announcement is a correction the clients score without: they run the native <c>GameOver</c> the
    ///       moment OpEnd lands (<c>ApplyEnd</c>), and settles arriving behind it correct a board nobody
    ///       will read again. "It is called" is satisfied by both orders; only one of them is the fix.
    ///   (b) IT IS THE SAME SWEEP AS THE TURN EDGE, not a second hand-written one. Both
    ///       <c>HostSweepTick</c> and <c>HostBroadcastEnd</c> must reach the SAME
    ///       <c>HostSettleAllLive</c> — the correction that is already proven in-game, including its
    ///       host-only gate, its keyed filter and its live filter. A bespoke end-of-mission copy is two
    ///       tables that will disagree.
    ///   (c) THE SWEEP STILL SWEEPS. <c>HostSettleAllLive</c> must read <c>NetworkEngine.IsHost</c> (a
    ///       client that settles is a client asserting its own board over the host's — the exact inversion
    ///       of this law), must consult <c>TacticalActorKey.Of</c> (an unkeyed actor cannot be named on the
    ///       wire) and must reach <c>HostSettle</c>. It is also EXECUTED here with no live engine, where it
    ///       must return quietly instead of throwing — a sweep that throws at mission end takes the
    ///       announcement down with it.
    ///   (d) THE PREMISE, checked rather than assumed: the scoring really is local. Both
    ///       <c>GiveExperienceForObjectives</c> and <c>RescueSoldiersFactionObjective.EvaluateObjective</c>
    ///       must still exist in the game assembly, and NOTHING in the mod may call or replace them. The day
    ///       somebody replicates the score directly, this law's reasoning is stale and must be re-argued —
    ///       not left quietly green over a mechanism that no longer matters.
    ///
    /// Falsify: delete the <c>HostSettleAllLive</c> call in <c>HostBroadcastEnd</c>, or move it AFTER the
    /// <c>Send</c> → (a) red. Point one of the two callers at a private copy → (b) red. Drop the IsHost
    /// guard / the key filter / the <c>HostSettle</c> call inside the sweep → (c) red. Make the mod call
    /// <c>GiveExperienceForObjectives</c> itself → (d) red.
    /// </summary>
    internal static class L329_ThePeersScoreTheHostsBoard
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var mod = typeof(IntentRail).Assembly;
            var turnSync = mod.GetType("Multiplayer.Tactical.TacticalTurnSync");
            var cmdSync = mod.GetType("Multiplayer.Tactical.TacticalCommandSync");
            var actorKey = mod.GetType("Multiplayer.Tactical.TacticalActorKey");

            var broadcastEnd = turnSync?.GetMethod("HostBroadcastEnd", All);
            var sweepTick = turnSync?.GetMethod("HostSweepTick", All);
            var send = turnSync?.GetMethod("Send", All);
            var sweep = cmdSync?.GetMethod("HostSettleAllLive", All);
            var settle = cmdSync?.GetMethod("HostSettle", All);
            var keyOf = actorKey?.GetMethod("Of", All);
            var isHost = typeof(NetworkEngine).GetProperty("IsHost", All)?.GetGetMethod(true);
            var giveXp = typeof(TacticalFaction).GetMethod("GiveExperienceForObjectives", All);
            var rescue = game.GetType("PhoenixPoint.Tactical.Levels.FactionObjectives.RescueSoldiersFactionObjective");
            var evaluate = rescue?.GetMethod("EvaluateObjective", All);

            if (broadcastEnd == null || sweepTick == null || send == null || sweep == null || settle == null ||
                keyOf == null || isHost == null || giveXp == null || evaluate == null)
            {
                yield return "L329 premise-changed: the mission-end scoring family no longer resolves " +
                             "(TacticalTurnSync.HostBroadcastEnd/HostSweepTick/Send, " +
                             "TacticalCommandSync.HostSettleAllLive/HostSettle, TacticalActorKey.Of, " +
                             "NetworkEngine.IsHost, TacticalFaction.GiveExperienceForObjectives, " +
                             "RescueSoldiersFactionObjective.EvaluateObjective). Every arm below would pass " +
                             "vacuously, so 'the peers score the host's board' is UNCHECKED rather than " +
                             "satisfied";
                yield break;
            }

            // ═══ (a) ORDER, WHICH IS THE WHOLE POINT ═══
            var seq = Program.CalleeSequence(broadcastEnd);
            int atSweep = seq.FindIndex(c => Same(c, sweep));
            int atSend = seq.FindIndex(c => Same(c, send));
            if (atSweep < 0)
                yield return "L329 mission-end-scores-two-boards: TacticalTurnSync.HostBroadcastEnd no longer " +
                             "sweeps the host's actors before announcing the end. Every peer computes its own " +
                             "objectives and its own XP from its OWN actors (RescueSoldiersFactionObjective" +
                             ".EvaluateObjective:37-72, TacticalFaction.GiveExperienceForObjectives:207-294), " +
                             "and a mission that ends on the same turn its last unit evacuates never reaches " +
                             "another turn edge for HostSweepTick to heal it — measured live 2026-08-08: 3 " +
                             "evacuated on the host, 2 on the client, XP diverged with them";
            else if (atSend < 0)
                yield return "L329 mission-end-announces-nothing: TacticalTurnSync.HostBroadcastEnd no longer " +
                             "reaches Send, so the outcome byte never leaves the host and the ordering this " +
                             "law asserts is over a message that does not exist";
            else if (atSweep > atSend)
                yield return "L329 settle-arrives-after-the-verdict: HostBroadcastEnd sweeps at callee #" +
                             atSweep + " but announces the end at #" + atSend + " — the sweep is AFTER the " +
                             "announcement. A client runs the native GameOver the moment OpEnd lands " +
                             "(ApplyEnd → tlc.GameOver), so settles queued behind it correct a board that has " +
                             "already been scored. Being called is not the fix; being called FIRST is";

            // ═══ (b) ONE SWEEP, THE ONE ALREADY PROVEN ═══
            if (!Reaches(sweepTick, sweep, mod))
                yield return "L329 turn-edge-and-mission-end-diverged: TacticalTurnSync.HostSweepTick no " +
                             "longer reaches the same HostSettleAllLive that HostBroadcastEnd does. Two " +
                             "sweeps is two tables, and the mission-end one is the copy nobody exercises " +
                             "until the summary is already wrong";

            // ═══ (c) THE SWEEP STILL SWEEPS ═══
            if (!Reaches(sweep, isHost, mod))
                yield return "L329 sweep-not-host-only: TacticalCommandSync.HostSettleAllLive no longer reads " +
                             "NetworkEngine.IsHost. A client that sweeps broadcasts ITS board over the host's " +
                             "— the exact inversion of this law, and it would land during the mission-end " +
                             "teardown where nothing corrects it afterwards";
            if (!Reaches(sweep, keyOf, mod))
                yield return "L329 sweep-ships-unnameable-actors: HostSettleAllLive no longer consults " +
                             "TacticalActorKey.Of, so it settles actors no peer can name on the wire; the " +
                             "records are dropped at the far end and the boards stay apart";
            if (!Reaches(sweep, settle, mod))
                yield return "L329 sweep-settles-nobody: HostSettleAllLive no longer reaches HostSettle — it " +
                             "walks the map and ships nothing, which reads exactly like a working sweep from " +
                             "every call site and from arm (a)";
            string threw = null;
            try { sweep.Invoke(null, new object[] { "railcheck" }); }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).Message; }
            if (threw != null)
                yield return "L329 sweep-throws-with-no-session: HostSettleAllLive(\"railcheck\") threw '" +
                             threw + "' with no live engine. It runs on the mission-end path now, inline " +
                             "before the outcome is announced, so anything it throws takes the announcement " +
                             "with it and every client is stranded in a battle the host has already ended";

            // ═══ (d) THE PREMISE: THE SCORING REALLY IS LOCAL ═══
            var scorers = new[] { (MethodBase)giveXp, evaluate };
            var callers = mod.GetTypes()
                             .SelectMany(Methods)
                             .Where(m => Program.CalleeSequence(m).Any(c => scorers.Any(s => Same(c, s))))
                             .Select(Name)
                             .Distinct()
                             .OrderBy(n => n, StringComparer.Ordinal)
                             .ToList();
            if (callers.Count > 0)
                yield return "L329 premise-stale-scoring-is-no-longer-local: [" +
                             string.Join(", ", callers.ToArray()) + "] now calls the game's own scoring " +
                             "(TacticalFaction.GiveExperienceForObjectives / RescueSoldiersFactionObjective" +
                             ".EvaluateObjective). This law's whole argument is that every peer computes the " +
                             "score for itself, which is WHY the boards must be made to agree first. If the " +
                             "mod drives or replicates the score directly, re-argue the law rather than " +
                             "leaving it green over a mechanism that no longer decides anything";
        }

        private static IEnumerable<MethodBase> Methods(Type t)
        {
            try { return t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)); }
            catch { return Enumerable.Empty<MethodBase>(); }
        }

        private static string Name(MethodBase m) =>
            (m.DeclaringType == null ? "?" : m.DeclaringType.FullName) + "." + m.Name;

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null && Program.Callees(from, asm).Any(c => Same(c, target));
    }
}
