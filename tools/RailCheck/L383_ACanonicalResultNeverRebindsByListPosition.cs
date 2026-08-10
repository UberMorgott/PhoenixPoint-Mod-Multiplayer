using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L383 / DWI-08 — production store persists typed stable identities; one unresolved ID quarantines the occurrence.</summary>
    internal static class L383_ACanonicalResultNeverRebindsByListPosition
    {
        internal static IEnumerable<string> Check()
        {
            var occurrence = new OccurrenceId("event", "trigger", new[] { "soldier", "site" });
            var member = new MembershipId("player");
            var choice = new CanonicalChoiceId(occurrence, "choice-stable-b");
            var result = new CanonicalResultId(occurrence, "result-stable-a");
            var reward = new CanonicalRewardItemId(occurrence, "soldier", "reward-stable-c");
            var ledger = new HostLedger(new[]
            {
                new InboxEntry(occurrence, member, InboxLifecycle.Read, choice, 4, 0, new HostOrderKey(2, occurrence.TriggerId))
            }, 4);
            var baseLedger = ledger.WithAuthority(3, ledger.Members);
            var store = new DurableInboxStore(baseLedger);
            var canonical = DurableInboxCanonicalState.Empty.With(occurrence, choice, result, new[] { reward })
                .WithDecision(new SharedChoiceDecision(occurrence, new EffectToken(occurrence, "effect-stable"),
                    choice, result, new[] { reward }, member, SharedChoicePhase.ChoiceLocked,
                    Array.Empty<DurableEffectStep>(), 4, new byte[] { 4, 3, 2, 1 }));
            if (!store.CommitWithCanonical(store.Ledger, ledger, canonical))
                yield return "L383 atomic-ledger-canonical-commit-refused";
            var root = store.CreateSaveRoot(); // production path: no test-only codec identity parameters

            var boundaryDecision = canonical.Decisions[0].WithRewardPayload(
                new byte[SharedChoiceDecision.MaxRewardPayloadBytes]);
            var boundaryCanonical = canonical.WithDecision(boundaryDecision);
            var boundaryStore = new DurableInboxStore(baseLedger);
            if (!boundaryStore.CommitWithCanonical(boundaryStore.Ledger, ledger, boundaryCanonical))
                yield return "L383 exact-boundary-reward-payload-commit-refused";
            else
            {
                DurableInboxRestore boundaryRestore; string boundaryRefusal;
                if (!DurableInboxSaveCodec.TryRestore(boundaryStore.CreateSaveRoot(), new TypedResolver(
                    new[] { "site", "soldier" }, new[] { "choice-stable-b" },
                    new[] { "result-stable-a" }, new[] { "soldier|reward-stable-c" }),
                    out boundaryRestore, out boundaryRefusal) ||
                    boundaryRestore.Canonical.Decisions.Single().RewardPayload.Length != SharedChoiceDecision.MaxRewardPayloadBytes)
                    yield return "L383 exact-boundary-reward-payload-did-not-roundtrip-save";
            }

            var failing = new DurableInboxStore(baseLedger) { WriteRecord = _ => false };
            if (failing.CommitWithCanonical(failing.Ledger, ledger, canonical) ||
                failing.Ledger.CommittedRevision != 3 || failing.Canonical.Choices.Count != 0 || failing.Journal.Count != 0)
                yield return "L383 failed-journal-write-exposed-a-torn-canonical-ledger-commit";
            var orphan = new OccurrenceId("event", "orphan", new[] { "site" });
            var orphanCanonical = new DurableInboxCanonicalState(new[] { new CanonicalChoiceId(orphan, "choice-stable-b") },
                new[] { new CanonicalResultId(orphan, "result-stable-a") }, Array.Empty<CanonicalRewardItemId>());
            var orphanStore = new DurableInboxStore(baseLedger);
            if (orphanStore.CommitWithCanonical(orphanStore.Ledger, ledger, orphanCanonical))
                yield return "L383 orphan-canonical-identity-was-committed-without-a-ledger-occurrence";

            var reordered = new TypedResolver(new[] { "site", "soldier" }, new[] { "choice-stable-b" },
                new[] { "result-stable-a" }, new[] { "soldier|reward-stable-c" });
            DurableInboxRestore restored; string refusal;
            if (!DurableInboxSaveCodec.TryRestore(root, reordered, out restored, out refusal))
                yield return "L383 valid-root-refused: " + refusal;
            else if (!restored.Canonical.Choices.Single().Equals(choice) ||
                     !restored.Canonical.Results.Single().Equals(result) ||
                     !restored.Canonical.Rewards.Single().Equals(reward) ||
                     restored.Canonical.Decisions.Count != 1 ||
                     restored.Canonical.Decisions[0].EffectToken.Value != "effect-stable" ||
                     restored.Canonical.Decisions[0].Phase != SharedChoicePhase.ChoiceLocked ||
                     restored.Canonical.Decisions[0].BeforeFact != "unanswered" ||
                     restored.Canonical.Decisions[0].AfterFact != "applied" ||
                     !restored.Canonical.Decisions[0].RewardPayload.SequenceEqual(new byte[] { 4, 3, 2, 1 }) ||
                     restored.Canonical.Decisions[0].EffectSteps.Count != 0 ||
                     !restored.Ledger.Get(occurrence, member).Choice.Equals(choice))
                yield return "L383 production-store-lost-stable-canonical-identities";

            foreach (var missing in new[] { "subject", "choice", "result", "reward-subject", "reward" })
            {
                var resolver = reordered.WithMissing(missing);
                if (!DurableInboxSaveCodec.TryRestore(root, resolver, out restored, out refusal) ||
                    !restored.Ledger.Contains(occurrence) || restored.Canonical.Choices.Count != 1 ||
                    restored.Canonical.Results.Count != 1 || restored.Canonical.Rewards.Count != 1 ||
                    restored.IsServable(occurrence) ||
                    !restored.Quarantine.SequenceEqual(new[] { occurrence }))
                    yield return "L383 unresolved-" + missing + "-did-not-quarantine-whole-occurrence";
            }

            DurableInboxSaveBridge.ActiveStore = store;
            DurableInboxSaveBridge.Extract(new object[] { root }).ToArray();
            if (DurableInboxSaveBridge.ActiveStore != null ||
                !DurableInboxSaveBridge.ReconcileAndInstall(reordered.WithMissing("reward"), out refusal) ||
                DurableInboxSaveBridge.ActiveStore == null || !DurableInboxSaveBridge.ActiveStore.Ledger.Contains(occurrence) ||
                DurableInboxSaveBridge.ActiveStore.IsServable(occurrence) ||
                !DurableInboxSaveBridge.PendingRestore.Quarantine.SequenceEqual(new[] { occurrence }))
                yield return "L383 authoritative-reconciliation-installed-an-unresolved-occurrence";
            if (!DurableInboxSaveBridge.ReconcileAndInstall(reordered, out refusal) ||
                !DurableInboxSaveBridge.ActiveStore.IsServable(occurrence) ||
                !DurableInboxSaveBridge.ActiveStore.Ledger.Contains(occurrence) ||
                DurableInboxSaveBridge.ActiveStore.Canonical.Rewards.Count != 1 ||
                DurableInboxSaveBridge.PendingRestore.Quarantine.Count != 0)
                yield return "L383 later-resolver-could-not-clear-quarantine-without-data-loss";

            // POSITIVE CONTROL: list position zero is a reward, while the saved choice remains choice-stable-b.
            var reorderedDefinitions = new[] { "reward-stable-c", "choice-stable-b", "result-stable-a" };
            if (reorderedDefinitions[0] == choice.Value)
                yield return "L383 control-not-red: reordered-position-did-not-change";
        }

        private sealed class TypedResolver : IDurableInboxStableResolver
        {
            private readonly HashSet<string> _subjects, _choices, _results, _rewards;
            internal TypedResolver(IEnumerable<string> subjects, IEnumerable<string> choices,
                IEnumerable<string> results, IEnumerable<string> rewards)
            { _subjects = new HashSet<string>(subjects); _choices = new HashSet<string>(choices); _results = new HashSet<string>(results); _rewards = new HashSet<string>(rewards); }
            public bool SubjectExists(string id) => _subjects.Contains(id);
            public bool ChoiceExists(OccurrenceId occurrence, string id) => _choices.Contains(id);
            public bool ResultExists(OccurrenceId occurrence, string id) => _results.Contains(id);
            public bool RewardExists(OccurrenceId occurrence, string subjectId, string id) => _rewards.Contains(subjectId + "|" + id);
            internal TypedResolver WithMissing(string kind)
            {
                var subjects = new HashSet<string>(_subjects); var choices = new HashSet<string>(_choices);
                var results = new HashSet<string>(_results); var rewards = new HashSet<string>(_rewards);
                if (kind == "subject") subjects.Remove("site");
                if (kind == "choice") choices.Clear();
                if (kind == "result") results.Clear();
                if (kind == "reward-subject") subjects.Remove("soldier");
                if (kind == "reward") rewards.Clear();
                return new TypedResolver(subjects, choices, results, rewards);
            }
        }
    }
}
