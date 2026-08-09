using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Multiplayer.Util;

namespace Multiplayer.Network.Sync
{
    internal enum DurableReferenceClass
    {
        SaveSnapshot,
        JournalRecord,
        PeerCursor,
        IncompleteSnapshot,
        WireReplay
    }

    internal sealed class CompactionProof
    {
        private readonly IReadOnlyDictionary<DurableReferenceClass, IReadOnlyCollection<OccurrenceId>> _references;

        private CompactionProof(IDictionary<DurableReferenceClass, IReadOnlyCollection<OccurrenceId>> references)
        {
            var copy = new Dictionary<DurableReferenceClass, IReadOnlyCollection<OccurrenceId>>();
            foreach (DurableReferenceClass source in Enum.GetValues(typeof(DurableReferenceClass)))
            {
                IReadOnlyCollection<OccurrenceId> values;
                if (!references.TryGetValue(source, out values)) values = Array.Empty<OccurrenceId>();
                copy[source] = new ReadOnlyCollection<OccurrenceId>(values.Distinct().OrderBy(value => value).ToArray());
            }
            _references = new ReadOnlyDictionary<DurableReferenceClass, IReadOnlyCollection<OccurrenceId>>(copy);
        }

        internal static CompactionProof Empty { get; } =
            new CompactionProof(new Dictionary<DurableReferenceClass, IReadOnlyCollection<OccurrenceId>>());

        internal CompactionProof WithReference(DurableReferenceClass source, OccurrenceId occurrence) =>
            Rewrite(source, values => values.Concat(new[] { occurrence }));

        internal CompactionProof WithoutReference(DurableReferenceClass source, OccurrenceId occurrence) =>
            Rewrite(source, values => values.Where(value => !value.Equals(occurrence)));

        internal bool CanName(OccurrenceId occurrence) =>
            _references.Values.Any(values => values.Contains(occurrence));

        private CompactionProof Rewrite(DurableReferenceClass source,
            Func<IEnumerable<OccurrenceId>, IEnumerable<OccurrenceId>> rewrite)
        {
            if (!Enum.IsDefined(typeof(DurableReferenceClass), source))
                throw new ArgumentOutOfRangeException(nameof(source));
            var copy = _references.ToDictionary(pair => pair.Key, pair => pair.Value);
            copy[source] = new ReadOnlyCollection<OccurrenceId>(rewrite(copy[source]).Distinct().ToArray());
            return new CompactionProof(copy);
        }
    }

    internal sealed class DurableInboxJournalRecord
    {
        private readonly byte[] _payload;
        private readonly IReadOnlyCollection<OccurrenceId> _occurrences;

        internal DurableInboxJournalRecord(ulong revision, byte[] payload, IEnumerable<OccurrenceId> occurrences)
        {
            Revision = revision;
            _payload = (byte[])(payload ?? throw new ArgumentNullException(nameof(payload))).Clone();
            _occurrences = new ReadOnlyCollection<OccurrenceId>((occurrences ??
                throw new ArgumentNullException(nameof(occurrences))).Distinct().OrderBy(value => value).ToArray());
            Crc = Crc32.Compute(_payload);
        }

        internal ulong Revision { get; }
        internal byte[] Payload => (byte[])_payload.Clone();
        internal uint Crc { get; }
        internal bool Names(OccurrenceId occurrence) => _occurrences.Contains(occurrence);
    }

    internal sealed class DurableInboxStore
    {
        private readonly object _gate = new object();
        private readonly List<DurableInboxJournalRecord> _journal = new List<DurableInboxJournalRecord>();
        private HostLedger _ledger;

        internal DurableInboxStore(HostLedger ledger)
        {
            _ledger = DurableInboxReducer.CloneAndValidate(ledger ?? throw new ArgumentNullException(nameof(ledger)));
        }

        internal HostLedger Ledger { get { lock (_gate) return _ledger; } }
        internal IReadOnlyList<DurableInboxJournalRecord> Journal
        {
            get { lock (_gate) return new ReadOnlyCollection<DurableInboxJournalRecord>(_journal.ToArray()); }
        }

        // Testable persistence seam. Returning false or throwing simulates a journal write failure.
        internal Func<DurableInboxJournalRecord, bool> WriteRecord { get; set; } = _ => true;
        internal Func<HostLedger, bool> ValidateCandidate { get; set; } = _ => true;

        internal bool Commit(HostLedger expected, HostLedger next)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (next == null) throw new ArgumentNullException(nameof(next));
            lock (_gate)
            {
                if (!ReferenceEquals(expected, _ledger) || expected.CommittedRevision == ulong.MaxValue ||
                    next.CommittedRevision != expected.CommittedRevision + 1)
                    return false;

                HostLedger candidate;
                DurableInboxJournalRecord record;
                try
                {
                    candidate = DurableInboxReducer.CloneAndValidate(next);
                    if (!(ValidateCandidate ?? (_ => true))(candidate)) return false;
                    record = new DurableInboxJournalRecord(candidate.CommittedRevision, candidate.EncodeCanonical(),
                        candidate.AllEntries.Select(entry => entry.Occurrence));
                    if (!(WriteRecord ?? (_ => true))(record)) return false;
                }
                catch (Exception)
                {
                    return false;
                }

                _journal.Add(record);
                _ledger = candidate;
                return true;
            }
        }

        internal bool CanCompact(OccurrenceId occurrence, CompactionProof proof)
        {
            if (proof == null) throw new ArgumentNullException(nameof(proof));
            lock (_gate)
            {
                var entitlements = _ledger.AllEntries.Where(entry => entry.Occurrence.Equals(occurrence)).ToArray();
                return DurableInboxCompaction.IsAllowed(entitlements, proof,
                    _journal.Any(record => record.Names(occurrence)));
            }
        }
    }

    internal static class DurableInboxCompaction
    {
        internal static bool IsAllowed(IReadOnlyCollection<InboxEntry> entitlements, CompactionProof proof,
            bool journalCanNameOccurrence)
        {
            if (entitlements == null) throw new ArgumentNullException(nameof(entitlements));
            if (proof == null) throw new ArgumentNullException(nameof(proof));
            return entitlements.Count != 0 && !journalCanNameOccurrence && !proof.CanName(entitlements.First().Occurrence) &&
                   entitlements.All(entry => entry.Lifecycle == InboxLifecycle.Dismissed ||
                                             entry.Lifecycle == InboxLifecycle.Removed);
        }
    }
}
