using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Multiplayer.Network.Sync
{
    internal static class InboxIdentity
    {
        internal static string Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " is required", name);
            return value;
        }

        internal static int Hash(params object[] values)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var value in values)
                {
                    var text = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
                    foreach (var c in text) { hash ^= c; hash *= 16777619; }
                    hash ^= 0xff; hash *= 16777619;
                }
                return (int)hash;
            }
        }
    }

    internal readonly struct MembershipId : IEquatable<MembershipId>, IComparable<MembershipId>
    {
        internal readonly string PlayerGuid;
        internal readonly ulong Epoch;

        internal MembershipId(string playerGuid, ulong epoch)
        {
            PlayerGuid = InboxIdentity.Required(playerGuid, nameof(playerGuid));
            Epoch = epoch;
        }

        public bool Equals(MembershipId other) => Epoch == other.Epoch &&
            string.Equals(PlayerGuid, other.PlayerGuid, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is MembershipId other && Equals(other);
        public override int GetHashCode() => InboxIdentity.Hash(PlayerGuid, Epoch);
        public int CompareTo(MembershipId other)
        {
            int byPlayer = string.Compare(PlayerGuid, other.PlayerGuid, StringComparison.Ordinal);
            return byPlayer != 0 ? byPlayer : Epoch.CompareTo(other.Epoch);
        }
    }

    internal readonly struct OccurrenceId : IEquatable<OccurrenceId>, IComparable<OccurrenceId>
    {
        internal readonly string EventId;
        internal readonly string TriggerId;
        private readonly IReadOnlyList<string> _subjectIds;
        internal IReadOnlyList<string> SubjectIds => _subjectIds ?? Array.Empty<string>();

        internal OccurrenceId(string eventId, string triggerId, IEnumerable<string> subjectIds)
        {
            EventId = InboxIdentity.Required(eventId, nameof(eventId));
            TriggerId = InboxIdentity.Required(triggerId, nameof(triggerId));
            if (subjectIds == null) throw new ArgumentNullException(nameof(subjectIds));
            var copy = subjectIds.Select(s => InboxIdentity.Required(s, nameof(subjectIds))).ToArray();
            if (copy.Length == 0) throw new ArgumentException("at least one subject is required", nameof(subjectIds));
            Array.Sort(copy, StringComparer.Ordinal);
            if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
                throw new ArgumentException("duplicate subjects are not canonical", nameof(subjectIds));
            _subjectIds = new ReadOnlyCollection<string>(copy);
        }

        public bool Equals(OccurrenceId other) =>
            string.Equals(EventId, other.EventId, StringComparison.Ordinal) &&
            string.Equals(TriggerId, other.TriggerId, StringComparison.Ordinal) &&
            SubjectIds.SequenceEqual(other.SubjectIds, StringComparer.Ordinal);
        public override bool Equals(object obj) => obj is OccurrenceId other && Equals(other);
        public override int GetHashCode()
        {
            int hash = InboxIdentity.Hash(EventId, TriggerId);
            foreach (var subject in SubjectIds) hash = InboxIdentity.Hash(hash, subject);
            return hash;
        }
        public int CompareTo(OccurrenceId other)
        {
            int value = string.Compare(EventId, other.EventId, StringComparison.Ordinal);
            if (value != 0) return value;
            value = string.Compare(TriggerId, other.TriggerId, StringComparison.Ordinal);
            if (value != 0) return value;
            int count = Math.Min(SubjectIds.Count, other.SubjectIds.Count);
            for (int i = 0; i < count; i++)
            {
                value = string.Compare(SubjectIds[i], other.SubjectIds[i], StringComparison.Ordinal);
                if (value != 0) return value;
            }
            return SubjectIds.Count.CompareTo(other.SubjectIds.Count);
        }
    }

    internal readonly struct HostOrderKey : IEquatable<HostOrderKey>, IComparable<HostOrderKey>
    {
        internal readonly ulong CampaignOrdinal;
        internal readonly string TriggerId;
        internal HostOrderKey(ulong campaignOrdinal, string triggerId)
        {
            CampaignOrdinal = campaignOrdinal;
            TriggerId = InboxIdentity.Required(triggerId, nameof(triggerId));
        }
        public bool Equals(HostOrderKey other) => CampaignOrdinal == other.CampaignOrdinal &&
            string.Equals(TriggerId, other.TriggerId, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is HostOrderKey other && Equals(other);
        public override int GetHashCode() => InboxIdentity.Hash(CampaignOrdinal, TriggerId);
        public int CompareTo(HostOrderKey other)
        {
            int value = CampaignOrdinal.CompareTo(other.CampaignOrdinal);
            return value != 0 ? value : string.Compare(TriggerId, other.TriggerId, StringComparison.Ordinal);
        }
    }

    internal readonly struct CanonicalChoiceId : IEquatable<CanonicalChoiceId>, IComparable<CanonicalChoiceId>
    {
        internal readonly OccurrenceId Occurrence;
        internal readonly string Value;
        internal CanonicalChoiceId(OccurrenceId occurrence, string value)
        {
            Occurrence = occurrence;
            Value = InboxIdentity.Required(value, nameof(value));
        }
        public bool Equals(CanonicalChoiceId other) => Occurrence.Equals(other.Occurrence) &&
            string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is CanonicalChoiceId other && Equals(other);
        public override int GetHashCode() => InboxIdentity.Hash(Occurrence.GetHashCode(), Value);
        public int CompareTo(CanonicalChoiceId other)
        {
            int value = Occurrence.CompareTo(other.Occurrence);
            return value != 0 ? value : string.Compare(Value, other.Value, StringComparison.Ordinal);
        }
    }

    internal readonly struct CanonicalResultId : IEquatable<CanonicalResultId>, IComparable<CanonicalResultId>
    {
        internal readonly OccurrenceId Occurrence;
        internal readonly string Value;
        internal CanonicalResultId(OccurrenceId occurrence, string value)
        {
            Occurrence = occurrence;
            Value = InboxIdentity.Required(value, nameof(value));
        }
        public bool Equals(CanonicalResultId other) => Occurrence.Equals(other.Occurrence) &&
            string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is CanonicalResultId other && Equals(other);
        public override int GetHashCode() => InboxIdentity.Hash(Occurrence.GetHashCode(), Value);
        public int CompareTo(CanonicalResultId other)
        {
            int value = Occurrence.CompareTo(other.Occurrence);
            return value != 0 ? value : string.Compare(Value, other.Value, StringComparison.Ordinal);
        }
    }

    internal readonly struct CanonicalRewardItemId : IEquatable<CanonicalRewardItemId>, IComparable<CanonicalRewardItemId>
    {
        internal readonly OccurrenceId Occurrence;
        internal readonly string SubjectId;
        internal readonly string Value;
        internal CanonicalRewardItemId(OccurrenceId occurrence, string subjectId, string value)
        {
            Occurrence = occurrence;
            SubjectId = InboxIdentity.Required(subjectId, nameof(subjectId));
            Value = InboxIdentity.Required(value, nameof(value));
            if (!occurrence.SubjectIds.Contains(SubjectId, StringComparer.Ordinal))
                throw new ArgumentException("reward subject is outside its occurrence namespace", nameof(subjectId));
        }
        public bool Equals(CanonicalRewardItemId other) => Occurrence.Equals(other.Occurrence) &&
            string.Equals(SubjectId, other.SubjectId, StringComparison.Ordinal) &&
            string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is CanonicalRewardItemId other && Equals(other);
        public override int GetHashCode() => InboxIdentity.Hash(Occurrence.GetHashCode(), SubjectId, Value);
        public int CompareTo(CanonicalRewardItemId other)
        {
            int value = Occurrence.CompareTo(other.Occurrence);
            if (value != 0) return value;
            value = string.Compare(SubjectId, other.SubjectId, StringComparison.Ordinal);
            return value != 0 ? value : string.Compare(Value, other.Value, StringComparison.Ordinal);
        }
    }

    internal enum InboxMessageKind : byte { TransportAck = 0x46, Lifecycle = 0x40 }
    // Append-only: values 0..4 already exist in campaign saves and on the wire.
    internal enum InboxLifecycle : byte { Queued, Open, Read, Dismissed, Removed, Suspended }
    internal enum InboxSuspensionReason : byte { None, PriorityPreemption, LevelTeardown }

    internal sealed class InboxWindowCheckpoint : IEquatable<InboxWindowCheckpoint>
    {
        internal string ContentPhase { get; }
        internal string Selection { get; }
        internal string ReadCheckpoint { get; }
        internal string EventId { get; }
        internal string Title { get; }
        internal string Narrative { get; }
        internal int NativePriority { get; }
        internal string NativeDataIdentity { get; }

        internal InboxWindowCheckpoint(string contentPhase, string selection, string readCheckpoint,
            string eventId = "", string title = "", string narrative = "", int nativePriority = 0,
            string nativeDataIdentity = "")
        {
            ContentPhase = InboxIdentity.Required(contentPhase, nameof(contentPhase));
            Selection = selection ?? "";
            ReadCheckpoint = readCheckpoint ?? "";
            EventId = eventId ?? ""; Title = title ?? ""; Narrative = narrative ?? "";
            NativePriority = nativePriority; NativeDataIdentity = nativeDataIdentity ?? "";
        }

        public bool Equals(InboxWindowCheckpoint other) => other != null &&
            string.Equals(ContentPhase, other.ContentPhase, StringComparison.Ordinal) &&
            string.Equals(Selection, other.Selection, StringComparison.Ordinal) &&
            string.Equals(ReadCheckpoint, other.ReadCheckpoint, StringComparison.Ordinal) &&
            string.Equals(EventId, other.EventId, StringComparison.Ordinal) &&
            string.Equals(Title, other.Title, StringComparison.Ordinal) &&
            string.Equals(Narrative, other.Narrative, StringComparison.Ordinal) && NativePriority == other.NativePriority &&
            string.Equals(NativeDataIdentity, other.NativeDataIdentity, StringComparison.Ordinal);
        public override bool Equals(object obj) => Equals(obj as InboxWindowCheckpoint);
        public override int GetHashCode() => InboxIdentity.Hash(ContentPhase, Selection, ReadCheckpoint,
            EventId, Title, Narrative, NativePriority, NativeDataIdentity);
    }

    internal sealed class InboxMessage
    {
        internal InboxMessageKind Kind { get; }
        internal OccurrenceId Occurrence { get; }
        internal CanonicalResultId ResultId { get; }
        internal IReadOnlyList<CanonicalRewardItemId> RewardIds { get; }
        internal MembershipId Membership { get; }
        internal HostOrderKey Order { get; }
        internal ulong LifecycleRevision { get; }
        internal ulong TombstoneRevision { get; }
        internal InboxLifecycle Lifecycle { get; }
        internal CanonicalChoiceId ChoiceId { get; }

        internal InboxMessage(InboxMessageKind kind, OccurrenceId occurrence, CanonicalResultId resultId,
            IEnumerable<CanonicalRewardItemId> rewardIds, MembershipId membership, HostOrderKey order,
            ulong lifecycleRevision, ulong tombstoneRevision, InboxLifecycle lifecycle, CanonicalChoiceId choiceId)
        {
            if (!Enum.IsDefined(typeof(InboxMessageKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!Enum.IsDefined(typeof(InboxLifecycle), lifecycle)) throw new ArgumentOutOfRangeException(nameof(lifecycle));
            if (!resultId.Occurrence.Equals(occurrence)) throw new ArgumentException("foreign result namespace", nameof(resultId));
            if (!string.Equals(order.TriggerId, occurrence.TriggerId, StringComparison.Ordinal))
                throw new ArgumentException("foreign order namespace", nameof(order));
            if (choiceId.Value != null && !choiceId.Occurrence.Equals(occurrence))
                throw new ArgumentException("foreign choice namespace", nameof(choiceId));
            var rewards = (rewardIds ?? throw new ArgumentNullException(nameof(rewardIds))).ToArray();
            if (rewards.Any(r => !r.Occurrence.Equals(occurrence)))
                throw new ArgumentException("foreign reward namespace", nameof(rewardIds));
            if (rewards.Distinct().Count() != rewards.Length)
                throw new ArgumentException("duplicate rewards are not canonical", nameof(rewardIds));
            Array.Sort(rewards);
            Kind = kind;
            Occurrence = occurrence;
            ResultId = resultId;
            RewardIds = new ReadOnlyCollection<CanonicalRewardItemId>(rewards);
            Membership = membership;
            Order = order;
            LifecycleRevision = lifecycleRevision;
            TombstoneRevision = tombstoneRevision;
            Lifecycle = lifecycle;
            ChoiceId = choiceId;
        }
    }

    internal sealed class InboxEntry
    {
        internal OccurrenceId Occurrence { get; }
        internal MembershipId Membership { get; }
        internal InboxLifecycle Lifecycle { get; }
        internal CanonicalChoiceId Choice { get; }
        internal ulong LifecycleRevision { get; }
        internal ulong TombstoneRevision { get; }
        internal HostOrderKey HostOrderKey { get; }
        internal InboxSuspensionReason SuspensionReason { get; }
        internal InboxWindowCheckpoint Checkpoint { get; }
        internal InboxEntry(OccurrenceId occurrence, MembershipId membership, InboxLifecycle lifecycle,
            CanonicalChoiceId choice, ulong lifecycleRevision, ulong tombstoneRevision, HostOrderKey hostOrderKey,
            InboxSuspensionReason suspensionReason = InboxSuspensionReason.None,
            InboxWindowCheckpoint checkpoint = null)
        {
            if (!string.Equals(hostOrderKey.TriggerId, occurrence.TriggerId, StringComparison.Ordinal))
                throw new ArgumentException("foreign order namespace", nameof(hostOrderKey));
            Occurrence = occurrence; Membership = membership; Lifecycle = lifecycle; Choice = choice;
            LifecycleRevision = lifecycleRevision; TombstoneRevision = tombstoneRevision; HostOrderKey = hostOrderKey;
            if (!Enum.IsDefined(typeof(InboxSuspensionReason), suspensionReason))
                throw new ArgumentOutOfRangeException(nameof(suspensionReason));
            if ((lifecycle == InboxLifecycle.Suspended) != (suspensionReason != InboxSuspensionReason.None))
                throw new ArgumentException("only suspended entries name a suspension reason", nameof(suspensionReason));
            if (lifecycle == InboxLifecycle.Suspended && checkpoint == null)
                throw new ArgumentNullException(nameof(checkpoint));
            SuspensionReason = suspensionReason;
            Checkpoint = checkpoint;
        }
        internal InboxEntry WithLifecycle(InboxLifecycle lifecycle, ulong revision) =>
            lifecycle == InboxLifecycle.Suspended
                ? new InboxEntry(Occurrence, Membership, lifecycle, Choice, revision, TombstoneRevision,
                    HostOrderKey, SuspensionReason, Checkpoint)
                : new InboxEntry(Occurrence, Membership, lifecycle, Choice, revision, TombstoneRevision, HostOrderKey);
        internal InboxEntry Suspend(InboxSuspensionReason reason, InboxWindowCheckpoint checkpoint, ulong revision) =>
            new InboxEntry(Occurrence, Membership, InboxLifecycle.Suspended, Choice, revision, TombstoneRevision,
                HostOrderKey, reason, checkpoint);
    }

    internal sealed class HostLedger
    {
        private readonly IReadOnlyList<InboxEntry> _entries;
        private readonly IReadOnlyDictionary<MembershipId, MemberPresence> _members;
        internal HostLedger(IEnumerable<InboxEntry> entries, ulong committedRevision = 0,
            IEnumerable<KeyValuePair<MembershipId, MemberPresence>> members = null)
        {
            var copy = (entries ?? throw new ArgumentNullException(nameof(entries))).ToArray();
            if (copy.GroupBy(e => Tuple.Create(e.Occurrence, e.Membership)).Any(g => g.Count() != 1))
                throw new ArgumentException("duplicate ledger entry", nameof(entries));
            if (copy.GroupBy(e => e.Occurrence).Any(g => g.Select(e => e.HostOrderKey).Distinct().Count() != 1))
                throw new ArgumentException("occurrence has inconsistent host order", nameof(entries));
            if (copy.Any(e => e.HostOrderKey.CampaignOrdinal > committedRevision))
                throw new ArgumentException("host order exceeds committed revision", nameof(entries));
            var memberCopy = members == null
                ? copy.Select(e => e.Membership).Distinct().ToDictionary(m => m, m => MemberPresence.Disconnected)
                : members.ToDictionary(pair => pair.Key, pair => pair.Value);
            _entries = new ReadOnlyCollection<InboxEntry>(copy);
            _members = new ReadOnlyDictionary<MembershipId, MemberPresence>(memberCopy);
            CommittedRevision = committedRevision;
        }
        internal int EntryCount => _entries.Count;
        internal ulong CommittedRevision { get; }
        internal IReadOnlyList<InboxEntry> AllEntries => _entries;
        internal IReadOnlyDictionary<MembershipId, MemberPresence> Members => _members;
        internal IReadOnlyList<InboxEntry> EntriesFor(MembershipId member) => new ReadOnlyCollection<InboxEntry>(
            _entries.Where(e => e.Membership.Equals(member)).OrderBy(e => e.HostOrderKey).ThenBy(e => e.Occurrence).ToArray());
        internal bool Contains(OccurrenceId occurrence) => _entries.Any(e => e.Occurrence.Equals(occurrence));
        internal InboxEntry Get(OccurrenceId occurrence, MembershipId member) =>
            _entries.Single(e => e.Occurrence.Equals(occurrence) && e.Membership.Equals(member));
        internal HostLedger Add(IEnumerable<InboxEntry> additions) => new HostLedger(_entries.Concat(additions), CommittedRevision, _members);
        internal HostLedger Replace(InboxEntry replacement) => new HostLedger(_entries.Select(e =>
            e.Occurrence.Equals(replacement.Occurrence) && e.Membership.Equals(replacement.Membership) ? replacement : e), CommittedRevision, _members);
        internal HostLedger ReplaceOccurrence(OccurrenceId occurrence, Func<InboxEntry, InboxEntry> replace) =>
            new HostLedger(_entries.Select(e => e.Occurrence.Equals(occurrence) ? replace(e) : e), CommittedRevision, _members);
        internal HostLedger WithAuthority(ulong revision, IEnumerable<KeyValuePair<MembershipId, MemberPresence>> members) =>
            new HostLedger(_entries, revision, members);
        internal byte[] EncodeCanonical() => DurableInboxCodec.EncodeLedger(_entries, CommittedRevision, _members);
    }

internal enum MemberPresence { Active, Disconnected, Loading, Tactical, NonGeoscape }

    internal sealed class HostInboxSequencer
    {
        // ponytail: one authority lock; split only if measured host inbox contention warrants it.
        private readonly object _authority = new object();
        private readonly Dictionary<MembershipId, MemberPresence> _members = new Dictionary<MembershipId, MemberPresence>();
        private readonly HashSet<OccurrenceId> _occurrences = new HashSet<OccurrenceId>();
        internal HostLedger Ledger { get; private set; }
        internal ulong CommittedRevision { get; private set; }

        internal HostInboxSequencer(HostLedger ledger)
        {
            Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            foreach (var pair in ledger.Members) _members.Add(pair.Key, pair.Value);
            foreach (var entry in ledger.AllEntries) _occurrences.Add(entry.Occurrence);
            CommittedRevision = ledger.CommittedRevision;
        }

        internal bool Enroll(MembershipId member, MemberPresence presence)
        {
            lock (_authority)
            {
                if (!Enum.IsDefined(typeof(MemberPresence), presence) || _members.ContainsKey(member)) return false;
                var committedRevision = checked(CommittedRevision + 1);
                var members = new Dictionary<MembershipId, MemberPresence>(_members) { [member] = presence };
                var ledger = Ledger.WithAuthority(committedRevision, members);
                _members.Add(member, presence);
                Ledger = ledger;
                CommittedRevision = committedRevision;
                return true;
            }
        }

        internal bool SetPresence(MembershipId member, MemberPresence presence)
        {
            lock (_authority)
            {
                if (!_members.ContainsKey(member) || !Enum.IsDefined(typeof(MemberPresence), presence)) return false;
                var members = new Dictionary<MembershipId, MemberPresence>(_members) { [member] = presence };
                var ledger = Ledger.WithAuthority(CommittedRevision, members);
                _members[member] = presence;
                Ledger = ledger;
                return true;
            }
        }

        internal bool CreateOccurrence(OccurrenceId occurrence)
        {
            lock (_authority)
            {
                if (_occurrences.Contains(occurrence)) return false;
                var committedRevision = checked(CommittedRevision + 1);
                var order = new HostOrderKey(committedRevision, occurrence.TriggerId);
                var additions = _members.Keys.Select(member => new InboxEntry(occurrence, member,
                    InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0, order)).ToArray();
                var ledger = new HostLedger(Ledger.AllEntries.Concat(additions), committedRevision, _members);
                _occurrences.Add(occurrence);
                Ledger = ledger;
                CommittedRevision = committedRevision;
                return true;
            }
        }

        internal bool ApplyLifecycle(MembershipId member, OccurrenceId occurrence, InboxLifecycle lifecycle, ulong revision)
        {
            lock (_authority)
            {
                if (!_members.ContainsKey(member)) return false;
                var result = DurableInboxReducer.Apply(Ledger,
                    InboxCommand.SetLifecycle(occurrence, member, lifecycle, revision));
                if (!result.Changed) return false;
                var committedRevision = checked(CommittedRevision + 1);
                Ledger = result.Ledger.WithAuthority(committedRevision, _members);
                CommittedRevision = committedRevision;
                return true;
            }
        }

        internal bool Tombstone(OccurrenceId occurrence, ulong revision)
        {
            lock (_authority)
            {
                if (!_occurrences.Contains(occurrence)) return false;
                var changed = false;
                var ledger = Ledger.ReplaceOccurrence(occurrence, entry =>
                {
                    if (revision <= entry.TombstoneRevision) return entry;
                    changed = true;
                    return new InboxEntry(entry.Occurrence, entry.Membership, InboxLifecycle.Removed,
                        entry.Choice, Math.Max(entry.LifecycleRevision, revision), revision, entry.HostOrderKey);
                });
                if (!changed) return false;
                var committedRevision = checked(CommittedRevision + 1);
                Ledger = ledger.WithAuthority(committedRevision, _members);
                CommittedRevision = committedRevision;
                return true;
            }
        }

        internal bool EndMembership(MembershipId member)
        {
            lock (_authority)
            {
                if (!_members.ContainsKey(member)) return false;
                var replacement = Ledger.EntriesFor(member)
                    .Where(entry => entry.Lifecycle != InboxLifecycle.Dismissed && entry.Lifecycle != InboxLifecycle.Removed)
                    .Select(entry =>
                    {
                        var revision = checked(entry.LifecycleRevision + 1);
                        return new InboxEntry(entry.Occurrence, entry.Membership, InboxLifecycle.Removed,
                            entry.Choice, revision, Math.Max(entry.TombstoneRevision, revision), entry.HostOrderKey);
                    })
                    .ToArray();
                var committedRevision = checked(CommittedRevision + 1);
                var members = new Dictionary<MembershipId, MemberPresence>(_members);
                members.Remove(member);
                var ledger = replacement.Aggregate(Ledger, (current, entry) => current.Replace(entry))
                    .WithAuthority(committedRevision, members);
                Ledger = ledger;
                _members.Remove(member);
                CommittedRevision = committedRevision;
                return true;
            }
        }

        internal IReadOnlyList<InboxEntry> Reconnect(MembershipId member)
        {
            lock (_authority)
                return _members.ContainsKey(member)
                    ? Ledger.EntriesFor(member)
                    : (IReadOnlyList<InboxEntry>)Array.Empty<InboxEntry>();
        }
    }

    internal enum InboxCommandKind { TransportAck, SetLifecycle }
    internal sealed class InboxCommand
    {
        internal InboxCommandKind Kind { get; }
        internal OccurrenceId Occurrence { get; }
        internal MembershipId Membership { get; }
        internal InboxLifecycle Lifecycle { get; }
        internal ulong Revision { get; }
        internal InboxSuspensionReason SuspensionReason { get; }
        internal InboxWindowCheckpoint Checkpoint { get; }
        private InboxCommand(InboxCommandKind kind, OccurrenceId occurrence, MembershipId membership,
            InboxLifecycle lifecycle, ulong revision, InboxSuspensionReason reason = InboxSuspensionReason.None,
            InboxWindowCheckpoint checkpoint = null)
        { Kind = kind; Occurrence = occurrence; Membership = membership; Lifecycle = lifecycle; Revision = revision;
          SuspensionReason = reason; Checkpoint = checkpoint; }
        internal static InboxCommand FromMessage(InboxMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (message.Kind != InboxMessageKind.TransportAck) throw new ArgumentException("message is not an ACK", nameof(message));
            return new InboxCommand(InboxCommandKind.TransportAck, message.Occurrence, message.Membership,
                message.Lifecycle, message.LifecycleRevision);
        }
        internal static InboxCommand SetLifecycle(OccurrenceId occurrence, MembershipId member,
            InboxLifecycle lifecycle, ulong revision) =>
            new InboxCommand(InboxCommandKind.SetLifecycle, occurrence, member, lifecycle, revision);
        internal static InboxCommand Suspend(OccurrenceId occurrence, MembershipId member,
            InboxSuspensionReason reason, InboxWindowCheckpoint checkpoint, ulong revision) =>
            new InboxCommand(InboxCommandKind.SetLifecycle, occurrence, member, InboxLifecycle.Suspended,
                revision, reason, checkpoint);
    }

    internal readonly struct ReduceResult
    {
        internal readonly HostLedger Ledger;
        internal readonly bool Changed;
        internal ReduceResult(HostLedger ledger, bool changed) { Ledger = ledger; Changed = changed; }
    }
}
