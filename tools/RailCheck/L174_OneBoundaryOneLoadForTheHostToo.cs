using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L174 — A HOST DOES NOT LOAD A WORLD IT ALREADY IS, AND SKIPPING THAT LOAD STILL LEAVES IT IN THE
    /// TERMINAL STATE EVERY OTHER PEER REACHES.
    ///
    /// THE REPORT (2026-08-07, and the third round on it): "creating a new game makes the HOST load twice".
    /// The host log dates both loads exactly — geoscape playable 23:09:04.174 (native campaign creation),
    /// then <c>BEGIN broadcast</c> 23:09:07.437 and a SECOND <c>OnReachedPlaying slot=0</c> at
    /// 23:09:16.631. 9.2 s spent deserializing bytes the host had produced from its own live campaign
    /// seconds earlier. <c>17bf9fe</c> removed the interactive geoscape that used to flash BETWEEN the two
    /// and could not remove the second load, because the second load was never a curtain bug.
    ///
    /// IT IS ALSO WHY THE SAME SESSION READS AS "the host finished loading first and could already play".
    /// It is literally true: the host's world was playable 16 s before the collective reveal, and that
    /// window exists ONLY because a second load had to run inside it. One structural defect, two reports —
    /// which is the shape of the owner's "we fix it and half of them come back": every previous round fixed
    /// the cosmetic half.
    ///
    /// THE ARM IS ALREADY PROVEN ON THE OTHER PATH, so this is an inconsistency being removed, not a new
    /// idea being tried. <c>HostBeginTacticalEntryTransfer</c> has always refused the same re-entry —
    /// "the host does NOT re-enter from the blob (it is already in this live tactical level)" — and that
    /// path ships. The new-campaign bootstrap is the one GEOSCAPE boundary with the identical property: its
    /// blob is an autosave OF THE WORLD THE HOST IS STANDING IN, written by
    /// <c>NewCampaignAutosaveAndTransferCrt</c> moments before. Every OTHER caller of
    /// <c>LaunchTransfer</c> is loading a DIFFERENT save (the lobby start from the menu, the F2 mid-session
    /// reload) and must still re-enter — which is why the flag defaults to false and exactly one call site
    /// passes true.
    ///
    /// WHAT THIS LAW ASSERTS IS THE TERMINAL STATE, NOT THE SKIP. Removing a load is trivial; removing it
    /// without hanging every peer is the whole problem, and each half below is a way the change could have
    /// silently broken the barrier instead of the bug:
    ///   (a) SessionBegin must STILL go out. The host is now already <c>_begun</c> when <c>Begin()</c> runs,
    ///       and <c>SessionBegin</c> is what releases the CLIENTS into their own load. If
    ///       <c>BeginSuppressed</c> swallowed it on the begun flag, the clients would sit behind a curtain
    ///       for a load nobody ever told them to start — a permanent hang, strictly worse than the bug.
    ///       Executed against the production predicate, plus the negative control that it does still
    ///       suppress a re-fire with no barrier open.
    ///   (b) The host must STILL report its own completion. <c>OpenBarrier</c> clears the done mark and no
    ///       <c>Loaded→Playing</c> edge is coming for a peer that never leaves its level, so nothing would
    ///       ever put slot 0 back into the done set and <c>AllDone</c> could never hold — every peer waits
    ///       forever, including the one that is already playing.
    ///   (c) The barrier must still WAIT for the others. Executed on the real tracker: the host alone is
    ///       not all-done; a peer that has not reported keeps it shut.
    ///   (d) The reload-boundary sweep the skipped prepare owed must still run. The host's geoscape WAS
    ///       replaced at this boundary — by the native creation rather than by a re-entry — so the in-flight
    ///       engine state pointing at the old one has to go somewhere, and the only other caller of
    ///       <c>ResetForReloadBoundary</c> is the branch that is no longer taken.
    ///
    /// DIVERGENCE IS DETECTED, NOT ASSUMED, AND BY REUSE. Only the host now keeps a graph it minted rather
    /// than deserialized, so the same-bytes invariant is paid for by the rail's existing law-7 drift
    /// backstop: each client CRCs one geoscape root per second with the host's own canonical walk
    /// (<c>GenericApplier.ClientCrcTick</c> → <c>DiffEngine.RootCrc</c>) and the host compares it against
    /// its live graph in <c>DiffEngine.HandleCrcReport</c> — by its own doc "the ONE thing in the rail that
    /// ever compares host and client state". No second fingerprint was built.
    ///
    /// SCOPE, so it is not confused with its sibling: L122 says one session entry into a TACTICAL level,
    /// one load per peer. This is the same claim on the geoscape side of the same coordinator, and neither
    /// weakens the other.
    ///
    /// Falsify: drop the <c>SendLoadComplete</c> from the skip branch → <c>host-never-reports</c> (and the
    /// barrier hangs); drop the sweep → <c>reload-sweep-skipped</c>; make <c>BeginSuppressed</c> honour the
    /// begun flag over an open barrier → <c>clients-never-released</c>; make <c>AllDone</c> hold on the host
    /// alone → <c>barrier-stops-waiting</c>; remove the parameter or rename any member →
    /// <c>premise-changed</c>. POSITIVE CONTROL: the suppression predicate is also asserted to still
    /// suppress when it should, so an always-false <c>BeginSuppressed</c> cannot make (a) pass.
    /// </summary>
    internal static class L174_OneBoundaryOneLoadForTheHostToo
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var mod = coord.Assembly;

            var launch = coord.GetMethod("LaunchTransfer", All);
            var sendBody = IteratorBody(coord, "HostSerializeAndSendCrt");
            var report = coord.GetMethod("SendLoadComplete", All);
            var enterLevel = coord.GetMethod("EnterLevel", All);
            var begun = coord.GetField("_begun", All);
            var holdsParam = launch?.GetParameters().FirstOrDefault(p => p.Name == "hostHoldsThisWorld");

            if (launch == null || sendBody == null || report == null || enterLevel == null ||
                begun == null || holdsParam == null)
            {
                yield return "L174 premise-changed: one of SaveTransferCoordinator.LaunchTransfer (with its " +
                             "hostHoldsThisWorld parameter), the HostSerializeAndSendCrt iterator body, " +
                             ".SendLoadComplete, .EnterLevel or ._begun no longer resolves. This law's whole " +
                             "subject is the branch where the host declines to re-enter a world it already " +
                             "is — re-read who loads what at that boundary before trusting any of it.";
                yield break;
            }

            if (holdsParam.ParameterType != typeof(bool) || !holdsParam.HasDefaultValue ||
                !false.Equals(holdsParam.DefaultValue))
                yield return "L174 premise-changed: LaunchTransfer.hostHoldsThisWorld is no longer a bool " +
                             "defaulting to FALSE. The default is what keeps the lobby start and the F2 " +
                             "mid-session reload re-entering — those load a DIFFERENT save and MUST rebuild " +
                             "from it. A default of true would silently leave both of them in whatever world " +
                             "they were already in, which is not the world they were asked to load.";

            // directCallsOnly: what this law means is that the branch ITSELF makes these two calls, so a
            // delegate LOAD (ldftn) referencing them without running them must not read as an edge —
            // Program.CallSites:10091 draws exactly that line. (Callees is already one method's own IL, not
            // transitive reachability, so this narrows the opcode set and nothing else.)
            var callees = Program.Callees(sendBody, mod, directCallsOnly: true).ToList();

            // ── (b) THE HOST STILL ANNOUNCES ITSELF LOADED ────────────────
            if (!callees.Any(Same(report)))
                yield return "L174 host-never-reports: HostSerializeAndSendCrt no longer reaches " +
                             "SendLoadComplete. A host that skips its own re-entry never gets a Loaded→Playing " +
                             "edge, so OnReachedPlaying cannot fire and OpenBarrier has just cleared the done " +
                             "mark — nothing puts slot 0 back into the tracker. AllDone can then never hold, " +
                             "RevealAll is never broadcast, and EVERY peer waits behind a curtain forever, " +
                             "including the one that is already standing in the finished world. That is the " +
                             "barrier's own hang, reached by removing a loading screen.";

            // ── (d) …AND STILL PAYS THE DEBT THE SKIPPED PREPARE OWED ─────
            if (!callees.Any(c => c.Name == "ResetForReloadBoundary"))
                yield return "L174 reload-sweep-skipped: HostSerializeAndSendCrt no longer reaches " +
                             "ResetForReloadBoundary. PrepareEntryFromBlobCrt was the host's only route to the " +
                             "rca-3 sweep, and the branch that skips it still crosses a boundary where the " +
                             "host's geoscape was replaced — by the native campaign creation instead of by a " +
                             "re-entry. The choice arbiter, event mirror, intent dedup, coalesce marks and " +
                             "vehicle mirrors would keep pointing at the campaign that no longer exists.";

            // ── (a) SessionBegin STILL REACHES THE CLIENTS ────────────────
            // EXECUTED on the production predicate at the row this change creates: the host is already begun
            // (it never left its level) and the barrier it just opened is still open.
            if (SaveTransferMath.BeginSuppressed(begun: true, hostEntryHold: false, barrierOpen: true))
                yield return "L174 clients-never-released: with the host already begun — which is exactly what " +
                             "declining the re-entry means — and its barrier open, BEGIN is SUPPRESSED. " +
                             "SessionBegin is the packet that releases every CLIENT into its own load, so " +
                             "suppressing it leaves them curtained for a load nobody ever told them to start, " +
                             "while the host plays the world alone. Removing the host's second loading screen " +
                             "must not cost the clients their first.";

            // POSITIVE CONTROL: the predicate must still be able to say YES, or (a) proves nothing.
            if (!SaveTransferMath.BeginSuppressed(begun: true, hostEntryHold: false, barrierOpen: false))
                yield return "L174 POSITIVE CONTROL failed: BeginSuppressed refuses to suppress a re-fire on an " +
                             "already-begun session with NO barrier open, so it now answers false for every " +
                             "input and arm (a) would pass however BEGIN behaved. A guard that cannot say yes " +
                             "is not guarding.";

            // ── (c) THE BARRIER STILL WAITS FOR EVERY OTHER PEER ──────────
            // The host reporting itself in must NOT be the whole roster: executed on the real tracker.
            var tracker = new RosterProgressTracker();
            var roster = new List<byte> { 0, 1 };
            tracker.MarkDone(0);                       // the host, via the SendLoadComplete above
            if (tracker.AllDone(roster))
                yield return "L174 barrier-stops-waiting: with only the host reported in, AllDone already " +
                             "holds. The host would broadcast RevealAll the instant it declined its own " +
                             "re-entry — taking the curtain down on a client still downloading the save. That " +
                             "turns this fix into the very defect L94 exists for: one player acting on a world " +
                             "the others have not reached.";
            tracker.MarkDone(1);
            if (!tracker.AllDone(roster))
                yield return "L174 barrier-never-opens: every roster slot has reported and AllDone still does " +
                             "not hold, so the reveal that frees everyone can never fire.";

            // ── AND THE NO-OP THAT MAKES IT SAFE IS REAL ──────────────────
            // Begin() still calls EnterLevel(); the host is only spared a second load because EnterLevel
            // self-guards on _begun. If it stopped reading that flag, the skip branch would re-enter anyway.
            if (!Program.ReadsField(enterLevel, begun))
                yield return "L174 reentry-guard-gone: EnterLevel no longer reads _begun, so its self-guard is " +
                             "gone and Begin() would drive the host through FinishLevel a second time after " +
                             "all — the double load returning by the back door, with the flag that was " +
                             "supposed to prevent it still set.";
        }

        private static Func<MethodBase, bool> Same(MethodBase b) =>
            a => a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        /// <summary>The compiler-generated MoveNext of an iterator method — the only place its real IL lives
        /// (same helper, same reason, as L86).</summary>
        private static MethodBase IteratorBody(Type owner, string methodName)
        {
            foreach (var t in owner.GetNestedTypes(All))
            {
                if (!t.Name.Contains("<" + methodName + ">")) continue;
                var mv = t.GetMethod("MoveNext", All);
                if (mv != null) return mv;
            }
            return null;
        }
    }
}
