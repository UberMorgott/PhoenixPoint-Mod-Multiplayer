using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplayer.Network.Sync;
using Multiplayer.Util;

namespace RailCheck
{
    /// <summary>L394 / DWI-19 — terminal identity survives until every durable reference class releases it.</summary>
    internal static class L394_ACompactionNeedsProofNoDurableSourceCanNameIt
    {
        internal static IEnumerable<string> Check()
        {
            var a = new MembershipId("player-a");
            var b = new MembershipId("player-b");
            var occurrence = new OccurrenceId("event", "trigger", new[] { "subject" });
            var order = new HostOrderKey(1, occurrence.TriggerId);
            InboxEntry Entry(MembershipId member, InboxLifecycle lifecycle, ulong tombstone = 0) =>
                new InboxEntry(occurrence, member, lifecycle, default(CanonicalChoiceId), 3, tombstone, order);

            var allTerminal = new DurableInboxStore(new HostLedger(new[]
            {
                Entry(a, InboxLifecycle.Dismissed), Entry(b, InboxLifecycle.Removed, 3)
            }, 3));
            if (!allTerminal.CanCompact(occurrence, CompactionProof.Empty))
                yield return "L394 all-terminal-entitlements-did-not-compact";

            var mixed = new DurableInboxStore(new HostLedger(new[]
            {
                Entry(a, InboxLifecycle.Removed, 3), Entry(b, InboxLifecycle.Read)
            }, 3));
            if (mixed.CanCompact(occurrence, CompactionProof.Empty))
                yield return "L394 one-terminal-entitlement-was-treated-as-all-terminal";

            var dismissedOnly = new DurableInboxStore(new HostLedger(new[]
            {
                Entry(a, InboxLifecycle.Dismissed), Entry(b, InboxLifecycle.Dismissed)
            }, 3));
            if (!dismissedOnly.CanCompact(occurrence, CompactionProof.Empty))
                yield return "L394 dismissed-entitlements-were-not-terminal";

            foreach (var source in new[]
            {
                DurableReferenceClass.SaveSnapshot,
                DurableReferenceClass.PeerCursor,
                DurableReferenceClass.IncompleteSnapshot,
                DurableReferenceClass.WireReplay
            })
            {
                var proof = CompactionProof.Empty.WithReference(source, occurrence);
                if (allTerminal.CanCompact(occurrence, proof))
                    yield return "L394 compacted-while-" + source.ToString().ToLowerInvariant() + "-can-name-it";
                if (!allTerminal.CanCompact(occurrence, proof.WithoutReference(source, occurrence)))
                    yield return "L394 explicit-release-proof-was-ignored-for-" + source.ToString().ToLowerInvariant();
            }

            // Journal references are derived by the store, never trusted to caller-supplied proof.
            var journalStore = new DurableInboxStore(new HostLedger(new[] { Entry(a, InboxLifecycle.Dismissed) }, 3));
            var journalExpected = journalStore.Ledger;
            if (!journalStore.Commit(journalExpected, journalExpected.WithAuthority(4, journalExpected.Members)) ||
                journalStore.CanCompact(occurrence, CompactionProof.Empty))
                yield return "L394 stores-own-journal-reference-did-not-block-compaction";

            foreach (var failure in AtomicFailureChecks(allTerminal, occurrence)) yield return failure;
            foreach (var failure in CompetingCommitChecks(allTerminal)) yield return failure;

            // POSITIVE CONTROL: the same oracle used by DurableInboxStore must reject a fake TTL decision
            // to delete a terminal tombstone while a durable reference can still name it.
            var terminalEntries = allTerminal.Ledger.AllEntries.Where(e => e.Occurrence.Equals(occurrence)).ToArray();
            bool fakeTtlDecision = TimeSpan.FromDays(3650) > TimeSpan.FromDays(30);
            bool oracleDecision = DurableInboxCompaction.IsAllowed(terminalEntries,
                CompactionProof.Empty.WithReference(DurableReferenceClass.SaveSnapshot, occurrence), false);
            if (!fakeTtlDecision || oracleDecision)
                yield return "L394 control-not-red: shared oracle did not reject fake TTL deletion";
        }

        private static IEnumerable<string> AtomicFailureChecks(DurableInboxStore source, OccurrenceId occurrence)
        {
            var store = new DurableInboxStore(source.Ledger);
            var expected = store.Ledger;
            var next = expected.WithAuthority(expected.CommittedRevision + 1, expected.Members);

            store.ValidateCandidate = _ => false;
            if (store.Commit(expected, next) || !Unchanged(store, expected, 0))
                yield return "L394 validator-false-was-not-atomic";
            store.ValidateCandidate = _ => throw new InvalidOperationException("validation injection");
            if (store.Commit(expected, next) || !Unchanged(store, expected, 0))
                yield return "L394 validator-throw-was-not-atomic";
            store.ValidateCandidate = _ => true;
            store.WriteRecord = _ => false;
            if (store.Commit(expected, next) || !Unchanged(store, expected, 0))
                yield return "L394 writer-false-was-not-atomic";
            store.WriteRecord = _ => throw new InvalidOperationException("write injection");
            if (store.Commit(expected, next) || !Unchanged(store, expected, 0))
                yield return "L394 writer-throw-was-not-atomic";

            store.WriteRecord = _ => true;
            if (!store.Commit(expected, next)) yield return "L394 valid-commit-refused";
            var committed = store.Ledger;
            var record = store.Journal.Single();
            var payload = record.Payload;
            if (record.Revision != committed.CommittedRevision || record.Crc != Crc32.Compute(payload))
                yield return "L394 journal-record-lacks-revision-or-crc";
            payload[0] ^= 0xff;
            if (record.Crc != Crc32.Compute(record.Payload))
                yield return "L394 journal-payload-is-not-defensively-copied";

            if (store.Commit(expected, committed.WithAuthority(committed.CommittedRevision + 1, committed.Members)) ||
                !Unchanged(store, committed, 1))
                yield return "L394 stale-expected-commit-was-not-rejected";
            var equalButWrongExpected = DurableInboxReducer.CloneAndValidate(committed);
            if (store.Commit(equalButWrongExpected, committed.WithAuthority(committed.CommittedRevision + 1, committed.Members)) ||
                !Unchanged(store, committed, 1))
                yield return "L394 wrong-expected-instance-was-not-rejected";
            if (store.Commit(committed, committed.WithAuthority(committed.CommittedRevision + 2, committed.Members)) ||
                !Unchanged(store, committed, 1))
                yield return "L394 revision-gap-was-not-rejected";

            var maxStore = new DurableInboxStore(committed.WithAuthority(ulong.MaxValue, committed.Members));
            if (maxStore.Commit(maxStore.Ledger, maxStore.Ledger) || maxStore.Journal.Count != 0)
                yield return "L394 revision-overflow-was-not-rejected-atomically";
        }

        private static IEnumerable<string> CompetingCommitChecks(DurableInboxStore source)
        {
            var store = new DurableInboxStore(source.Ledger);
            var expected = store.Ledger;
            var nextA = expected.WithAuthority(expected.CommittedRevision + 1, expected.Members);
            var nextB = expected.WithAuthority(expected.CommittedRevision + 1, expected.Members);
            var results = new ConcurrentBag<bool>();
            var errors = new ConcurrentQueue<Exception>();
            using (var gate = new ManualResetEventSlim(false))
            {
                var tasks = new[] { nextA, nextB }.Select(next => Task.Run(() =>
                {
                    try { gate.Wait(); results.Add(store.Commit(expected, next)); }
                    catch (Exception ex) { errors.Enqueue(ex); }
                })).ToArray();
                gate.Set();
                Task.WaitAll(tasks);
            }
            if (!errors.IsEmpty || results.Count(value => value) != 1 || store.Journal.Count != 1 ||
                store.Ledger.CommittedRevision != expected.CommittedRevision + 1)
                yield return "L394 competing-commits-did-not-produce-one-atomic-winner";
        }

        private static bool Unchanged(DurableInboxStore store, HostLedger ledger, int journalCount) =>
            ReferenceEquals(store.Ledger, ledger) && store.Journal.Count == journalCount;
    }
}
