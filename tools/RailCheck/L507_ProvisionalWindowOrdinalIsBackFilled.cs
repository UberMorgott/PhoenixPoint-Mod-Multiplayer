using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L507 — A WINDOW THE HOST RAISES OUTSIDE AN APPLY SORTS BY THE ORDINAL ITS CAUSE ACTUALLY LEAVES ON,
    /// NOT BY THE ONE IT GUESSED WHILE IT WAS BORN.
    ///
    /// THE DEFECT (fixed in <c>d449ea4</c>). L124 established the cross-surface key: one ordinal minted per
    /// outbound envelope at the single encoder, inherited by anything a peer produces from inside an apply.
    /// That made every window a peer RECEIVES agree with every other peer. It did not make the HOST agree
    /// with them. The host raises a research-complete modal NATIVELY, in the sim, a whole diff cycle before
    /// the 0xAC batch carrying the research field goes out, so <c>WindowOrder.Stamp</c> ran with
    /// <c>RailOrdinal.Current == 0</c> and took <c>ForNewWindow()</c> — "one past everything seen or sent",
    /// which is a guess about a message that has not been minted yet. Anything arriving between the raise
    /// and that batch (any inbound envelope at all — <c>Observe</c> raises this peer's floor) moves the
    /// value the batch is finally minted with, and the clients inherit THAT while applying it. Host sorted
    /// by CREATION order, clients by WIRE order, and the two disagreed exactly in the case L124 was written
    /// for.
    ///
    /// THE FIX IS THE BACK-FILL, and this law asserts it as an OBSERVABLE, not as a call: a stamp taken
    /// outside an apply registers itself PROVISIONAL (<c>RailOrdinal.Provisional</c>) and the next
    /// <c>RailOrdinal.Mint</c> — the one encoder, i.e. the envelope that carries the cause out — drains the
    /// list and writes the ordinal it just minted into the key. A stamp taken INSIDE an apply already holds
    /// the shared ordinal and must be left alone, or a mirrored window would be re-keyed to whatever this
    /// peer sent next.
    ///
    /// EXECUTED, not reflected. Arms B and C drive the production <c>WindowOrder.StampAt</c> (the clock is
    /// injected precisely so a law can call it: <c>Time.realtimeSinceStartup</c> is an ECall that throws
    /// outside a player) and read the key back through the production <c>WindowOrder.OrdinalOf</c>. Arm D
    /// then runs the REAL <c>WindowOrder.Reorder</c> over a REAL queue and asserts the ORDER, which is the
    /// thing the player saw wrong — a back-fill that lands but does not change the sort proves nothing.
    ///
    /// NO PEER AND NO QUORUM (P13): every value here is this peer's own encoder counter. The predicate that
    /// releases the provisional key is "this peer minted an envelope", which happens every DiffEngine tick;
    /// nothing waits on another player acting, or acting at all.
    ///
    /// Falsify (each verified RED, then restored):
    ///   • drop the RailOrdinal.Provisional(...) registration from WindowOrder.StampAt → host-key-not-back-filled
    ///     / host-sorts-by-creation-order
    ///   • make RailOrdinal.Mint drain the list without calling the back-fills      → host-key-not-back-filled
    ///   • register the provisional unconditionally (drop the Current == 0 test)    → applied-key-overwritten
    ///   • put the clock read back inside StampAt, or stop routing Stamp through it → premise-changed
    /// </summary>
    internal static class L507_ProvisionalWindowOrdinalIsBackFilled
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            // ─── ARM A: PREMISE — the game's own stamp is the one the arms below execute ───
            // StampAt is not a test double: WindowOrder.Stamp must still be nothing but the clock read and
            // the catch around it, or this law observes code the game never runs.
            var stamp = typeof(WindowOrder).GetMethod("Stamp", All);
            var stampAt = typeof(WindowOrder).GetMethod("StampAt", All);
            var mint = typeof(RailOrdinal).GetMethod("Mint", All);
            var encode = typeof(SyncProtocol).GetMethod("EncodeEnvelope", All);
            if (stamp == null || stampAt == null || mint == null || encode == null ||
                !Program.CalleeSequence(stamp).Any(c => c != null && c.Name == "StampAt"))
            {
                yield return "L507 premise-changed: WindowOrder.Stamp no longer routes through StampAt, or " +
                             "RailOrdinal.Mint / SyncProtocol.EncodeEnvelope did not resolve. The arms below " +
                             "would then be observing a stamp the game does not take.";
                yield break;
            }
            if (!Program.CalleeSequence(encode).Any(c => c != null && c.Name == "Mint"))
                yield return "L507 premise-changed: SyncProtocol.EncodeEnvelope no longer mints. The back-fill " +
                             "below is released by the ONE encoder; if minting moves elsewhere, a provisional " +
                             "key is released by something that is not the envelope carrying its cause.";

            // ─── ARM B: EXECUTED — a host-local stamp carries the ordinal the NEXT envelope minted ───
            RailOrdinal.Reset();
            var hostWindow = new GeoscapeViewStateSwitchRequest(null, 0);
            WindowOrder.StampAt(hostWindow, 0f);          // Current == 0: raised in the sim, not in an apply
            uint guessed = WindowOrder.OrdinalOf(hostWindow);
            RailOrdinal.Observe(1000u);                   // an inbound envelope lands before our batch leaves
            uint minted = RailOrdinal.Mint();             // the envelope that finally carries the cause out
            uint carried = WindowOrder.OrdinalOf(hostWindow);

            // POSITIVE CONTROL, and it is the whole reason arm B can fail: if the value the stamp guessed
            // already equalled the minted one, the assertion below would pass with the back-fill deleted.
            if (guessed == 0u || minted == 0u || guessed == minted)
                yield return "L507 positive-control: the host stamp guessed " + guessed + " and the next mint " +
                             "produced " + minted + ". The two must differ for this arm to test anything — " +
                             "RailOrdinal.Observe no longer moves this peer's floor, so the divergence the " +
                             "back-fill exists to close cannot be staged and every verdict below is vacuous.";
            if (carried != minted)
                yield return "L507 host-key-not-back-filled: a window stamped outside an apply still carries " +
                             carried + " after the next envelope was minted with " + minted + ". That envelope " +
                             "is the one a client applies, so the client's copy of this window inherits " +
                             minted + " and the host sorts it as " + carried + " — host and clients disagree " +
                             "about the order of two windows again, which is the 2026-08-05 report.";

            // ─── ARM C: EXECUTED — a stamp taken INSIDE an apply is left exactly as it was ───
            // The mirrored half must not be re-keyed: its ordinal is already the shared one.
            const uint applying = 4242u;
            var mirroredWindow = new GeoscapeViewStateSwitchRequest(null, 0);
            using (RailOrdinal.Applying(applying)) WindowOrder.StampAt(mirroredWindow, 0f);
            uint inherited = WindowOrder.OrdinalOf(mirroredWindow);
            RailOrdinal.Mint();
            uint afterMint = WindowOrder.OrdinalOf(mirroredWindow);
            if (inherited != applying || afterMint != applying)
                yield return "L507 applied-key-overwritten: a window born while ordinal " + applying + " was " +
                             "being applied carried " + inherited + " and " + afterMint + " after the next " +
                             "mint. Registering it provisional too would replace the ordinal it SHARES with " +
                             "every other peer by whatever this peer happened to send next — the same " +
                             "divergence, mirrored.";

            // ─── ARM D: EXECUTED — the REAL sort, over a REAL queue: the host now sorts like a client ───
            // A key that is back-filled but does not move the queue would have fixed nothing the player saw.
            var earlier = new GeoscapeViewStateSwitchRequest(null, 0);
            using (RailOrdinal.Applying(minted - 1u)) WindowOrder.StampAt(earlier, 0f);
            var queue = new List<GeoscapeViewStateSwitchRequest> { hostWindow, earlier };
            WindowOrder.Reorder(queue, WindowOrder.OrdinalOf);
            if (!ReferenceEquals(queue[0], earlier))
                yield return "L507 host-sorts-by-creation-order: the host's own queue still opens the " +
                             "locally-raised window (ordinal " + WindowOrder.OrdinalOf(hostWindow) + ") before " +
                             "a window caused by ordinal " + (minted - 1u) + ", while every client — which " +
                             "reads " + minted + " for the first — opens them the other way round. Two peers, " +
                             "two orders, correct contents: exactly the defect the ordinal was introduced for.";
            RailOrdinal.Reset();
        }
    }
}
