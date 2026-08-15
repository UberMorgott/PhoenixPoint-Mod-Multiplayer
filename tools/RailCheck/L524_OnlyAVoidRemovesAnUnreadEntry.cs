using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L524 — AN UNREAD JOURNAL ENTRY IS REMOVED ONLY BY BEING READ OR BY A HOST-MINTED VOID.
    ///
    /// There is no cap, no tail-trim, no time-based staleness, no LRU and no compaction pass (§A.2, §A.6).
    /// The backlog's length is bounded by what the local player has not looked at — a quantity the player
    /// controls. The 4096 line is a RUNAWAY-RAISER CANARY: it logs ONCE and the append CONTINUES; it never
    /// drops an entry and never stops the append.
    ///
    /// The shipped QueueCap = 64 (src/Rail/GeoWindowCoverage.cs:587) trimmed from the TAIL (:618-636),
    /// i.e. dropped the NEWEST pending window. Under accumulation that is exactly backwards, so both the
    /// constant and the trim go, together with the L82 bound arm that asserted them.
    ///
    /// ARMS, all EXECUTED against the real journal in a process with no game:
    ///   (a) trim-survives — no QueueCap constant and no TrimQueue method survive in the assembly.
    ///   (b) canary-drops — appending past RunawayCanaryAt keeps every entry.
    ///   (c) canary-stops-appending — the entry appended AFTER the canary fires is still present.
    ///   (d) void-removes — an explicit host-minted void DOES remove an unread entry (positive control:
    ///       without this, arms (b) and (c) pass against a journal from which nothing can ever be removed,
    ///       and the GLOBAL dismissal of §A.5 would silently not work).
    ///
    /// ROLES SEPARATED (§C.3): (b) and (c) execute the CLIENT role (append only), (d) executes the effect
    /// of a HOST-minted record; no arm mints a position, so a host mint fault cannot mask a client fault.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add QueueCap/TrimQueue to GeoWindowCoverage
    /// → (a); add a `while (_unread.Count > 64) _unread.RemoveAt(_unread.Count - 1);` tail-trim to
    /// WindowJournal.Append → (b); make the canary `return;` instead of falling through → (c); make
    /// ApplyVoid always return false without removing → (d).
    /// </summary>
    internal static class L524_OnlyAVoidRemovesAnUnreadEntry
    {
        internal static IEnumerable<string> Check()
        {
            var asm = typeof(WindowJournal).Assembly;
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            var coverage = asm.GetTypes().FirstOrDefault(t => t.Name == "GeoWindowCoverage");
            if (coverage == null)
            {
                yield return "L524 premise-changed: GeoWindowCoverage did not resolve, so arm (a) cannot " +
                             "see whether the cap it exists to forbid is still there.";
                yield break;
            }
            if (coverage.GetField("QueueCap", Any) != null || coverage.GetMethod("TrimQueue", Any) != null)
                yield return "L524 trim-survives: GeoWindowCoverage still carries QueueCap and/or " +
                             "TrimQueue. That trim removed from the TAIL — it dropped the NEWEST pending " +
                             "window — which directly contradicts accumulating what the local player has " +
                             "not yet looked at. Read ⇒ delete is the replacement, not a bigger cap.";

            WindowJournal.Reset();
            const int n = WindowJournal.RunawayCanaryAt + 8;
            for (uint i = 1; i <= n; i++) WindowJournal.Append(i, "UIStateGeoModal", new byte[] { 1 });
            if (WindowJournal.UnreadCount != n)
                yield return "L524 canary-drops: appended " + n + " entries, journal holds " +
                             WindowJournal.UnreadCount + ". The canary is a log line and nothing else — " +
                             "it NEVER drops an entry. An accepted entry that vanishes is a window the " +
                             "player will never be asked about.";

            uint after = (uint)(n + 1);
            WindowJournal.Append(after, "UIStateGeoscapeEvent", new byte[] { 2 });
            if (WindowJournal.UnreadCount != n + 1)
                yield return "L524 canary-stops-appending: the entry appended after the canary fired did " +
                             "not land. The canary must keep appending — it exists to make a raiser loop " +
                             "VISIBLE in a log, not to become the loop's silent enforcement.";

            if (!WindowJournal.ApplyVoid(after) || WindowJournal.UnreadCount != n)
                yield return "L524 positive-control: an explicit host-minted void did not remove an unread " +
                             "entry. Without a working void, the arms above pass against a journal from " +
                             "which nothing can ever be removed, and the GLOBAL dismissal of §A.5 would " +
                             "silently not work — two peers would then time out differently and diverge " +
                             "(FIX gap-fill, §2.5).";
            WindowJournal.Reset();
        }
    }
}
