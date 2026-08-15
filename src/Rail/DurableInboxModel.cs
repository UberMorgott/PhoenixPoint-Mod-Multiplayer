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

        internal MembershipId(string playerGuid)
        {
            PlayerGuid = InboxIdentity.Required(playerGuid, nameof(playerGuid));
        }

        public bool Equals(MembershipId other) =>
            string.Equals(PlayerGuid, other.PlayerGuid, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is MembershipId other && Equals(other);
        public override int GetHashCode() => InboxIdentity.Hash(PlayerGuid);
        public int CompareTo(MembershipId other) =>
            string.Compare(PlayerGuid, other.PlayerGuid, StringComparison.Ordinal);
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

    internal enum InboxMessageKind : byte { TransportAck = 0x46 }
    // Append-only: values 0..5 already exist in campaign saves and on the wire.
    internal enum InboxLifecycle : byte { Queued, Open, Read, Dismissed, Removed, Suspended, Deferred }
    // On the wire (DurableInboxCodec:336, Enum.IsDefined-guarded). Value 2 was LevelTeardown — deleted
    // 2026-08-10 with zero writers, never encoded by any build. Do not reuse it.
    internal enum InboxSuspensionReason : byte { None = 0, PriorityPreemption = 1 }

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

    internal readonly struct EffectToken : IEquatable<EffectToken>, IComparable<EffectToken>
    {
        internal readonly OccurrenceId Occurrence;
        internal readonly string Value;
        internal EffectToken(OccurrenceId occurrence, string value)
        { Occurrence = occurrence; Value = InboxIdentity.Required(value, nameof(value)); }
        public bool Equals(EffectToken other) => Occurrence.Equals(other.Occurrence) &&
            string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is EffectToken other && Equals(other);
        public override int GetHashCode() => InboxIdentity.Hash(Occurrence.GetHashCode(), Value);
        public int CompareTo(EffectToken other)
        { int c = Occurrence.CompareTo(other.Occurrence); return c != 0 ? c : string.Compare(Value, other.Value, StringComparison.Ordinal); }
    }

    internal enum SharedChoicePhase : byte { EffectPending, EffectApplied, ChoiceLocked }
    internal enum DurableEffectStepState : byte { Prepared, Applied }
    // Not serialised anywhere. ChargeApplied/RewardApplied went with the effect-transaction machinery.
    internal enum SharedEffectReceipt : byte { None, NativeCompleted }

    internal sealed class DurableEffectStep : IEquatable<DurableEffectStep>
    {
        internal DurableEffectStep(string key, string operation, string beforeFact, string afterFact,
            DurableEffectStepState state = DurableEffectStepState.Prepared)
        {
            Key = InboxIdentity.Required(key, nameof(key)); Operation = InboxIdentity.Required(operation, nameof(operation));
            BeforeFact = InboxIdentity.Required(beforeFact, nameof(beforeFact)); AfterFact = InboxIdentity.Required(afterFact, nameof(afterFact));
            if (!Enum.IsDefined(typeof(DurableEffectStepState), state)) throw new ArgumentOutOfRangeException(nameof(state));
            State = state;
        }
        internal string Key { get; }
        internal string Operation { get; }
        internal string BeforeFact { get; }
        internal string AfterFact { get; }
        internal DurableEffectStepState State { get; }
        internal DurableEffectStep Applied() => new DurableEffectStep(Key, Operation, BeforeFact, AfterFact, DurableEffectStepState.Applied);
        public bool Equals(DurableEffectStep other) => other != null && Key == other.Key && Operation == other.Operation &&
            BeforeFact == other.BeforeFact && AfterFact == other.AfterFact && State == other.State;
        public override bool Equals(object obj) => Equals(obj as DurableEffectStep);
        public override int GetHashCode() => InboxIdentity.Hash(Key, Operation, BeforeFact, AfterFact, (byte)State);
    }

    internal sealed class SharedChoiceDecision : IEquatable<SharedChoiceDecision>
    {
        internal const int MaxRewardPayloadBytes = 4 * 1024 * 1024;

        internal SharedChoiceDecision(OccurrenceId occurrence, EffectToken effectToken, CanonicalChoiceId choice,
            CanonicalResultId result, IEnumerable<CanonicalRewardItemId> rewards, MembershipId winner,
            SharedChoicePhase phase, IEnumerable<DurableEffectStep> effectSteps = null, ulong sharedRevision = 0,
            byte[] rewardPayload = null)
        {
            if (!Enum.IsDefined(typeof(SharedChoicePhase), phase)) throw new ArgumentOutOfRangeException(nameof(phase));
            if (!effectToken.Occurrence.Equals(occurrence) || !choice.Occurrence.Equals(occurrence) ||
                !result.Occurrence.Equals(occurrence)) throw new ArgumentException("foreign shared-choice namespace");
            var copy = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
            if (copy.Any(x => !x.Occurrence.Equals(occurrence)) || copy.Distinct().Count() != copy.Length)
                throw new ArgumentException("non-canonical shared-choice rewards", nameof(rewards));
            Array.Sort(copy);
            Occurrence = occurrence; EffectToken = effectToken; Choice = choice; Result = result;
            Rewards = new ReadOnlyCollection<CanonicalRewardItemId>(copy); Winner = winner; Phase = phase;
            var steps = (effectSteps ?? Enumerable.Empty<DurableEffectStep>()).ToArray();
            if (steps.Any(x => x == null) || steps.GroupBy(x => x.Key, StringComparer.Ordinal).Any(x => x.Count() != 1))
                throw new ArgumentException("duplicate durable effect step", nameof(effectSteps));
            EffectSteps = new ReadOnlyCollection<DurableEffectStep>(steps);
            if (phase == SharedChoicePhase.ChoiceLocked && sharedRevision == 0)
                throw new ArgumentException("locked decision requires authoritative shared revision", nameof(sharedRevision));
            SharedRevision = sharedRevision;
            var payload = rewardPayload ?? Array.Empty<byte>();
            if (payload.Length > MaxRewardPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(rewardPayload), "reward payload exceeds the durable wire/save bound");
            RewardPayload = (byte[])payload.Clone();
        }
        internal OccurrenceId Occurrence { get; }
        internal EffectToken EffectToken { get; }
        internal CanonicalChoiceId Choice { get; }
        internal CanonicalResultId Result { get; }
        internal IReadOnlyList<CanonicalRewardItemId> Rewards { get; }
        internal MembershipId Winner { get; }
        internal SharedChoicePhase Phase { get; }
        internal IReadOnlyList<DurableEffectStep> EffectSteps { get; }
        internal ulong SharedRevision { get; }
        internal byte[] RewardPayload { get; }
        internal string BeforeFact => EffectSteps.Count == 0 ? "unanswered" : EffectSteps[0].BeforeFact;
        internal string AfterFact => EffectSteps.Count == 0 ? "applied" : EffectSteps[0].AfterFact;
        internal SharedEffectReceipt Receipt => EffectSteps.Count != 0 && EffectSteps.All(x => x.State == DurableEffectStepState.Applied)
            ? SharedEffectReceipt.NativeCompleted : SharedEffectReceipt.None;
        internal SharedChoiceDecision WithPhase(SharedChoicePhase phase) =>
            new SharedChoiceDecision(Occurrence, EffectToken, Choice, Result, Rewards, Winner, phase, EffectSteps,
                phase == SharedChoicePhase.ChoiceLocked ? SharedRevision : 0, RewardPayload);
        internal SharedChoiceDecision WithPhase(SharedChoicePhase phase, ulong sharedRevision) =>
            new SharedChoiceDecision(Occurrence, EffectToken, Choice, Result, Rewards, Winner, phase, EffectSteps, sharedRevision, RewardPayload);
        internal SharedChoiceDecision WithAppliedStep(string key) =>
            new SharedChoiceDecision(Occurrence, EffectToken, Choice, Result, Rewards, Winner, Phase,
                EffectSteps.Select(x => x.Key == key ? x.Applied() : x), SharedRevision, RewardPayload);
        internal SharedChoiceDecision WithRewardPayload(byte[] payload) =>
            new SharedChoiceDecision(Occurrence, EffectToken, Choice, Result, Rewards, Winner, Phase,
                EffectSteps, SharedRevision, payload);
        public bool Equals(SharedChoiceDecision other) => other != null && Occurrence.Equals(other.Occurrence) &&
            EffectToken.Equals(other.EffectToken) && Choice.Equals(other.Choice) && Result.Equals(other.Result) &&
            Rewards.SequenceEqual(other.Rewards) && Winner.Equals(other.Winner) && Phase == other.Phase &&
            EffectSteps.SequenceEqual(other.EffectSteps) && SharedRevision == other.SharedRevision &&
            RewardPayload.SequenceEqual(other.RewardPayload);
        public override bool Equals(object obj) => Equals(obj as SharedChoiceDecision);
        public override int GetHashCode() => InboxIdentity.Hash(Occurrence.GetHashCode(), EffectToken.GetHashCode(),
            Choice.GetHashCode(), Result.GetHashCode(), Winner.GetHashCode(), (byte)Phase);
    }

    internal sealed class InboxMessage
    {
        internal InboxMessageKind Kind { get; }
        internal OccurrenceId Occurrence { get; }
        internal CanonicalResultId ResultId { get; }
        internal IReadOnlyList<CanonicalRewardItemId> RewardIds { get; }
        internal MembershipId Membership { get; }
        internal ulong LifecycleRevision { get; }
        internal ulong TombstoneRevision { get; }
        internal InboxLifecycle Lifecycle { get; }
        internal CanonicalChoiceId ChoiceId { get; }

        internal InboxMessage(InboxMessageKind kind, OccurrenceId occurrence, CanonicalResultId resultId,
            IEnumerable<CanonicalRewardItemId> rewardIds, MembershipId membership,
            ulong lifecycleRevision, ulong tombstoneRevision, InboxLifecycle lifecycle, CanonicalChoiceId choiceId)
        {
            if (!Enum.IsDefined(typeof(InboxMessageKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!Enum.IsDefined(typeof(InboxLifecycle), lifecycle)) throw new ArgumentOutOfRangeException(nameof(lifecycle));
            if (!resultId.Occurrence.Equals(occurrence)) throw new ArgumentException("foreign result namespace", nameof(resultId));
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
        internal TerminalReason? TerminalReason { get; }
        internal InboxSuspensionReason SuspensionReason { get; }
        internal InboxWindowCheckpoint Checkpoint { get; }
        internal OccurrenceId? SupersededBy { get; }
        internal OccurrenceId? Predecessor { get; }
        internal ulong PreparationRevision { get; }
        internal ulong PreparationAuthorityRevision { get; }
        internal InboxEntry(OccurrenceId occurrence, MembershipId membership, InboxLifecycle lifecycle,
            CanonicalChoiceId choice, ulong lifecycleRevision, ulong tombstoneRevision,
            InboxSuspensionReason suspensionReason = InboxSuspensionReason.None,
            InboxWindowCheckpoint checkpoint = null, TerminalReason? terminalReason = null,
            OccurrenceId? supersededBy = null, OccurrenceId? predecessor = null,
            ulong preparationRevision = 0, ulong preparationAuthorityRevision = 0)
        {
            Occurrence = occurrence; Membership = membership; Lifecycle = lifecycle; Choice = choice;
            LifecycleRevision = lifecycleRevision; TombstoneRevision = tombstoneRevision;
            if (terminalReason.HasValue && !Enum.IsDefined(typeof(TerminalReason), terminalReason.Value))
                throw new ArgumentOutOfRangeException(nameof(terminalReason));
            if (terminalReason.HasValue && (lifecycle != InboxLifecycle.Removed || tombstoneRevision == 0))
                throw new ArgumentException("only tombstones name a terminal reason", nameof(terminalReason));
            TerminalReason = terminalReason;
            if (supersededBy.HasValue && (!terminalReason.HasValue || terminalReason.Value != Multiplayer.Network.Sync.TerminalReason.Superseded))
                throw new ArgumentException("only a superseded tombstone names its successor", nameof(supersededBy));
            if (predecessor.HasValue && predecessor.Value.Equals(occurrence))
                throw new ArgumentException("successor cannot precede itself", nameof(predecessor));
            SupersededBy = supersededBy; Predecessor = predecessor;
            PreparationRevision = preparationRevision;
            PreparationAuthorityRevision = preparationAuthorityRevision;
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
                    SuspensionReason, Checkpoint, predecessor: Predecessor,
                    preparationRevision: PreparationRevision, preparationAuthorityRevision: PreparationAuthorityRevision)
                : new InboxEntry(Occurrence, Membership, lifecycle, Choice, revision, TombstoneRevision,
                    terminalReason: TerminalReason, supersededBy: SupersededBy, predecessor: Predecessor,
                    preparationRevision: PreparationRevision, preparationAuthorityRevision: PreparationAuthorityRevision);
        internal InboxEntry Suspend(InboxSuspensionReason reason, InboxWindowCheckpoint checkpoint, ulong revision) =>
            new InboxEntry(Occurrence, Membership, InboxLifecycle.Suspended, Choice, revision, TombstoneRevision,
                reason, checkpoint, predecessor: Predecessor,
                preparationRevision: PreparationRevision, preparationAuthorityRevision: PreparationAuthorityRevision);
        internal InboxEntry Defer(ulong revision) =>
            new InboxEntry(Occurrence, Membership, InboxLifecycle.Deferred, Choice, revision, TombstoneRevision,
                checkpoint: Checkpoint, predecessor: Predecessor,
                preparationRevision: PreparationRevision, preparationAuthorityRevision: PreparationAuthorityRevision);
        internal InboxEntry RestoreDeferred(ulong revision) =>
            new InboxEntry(Occurrence, Membership, InboxLifecycle.Open, Choice, revision, TombstoneRevision,
                checkpoint: Checkpoint, predecessor: Predecessor,
                preparationRevision: PreparationRevision, preparationAuthorityRevision: PreparationAuthorityRevision);
        internal InboxEntry WithPreparationRevision(ulong revision, ulong authorityRevision = 0) =>
            new InboxEntry(Occurrence, Membership,
                Lifecycle == InboxLifecycle.Deferred && revision > PreparationRevision
                    ? InboxLifecycle.Queued : Lifecycle,
                Choice, Lifecycle == InboxLifecycle.Deferred && revision > PreparationRevision
                    ? checked(LifecycleRevision + 1) : LifecycleRevision, TombstoneRevision,
                SuspensionReason, Checkpoint, TerminalReason, SupersededBy, Predecessor,
                revision, authorityRevision == 0 ? PreparationAuthorityRevision : authorityRevision);
    }

    internal sealed class HostLedger
    {
        private readonly IReadOnlyList<InboxEntry> _entries;
        private readonly IReadOnlyCollection<MembershipId> _members;
        internal HostLedger(IEnumerable<InboxEntry> entries, ulong committedRevision = 0,
            IEnumerable<MembershipId> members = null)
        {
            var copy = (entries ?? throw new ArgumentNullException(nameof(entries))).ToArray();
            if (copy.GroupBy(e => Tuple.Create(e.Occurrence, e.Membership)).Any(g => g.Count() != 1))
                throw new ArgumentException("duplicate ledger entry", nameof(entries));
            foreach (var successorGroup in copy.Where(e => e.Predecessor.HasValue).GroupBy(e => e.Occurrence))
            {
                var predecessors = successorGroup.Select(e => e.Predecessor.Value).Distinct().ToArray();
                if (predecessors.Length != 1 || successorGroup.Count() != copy.Count(e => e.Occurrence.Equals(successorGroup.Key)))
                    throw new ArgumentException("successor has inconsistent predecessor linkage", nameof(entries));
                var predecessorEntries = copy.Where(e => e.Occurrence.Equals(predecessors[0])).ToArray();
                if (predecessorEntries.Length == 0 || predecessorEntries.Any(e => !e.SupersededBy.HasValue ||
                    !e.SupersededBy.Value.Equals(successorGroup.Key) || e.TerminalReason != Multiplayer.Network.Sync.TerminalReason.Superseded) ||
                    !successorGroup.Key.SubjectIds.SequenceEqual(predecessors[0].SubjectIds, StringComparer.Ordinal))
                    throw new ArgumentException("dangling or foreign successor linkage", nameof(entries));
            }
            foreach (var predecessorGroup in copy.Where(e => e.SupersededBy.HasValue).GroupBy(e => e.Occurrence))
            {
                var successors = predecessorGroup.Select(e => e.SupersededBy.Value).Distinct().ToArray();
                if (successors.Length != 1 || predecessorGroup.Count() != copy.Count(e => e.Occurrence.Equals(predecessorGroup.Key)) ||
                    !copy.Any(e => e.Occurrence.Equals(successors[0]) && e.Predecessor.HasValue && e.Predecessor.Value.Equals(predecessorGroup.Key)))
                    throw new ArgumentException("dangling or inconsistent supersession linkage", nameof(entries));
            }
            // Membership is derived, never enrolled: an entry for a player IS that player's membership.
            var memberCopy = new HashSet<MembershipId>(members ?? copy.Select(e => e.Membership));
            _entries = new ReadOnlyCollection<InboxEntry>(copy);
            _members = memberCopy;
            CommittedRevision = committedRevision;
        }
        internal int EntryCount => _entries.Count;
        internal ulong CommittedRevision { get; }
        internal IReadOnlyList<InboxEntry> AllEntries => _entries;
        internal IReadOnlyCollection<MembershipId> Members => _members;
        internal IReadOnlyList<InboxEntry> EntriesFor(MembershipId member) => new ReadOnlyCollection<InboxEntry>(
            // NOT A PRESENTATION ORDER (L523). The ledger answers "has this been answered", never "what
            // comes next"; Occurrence is a stable, role-independent enumeration key so two peers walking
            // the same ledger walk it the same way. Presentation order is the host journal position.
            _entries.Where(e => e.Membership.Equals(member)).OrderBy(e => e.Occurrence).ToArray());
        internal bool Contains(OccurrenceId occurrence) => _entries.Any(e => e.Occurrence.Equals(occurrence));
        internal InboxEntry Get(OccurrenceId occurrence, MembershipId member) =>
            _entries.Single(e => e.Occurrence.Equals(occurrence) && e.Membership.Equals(member));
        internal HostLedger Add(IEnumerable<InboxEntry> additions) => new HostLedger(_entries.Concat(additions), CommittedRevision, _members);
        internal HostLedger Replace(InboxEntry replacement) => new HostLedger(_entries.Select(e =>
            e.Occurrence.Equals(replacement.Occurrence) && e.Membership.Equals(replacement.Membership) ? replacement : e), CommittedRevision, _members);
        internal HostLedger ReplaceOccurrence(OccurrenceId occurrence, Func<InboxEntry, InboxEntry> replace) =>
            new HostLedger(_entries.Select(e => e.Occurrence.Equals(occurrence) ? replace(e) : e), CommittedRevision, _members);
        internal HostLedger WithAuthority(ulong revision, IEnumerable<MembershipId> members) =>
            new HostLedger(_entries, revision, members);
        internal byte[] EncodeCanonical() => DurableInboxCodec.EncodeLedger(_entries, CommittedRevision, _members);
    }

    internal sealed class HostInboxSequencer
    {
        // ponytail: one authority lock; split only if measured host inbox contention warrants it.
        private readonly object _authority = new object();
        private readonly HashSet<MembershipId> _members = new HashSet<MembershipId>();
        private readonly HashSet<OccurrenceId> _occurrences = new HashSet<OccurrenceId>();
        internal HostLedger Ledger { get; private set; }
        internal ulong CommittedRevision { get; private set; }

        internal HostInboxSequencer(HostLedger ledger)
        {
            Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            foreach (var member in ledger.Members) _members.Add(member);
            foreach (var entry in ledger.AllEntries) _occurrences.Add(entry.Occurrence);
            CommittedRevision = ledger.CommittedRevision;
        }

        internal bool CreateOccurrence(OccurrenceId occurrence)
        {
            lock (_authority)
            {
                if (_occurrences.Contains(occurrence)) return false;
                var committedRevision = checked(CommittedRevision + 1);
                var additions = _members.Select(member => new InboxEntry(occurrence, member,
                    InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0)).ToArray();
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
                if (!_members.Contains(member)) return false;
                var result = DurableInboxReducer.Apply(Ledger,
                    InboxCommand.SetLifecycle(occurrence, member, lifecycle, revision));
                if (!result.Changed) return false;
                var committedRevision = checked(CommittedRevision + 1);
                Ledger = result.Ledger.WithAuthority(committedRevision, _members);
                CommittedRevision = committedRevision;
                return true;
            }
        }

        internal bool Tombstone(OccurrenceId occurrence, ulong revision,
            TerminalReason reason = TerminalReason.Invalidated)
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
                        entry.Choice, Math.Max(entry.LifecycleRevision, revision), revision,
                        terminalReason: reason, predecessor: entry.Predecessor);
                });
                if (!changed) return false;
                var committedRevision = checked(CommittedRevision + 1);
                Ledger = ledger.WithAuthority(committedRevision, _members);
                CommittedRevision = committedRevision;
                return true;
            }
        }

        internal IReadOnlyList<InboxEntry> Reconnect(MembershipId member)
        {
            lock (_authority)
                return _members.Contains(member)
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
