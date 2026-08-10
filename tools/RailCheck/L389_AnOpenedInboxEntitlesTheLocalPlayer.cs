using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L389 — THE INBOX A SESSION OPENS MUST ALREADY ENTITLE THE LOCAL PLAYER.
    ///
    /// THE DEFECT (live 2026-08-10, all three peers). <c>HostLedger</c> DERIVES its member set from its own
    /// entries when none is supplied (<c>DurableInboxModel</c>:485) and
    /// <c>HostInboxSequencer.CreateOccurrence</c> mints one entry PER MEMBER (:534). A store opened with an
    /// empty ledger therefore had no member, so it created no entry, so it still had no member — a bootstrap
    /// that can never start. Everything downstream fails CLOSED and SILENTLY:
    /// <c>WindowQueueSync.TryLocalMember</c>:315 answers false forever, and the event click path
    /// (<c>EventPopup</c>:1838 "answer dropped — no active local entitlement", <c>EventSync</c>:378
    /// "host-local answer blocked") throws the click away. Observed: the mission-start window
    /// <c>PROG_NJ0_MISS</c> swallowed eleven consecutive clicks on a client and three on the host.
    /// <c>HostInboxSequencer.Enroll</c> was deleted in <c>da02dce</c> for having "zero production callers" —
    /// true, and that was the bug, not the justification. The laws that would have caught it (L376, L397-L400)
    /// went with it.
    ///
    /// ARMS
    ///   (a) <c>bootstrapped-ledger-entitles-nobody</c> — EXECUTED. A ledger opened WITH one member must, on
    ///       <c>CreateOccurrence</c>, produce exactly one entry for that member, and the member must be
    ///       reachable through <c>Members</c> — the collection <c>TryLocalMember</c> searches.
    ///   (b) <c>control-not-red</c> — POSITIVE CONTROL, and it is the defect itself. The SAME call over a
    ///       member-less ledger must still produce ZERO entries. If that ever stops being true the bootstrap
    ///       has moved somewhere else and arm (a) passes by construction while proving nothing.
    ///   (c) <c>opened-store-does-not-name-the-local-player</c> — STRUCTURAL, and it is what ties (a) to
    ///       production: <c>DurableInboxSession.OpenSessionStore</c> must reach <c>ClientIdentity.PlayerGuid</c>
    ///       and construct a <c>MembershipId</c>. Executing it is impossible here — it needs a live
    ///       <c>NetworkEngine</c> session, and <c>ClientIdentity</c> reads Unity's
    ///       <c>Application.persistentDataPath</c>, an ECall the headless harness cannot JIT.
    ///   (d) <c>entitlement-consumer-stopped-reading-the-member-set</c> — STRUCTURAL. <c>TryLocalMember</c>
    ///       must still read <c>HostLedger.Members</c>; a consumer that stopped asking makes (a) irrelevant.
    ///
    /// Falsify:
    ///   • VERIFIED RED (RUN 2026-08-10, against a real build, then restored) — revert the member argument in
    ///     <c>OpenSessionStore</c> back to <c>new HostLedger(Array.Empty&lt;InboxEntry&gt;())</c> →
    ///     <c>opened-store-does-not-name-the-local-player</c>
    ///   • not run — make <c>CreateOccurrence</c> mint an entry for a member it was not given → arm (b)
    ///   • not run — rename either method → <c>premise-changed</c>
    /// </summary>
    internal static class L389_AnOpenedInboxEntitlesTheLocalPlayer
    {
        internal static IEnumerable<string> Check()
        {
            var open = typeof(DurableInboxSession).GetMethod("OpenSessionStore",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var local = typeof(WindowQueueSync).GetMethod("TryLocalMember",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (open == null || local == null)
            { yield return "L389 premise-changed: the session store opener or the local-member lookup disappeared"; yield break; }

            var occurrence = new OccurrenceId("Story", "trigger", new[] { "subject" });

            // (a) WITH a member: one entitlement, and it is findable the way production finds it.
            var member = new MembershipId("00000000-0000-0000-0000-00000000000a");
            var seeded = new HostInboxSequencer(new HostLedger(Array.Empty<InboxEntry>(), 0, new[] { member }));
            if (!seeded.CreateOccurrence(occurrence) ||
                seeded.Ledger.AllEntries.Count(x => x.Occurrence.Equals(occurrence) && x.Membership.Equals(member)) != 1 ||
                !seeded.Ledger.Members.Contains(member))
                yield return "L389 bootstrapped-ledger-entitles-nobody: an inbox opened for this peer created " +
                             "no entitlement for it, so every window it routes will swallow the click";

            // (b) POSITIVE CONTROL — the defect, executed. No member in, no entry out.
            var bare = new HostInboxSequencer(new HostLedger(Array.Empty<InboxEntry>()));
            bare.CreateOccurrence(occurrence);
            if (bare.Ledger.AllEntries.Count != 0 || bare.Ledger.Members.Count != 0)
                yield return "L389 control-not-red: a member-less ledger now entitles somebody by itself, so " +
                             "arm (a) no longer proves the bootstrap happens at the session seam";

            // (c) the production seam must actually name this peer.
            var assembly = typeof(SyncEngine).Assembly;
            // NOT directCallsOnly: `new MembershipId(...)` is a NEWOBJ, which that filter drops on purpose
            // (it keeps ldftn out for L21) — and the constructor is half of what this arm has to see.
            var callees = Program.Callees(open, assembly).ToArray();
            if (!callees.Any(x => x.DeclaringType == typeof(Multiplayer.Network.ClientIdentity) &&
                                  x.Name == "get_PlayerGuid") ||
                !callees.Any(x => x.DeclaringType == typeof(MembershipId)))
                yield return "L389 opened-store-does-not-name-the-local-player: OpenSessionStore mints a ledger " +
                             "without reaching ClientIdentity.PlayerGuid and a MembershipId, so the member set " +
                             "is empty again and no entitlement can ever exist";

            // (d) the consumer must still be asking the collection arm (a) fills.
            if (!Program.Callees(local, assembly, true).Any(x => x.Name == "get_Members"))
                yield return "L389 entitlement-consumer-stopped-reading-the-member-set: TryLocalMember no longer " +
                             "reads HostLedger.Members, so the bootstrap above is checked against nothing";
        }
    }
}
