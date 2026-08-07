using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L85 — A HOST STREAM THAT RESTARTS IS APPLIED, SO THE HOST'S "EVERY PEER FOLLOWS ME OUT OF THIS
    /// BATTLE" CANNOT BE DROPPED IN SILENCE.
    ///
    /// THE REPORT (2026-08-07, 3-peer session). After the second mission the host pressed RETURN TO
    /// GEOSCAPE and parked on "Waiting for players…" — for 18 minutes, until the ally clicked Continue on
    /// his own battle summary at 22:01:35 and the whole session moved again. The ally had never been told
    /// to leave: his mission-results table simply froze.
    ///
    /// THE ROOT IS A CURSOR THAT OUTLIVED THE RESET THAT DROPPED IT. <c>SurfaceIds.TacTurn</c> (0x80) is
    /// per-BATTLE: <c>TacticalTurnSync.Reset</c> runs on every peer at tactical teardown and clears the
    /// seq, and the host's <c>Next</c> hands out 1 again for the next battle. The two resets are NOT
    /// simultaneous, and ONE message is by construction sent after the receiver has already reset — the
    /// host's <c>OpLeave</c>, which exists precisely to reach a peer that has not left yet. Measured in
    /// the client's own log: 21:22:28.540 the client's GoToGeoscape → FinishLevel → Reset (proved by the
    /// line 118 ms later reporting <c>LeftBattle</c> FALSE), 21:22:28.658 the host's trailing leave
    /// applied and <c>Mark</c> put the cursor back to the OLD battle's value. Battle 2 then ran from
    /// 21:35 to 22:01 with ZERO 0x80 messages applied — no turn cursor, no mission end, no leave — while
    /// 0x82/0x83/0x84 flowed the whole time, because those streams had grown past their own stale
    /// cursors and this one had not. Not one log line was produced by any of the drops.
    ///
    /// WHY THE RULE IS ON <see cref="SurfaceSeq"/> AND NOT ON THE TURN FAMILY. The hazard is the generic
    /// keying seam, not the tactical arc: every per-battle stream resets the same way and any of them can
    /// be re-armed by a message in flight across its own teardown. One predicate on the shared primitive
    /// covers all of them; a guard inside <c>TacticalTurnSync</c> would leave the others waiting for
    /// their own session to be lost first.
    ///
    /// ARM (b) IS WHY THIS IS NOT "APPLY EVERYTHING". The restart is exactly seq 1 against a cursor that
    /// has ALREADY advanced past 1; an ordinary stale/duplicate message is still dropped, which is the
    /// whole reason the guard exists. Both directions are executed here, so widening the rule to accept
    /// anything smaller than the cursor turns this law red rather than passing unnoticed.
    ///
    /// Falsify: revert <c>ShouldApply</c> to the bare <c>seq &gt; last</c> → <c>restart-swallowed</c>;
    /// leave <c>ShouldApply</c> alone but not <c>Mark</c> → <c>restart-not-marked</c> (the restart applies
    /// once and the very next message of the new stream is dropped again); make <c>IsStreamRestart</c>
    /// return true for any backwards seq → <c>stale-accepted</c>; drop the <c>LastApplied</c> /
    /// <c>IsStreamRestart</c> pair from <c>TacticalTurnSync.HandleInbound</c> → <c>drop-is-silent</c>;
    /// break either end of the carry path → <c>carry-path-broken</c>; rename any member →
    /// <c>premise-changed</c>.
    /// </summary>
    internal static class L85_RestartedHostStreamIsApplied
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private const ushort Surface = 0x80;   // SurfaceIds.TacTurn, the stream the report was lost on

        internal static IEnumerable<string> Check()
        {
            var seqType = typeof(SurfaceSeq);
            var turn = typeof(TacticalTurnSync);
            var mod = turn.Assembly;

            var isRestart = seqType.GetMethod("IsStreamRestart", All);
            var lastApplied = seqType.GetMethod("LastApplied", All);
            var handleInbound = turn.GetMethod("HandleInbound", All);
            var applyLeave = turn.GetMethod("ApplyLeave", All);
            var onLocalLeave = turn.GetMethod("OnLocalLeaveBattle", All);
            var hostBroadcastLeave = turn.GetMethod("HostBroadcastLeave", All);

            if (isRestart == null || lastApplied == null || handleInbound == null || applyLeave == null ||
                onLocalLeave == null || hostBroadcastLeave == null)
            {
                yield return "L85 premise-changed: one of SurfaceSeq.{IsStreamRestart,LastApplied} or " +
                             "TacticalTurnSync.{HandleInbound,ApplyLeave,OnLocalLeaveBattle,HostBroadcastLeave} " +
                             "no longer resolves. Either the shared seq primitive or the leave-battle carry " +
                             "path has been reshaped, and this law is asserting something about a shape it no " +
                             "longer has — re-read how a peer is carried out of a finished battle before " +
                             "trusting any of it.";
                yield break;
            }

            // ── (a) THE MEASURED ROW, EXECUTED ────────────────────────────
            // Battle 1 advances the cursor past 1 and a trailing message re-arms it; battle 2 starts at 1.
            var seq = new SurfaceSeq();
            for (uint s = 1; s <= 12; s++) { seq.ShouldApply(Surface, s); seq.Mark(Surface, s); }
            if (seq.LastApplied(Surface) != 12)
                yield return "L85 premise-changed: SurfaceSeq no longer records the last applied seq per " +
                             "surface, so the cursor this law is about does not exist any more.";

            if (!seq.ShouldApply(Surface, 1))
                yield return "L85 restart-swallowed: the host's next battle opens its 0x80 stream at seq 1 " +
                             "against a cursor still holding the PREVIOUS battle's 12, and SurfaceSeq drops " +
                             "it. Every turn edge, the mission end and the host's 'battle LEFT — every peer " +
                             "follows' vanish with no log line; the host then waits on the reveal barrier for " +
                             "a peer it never told to load, forever, and the only way out is a human clicking " +
                             "Continue on a battle summary nobody knew he was still looking at.";

            seq.Mark(Surface, 1);
            if (seq.LastApplied(Surface) != 1)
                yield return "L85 restart-not-marked: the restart was accepted but the cursor was not " +
                             "rewound, so it still reads 12 and seq 2 of the new stream is dropped again. " +
                             "One message gets through and the rest of the battle is silent — the same hang " +
                             "one message later, which is worse than the original because it looks fixed.";

            if (!seq.ShouldApply(Surface, 2))
                yield return "L85 restart-not-marked: after the restart the stream does not continue — seq 2 " +
                             "of the new battle is refused, so the leave that follows the mission end can " +
                             "still never arrive.";

            // ── (b) NEGATIVE CONTROL: an ordinary stale message is STILL dropped ──
            if (SurfaceSeq.IsStreamRestart(2, 5) || SurfaceSeq.IsStreamRestart(4, 12) ||
                SurfaceSeq.IsStreamRestart(1, 1) || SurfaceSeq.IsStreamRestart(1, 0))
                yield return "L85 stale-accepted: IsStreamRestart calls something a restart that is not one. " +
                             "A restart is exactly seq 1 against a cursor that has already moved PAST 1; " +
                             "anything else backwards is the stale re-delivery this guard exists to drop, and " +
                             "a second seq 1 against a cursor of 1 is a duplicate, not a new battle. Widen " +
                             "this and the last-writer-wins guarantee every surface rests on is gone — a " +
                             "re-sent record would replay on top of newer state.";

            var fresh = new SurfaceSeq();
            fresh.Mark(Surface, 7);
            if (fresh.ShouldApply(Surface, 3))
                yield return "L85 stale-accepted: SurfaceSeq now applies a seq BELOW its cursor that is not a " +
                             "stream restart. The restart rule was meant to recognise one exact edge, not to " +
                             "disable the ordering guard.";

            // ── (c) THE DROP IS NOT SILENT on the surface the report was lost on ──
            var inboundCallees = Program.Callees(handleInbound, mod).ToList();
            if (!inboundCallees.Any(c => Same(c, lastApplied)) || !inboundCallees.Any(c => Same(c, isRestart)))
                yield return "L85 drop-is-silent: TacticalTurnSync.HandleInbound no longer reads its own " +
                             "cursor (LastApplied) and asks IsStreamRestart before the seq guard, so a battle " +
                             "whose whole 0x80 stream is refused says nothing at all. Silence is what cost the " +
                             "2026-08-07 session 18 minutes: the drop is invisible in the logs and the symptom " +
                             "surfaces one screen away, on a host parked at 'Waiting for players…'.";

            // ── (d) THE OUTCOME: both ends of the carry path still exist ──
            if (!Program.Callees(onLocalLeave, mod).Any(c => Same(c, hostBroadcastLeave)))
                yield return "L85 carry-path-broken: the host's own exit from a finished battle " +
                             "(OnLocalLeaveBattle) no longer reaches HostBroadcastLeave, so nothing tells the " +
                             "other peers to follow it out. The seq rule above then protects a message that is " +
                             "never sent, and every peer sits on its battle summary while the host waits for " +
                             "them on the geoscape.";

            if (!Program.Callees(handleInbound, mod).Any(c => Same(c, applyLeave)))
                yield return "L85 carry-path-broken: the 0x80 inbound no longer dispatches to ApplyLeave, so " +
                             "the message the host sends to carry every peer out of the battle arrives and " +
                             "does nothing. Same outcome as the drop this law exists for, one layer down.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
