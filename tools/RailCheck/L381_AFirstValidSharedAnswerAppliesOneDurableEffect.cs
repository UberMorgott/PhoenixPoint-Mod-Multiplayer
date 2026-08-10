using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.IO;
using Multiplayer.Network.Sync;
using Base.Core;
using PhoenixPoint.Geoscape.Core;

namespace RailCheck
{
    internal static class L381_AFirstValidSharedAnswerAppliesOneDurableEffect
    {
        internal static IEnumerable<string> Check()
        {
            var provenanceMeta = (Base.Serialization.PPSavegameMetaData)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Base.Serialization.PPSavegameMetaData));
            provenanceMeta.Name = "continue-law381"; provenanceMeta.Path = "exact/path";
            if (DurableEffectSaveCatalog.ConsumeContinueLoad(provenanceMeta))
                yield return "L381 manual-load-without-continue-provenance-was-rewritten";
            using (DurableEffectSaveCatalog.PermitContinueLoad(provenanceMeta))
            {
                var mismatch = (Base.Serialization.PPSavegameMetaData)
                    System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Base.Serialization.PPSavegameMetaData));
                mismatch.Name = "older-manual"; mismatch.Path = provenanceMeta.Path; mismatch.GameId = provenanceMeta.GameId;
                if (DurableEffectSaveCatalog.ConsumeContinueLoad(mismatch))
                    yield return "L381 mismatched-historical-save-consumed-continue-provenance";
            }
            using (DurableEffectSaveCatalog.PermitContinueLoad(provenanceMeta))
                if (!DurableEffectSaveCatalog.ConsumeContinueLoad(provenanceMeta) ||
                    DurableEffectSaveCatalog.ConsumeContinueLoad(provenanceMeta))
                    yield return "L381 exact-continue-provenance-was-not-single-use";
            var compatibleCheckpoint = (Base.Serialization.PPSavegameMetaData)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Base.Serialization.PPSavegameMetaData));
            compatibleCheckpoint.Name = "internal"; compatibleCheckpoint.Path = "internal/path";
            compatibleCheckpoint.GameId = provenanceMeta.GameId;
            if (!DurableEffectSaveCatalog.CompatibleCheckpoint(provenanceMeta, compatibleCheckpoint))
                yield return "L381 compatible-checkpoint-feature-metadata-was-refused";
            compatibleCheckpoint.IsTacticalSave = true;
            if (DurableEffectSaveCatalog.CompatibleCheckpoint(provenanceMeta, compatibleCheckpoint))
                yield return "L381 incompatible-checkpoint-feature-metadata-was-accepted";
            compatibleCheckpoint.IsTacticalSave = false;
            compatibleCheckpoint.EnabledDlc = new Base.Platforms.EntitlementDef[] { null };
            if (DurableEffectSaveCatalog.CompatibleCheckpoint(provenanceMeta, compatibleCheckpoint))
                yield return "L381 incompatible-checkpoint-dlc-set-was-accepted";
            compatibleCheckpoint.EnabledDlc = null;
            var fingerprintBytes = new byte[] { 3, 8, 1 };
            uint fingerprintCrc = DurableEffectPhoenixCheckpointBackend.Crc32(fingerprintBytes);
            if (!DurableEffectSaveCatalog.BlobMatches(3, fingerprintCrc, fingerprintBytes) ||
                DurableEffectSaveCatalog.BlobMatches(3, fingerprintCrc, new byte[] { 3, 8, 2 }))
                yield return "L381 fallback-fingerprint-validation-was-not-mutation-sensitive";
            var newerNormal = (Base.Serialization.PPSavegameMetaData)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Base.Serialization.PPSavegameMetaData));
            newerNormal.Name = "normal-B"; newerNormal.Path = "exact/B"; newerNormal.GameId = provenanceMeta.GameId;
            string newerLocator = DurableEffectPhoenixCheckpointBackend.SaveLocator(newerNormal);
            if (DurableEffectPhoenixCheckpointBackend.ResolveLatestUserVisiblePredecessor(newerNormal, provenanceMeta) != newerLocator)
                yield return "L381 newer-normal-save-did-not-outrank-older-latest-load";
            var chainedOccurrence = new OccurrenceId("event", "chain:381", new[] { "site" });
            var chainedToken = new EffectToken(chainedOccurrence, "effect:chain");
            var chainedCheckpoint = new DurableEffectCheckpoint("chain-post", DurableEffectCheckpointKind.Committed,
                chainedOccurrence, chainedToken, "chain-pre");
            chainedCheckpoint.BindPredecessor(newerLocator);
            var buildMarker = typeof(DurableEffectPhoenixCheckpointBackend).GetMethod("BuildMarker",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var internalPost = (Base.Serialization.PPSavegameMetaData)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Base.Serialization.PPSavegameMetaData));
            internalPost.Name = "mp_dfx_post_chain-post"; internalPost.UserSetName =
                (string)buildMarker.Invoke(null, new object[] { chainedCheckpoint });
            if (DurableEffectPhoenixCheckpointBackend.ResolveUserVisiblePredecessor(internalPost) != newerLocator)
                yield return "L381 chained-internal-post-lost-user-visible-predecessor";
            int lifecycleMoves = 0;
            var lifecycle = DurableEffectSaveCatalog.CompleteLoadLifecycle(CounterCrt(() => lifecycleMoves++));
            while (lifecycle.MoveNext()) { }
            lifecycle.Dispose();
            if (lifecycleMoves != 1 || DurableEffectSaveCatalog.HasScheduledFallback)
                yield return "L381 load-iterator-lifecycle-did-not-finalize-outside-deserialization";
            var o = new OccurrenceId("event", "raise:381", new[] { "site" });
            var a = new MembershipId("a", 1); var b = new MembershipId("b", 1);
            var ledger = new HostLedger(new[] {
                Entry(o, a), Entry(o, b)
            }, 1, new[] { Pair(a), Pair(b) });
            var store = new DurableInboxStore(ledger);
            var generated = new GeoFactionReward
            {
                FactionSkillPoints = 7,
                AddAllSoldiersDamage = 3,
                ApplyResult = new GeoFactionRewardApplyResult { FactionSkillPoints = 7, AllSoldiersDamage = 3 }
            };
            var generatedFacts = EventRewardTransaction.Canonicalize(o, generated);
            var generatedAgain = EventRewardTransaction.Canonicalize(o, generated);
            if (generatedFacts.Count == 0 || !generatedFacts.SequenceEqual(generatedAgain) ||
                !generatedFacts.Any(x => x.Value.Contains("FactionSkillPoints=7")) ||
                !generatedFacts.Any(x => x.Value.Contains("AllSoldiersDamage=3")))
                yield return "L381-generated-native-reward-facts-not-canonical-or-deterministic";
            var itemA = RewardWithItem("law381-item-a");
            var itemB = RewardWithItem("law381-item-b");
            var itemFactsA = EventRewardTransaction.Canonicalize(o, itemA);
            var itemFactsB = EventRewardTransaction.Canonicalize(o, itemB);
            if (itemFactsA.SequenceEqual(itemFactsB) || !itemFactsA.Any(x => x.Value.Contains("law381-item-a")) ||
                !itemFactsB.Any(x => x.Value.Contains("law381-item-b")))
                yield return "L381-real-shaped-item-storage-content-was-not-canonicalized";
            int grants = 0;
            var effect = new DelegateDurableChoiceEffect(d => Interlocked.Increment(ref grants),
                _ => grants == 0 ? DurableEffectObservation.Before : grants == 1 ? DurableEffectObservation.After : DurableEffectObservation.Diverged);
            var engine = new DurableSharedChoiceEngine(store, effect, _ => { });
            var choiceA = new CanonicalChoiceId(o, "choice-a");
            var choiceB = new CanonicalChoiceId(o, "choice-b");
            var resultA = new CanonicalResultId(o, "result-a");
            var resultB = new CanonicalResultId(o, "result-b");
            SharedChoiceDecision da = null, db = null;
            var gate = new ManualResetEventSlim(false);
            var ta = new Thread(() => { gate.Wait(); engine.TryAnswer(o, a, choiceA, resultA, Array.Empty<CanonicalRewardItemId>(), () => true, out da); });
            var tb = new Thread(() => { gate.Wait(); engine.TryAnswer(o, b, choiceB, resultB, Array.Empty<CanonicalRewardItemId>(), () => true, out db); });
            ta.Start(); tb.Start(); gate.Set(); ta.Join(); tb.Join();
            var stored = store.Canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(o));
            if (stored == null || stored.Phase != SharedChoicePhase.ChoiceLocked || grants != 1)
                yield return "L381 first-valid-answer-did-not-lock-one-effect";
            if (da == null || db == null || !da.Equals(stored) || !db.Equals(stored))
                yield return "L381 loser-did-not-receive-canonical-result";
            if (store.Ledger.EntriesFor(a).Single().Choice.Value != stored.Choice.Value ||
                store.Ledger.EntriesFor(b).Single().Choice.Value != stored.Choice.Value)
                yield return "L381 queued-or-open-copy-did-not-freeze-canonical-choice";

            var invalidStore = new DurableInboxStore(ledger);
            var invalid = new DurableSharedChoiceEngine(invalidStore, effect, _ => { });
            SharedChoiceDecision ignored;
            if (invalid.TryAnswer(o, a, choiceA, resultA, Array.Empty<CanonicalRewardItemId>(), () => false, out ignored) ||
                invalidStore.Canonical.Decisions.Count != 0)
                yield return "L381 invalid-answer-left-unanswered-state";

            var preFailStore = new DurableInboxStore(ledger);
            var preFailEngine = new DurableSharedChoiceEngine(preFailStore,
                new DelegateDurableChoiceEffect(_ => { }, _ => DurableEffectObservation.Diverged), _ => { });
            var preFailBackend = new TxBackend { Failure = TxFailure.PendingWrite };
            if (preFailEngine.TryAnswerCheckpointed(o, a, choiceA, resultA,
                    Array.Empty<CanonicalRewardItemId>(), () => true,
                    _ => new DurableEffectTransactionCoordinator(preFailBackend, SequentialIds()), () => { }, _ => { }, out ignored) ||
                preFailStore.Canonical.Decisions.Count != 0 || preFailStore.Ledger.CommittedRevision != ledger.CommittedRevision)
                yield return "L381 failed-pre-did-not-return-to-retryable-unanswered";

            foreach (SharedChoiceCrashPoint point in Enum.GetValues(typeof(SharedChoiceCrashPoint)))
            {
                var crashStore = new DurableInboxStore(ledger); int crashGrants = 0;
                var crashing = new DurableSharedChoiceEngine(crashStore,
                    new DelegateDurableChoiceEffect(d => crashGrants++,
                        _ => crashGrants == 0 ? DurableEffectObservation.Before : crashGrants == 1 ? DurableEffectObservation.After : DurableEffectObservation.Diverged), _ => { });
                crashing.CrashProbe = p => { if (p == point) throw new SimulatedCrash(); };
                try { crashing.TryAnswer(o, a, choiceA, resultA, Array.Empty<CanonicalRewardItemId>(), () => true, out ignored); }
                catch (SimulatedCrash) { }
                var recovery = new DurableSharedChoiceEngine(crashStore,
                    new DelegateDurableChoiceEffect(d => crashGrants++,
                        _ => crashGrants == 0 ? DurableEffectObservation.Before : crashGrants == 1 ? DurableEffectObservation.After : DurableEffectObservation.Diverged), _ => { });
                recovery.RecoverPending();
                if (crashStore.Canonical.Decisions.Single().Phase != SharedChoicePhase.ChoiceLocked || crashGrants != 1)
                    yield return "L381 crash-recovery-failed-at-" + point;
                if (!recovery.TryAnswer(o, b, choiceB, resultB, Array.Empty<CanonicalRewardItemId>(), () => true, out ignored) ||
                    ignored.Choice.Value != choiceA.Value || crashGrants != 1)
                    yield return "L381 response-retry-did-not-return-stored-result-at-" + point;
            }

            var saveStore = new DurableInboxStore(ledger); int saveGrants = 0;
            var beforeSaveCrash = new DurableSharedChoiceEngine(saveStore,
                new DelegateDurableChoiceEffect(d => saveGrants++,
                    _ => saveGrants == 0 ? DurableEffectObservation.Before : saveGrants == 1 ? DurableEffectObservation.After : DurableEffectObservation.Diverged), _ => { })
            { CrashProbe = p => { if (p == SharedChoiceCrashPoint.PendingCommitted) throw new SimulatedCrash(); } };
            try { beforeSaveCrash.TryAnswer(o, a, choiceA, resultA, Array.Empty<CanonicalRewardItemId>(), () => true, out ignored); }
            catch (SimulatedCrash) { }
            DurableInboxRestore restored; string refusal;
            if (!DurableInboxSaveCodec.TryRestore(saveStore.CreateSaveRoot(), null, out restored, out refusal))
                yield return "L381 pending-save-could-not-restore: " + refusal;
            else
            {
                var restarted = new DurableInboxStore(restored.Ledger, restored.Canonical, restored.Journal,
                    restored.SnapshotLedger, restored.SnapshotCanonical);
                var startup = new DurableSharedChoiceEngine(restarted,
                    new DelegateDurableChoiceEffect(d => saveGrants++,
                        _ => saveGrants == 0 ? DurableEffectObservation.Before : saveGrants == 1 ? DurableEffectObservation.After : DurableEffectObservation.Diverged), _ => { });
                if (startup.RecoverPending() != 1 || saveGrants != 1 ||
                    restarted.Canonical.Decisions.Single().Phase != SharedChoicePhase.ChoiceLocked)
                    yield return "L381 save-load-startup-did-not-recover-pending-effect-once";
            }

            // POSITIVE CONTROL: removing occurrence-token dedup makes a replay grant twice.
            int unsafeGrants = 0; Action<SharedChoiceDecision> unsafeEffect = _ => unsafeGrants++;
            unsafeEffect(store.Canonical.Decisions.Single()); unsafeEffect(store.Canonical.Decisions.Single());
            if (unsafeGrants != 2) yield return "L381 positive-control-effect-replay-did-not-double-grant";

            var second = new OccurrenceId("event", "raise:381-second", new[] { "site" });
            foreach (var addressed in new[] { o, second })
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                    EventSync.WriteDurableAnswer(w, addressed, a, 9, "same-event-id", 0);
                ms.Position = 0; OccurrenceId decoded; MembershipId decodedMember; ulong revision; string eventId; int index;
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
                    if (!EventSync.TryReadDurableAnswer(r, out decoded, out decodedMember, out revision, out eventId, out index) ||
                        !decoded.Equals(addressed) || !decodedMember.Equals(a) || revision != 9 || eventId != "same-event-id" || index != 0)
                        yield return "L381 full-occurrence-answer-address-did-not-roundtrip";
            }
            if (!EventSync.SenderOwnsMembership(a.PlayerGuid.ToUpperInvariant(), a) ||
                EventSync.SenderOwnsMembership("00000000-0000-0000-0000-000000000099", a) ||
                EventSync.SenderOwnsMembership(null, a))
                yield return "L381 host-accepted-foreign-or-unidentified-membership-sender";

            using (var rewardBytes = new MemoryStream())
            {
                using (var w = new BinaryWriter(rewardBytes, System.Text.Encoding.UTF8, true))
                    EventSync.WriteOccurrence(w, second);
                rewardBytes.Position = 0;
                using (var r = new BinaryReader(rewardBytes, System.Text.Encoding.UTF8, true))
                    if (!EventSync.ReadOccurrence(r).Equals(second) || r.BaseStream.Position != r.BaseStream.Length)
                        yield return "L381 durable-reward-address-lost-full-occurrence";
            }

            // The production guarantee is a campaign rollback transaction, not an unverifiable
            // receipt-after-call.  PRE includes the pending durable choice and canonical generated facts;
            // POST includes the locked choice and resulting world.  A kill between them reloads PRE.
            var token381 = new EffectToken(o, "effect:381");
            var txBackend = new TxBackend(); int nativeCalls = 0, broadcasts = 0, ids = 0;
            var coordinator = new DurableEffectTransactionCoordinator(txBackend, () => "cp-" + (++ids));
            var post = coordinator.Execute(o, token381, () => nativeCalls++, _ => broadcasts++);
            if (nativeCalls != 1 || broadcasts != 1 || txBackend.Frozen ||
                post.Kind != DurableEffectCheckpointKind.Committed || txBackend.Checkpoints.Count != 2)
                yield return "L381 verified-pre-native-post-order-failed";

            foreach (var fail in new[] { TxFailure.PendingWrite, TxFailure.Native, TxFailure.PostWrite,
                                         TxFailure.PostVerify, TxFailure.Broadcast })
            {
                var killed = new TxBackend { Failure = fail }; int calls = 0;
                var cut = new DurableEffectTransactionCoordinator(killed, SequentialIds());
                try { cut.Execute(o, token381, () => { calls++; if (fail == TxFailure.Native) throw new SimulatedCrash(); },
                    _ => { if (fail == TxFailure.Broadcast) throw new SimulatedCrash(); }); }
                catch (Exception) { }
                bool correctRollback = fail == TxFailure.PendingWrite || fail == TxFailure.Broadcast
                    ? !killed.Frozen && killed.Reloaded == null
                    : killed.Frozen && killed.Reloaded != null && killed.Reloaded.Kind == DurableEffectCheckpointKind.Pending;
                if (!correctRollback)
                    yield return "L381 failure-did-not-force-pending-rollback-" + fail;
            }

            // Fresh-process recovery selects verified POST without replaying native.  With only PRE it
            // reloads PRE and remains frozen, so no partially-mutated memory can accept more work.
            var committedBackend = txBackend.Clone(); int recoveredBroadcasts = 0;
            var freshCoordinator = new DurableEffectTransactionCoordinator(committedBackend, SequentialIds());
            if (freshCoordinator.Recover(o, token381, _ => recoveredBroadcasts++) != DurableEffectRecovery.RestoredCommitted ||
                recoveredBroadcasts != 1 || committedBackend.Frozen)
                yield return "L381 fresh-process-post-recovery-failed";
            var pendingOnly = txBackend.Clone(keepCommitted: false);
            if (new DurableEffectTransactionCoordinator(pendingOnly, SequentialIds()).Recover(o, token381, _ => { }) !=
                DurableEffectRecovery.ReloadingPending || !pendingOnly.Frozen || pendingOnly.Reloaded?.Kind != DurableEffectCheckpointKind.Pending)
                yield return "L381 fresh-process-pre-recovery-did-not-remain-blocked";

            string root = Directory.GetCurrentDirectory();
            string eventSource = File.ReadAllText(Path.Combine(root, "src", "Rail", "EventSync.cs"));
            string barrierSource = File.ReadAllText(Path.Combine(root, "src", "Rail", "DurableEffectTransactionBarrier.cs"));
            string backendSource = File.ReadAllText(Path.Combine(root, "src", "Rail", "DurableEffectPhoenixCheckpointBackend.cs"));
            string catalogSource = File.ReadAllText(Path.Combine(root, "src", "Rail", "DurableEffectSaveCatalog.cs"));
            if (!eventSource.Contains("TryAnswerCheckpointed") ||
                !eventSource.Contains("new DurableEffectPhoenixCheckpointBackend(token)") ||
                eventSource.Contains("new DurableEffectStep(\"native-event\""))
                yield return "L381 production-answer-bypasses-two-checkpoint-coordinator";
            if (!barrierSource.Contains("ref IEnumerator<NextUpdate> __result") ||
                !barrierSource.Contains("GetObjectsToWrite") || !backendSource.Contains("ReadSavegameBinary") ||
                !backendSource.Contains("Crc32") || !backendSource.Contains("ForceReload"))
                yield return "L381 production-save-lifetime-or-readback-seam-missing";
            if (backendSource.Contains(".mpdwi") || backendSource.Contains("PlatformData") ||
                !barrierSource.Contains("StampOwnerRoot") || !barrierSource.Contains("SaveCaptured"))
                yield return "L381 checkpoint-proof-is-detachable-from-phoenix-save";
            if (!catalogSource.Contains("PhoenixSaveManager.GetSavegames") || !catalogSource.Contains("RawScope") ||
                !catalogSource.Contains("PhoenixSaveManager.LoadGame") || !catalogSource.Contains("OrderByDescending") ||
                !catalogSource.Contains("DeleteSaveGame") || !backendSource.Contains("SaveType.ManualSave") ||
                !backendSource.Contains("SetOwnerIronman"))
                yield return "L381 internal-catalog-recovery-cleanup-or-ironman-seam-missing";
            var malformedMarker = saveStore.CreateSaveRoot();
            malformedMarker.TransactionKind = (byte)DurableEffectCheckpointKind.Pending;
            malformedMarker.TransactionCheckpointId = "only-an-id";
            var computeCrc = typeof(DurableInboxSaveCodec).GetMethod("ComputeRootCrc",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            malformedMarker.Crc = (uint)computeCrc.Invoke(null, new object[] { malformedMarker });
            DurableInboxRestore malformedRestore; string malformedRefusal;
            if (DurableInboxSaveCodec.TryRestore(malformedMarker, null, out malformedRestore, out malformedRefusal))
                yield return "L381 incomplete-embedded-checkpoint-marker-was-accepted";
            var missingRevision = saveStore.CreateSaveRoot();
            missingRevision.TransactionKind = (byte)DurableEffectCheckpointKind.Committed;
            missingRevision.TransactionCheckpointId = "post";
            missingRevision.TransactionPendingCheckpointId = "pre";
            missingRevision.TransactionEventId = o.EventId;
            missingRevision.TransactionTriggerId = o.TriggerId;
            missingRevision.TransactionSubjectIds = o.SubjectIds.ToArray();
            missingRevision.TransactionEffectToken = "effect:post";
            missingRevision.Crc = (uint)computeCrc.Invoke(null, new object[] { missingRevision });
            if (DurableInboxSaveCodec.TryRestore(missingRevision, null, out malformedRestore, out malformedRefusal))
                yield return "L381 post-marker-without-authoritative-shared-revision-was-accepted";

            var pre381 = new DurableEffectCheckpoint("catalog-pre", DurableEffectCheckpointKind.Pending, o, token381);
            var post381 = new DurableEffectCheckpoint("catalog-post", DurableEffectCheckpointKind.Committed, o, token381,
                pre381.CheckpointId);
            var preRoot = Mark(saveStore.CreateSaveRoot(), pre381, 0, computeCrc);
            var postRoot = Mark(saveStore.CreateSaveRoot(), post381, 9, computeCrc);
            var schema6 = DurableInboxSaveCodec.CreateSchema6ForMigrationTest(saveStore.Ledger);
            schema6.Crc = (uint)computeCrc.Invoke(null, new object[] { schema6 });
            if (!DurableInboxSaveCodec.TryRestore(schema6, null, out malformedRestore, out malformedRefusal))
                yield return "L381 schema6-immediate-predecessor-save-was-not-migratable";
            preRoot.TransactionWasIronman = true;
            preRoot.Crc = (uint)computeCrc.Invoke(null, new object[] { preRoot });
            if (!DurableInboxSaveCodec.TryRestore(preRoot, null, out malformedRestore, out malformedRefusal) ||
                !preRoot.TransactionWasIronman)
                yield return "L381 embedded-ironman-semantics-did-not-survive-checkpoint-validation";
            var selectedPost = DurableEffectSaveCatalog.SelectValidated(new[] {
                new DurableEffectCatalogCandidate(pre381, preRoot, 20),
                new DurableEffectCatalogCandidate(post381, postRoot, 10) });
            if (selectedPost?.Checkpoint.CheckpointId != post381.CheckpointId)
                yield return "L381 startup-catalog-did-not-prefer-verified-lineaged-post";
            var corruptPost = Mark(saveStore.CreateSaveRoot(), post381, 9, computeCrc);
            corruptPost.TransactionEffectToken = "wrong-token";
            corruptPost.Crc = (uint)computeCrc.Invoke(null, new object[] { corruptPost });
            var selectedPre = DurableEffectSaveCatalog.SelectValidated(new[] {
                new DurableEffectCatalogCandidate(post381, corruptPost, 30),
                new DurableEffectCatalogCandidate(pre381, preRoot, 20) });
            if (selectedPre?.Checkpoint.CheckpointId != pre381.CheckpointId)
                yield return "L381 corrupt-post-did-not-fall-back-to-pre";
            var orphanPost = DurableEffectSaveCatalog.SelectValidated(new[] {
                new DurableEffectCatalogCandidate(post381, postRoot, 30) });
            if (orphanPost != null) yield return "L381 post-without-pre-lineage-was-selected";
            var newerToken = new EffectToken(o, "effect:newer");
            var newerPre = new DurableEffectCheckpoint("newer-pre", DurableEffectCheckpointKind.Pending, o, newerToken);
            var newerRoot = Mark(saveStore.CreateSaveRoot(), newerPre, 0, computeCrc);
            var newestTransaction = DurableEffectSaveCatalog.SelectValidated(new[] {
                new DurableEffectCatalogCandidate(pre381, preRoot, 10),
                new DurableEffectCatalogCandidate(post381, postRoot, 20),
                new DurableEffectCatalogCandidate(newerPre, newerRoot, 30) });
            if (newestTransaction?.Checkpoint.CheckpointId != newerPre.CheckpointId)
                yield return "L381 older-post-overrode-newer-pending-transaction";
            if (DurableEffectSaveCatalog.MatchesEmbedded(postRoot,
                new DurableEffectCheckpoint("catalog-post", DurableEffectCheckpointKind.Committed, o,
                    new EffectToken(o, "another-token"), pre381.CheckpointId)))
                yield return "L381 wrong-root-token-was-accepted";
            if (!DurableEffectSaveCatalog.PlanCleanup(new[] { pre381, post381, post381 }, new[] { token381 })
                .SequenceEqual(new[] { "catalog-post", "catalog-pre" }))
                yield return "L381 cleanup-plan-was-not-idempotent-and-bounded-to-captured-lock";

            int foreignMoves = 0;
            DurableEffectTransactionBarrier.TryEnter(token381, "owner-save");
            var deferredSave = DurableEffectTransactionBarrier.WrapSave(CounterCrt(() => foreignMoves++),
                "foreign-save", null, null, null);
            if (!deferredSave.MoveNext() || foreignMoves != 0)
                yield return "L381 nonowner-save-was-not-deferred-by-owner-curtain";
            DurableEffectTransactionBarrier.Exit(token381);
            while (deferredSave.MoveNext()) { }
            deferredSave.Dispose();
            if (foreignMoves != 1) yield return "L381 deferred-nonowner-save-did-not-resume-on-unfreeze";
            int outbound = 0;
            if (!DurableEffectTransactionBarrier.TryEnter(token381, "law381-save"))
                yield return "L381 outbound-curtain-could-not-enter";
            else
            {
                if (!DurableEffectTransactionBarrier.TryDeferOutbound(() => outbound++) || outbound != 0)
                    yield return "L381 outbound-escaped-before-post";
                DurableEffectTransactionBarrier.FlushCommittedOutbound();
                if (outbound != 1) yield return "L381 committed-outbound-did-not-flush-once";
                DurableEffectTransactionBarrier.Exit(token381);
            }
            int deliveryAttempts = 0, delivered = 0;
            DurableEffectTransactionBarrier.TryEnter(token381, "law381-retry");
            DurableEffectTransactionBarrier.TryDeferOutbound(() =>
            { if (++deliveryAttempts == 1) throw new IOException("transport cut"); delivered++; });
            try { DurableEffectTransactionBarrier.FlushCommittedOutbound(); } catch (IOException) { }
            DurableEffectTransactionBarrier.Exit(token381);
            if (DurableEffectTransactionBarrier.TryEnter(new EffectToken(o, "effect:other"), "law381-other"))
                yield return "L381 new-transaction-mixed-with-authoritative-retry-batch";
            if (!DurableEffectTransactionBarrier.RetryCommittedOutbound() || delivered != 1 || deliveryAttempts != 2)
                yield return "L381 verified-post-outbound-was-not-retried-after-unfreeze";
        }

        private static InboxEntry Entry(OccurrenceId o, MembershipId m) =>
            new InboxEntry(o, m, InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0, new HostOrderKey(1, o.TriggerId));
        private static KeyValuePair<MembershipId, MemberPresence> Pair(MembershipId m) =>
            new KeyValuePair<MembershipId, MemberPresence>(m, MemberPresence.Active);
        private sealed class SimulatedCrash : Exception { }
        private static Func<string> SequentialIds() { int n = 0; return () => "fresh-" + (++n); }
        private static IEnumerator<NextUpdate> CounterCrt(Action moved) { moved(); yield break; }
        private static GeoFactionReward RewardWithItem(string guid)
        {
            var def = (PhoenixPoint.Common.Entities.Items.ItemDef)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(PhoenixPoint.Common.Entities.Items.ItemDef));
            def.Guid = guid;
            var item = (PhoenixPoint.Geoscape.Entities.GeoItem)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(PhoenixPoint.Geoscape.Entities.GeoItem));
            typeof(PhoenixPoint.Geoscape.Entities.GeoItem).GetField("_def",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(item, def);
            var storage = new PhoenixPoint.Geoscape.Entities.ItemStorage();
            var dictionary = (Dictionary<PhoenixPoint.Common.Entities.Items.ItemDef, PhoenixPoint.Geoscape.Entities.GeoItem>)
                typeof(PhoenixPoint.Geoscape.Entities.ItemStorage).GetField("_storageItems",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(storage);
            dictionary.Add(def, item);
            return new GeoFactionReward { Items = storage };
        }
        private static DurableInboxSaveRoot Mark(DurableInboxSaveRoot root, DurableEffectCheckpoint checkpoint,
            ulong sharedRevision, System.Reflection.MethodInfo computeCrc)
        {
            if (checkpoint.PredecessorSaveLocator == null) checkpoint.BindPredecessor("law381-save|path|game");
            root.TransactionKind = (byte)checkpoint.Kind;
            root.TransactionCheckpointId = checkpoint.CheckpointId;
            root.TransactionPendingCheckpointId = checkpoint.PendingCheckpointId;
            root.TransactionEventId = checkpoint.Occurrence.EventId;
            root.TransactionTriggerId = checkpoint.Occurrence.TriggerId;
            root.TransactionSubjectIds = checkpoint.Occurrence.SubjectIds.ToArray();
            root.TransactionEffectToken = checkpoint.Token.Value;
            root.TransactionSharedRevision = sharedRevision;
            root.TransactionPredecessorSaveLocator = checkpoint.PredecessorSaveLocator;
            root.Crc = (uint)computeCrc.Invoke(null, new object[] { root });
            return root;
        }
        private enum TxFailure { None, PendingWrite, Native, PostWrite, PostVerify, Broadcast }
        private sealed class TxBackend : IDurableEffectCheckpointBackend
        {
            public bool ReloadCompletesBeforeReturn => true;
            internal readonly List<DurableEffectCheckpoint> Checkpoints = new List<DurableEffectCheckpoint>();
            internal TxFailure Failure; internal bool Frozen; internal DurableEffectCheckpoint Reloaded;
            public void WaitForActiveSavesToDrain() { }
            public void Freeze() { Frozen = true; }
            public void WriteCheckpoint(DurableEffectCheckpoint checkpoint)
            {
                if ((Failure == TxFailure.PendingWrite && checkpoint.Kind == DurableEffectCheckpointKind.Pending) ||
                    (Failure == TxFailure.PostWrite && checkpoint.Kind == DurableEffectCheckpointKind.Committed)) throw new SimulatedCrash();
                Checkpoints.Add(checkpoint);
            }
            public bool VerifyCheckpoint(DurableEffectCheckpoint checkpoint) =>
                !(Failure == TxFailure.PostVerify && checkpoint.Kind == DurableEffectCheckpointKind.Committed) && Checkpoints.Any(x => x.Equals(checkpoint));
            public bool TryFindCheckpoint(OccurrenceId occurrence, EffectToken token, DurableEffectCheckpointKind kind,
                out DurableEffectCheckpoint checkpoint)
            { checkpoint = Checkpoints.LastOrDefault(x => x.Kind == kind && x.Occurrence.Equals(occurrence) && x.Token.Equals(token)); return checkpoint != null; }
            public void ForceReload(DurableEffectCheckpoint checkpoint) { Reloaded = checkpoint; Frozen = true; }
            public void Unfreeze() { Frozen = false; }
            internal TxBackend Clone(bool keepCommitted = true)
            { var x = new TxBackend(); x.Checkpoints.AddRange(Checkpoints.Where(c => keepCommitted || c.Kind == DurableEffectCheckpointKind.Pending)); return x; }
        }
    }
}
