using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Util;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;

namespace Multiplayer.Network.Sync
{
    internal static class DurableInboxReducer
    {
        internal static HostLedger CloneAndValidate(HostLedger ledger)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            var entries = new InboxEntry[ledger.AllEntries.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = ledger.AllEntries[i];
                entries[i] = new InboxEntry(entry.Occurrence, entry.Membership, entry.Lifecycle, entry.Choice,
                    entry.LifecycleRevision, entry.TombstoneRevision, entry.HostOrderKey,
                    entry.SuspensionReason, entry.Checkpoint, entry.TerminalReason,
                    entry.SupersededBy, entry.Predecessor, entry.PreparationRevision,
                    entry.PreparationAuthorityRevision);
            }
            return new HostLedger(entries, ledger.CommittedRevision, ledger.Members);
        }

        internal static ReduceResult Apply(HostLedger ledger, InboxCommand command)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.Kind == InboxCommandKind.TransportAck) return new ReduceResult(ledger, false);

            InboxEntry current;
            try { current = ledger.Get(command.Occurrence, command.Membership); }
            catch (InvalidOperationException) { return new ReduceResult(ledger, false); }
            if (command.Revision <= current.LifecycleRevision || current.Lifecycle == InboxLifecycle.Removed ||
                !Enum.IsDefined(typeof(InboxLifecycle), command.Lifecycle) || command.Lifecycle == InboxLifecycle.Removed)
                return new ReduceResult(ledger, false);

            bool sameLifecycle = current.Lifecycle == command.Lifecycle;
            bool validTransition = current.Lifecycle == InboxLifecycle.Queued && command.Lifecycle == InboxLifecycle.Open ||
                                   current.Lifecycle == InboxLifecycle.Open &&
                                       (command.Lifecycle == InboxLifecycle.Suspended || command.Lifecycle == InboxLifecycle.Deferred ||
                                        command.Lifecycle == InboxLifecycle.Read || command.Lifecycle == InboxLifecycle.Dismissed) ||
                                   current.Lifecycle == InboxLifecycle.Suspended && command.Lifecycle == InboxLifecycle.Open ||
                                   current.Lifecycle == InboxLifecycle.Read && command.Lifecycle == InboxLifecycle.Dismissed;
            if (!sameLifecycle && !validTransition) return new ReduceResult(ledger, false);

            var replacement = command.Lifecycle == InboxLifecycle.Suspended
                ? current.Suspend(command.SuspensionReason == InboxSuspensionReason.None ? current.SuspensionReason : command.SuspensionReason,
                    command.Checkpoint ?? current.Checkpoint, command.Revision)
                : command.Lifecycle == InboxLifecycle.Deferred
                    ? current.Defer(command.Revision)
                : current.WithLifecycle(command.Lifecycle, command.Revision);
            return new ReduceResult(ledger.Replace(replacement), true);
        }
    }

    /// <summary>Holds the one live durable inbox store.  The store is SESSION state, not save state: the
    /// native game already persists the window queue and <see cref="WindowQueueSync"/> rides that restore,
    /// so this ledger is created empty when a co-op geoscape starts and dropped at level teardown.</summary>
    internal static class DurableInboxSession
    {
        private static readonly object Gate = new object();
        private static DurableInboxStore _activeStore;
        internal static DurableInboxStore ActiveStore
        {
            get { lock (Gate) return _activeStore; }
            set
            {
                bool changed; DurableInboxStore old;
                lock (Gate) { old = _activeStore; changed = !ReferenceEquals(old, value); _activeStore = value; }
                if (changed) { MissionSync.ClearScheduledSourceRevalidationDeltas();
                    WindowQueueSync.ClearDurableRuntimeCarriers();
                    // Occurrence trigger ids restart with a new store, so a wallet-charge claim left over
                    // from the previous session would suppress a legitimate charge in this one.
                    EventSync.ClearWalletChargeClaims();
                    old?.Carriers.AbandonStore(); }
            }
        }

        /// <summary>Called from the game's own "the geoscape is built" callback.  Outside an active co-op
        /// session there is nothing to reconcile, so no store is minted.
        ///
        /// THE MEMBERSHIP BOOTSTRAP IS LOAD-BEARING, and its absence made every durable window eat every
        /// click. <c>HostLedger</c> DERIVES <c>Members</c> from its own entries when none is supplied
        /// (DurableInboxModel.cs:485) and <c>HostInboxSequencer.CreateOccurrence</c> mints one entry PER
        /// MEMBER (:534) — so a ledger opened empty had no member, therefore created no entry, therefore
        /// still had no member: a bootstrap that can never start. Every entitlement lookup then failed
        /// closed for the whole session on EVERY peer, host included —
        /// <c>WindowQueueSync.TryLocalMember</c>:315, <c>EventPopup</c>'s click path :1838 ("answer dropped
        /// — no active local entitlement") and <c>EventSync</c>:378 ("host-local answer blocked") — which is
        /// a mission-start event window that swallows eleven consecutive clicks (live 2026-08-10, both
        /// clients and the host). <c>HostInboxSequencer.Enroll</c> was deleted in da02dce as having "zero
        /// production callers"; that was true and was itself the defect.
        ///
        /// THIS PEER'S OWN GUID IS THE WHOLE MEMBER SET, deliberately. The store is per-peer SESSION state
        /// that no rail surface carries (bc9b404) — every peer runs its own copy and reads only its own
        /// entitlement — so a row for anybody else would be a ghost nothing ever looks up.</summary>
        internal static void OpenSessionStore()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            ActiveStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>(), 0,
                new[] { new MembershipId(ClientIdentity.PlayerGuid.ToString("D")) }));
        }
    }

    internal sealed class DurableInboxCanonicalState
    {
        internal static DurableInboxCanonicalState Empty { get; } = new DurableInboxCanonicalState(
            Array.Empty<CanonicalChoiceId>(), Array.Empty<CanonicalResultId>(), Array.Empty<CanonicalRewardItemId>());
        internal DurableInboxCanonicalState(IEnumerable<CanonicalChoiceId> choices,
            IEnumerable<CanonicalResultId> results, IEnumerable<CanonicalRewardItemId> rewards,
            IEnumerable<SharedChoiceDecision> decisions = null)
        {
            Choices = new ReadOnlyCollection<CanonicalChoiceId>((choices ?? throw new ArgumentNullException(nameof(choices))).Distinct().OrderBy(x => x).ToArray());
            Results = new ReadOnlyCollection<CanonicalResultId>((results ?? throw new ArgumentNullException(nameof(results))).Distinct().OrderBy(x => x).ToArray());
            Rewards = new ReadOnlyCollection<CanonicalRewardItemId>((rewards ?? throw new ArgumentNullException(nameof(rewards))).Distinct().OrderBy(x => x).ToArray());
            var decisionCopy = (decisions ?? Enumerable.Empty<SharedChoiceDecision>()).ToArray();
            if (decisionCopy.Any(x => x == null) || decisionCopy.GroupBy(x => x.Occurrence).Any(x => x.Count() != 1))
                throw new ArgumentException("duplicate shared-choice decision", nameof(decisions));
            Decisions = new ReadOnlyCollection<SharedChoiceDecision>(decisionCopy.OrderBy(x => x.Occurrence).ToArray());
            if (decisionCopy.Any(d => !Choices.Contains(d.Choice) || !Results.Contains(d.Result) ||
                d.Rewards.Any(r => !Rewards.Contains(r))))
                throw new ArgumentException("shared-choice decision identities are not canonical", nameof(decisions));
        }
        internal IReadOnlyList<CanonicalChoiceId> Choices { get; }
        internal IReadOnlyList<CanonicalResultId> Results { get; }
        internal IReadOnlyList<CanonicalRewardItemId> Rewards { get; }
        internal IReadOnlyList<SharedChoiceDecision> Decisions { get; }
        internal DurableInboxCanonicalState With(OccurrenceId occurrence, CanonicalChoiceId choice,
            CanonicalResultId result, IEnumerable<CanonicalRewardItemId> rewards)
        {
            if (!choice.Occurrence.Equals(occurrence) || !result.Occurrence.Equals(occurrence))
                throw new ArgumentException("foreign canonical namespace");
            var rewardArray = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
            if (rewardArray.Any(x => !x.Occurrence.Equals(occurrence))) throw new ArgumentException("foreign reward namespace");
            return new DurableInboxCanonicalState(
                Choices.Where(x => !x.Occurrence.Equals(occurrence)).Concat(new[] { choice }),
                Results.Where(x => !x.Occurrence.Equals(occurrence)).Concat(new[] { result }),
                Rewards.Where(x => !x.Occurrence.Equals(occurrence)).Concat(rewardArray), Decisions);
        }
        internal DurableInboxCanonicalState WithDecision(SharedChoiceDecision decision)
        {
            if (decision == null) throw new ArgumentNullException(nameof(decision));
            return new DurableInboxCanonicalState(
                Choices.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(new[] { decision.Choice }),
                Results.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(new[] { decision.Result }),
                Rewards.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(decision.Rewards),
                Decisions.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(new[] { decision }));
        }
        internal DurableInboxCanonicalState Without(ISet<OccurrenceId> occurrences) => new DurableInboxCanonicalState(
            Choices.Where(x => !occurrences.Contains(x.Occurrence)), Results.Where(x => !occurrences.Contains(x.Occurrence)),
            Rewards.Where(x => !occurrences.Contains(x.Occurrence)), Decisions.Where(x => !occurrences.Contains(x.Occurrence)));
        internal DurableInboxCanonicalState WithoutDecision(OccurrenceId occurrence) => new DurableInboxCanonicalState(
            Choices.Where(x => !x.Occurrence.Equals(occurrence)), Results.Where(x => !x.Occurrence.Equals(occurrence)),
            Rewards.Where(x => !x.Occurrence.Equals(occurrence)), Decisions.Where(x => !x.Occurrence.Equals(occurrence)));
    }

    internal static class DurableInboxSaveBlobTransit
    {
        internal static BinaryDataLevelSerializedDataSource OpenExactReadSavegameBinaryBlob(byte[] blob, string extension)
        {
            return new BinaryDataLevelSerializedDataSource(blob ?? throw new ArgumentNullException(nameof(blob)),
                InboxIdentity.Required(extension, nameof(extension)));
        }
    }

    // ponytail: compatibility shim, not a feature.  A save written by the durable-inbox build carries a
    // "Multiplayer.DurableInbox/v1" section whose type no longer exists.  Phoenix's reader tolerates the
    // unknown section and skips it by length, but it hands SetReadObjects a null element in its place and
    // the native method is not known to survive that.  Filtering nulls is the whole job.  Delete this patch
    // once no DWI-era saves remain in the wild.
    [HarmonyPatch(typeof(GeoLevelSavegame), nameof(GeoLevelSavegame.SetReadObjects))]
    internal static class DurableInboxLegacyRootFilterPatch
    {
        private static void Prefix(ref IEnumerable<object> deserialized) =>
            deserialized = (deserialized ?? Enumerable.Empty<object>()).Where(x => x != null).ToArray();
    }
}
