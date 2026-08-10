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
        private readonly HostLedger _snapshot;
        private DurableInboxCanonicalState _canonical;
        private readonly DurableInboxCanonicalState _snapshotCanonical;
        private readonly HashSet<OccurrenceId> _unservable;
        private readonly Dictionary<OccurrenceId, object> _effectGates = new Dictionary<OccurrenceId, object>();
        private readonly Dictionary<OccurrenceId, KeyValuePair<ulong, TerminalReason>> _terminalBoundaries =
            new Dictionary<OccurrenceId, KeyValuePair<ulong, TerminalReason>>();
        internal DurableCarrierRegistry Carriers { get; }

        internal DurableInboxStore(HostLedger ledger, DurableInboxCanonicalState canonical = null,
            IEnumerable<DurableInboxJournalRecord> journal = null, HostLedger snapshot = null,
            DurableInboxCanonicalState snapshotCanonical = null)
        {
            _ledger = DurableInboxReducer.CloneAndValidate(ledger ?? throw new ArgumentNullException(nameof(ledger)));
            _snapshot = DurableInboxReducer.CloneAndValidate(snapshot ?? ledger);
            _canonical = canonical ?? DurableInboxCanonicalState.Empty;
            _snapshotCanonical = snapshotCanonical ?? _canonical;
            if (journal != null) _journal.AddRange(journal);
            _unservable = new HashSet<OccurrenceId>();
            Carriers = new DurableCarrierRegistry();
        }

        internal HostLedger Ledger { get { lock (_gate) return _ledger; } }
        internal IReadOnlyList<DurableInboxJournalRecord> Journal
        {
            get { lock (_gate) return new ReadOnlyCollection<DurableInboxJournalRecord>(_journal.ToArray()); }
        }

        internal DurableInboxSaveRoot CreateSaveRoot()
        {
            lock (_gate) return DurableInboxSaveCodec.Create(_snapshot, _journal, _snapshotCanonical);
        }

        internal DurableInboxCanonicalState Canonical { get { lock (_gate) return _canonical; } }
        internal bool IsServable(OccurrenceId occurrence) { lock (_gate) return !_unservable.Contains(occurrence); }
        internal void SetUnservable(IEnumerable<OccurrenceId> occurrences)
        { lock (_gate) { _unservable.Clear(); foreach (var occurrence in occurrences) _unservable.Add(occurrence); } }

        internal bool AuthorizeCarrierRemoval(OccurrenceId occurrence, TerminalReason reason,
            ulong tombstoneRevision, out string refusal)
        {
            lock (_gate)
            {
                var entries = _ledger.AllEntries.Where(x => x.Occurrence.Equals(occurrence)).ToArray();
                if (tombstoneRevision == 0 || entries.Length == 0 || entries.Any(x =>
                    x.Lifecycle != InboxLifecycle.Removed || x.TombstoneRevision != tombstoneRevision ||
                    !x.TerminalReason.HasValue || x.TerminalReason.Value != reason))
                { refusal = "terminal lifecycle has not been durably committed"; return false; }
                KeyValuePair<ulong, TerminalReason> boundary;
                if (_terminalBoundaries.TryGetValue(occurrence, out boundary))
                {
                    if (boundary.Key != tombstoneRevision || boundary.Value != reason)
                    { refusal = "terminal revision or reason does not match the occurrence boundary"; return false; }
                }
                else _terminalBoundaries.Add(occurrence,
                    new KeyValuePair<ulong, TerminalReason>(tombstoneRevision, reason));
                refusal = null; return true;
            }
        }

        // Testable persistence seam. Returning false or throwing simulates a journal write failure.
        internal Func<DurableInboxJournalRecord, bool> WriteRecord { get; set; } = _ => true;
        internal Func<HostLedger, bool> ValidateCandidate { get; set; } = _ => true;

        internal bool Commit(HostLedger expected, HostLedger next) => CommitWithCanonical(expected, next, null);

        internal bool CommitWithCanonical(HostLedger expected, HostLedger next, DurableInboxCanonicalState nextCanonical)
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
                    var canonicalCandidate = nextCanonical ?? _canonical;
                    if (canonicalCandidate.Choices.Select(x => x.Occurrence)
                        .Concat(canonicalCandidate.Results.Select(x => x.Occurrence))
                        .Concat(canonicalCandidate.Rewards.Select(x => x.Occurrence))
                        .Concat(canonicalCandidate.Decisions.Select(x => x.Occurrence))
                        .Any(x => !candidate.Contains(x))) return false;
                    if (!(ValidateCandidate ?? (_ => true))(candidate)) return false;
                    record = new DurableInboxJournalRecord(candidate.CommittedRevision,
                        DurableInboxSaveCodec.EncodeJournalCandidate(candidate, canonicalCandidate),
                        candidate.AllEntries.Select(entry => entry.Occurrence));
                    if (!(WriteRecord ?? (_ => true))(record)) return false;
                }
                catch (Exception)
                {
                    return false;
                }

                _journal.Add(record);
                _ledger = candidate;
                _canonical = nextCanonical ?? _canonical;
                return true;
            }
        }

        internal bool InstallAuthoritativeDecision(SharedChoiceDecision decision)
        {
            if (decision == null || decision.Phase != SharedChoicePhase.ChoiceLocked || decision.SharedRevision == 0) return false;
            lock (_gate)
            {
                var expected = _ledger; var canonical = _canonical;
                var existing = canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(decision.Occurrence));
                if (existing != null && existing.Phase == SharedChoicePhase.ChoiceLocked) return existing.Equals(decision);
                if (existing != null && existing.SharedRevision >= decision.SharedRevision) return false;
                if (existing != null && (!existing.Choice.Equals(decision.Choice) ||
                    !existing.Result.Equals(decision.Result) || !existing.Winner.Equals(decision.Winner) ||
                    !existing.EffectToken.Equals(decision.EffectToken))) return false;
                if (!expected.Contains(decision.Occurrence)) return false;
                var entries = expected.AllEntries.Select(x => x.Occurrence.Equals(decision.Occurrence)
                    ? new InboxEntry(x.Occurrence, x.Membership, x.Lifecycle, decision.Choice,
                        x.LifecycleRevision, x.TombstoneRevision, x.HostOrderKey,
                        x.SuspensionReason, x.Checkpoint, x.TerminalReason) : x).ToArray();
                // A peer installs the host's shared decision; it must not mint a host authority revision
                // merely because its local read/open/dismiss lifecycle has moved further meanwhile.
                ulong authorityRevision = Math.Max(expected.CommittedRevision, decision.SharedRevision);
                _ledger = DurableInboxReducer.CloneAndValidate(new HostLedger(entries, authorityRevision, expected.Members));
                _canonical = canonical.WithDecision(decision);
                return true;
            }
        }

        internal bool ReplacePendingDecisionRewards(OccurrenceId occurrence,
            IEnumerable<CanonicalRewardItemId> rewards, byte[] rewardPayload = null)
        {
            lock (_gate)
            {
                var pending = _canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(occurrence));
                if (pending == null || pending.Phase == SharedChoicePhase.ChoiceLocked ||
                    _ledger.CommittedRevision == ulong.MaxValue) return false;
                var nextDecision = pending.WithRewards(rewards);
                if (rewardPayload != null) nextDecision = nextDecision.WithRewardPayload(rewardPayload);
                var nextCanonical = _canonical.WithDecision(nextDecision);
                var next = new HostLedger(_ledger.AllEntries, _ledger.CommittedRevision + 1, _ledger.Members);
                return CommitWithCanonical(_ledger, next, nextCanonical);
            }
        }

        internal T WithEffectGate<T>(OccurrenceId occurrence, Func<T> action)
        {
            object gate;
            lock (_gate)
            { if (!_effectGates.TryGetValue(occurrence, out gate)) _effectGates.Add(occurrence, gate = new object()); }
            lock (gate) return action();
        }

        internal bool RollbackUncheckpointedDecision(SharedChoiceDecision pending)
        {
            lock (_gate)
            {
                var live = _canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(pending.Occurrence));
                if (live == null || live.Phase != SharedChoicePhase.EffectPending || !live.Equals(pending) ||
                    _ledger.CommittedRevision == 0 || _journal.Count == 0 ||
                    _journal[_journal.Count - 1].Revision != _ledger.CommittedRevision) return false;
                var entries = _ledger.AllEntries.Select(x => x.Occurrence.Equals(pending.Occurrence)
                    ? new InboxEntry(x.Occurrence, x.Membership, x.Lifecycle, default(CanonicalChoiceId),
                        x.LifecycleRevision, x.TombstoneRevision, x.HostOrderKey, x.SuspensionReason,
                        x.Checkpoint, x.TerminalReason) : x).ToArray();
                _journal.RemoveAt(_journal.Count - 1);
                _ledger = DurableInboxReducer.CloneAndValidate(new HostLedger(entries,
                    _ledger.CommittedRevision - 1, _ledger.Members));
                _canonical = _canonical.WithoutDecision(pending.Occurrence);
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
