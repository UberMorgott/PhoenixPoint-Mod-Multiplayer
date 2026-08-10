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

    /// <summary>Holds the one live durable inbox store.  The store is SESSION state, not save state: the
    /// native game already persists the window queue and <see cref="WindowQueueSync"/> rides that restore,
    /// so this ledger is created empty when a co-op geoscape starts and dropped at level teardown.</summary>
    internal static class DurableInboxSaveBridge
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
                    WindowQueueSync.ClearDurableRuntimeCarriers(); old?.Carriers.AbandonStore(); }
            }
        }

        /// <summary>Called from the game's own "the geoscape is built" callback.  Outside an active co-op
        /// session there is nothing to reconcile, so no store is minted.</summary>
        internal static void OpenSessionStore()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            ActiveStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>()));
        }
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
