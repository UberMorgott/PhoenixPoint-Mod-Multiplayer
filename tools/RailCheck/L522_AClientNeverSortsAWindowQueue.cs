using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L522 — A CLIENT NEVER SORTS A WINDOW QUEUE. The host publishes an ORDERED stream; the client
    /// reconciles its own queue to it. It does not invent a key and it does not compare two windows.
    ///
    /// This is the Kafka single-partition constraint (§2.5): a total order exists only within ONE
    /// partition, and two order keys IS the bug. The client half of the fix is not "sort with the right
    /// key", it is "do not sort".
    ///
    /// ARMS:
    ///   (a) client-comparator-survives / client-resort-survives — WindowOrder exposes neither Compare nor
    ///       Reorder.
    ///   (b) settle-survives — WindowOrder exposes neither SettleSeconds nor SettleExpired. The settle was
    ///       a 150 ms hold-and-reorder and the measured skew was 363 ms; beside the journal it is a second
    ///       ordering system, which is the exact mistake §A.7 deletes.
    ///   (c) head-is-not-the-lowest-position — EXECUTED, CLIENT ROLE ONLY: no MintHostPosition call appears
    ///       in this arm, so a host-side fault cannot make it pass (§C.3).
    ///   (d) gap-never-permanent — EXECUTED with the clock INJECTED: a gap holds, then self-releases. A
    ///       gate that could hold forever would be a wait on another peer, which §7.6 forbids outright.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add
    /// `internal static int Compare(int, uint, int, uint)` to WindowOrder → (a); re-add
    /// `internal const float SettleSeconds = 0.15f;` → (b); make `WindowJournal.Append` use `_unread.Add`
    /// → (c); make `WindowGap.SelfReleasedAt` return false unconditionally → (d).
    /// </summary>
    internal static class L522_AClientNeverSortsAWindowQueue
    {
        internal static IEnumerable<string> Check()
        {
            var order = typeof(WindowOrder);
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.DeclaredOnly;

            if (order.GetMethod("ReadyToDequeue", Any) == null)
            {
                yield return "L522 premise-changed: WindowOrder.ReadyToDequeue did not resolve, so the " +
                             "drain gate this law is about has moved. Re-point it before believing the " +
                             "verdict.";
                yield break;
            }

            if (order.GetMethod("Compare", Any) != null)
                yield return "L522 client-comparator-survives: WindowOrder.Compare still exists. The host " +
                             "publishes an ordered stream and the client reconciles to it — a client that " +
                             "can compare two windows is a second ordering authority, and two order keys " +
                             "on one stream is the bug this work removes.";

            if (order.GetMethod("Reorder", Any) != null)
                yield return "L522 client-resort-survives: WindowOrder.Reorder still exists. Re-sorting a " +
                             "settled queue is the device that never once ran in the field — the string " +
                             "'settled queue re-ordered by rail ordinal' appears ZERO times across all " +
                             "three complete logs of the 2026-08-15 session.";

            if (order.GetField("SettleSeconds", Any) != null || order.GetMethod("SettleExpired", Any) != null)
                yield return "L522 settle-survives: WindowOrder still carries the settle timer. It was a " +
                             "150 ms hold-and-reorder against a measured 363 ms skew — it cannot be tuned " +
                             "into correctness, and beside the journal it is a second ordering system.";

            // (c) CLIENT ROLE ONLY.
            WindowJournal.Reset();
            WindowJournal.Append(9, "UIStateGeoscapeEvent", new byte[] { 9 });
            WindowJournal.Append(4, "UIStateGeoModal", new byte[] { 4 });
            WindowJournal.Append(7, "UIStateAssetDeployment", new byte[] { 7 });
            var head = WindowJournal.PeekHead();
            if (head == null || head.Pos != 4)
                yield return "L522 head-is-not-the-lowest-position: after appending 9, 4, 7 the head was " +
                             (head == null ? "<null>" : head.Pos.ToString()) + ", not 4. The client's " +
                             "entire reconciliation is 'the head is the lowest unread HOST position'; if " +
                             "that is false the client is ordering by arrival again.";

            int drained = 0;
            JournalEntry e;
            while (WindowJournal.TryRead(out e)) drained++;
            if (drained != 3)
                yield return "L522 positive-control: the journal yielded " + drained + " of 3 appended " +
                             "entries, so arm (c) inspected a queue that does not hold windows and would " +
                             "report a correct head against an empty list.";
            WindowJournal.Reset();

            // (d) THE GAP ENDS BY ITSELF.
            WindowGap.Reset();
            if (WindowGap.SelfReleasedAt(42, 100.0))
                yield return "L522 gap-released-immediately: a gap released on first sight. It must hold " +
                             "long enough for the raise to arrive, or the host's order is being abandoned " +
                             "the moment it is inconvenient.";
            if (WindowGap.SelfReleasedAt(42, 100.0 + WindowGap.SelfReleaseSeconds - 0.01))
                yield return "L522 gap-released-early: a gap released before its armed interval elapsed.";
            if (!WindowGap.SelfReleasedAt(42, 100.0 + WindowGap.SelfReleaseSeconds + 0.01))
                yield return "L522 gap-never-permanent: a gap did NOT self-release after its armed " +
                             "interval. A drain gate that can hold forever is a wait on another peer, " +
                             "which the no-blockers rule forbids outright — one player must be able to " +
                             "drive the whole game while every other peer is AFK.";
            WindowGap.Reset();
        }
    }
}
