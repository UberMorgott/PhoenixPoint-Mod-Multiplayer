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
        // Monitor locks are re-entrant.  Native game callbacks can therefore loop back into a second
        // store command on the same thread unless the transaction owns an explicit write barrier.
        private bool _nativeTransitionActive;
        private bool _sourceTransitionActive;
        private readonly DurableInboxCanonicalState _snapshotCanonical;
        private readonly HashSet<OccurrenceId> _unservable;
        private readonly Dictionary<OccurrenceId, object> _effectGates = new Dictionary<OccurrenceId, object>();
        private readonly Dictionary<OccurrenceId, object> _transitionGates = new Dictionary<OccurrenceId, object>();
        private readonly HashSet<OccurrenceId> _transitionDeltaEmitted = new HashSet<OccurrenceId>();
        private readonly HashSet<OccurrenceId> _transitionTeardownComplete = new HashSet<OccurrenceId>();
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
        internal Action PreparationMaterializationProbe { get; set; } = () => { };
        internal Action PreparationCapacityProbe { get; set; } = () => { };

        internal bool Commit(HostLedger expected, HostLedger next) => CommitWithCanonical(expected, next, null);

        /// <summary>One host-serialized offer-to-preparation journal record.  The successor and every
        /// fresh entitlement become durable in the same record that supersedes the offer, so native
        /// carrier teardown can never consume the last recoverable route to the mission.</summary>
        internal bool TryStartDeployment(OccurrenceId offer, OccurrenceId successor,
            Func<HostLedger, bool> validate, out ulong tombstoneRevision)
        {
            tombstoneRevision = 0;
            lock (_gate)
            {
                var existing = _ledger.AllEntries.FirstOrDefault(x => x.Predecessor.HasValue &&
                    x.Predecessor.Value.Equals(offer));
                if (!DurableWindowRegistry.IsMissionOfferOccurrence(offer) || validate == null ||
                    !successor.SubjectIds.SequenceEqual(offer.SubjectIds, StringComparer.Ordinal)) return false;
                if (existing != null) { tombstoneRevision = _ledger.AllEntries.Where(x => x.Occurrence.Equals(offer))
                    .Select(x => x.TombstoneRevision).Aggregate(0UL, Math.Max);
                    return existing.Occurrence.Equals(successor); }
                var offers = _ledger.AllEntries.Where(x => x.Occurrence.Equals(offer)).ToArray();
                if (offers.Length == 0 || offers.All(x => x.Lifecycle == InboxLifecycle.Removed) ||
                    _ledger.Contains(successor) || successor.Equals(offer) ||
                    !string.Equals(successor.EventId, "DeploymentPreparing", StringComparison.Ordinal)) return false;
                bool valid; try { valid = validate(_ledger); } catch { return false; }
                if (!valid || _ledger.CommittedRevision == ulong.MaxValue) return false;
                ulong revision = _ledger.CommittedRevision + 1;
                var successorOrder = new HostOrderKey(revision, successor.TriggerId);
                var terminal = offers.Select(x => new InboxEntry(x.Occurrence, x.Membership,
                    InboxLifecycle.Removed, x.Choice, Math.Max(x.LifecycleRevision + 1, revision), revision,
                    x.HostOrderKey, terminalReason: TerminalReason.Superseded, supersededBy: successor,
                    predecessor: x.Predecessor)).ToArray();
                var retained = _ledger.AllEntries.Where(x => !x.Occurrence.Equals(offer));
                var fresh = _ledger.Members.Keys.Select(member => new InboxEntry(successor, member,
                    InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0, successorOrder,
                    predecessor: offer));
                var next = new HostLedger(retained.Concat(terminal).Concat(fresh), revision, _ledger.Members);
                if (!CommitWithCanonical(_ledger, next, null)) return false;
                tombstoneRevision = revision; return true;
            }
        }

        internal bool InstallDeploymentTransition(DeploymentTransitionDelta delta)
        {
            if (delta == null || delta.Reason != TerminalReason.Superseded ||
                !delta.Order.TriggerId.Equals(delta.Preparation.TriggerId, StringComparison.Ordinal) ||
                !delta.Preparation.SubjectIds.SequenceEqual(delta.Offer.SubjectIds, StringComparer.Ordinal)) return false;
            lock (_gate)
            {
                var existing = _ledger.AllEntries.FirstOrDefault(x => x.Predecessor.HasValue &&
                    x.Predecessor.Value.Equals(delta.Offer));
                if (existing != null) return existing.Occurrence.Equals(delta.Preparation) &&
                    existing.HostOrderKey.Equals(delta.Order);
                var offers = _ledger.AllEntries.Where(x => x.Occurrence.Equals(delta.Offer)).ToArray();
                if (offers.Length == 0 || delta.Order.CampaignOrdinal > delta.TombstoneRevision) return false;
                ulong localRevision = checked(Math.Max(_ledger.CommittedRevision, delta.TombstoneRevision) + 1);
                var terminal = offers.Select(x => new InboxEntry(x.Occurrence, x.Membership, InboxLifecycle.Removed,
                    x.Choice, Math.Max(x.LifecycleRevision + 1, delta.TombstoneRevision), delta.TombstoneRevision,
                    x.HostOrderKey, terminalReason: delta.Reason, supersededBy: delta.Preparation,
                    predecessor: x.Predecessor));
                var fresh = _ledger.Members.Keys.Select(m => new InboxEntry(delta.Preparation, m,
                    InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0, delta.Order, predecessor: delta.Offer));
                var next = new HostLedger(_ledger.AllEntries.Where(x => !x.Occurrence.Equals(delta.Offer))
                    .Concat(terminal).Concat(fresh), localRevision, _ledger.Members);
                return CommitWithCanonical(_ledger, next, null);
            }
        }

        internal bool InstallPreparationEdit(PreparationEditDelta delta)
        {
            if (delta == null || !string.Equals(delta.Occurrence.EventId, "DeploymentPreparing", StringComparison.Ordinal)) return false;
            return WithTransitionGate(delta.Occurrence, () =>
            {
                var expected = Ledger;
                var copies = expected.AllEntries.Where(x => x.Occurrence.Equals(delta.Occurrence)).ToArray();
                if (copies.Length == 0 || copies.Any(x => x.Lifecycle == InboxLifecycle.Removed)) return false;
                ulong current = copies[0].PreparationRevision;
                if (copies.Any(x => x.PreparationRevision != current ||
                    x.PreparationAuthorityRevision != copies[0].PreparationAuthorityRevision) ||
                    delta.PreparationRevision != current + 1 ||
                    delta.AuthoritativeLedgerRevision <= copies[0].PreparationAuthorityRevision) return false;
                var next = expected.ReplaceOccurrence(delta.Occurrence,
                    x => x.WithPreparationRevision(delta.PreparationRevision,
                        delta.AuthoritativeLedgerRevision)).WithAuthority(
                        checked(expected.CommittedRevision + 1), expected.Members);
                return Commit(expected, next);
            });
        }

        /// <summary>Commits the campaign consequence of one deployment-source departure.  A partial
        /// departure is a material preparation revision for every entitlement (therefore a Deferred copy
        /// becomes eligible again by the Task 11 rule); loss of the last/unique source is one exact
        /// Invalidated tombstone boundary.  Carrier teardown is deliberately outside this method and may
        /// run only after this record has committed.</summary>
        internal bool CommitSourceRevalidationBatch(IEnumerable<SourceRevalidationPlan> sourcePlans,
            out SourceRevalidationBatchDelta delta, out string refusal)
        {
            lock (_gate)
            {
                if (_sourceTransitionActive)
                { delta = null; refusal = "a source revalidation batch is already active"; return false; }
                _sourceTransitionActive = true;
                try { return CommitSourceRevalidationBatchLocked(sourcePlans, out delta, out refusal); }
                finally { _sourceTransitionActive = false; }
            }
        }

        private bool CommitSourceRevalidationBatchLocked(IEnumerable<SourceRevalidationPlan> sourcePlans,
            out SourceRevalidationBatchDelta delta, out string refusal)
        {
            delta = null; refusal = null;
            lock (_gate)
            {
                var plans = (sourcePlans ?? Enumerable.Empty<SourceRevalidationPlan>()).ToArray();
                if (plans.Length == 0 || plans.Length > 1024 || plans.Select(x => x.Occurrence).Distinct().Count() != plans.Length)
                { refusal = "invalid source revalidation batch"; return false; }
                ulong revision;
                try { revision = checked(_ledger.CommittedRevision + 1); }
                catch (OverflowException) { refusal = "ledger revision exhausted"; return false; }
                var prepared = new List<Tuple<SourceRevalidationPlan, ulong, ulong>>(plans.Length);
                foreach (var plan in plans)
                {
                    if ((!string.Equals(plan.Occurrence.EventId, "DeploymentPreparing", StringComparison.Ordinal) &&
                         !DurableWindowRegistry.IsMissionOfferOccurrence(plan.Occurrence)))
                    { refusal = "source revalidation does not name a mission occurrence"; return false; }
                    var copies = _ledger.AllEntries.Where(x => x.Occurrence.Equals(plan.Occurrence)).ToArray();
                    if (copies.Length == 0 || copies.Any(x => x.Lifecycle == InboxLifecycle.Removed) ||
                        copies.Any(x => x.PreparationRevision != copies[0].PreparationRevision))
                    { refusal = "mission occurrence is absent, terminal, or internally split"; return false; }
                    ulong nextPreparation = copies[0].PreparationRevision;
                    if (!plan.Terminal) try { nextPreparation = checked(nextPreparation + 1); }
                    catch (OverflowException) { refusal = "preparation revision exhausted"; return false; }
                    if (plan.Terminal && copies.Any(x => x.LifecycleRevision == ulong.MaxValue))
                    { refusal = "lifecycle revision exhausted"; return false; }
                    ulong watermark = Math.Max(copies[0].PreparationAuthorityRevision, copies[0].TombstoneRevision);
                    if (copies.Any(x => Math.Max(x.PreparationAuthorityRevision, x.TombstoneRevision) != watermark))
                    { refusal = "source authority watermark is internally split"; return false; }
                    prepared.Add(Tuple.Create(plan, nextPreparation, watermark));
                }
                HostLedger candidate = _ledger;
                foreach (var pair in prepared)
                {
                    var plan = pair.Item1;
                    candidate = plan.Terminal
                        ? candidate.ReplaceOccurrence(plan.Occurrence, x => new InboxEntry(x.Occurrence, x.Membership,
                            InboxLifecycle.Removed, x.Choice, Math.Max(x.LifecycleRevision + 1, revision), revision,
                            x.HostOrderKey, terminalReason: TerminalReason.SourceInvalidated, predecessor: x.Predecessor,
                            preparationRevision: x.PreparationRevision, preparationAuthorityRevision: revision))
                        : candidate.ReplaceOccurrence(plan.Occurrence, x => x.WithPreparationRevision(pair.Item2, revision));
                }
                candidate = candidate.WithAuthority(revision, _ledger.Members);
                SourceRevalidationBatchDelta preparedDelta;
                try
                {
                    var departedSources = prepared.Select(x => x.Item1.DepartedSource).Distinct(StringComparer.Ordinal).ToArray();
                    if (departedSources.Length != 1) throw new InvalidOperationException("batch spans multiple departed sources");
                    var watermarks = prepared.Select(x => x.Item1.DepartureWatermark).Distinct().ToArray();
                    if (watermarks.Length != 1 || watermarks[0] == 0) throw new InvalidOperationException("batch spans invalid departure watermarks");
                    preparedDelta = new SourceRevalidationBatchDelta(revision, departedSources[0], watermarks[0], prepared.Select(x =>
                        new SourceRevalidationItem(x.Item1.Occurrence, x.Item1.Terminal, x.Item2, x.Item3)));
                    DurableInboxCodec.EncodeSourceRevalidation(preparedDelta); // bounded wire preflight before authority
                }
                catch (Exception ex)
                { refusal = "source delta materialization failed: " + ex.Message; return false; }
                if (!CommitWithCanonical(_ledger, candidate, null))
                { refusal = "source revalidation journal commit failed"; return false; }
                delta = preparedDelta;
                return true;
            }
        }

        internal bool InstallSourceRevalidationBatch(SourceRevalidationBatchDelta delta, out string refusal)
        {
            refusal = null;
            if (delta == null) { refusal = "source delta is null"; return false; }
            lock (_gate)
            {
                var readiness = SourceRevalidationReadiness(delta);
                if (readiness != SourceDeltaReadiness.Ready)
                { refusal = "source delta is " + readiness.ToString().ToLowerInvariant(); return false; }
                ulong localRevision;
                try { localRevision = checked(_ledger.CommittedRevision + 1); }
                catch (OverflowException) { refusal = "ledger revision exhausted"; return false; }
                HostLedger candidate = _ledger;
                foreach (var item in delta.Items)
                {
                    var copies = candidate.AllEntries.Where(x => x.Occurrence.Equals(item.Occurrence)).ToArray();
                    if (copies.Length == 0 || copies.Any(x => x.Lifecycle == InboxLifecycle.Removed) ||
                        copies.Any(x => x.PreparationRevision != copies[0].PreparationRevision))
                    { refusal = "source delta names absent, terminal, or split occurrence"; return false; }
                    if (!item.Terminal)
                    {
                        ulong expectedPreparation;
                        try { expectedPreparation = checked(copies[0].PreparationRevision + 1); }
                        catch (OverflowException) { refusal = "preparation revision exhausted"; return false; }
                        if (item.PreparationRevision != expectedPreparation)
                        { refusal = "source delta skipped preparation revision"; return false; }
                    }
                    if (item.Terminal && copies.Any(x => x.LifecycleRevision == ulong.MaxValue))
                    { refusal = "lifecycle revision exhausted"; return false; }
                    candidate = item.Terminal
                        ? candidate.ReplaceOccurrence(item.Occurrence, x => new InboxEntry(x.Occurrence, x.Membership,
                            InboxLifecycle.Removed, x.Choice, x.LifecycleRevision + 1,
                            delta.AuthorityRevision, x.HostOrderKey, terminalReason: TerminalReason.SourceInvalidated,
                            predecessor: x.Predecessor, preparationRevision: x.PreparationRevision,
                            preparationAuthorityRevision: delta.AuthorityRevision))
                        : candidate.ReplaceOccurrence(item.Occurrence,
                            x => x.WithPreparationRevision(item.PreparationRevision, delta.AuthorityRevision));
                }
                candidate = candidate.WithAuthority(localRevision, candidate.Members);
                if (!CommitWithCanonical(_ledger, candidate, null))
                { refusal = "source delta journal commit failed"; return false; }
                return true;
            }
        }

        internal bool IsSourceRevalidationBatchInstalled(SourceRevalidationBatchDelta delta)
            => SourceRevalidationReadiness(delta) == SourceDeltaReadiness.Installed;

        internal SourceDeltaReadiness SourceRevalidationReadiness(SourceRevalidationBatchDelta delta)
        {
            if (delta == null) return SourceDeltaReadiness.Conflict;
            lock (_gate)
            {
                bool allInstalled = true, anyFuture = false;
                foreach (var item in delta.Items)
                {
                    var copies = _ledger.AllEntries.Where(x => x.Occurrence.Equals(item.Occurrence)).ToArray();
                    if (copies.Length == 0) return SourceDeltaReadiness.Conflict;
                    bool installed;
                    if (item.Terminal)
                    {
                        installed = copies.All(x => x.Lifecycle == InboxLifecycle.Removed &&
                            x.TerminalReason == TerminalReason.SourceInvalidated &&
                            x.TombstoneRevision == delta.AuthorityRevision);
                    }
                    else installed = copies.All(x => x.PreparationRevision == item.PreparationRevision &&
                        x.PreparationAuthorityRevision == delta.AuthorityRevision);
                    if (installed) continue;
                    allInstalled = false;
                    if (copies.Any(x => x.Lifecycle == InboxLifecycle.Removed)) return SourceDeltaReadiness.Conflict;
                    ulong watermark = Math.Max(copies[0].PreparationAuthorityRevision, copies[0].TombstoneRevision);
                    if (copies.Any(x => Math.Max(x.PreparationAuthorityRevision, x.TombstoneRevision) != watermark))
                        return SourceDeltaReadiness.Conflict;
                    if (watermark < item.ExpectedAuthorityRevision) anyFuture = true;
                    else if (watermark > item.ExpectedAuthorityRevision) return SourceDeltaReadiness.Conflict;
                }
                return allInstalled ? SourceDeltaReadiness.Installed :
                    anyFuture ? SourceDeltaReadiness.Future : SourceDeltaReadiness.Ready;
            }
        }

        internal bool CommitPreparationNative(OccurrenceId occurrence, ulong expectedPreparationRevision,
            Func<bool> validateAuthority, Func<bool> applyNative, Action rollbackNative,
            IEnumerable<string> touchedStableIdentities, out PreparationEditDelta delta, out string refusal)
        {
            delta = null; refusal = null;
            if (validateAuthority == null || applyNative == null || rollbackNative == null)
                throw new ArgumentNullException("preparation native transaction delegate");
            lock (_gate)
            {
                // Monitor.Enter is recursive.  Refuse before validation/materialization so an engine callback
                // cannot begin a nested native transaction or let its finally clear the outer transaction's guard.
                if (_nativeTransitionActive)
                { refusal = "a native preparation transition is already active on this store"; return false; }
                if (!string.Equals(occurrence.EventId, "DeploymentPreparing", StringComparison.Ordinal))
                { refusal = "edit does not name a deployment preparation"; return false; }
                bool authority; try { authority = validateAuthority(); }
                catch (Exception ex) { refusal = "authority validation threw: " + ex.Message; return false; }
                if (!authority) { refusal = "sender has no open addressed preparation entitlement"; return false; }
                var expected = _ledger;
                var copies = expected.AllEntries.Where(x => x.Occurrence.Equals(occurrence)).ToArray();
                if (copies.Length == 0 || copies.Any(x => x.Lifecycle == InboxLifecycle.Removed) ||
                    copies.Any(x => x.PreparationRevision != expectedPreparationRevision))
                { refusal = "stale or terminal preparation revision"; return false; }
                ulong revision; try { revision = checked(expected.CommittedRevision + 1); }
                catch (OverflowException) { refusal = "ledger revision exhausted"; return false; }
                HostLedger candidate; DurableInboxJournalRecord record;
                PreparationEditDelta preparedDelta;
                try
                {
                    candidate = DurableInboxReducer.CloneAndValidate(expected.ReplaceOccurrence(occurrence,
                        x => x.WithPreparationRevision(checked(expectedPreparationRevision + 1), revision))
                        .WithAuthority(revision, expected.Members));
                    if (!(ValidateCandidate ?? (_ => true))(candidate))
                    { refusal = "preparation revision candidate refused"; return false; }
                    record = new DurableInboxJournalRecord(candidate.CommittedRevision,
                        DurableInboxSaveCodec.EncodeJournalCandidate(candidate, _canonical),
                        candidate.AllEntries.Select(entry => entry.Occurrence));
                    preparedDelta = new PreparationEditDelta(occurrence,
                        checked(expectedPreparationRevision + 1), revision, touchedStableIdentities);
                    (PreparationMaterializationProbe ?? (() => { }))();
                    if (!(WriteRecord ?? (_ => true))(record))
                    { refusal = "preparation revision journal preflight refused"; return false; }
                    (PreparationCapacityProbe ?? (() => { }))();
                    if (_journal.Count == _journal.Capacity) _journal.Capacity = checked(_journal.Count + 1);
                }
                catch (Exception ex) { refusal = "preparation revision preflight threw: " + ex.Message; return false; }

                bool applied;
                _nativeTransitionActive = true;
                try
                {
                    try { applied = applyNative(); }
                    catch (Exception ex)
                    {
                        try { rollbackNative(); }
                        catch (Exception rollback) { throw new InvalidOperationException("native preparation rollback failed", rollback); }
                        refusal = "native preparation edit threw and was rolled back: " + ex.Message; return false;
                    }
                    if (!applied)
                    {
                        try { rollbackNative(); }
                        catch (Exception rollback) { throw new InvalidOperationException("native preparation rollback failed", rollback); }
                        refusal = "native preparation edit refused and was rolled back"; return false;
                    }
                }
                finally { _nativeTransitionActive = false; }
                // Every fallible operation completed before native mutation.  The store lock also excludes
                // save/root readers, so these three in-memory assignments are the guaranteed commit point.
                _journal.Add(record); _ledger = candidate;
                delta = preparedDelta;
                return true;
            }
        }

        internal bool CommitWithCanonical(HostLedger expected, HostLedger next, DurableInboxCanonicalState nextCanonical)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (next == null) throw new ArgumentNullException(nameof(next));
            lock (_gate)
            {
                if (_nativeTransitionActive) return false;
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
                if (_nativeTransitionActive) return false;
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

        internal T WithTransitionGate<T>(OccurrenceId occurrence, Func<T> action)
        {
            object gate; lock (_gate)
            { if (!_transitionGates.TryGetValue(occurrence, out gate)) _transitionGates.Add(occurrence, gate = new object()); }
            lock (gate) return action();
        }
        internal bool MarkTransitionDelta(OccurrenceId occurrence)
        { lock (_gate) return _transitionDeltaEmitted.Add(occurrence); }
        internal bool HasTransitionDelta(OccurrenceId occurrence)
        { lock (_gate) return _transitionDeltaEmitted.Contains(occurrence); }
        internal bool IsTransitionTeardownComplete(OccurrenceId occurrence)
        { lock (_gate) return _transitionTeardownComplete.Contains(occurrence); }
        internal void MarkTransitionTeardownComplete(OccurrenceId occurrence)
        { lock (_gate) _transitionTeardownComplete.Add(occurrence); }

        internal bool RollbackUncheckpointedDecision(SharedChoiceDecision pending)
        {
            lock (_gate)
            {
                if (_nativeTransitionActive) return false;
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
