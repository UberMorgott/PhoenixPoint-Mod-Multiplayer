using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L395 / DWI-20 — one committed terminal boundary removes every carrier without callbacks.</summary>
    internal static class L395_TerminalRemovalReachesEveryCarrier
    {
        internal static IEnumerable<string> Check()
        {
            var memberA = new MembershipId("a", 1); var memberB = new MembershipId("b", 2);
            var occurrence = new OccurrenceId("mission", "offer-7", new[] { "site" });
            var order = new HostOrderKey(4, occurrence.TriggerId);
            InboxEntry Entry(MembershipId member, InboxLifecycle lifecycle, ulong tombstone) =>
                new InboxEntry(occurrence, member, lifecycle, default(CanonicalChoiceId), 5, tombstone, order,
                    terminalReason: tombstone == 0 ? (TerminalReason?)null : TerminalReason.Invalidated);

            var live = new DurableInboxStore(new HostLedger(new[]
            { Entry(memberA, InboxLifecycle.Open, 0), Entry(memberB, InboxLifecycle.Read, 0) }, 5));
            var refusedRegistry = new DurableCarrierRegistry();
            var untouched = new FakeCarrier();
            refusedRegistry.Register(occurrence, DurableCarrierClass.NativeCurrent, untouched);
            refusedRegistry.Register(occurrence, DurableCarrierClass.NativePending, untouched);
            if (refusedRegistry.Count(occurrence) != 1)
                yield return "L395 repeated-bind-created-a-second-wrapper-instead-of-transitioning-role";
            var normallyCompleted = new FakeCarrier();
            refusedRegistry.Register(occurrence, DurableCarrierClass.ModQueued, normallyCompleted);
            refusedRegistry.Unregister(occurrence, normallyCompleted);
            if (refusedRegistry.Count(occurrence) != 1 || normallyCompleted.Removed != 0)
                yield return "L395 normal-completion-did-not-unregister-without-terminal-teardown";
            string refusal;
            if (new DurableInboxEngine(live, memberA, untouched, refusedRegistry)
                    .RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal) ||
                string.IsNullOrEmpty(refusal) || untouched.Removed != 0)
                yield return "L395 removed-before-terminal-commit";

            var terminal = new DurableInboxStore(new HostLedger(new[]
            { Entry(memberA, InboxLifecycle.Removed, 5), Entry(memberB, InboxLifecycle.Removed, 5) }, 8));
            HostLedger roundTrip; string roundTripRefusal;
            if (!DurableInboxCodec.TryDecodeLedger(terminal.Ledger.EncodeCanonical(), out roundTrip,
                    out roundTripRefusal) || roundTrip.AllEntries.Any(x =>
                    x.TerminalReason != TerminalReason.Invalidated || x.TombstoneRevision != 5))
                yield return "L395 terminal-reason-did-not-survive-ledger-roundtrip";
            DurableInboxRestore saveRestore;
            if (!DurableInboxSaveCodec.TryRestore(terminal.CreateSaveRoot(), null, out saveRestore,
                    out roundTripRefusal) || saveRestore.Ledger.AllEntries.Any(x =>
                    x.TerminalReason != TerminalReason.Invalidated || x.TombstoneRevision != 5))
                yield return "L395 terminal-reason-did-not-survive-save-roundtrip";
            var classes = Enum.GetValues(typeof(DurableCarrierClass)).Cast<DurableCarrierClass>().ToArray();
            var callbacks = new int[classes.Length];
            Action<TerminalReason> Remove(DurableCarrierClass kind) => _ => callbacks[Array.IndexOf(classes, kind)]++;
            var leases = new[]
            {
                WindowQueueSync.BindNativeCurrentCarrier(terminal, occurrence, Remove(DurableCarrierClass.NativeCurrent)),
                WindowQueueSync.BindNativePendingCarrier(terminal, occurrence, Remove(DurableCarrierClass.NativePending)),
                GeoModalMirror.BindQueuedCarrier(terminal, occurrence, Remove(DurableCarrierClass.ModQueued)),
                WindowQueueSync.BindSuspendedCarrier(terminal, occurrence, Remove(DurableCarrierClass.ModSuspended)),
                EventPopup.BindDeferredCarrier(terminal, occurrence, Remove(DurableCarrierClass.ModDeferred)),
                CutsceneMirror.BindReplayCarrier(terminal, occurrence, Remove(DurableCarrierClass.WireReplay)),
                MissionOutcomeMirror.BindTacticalHeldCarrier(terminal, occurrence, Remove(DurableCarrierClass.TacticalHeld)),
                DeploymentWindowClose.BindDeploymentCarrier(terminal, occurrence, Remove(DurableCarrierClass.Deployment)),
            };
            var otherOccurrence = new OccurrenceId("event", "other", new[] { "other-subject" });
            var otherStore = new DurableInboxStore(new HostLedger(new[] { new InboxEntry(otherOccurrence, memberA,
                InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0, new HostOrderKey(1, otherOccurrence.TriggerId)) }, 8));
            int otherRemoved = 0;
            var other = WindowQueueSync.BindNativePendingCarrier(otherStore, otherOccurrence, _ => otherRemoved++);
            var completed = EventPopup.BindDeferredCarrier(otherStore, otherOccurrence, _ => otherRemoved++);
            completed.Dispose();
            if (otherStore.Carriers.Count(otherOccurrence) != 1 || otherRemoved != 0)
                yield return "L395 production-normal-completion-did-not-unregister-silently";
            bool rejectedUnknown = false;
            try { EventPopup.BindDeferredCarrier(terminal, otherOccurrence, _ => otherRemoved++); }
            catch (InvalidOperationException) { rejectedUnknown = true; }
            if (!rejectedUnknown) yield return "L395 family-binding-accepted-a-non-authoritative-occurrence";
            var engine = new DurableInboxEngine(terminal, memberA, new FakeCarrier());
            if (engine.RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 8, out refusal))
                yield return "L395 unrelated-global-revision-authorized-removal";
            if (!engine.RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal) || refusal != null)
                yield return "L395 committed-terminal-removal-refused";
            if (callbacks.Any(c => c != 1))
                yield return "L395 not-every-carrier-was-silently-removed";
            if (otherRemoved != 0 || otherStore.Carriers.Count(otherOccurrence) != 1)
                yield return "L395 unrelated-occurrence-was-removed";
            if (!engine.RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal) ||
                callbacks.Any(c => c != 1))
                yield return "L395 removal-was-not-idempotent";
            if (engine.RemoveAllCarriers(occurrence, TerminalReason.Superseded, 5, out refusal) ||
                string.IsNullOrEmpty(refusal))
                yield return "L395 terminal-retry-accepted-a-different-reason";

            var legacyUnknownLedger = new HostLedger(new[]
            {
                new InboxEntry(occurrence, memberA, InboxLifecycle.Removed, default(CanonicalChoiceId),
                    5, 5, order),
                new InboxEntry(occurrence, memberB, InboxLifecycle.Removed, default(CanonicalChoiceId),
                    5, 5, order)
            }, 8);
            DurableInboxRestore legacyUnknownRestore;
            if (!DurableInboxSaveCodec.TryRestore(
                    DurableInboxSaveCodec.CreateSchema1ForMigrationTest(legacyUnknownLedger), null,
                    out legacyUnknownRestore, out refusal))
            {
                yield return "L395 legacy-terminal-migration-refused: " + refusal;
                yield break;
            }
            var legacyUnknown = new DurableInboxStore(legacyUnknownRestore.Ledger);
            if (new DurableInboxEngine(legacyUnknown, memberA, new FakeCarrier()).RemoveAllCarriers(
                    occurrence, TerminalReason.Invalidated, 5, out refusal) || string.IsNullOrEmpty(refusal))
                yield return "L395 legacy-terminal-without-reason-authorized-teardown";

            var retryRegistry = new DurableCarrierRegistry();
            var flaky = new FakeCarrier { ThrowsRemaining = 1 }; var survivor = new FakeCarrier();
            retryRegistry.Register(occurrence, DurableCarrierClass.WireReplay, flaky);
            retryRegistry.Register(occurrence, DurableCarrierClass.TacticalHeld, survivor);
            retryRegistry.Register(occurrence, DurableCarrierClass.TacticalHeld, survivor);
            var retryEngine = new DurableInboxEngine(terminal, memberA, survivor, retryRegistry);
            if (retryEngine.RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal) ||
                string.IsNullOrEmpty(refusal) || survivor.Removed != 1 || retryRegistry.Count(occurrence) != 1)
                yield return "L395 one-refusal-stopped-other-carriers-or-lost-retry";
            if (!retryEngine.RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal) ||
                flaky.Removed != 1 || retryRegistry.Count(occurrence) != 0)
                yield return "L395 refused-carrier-was-not-retryable";

            // POSITIVE CONTROL: deleting only the native current carrier leaves every other class alive.
            var control = new int[classes.Length];
            // The mutation deletes only the current native carrier, leaving all seven real family seams live.
            control[Array.IndexOf(classes, DurableCarrierClass.NativeCurrent)]++;
            if (control.Count(c => c == 0) != classes.Length - 1)
                yield return "L395 control-not-red: current-only deletion did not leave carriers behind";

            foreach (var failure in SealRace(terminal, memberA, occurrence)) yield return failure;
            foreach (var failure in InFlightSealBarrier(terminal, memberA, occurrence)) yield return failure;

            var leaseRetryStore = new DurableInboxStore(terminal.Ledger);
            int leaseAttempts = 0;
            CutsceneMirror.BindReplayCarrier(leaseRetryStore, occurrence, _ =>
            { if (++leaseAttempts == 1) throw new InvalidOperationException("lease retry injection"); });
            var leaseRetryEngine = new DurableInboxEngine(leaseRetryStore, memberA, new FakeCarrier());
            if (leaseRetryEngine.RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal) ||
                leaseAttempts != 1 || leaseRetryStore.Carriers.Count(occurrence) != 1 ||
                !leaseRetryEngine.RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal) ||
                leaseAttempts != 2 || leaseRetryStore.Carriers.Count(occurrence) != 0)
                yield return "L395 real-family-lease-throw-was-not-retryable";

            var swapOccurrence = new OccurrenceId("event", "swap", new[] { "subject" });
            var swapEntry = new InboxEntry(swapOccurrence, memberA, InboxLifecycle.Queued,
                default(CanonicalChoiceId), 1, 0, new HostOrderKey(1, swapOccurrence.TriggerId));
            var oldStore = new DurableInboxStore(new HostLedger(new[] { swapEntry }, 1));
            int swapRemoved = 0;
            EventPopup.BindDeferredCarrier(oldStore, swapOccurrence, _ => swapRemoved++);
            DurableInboxSaveBridge.ActiveStore = oldStore;
            DurableInboxSaveBridge.ActiveStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>()));
            if (swapRemoved != 1 || oldStore.Carriers.Count(swapOccurrence) != 0)
                yield return "L395 store-swap-retained-old-carrier-or-lease";
            var extractStore = new DurableInboxStore(new HostLedger(new[] { swapEntry }, 1)); int extractRemoved = 0;
            DurableInboxSaveBridge.ActiveStore = extractStore;
            EventPopup.BindDeferredCarrier(extractStore, swapOccurrence, _ => extractRemoved++);
            DurableInboxSaveBridge.Extract(Array.Empty<object>()).ToArray();
            if (extractRemoved != 1 || extractStore.Carriers.Count(swapOccurrence) != 0 ||
                DurableInboxSaveBridge.ActiveStore != null)
                yield return "L395 extract-retained-old-store-carriers";

            DurableInboxSaveBridge.Extract(new object[] { terminal.CreateSaveRoot() }).ToArray();
            var reconcileOld = new DurableInboxStore(new HostLedger(new[] { swapEntry }, 1)); int reconcileRemoved = 0;
            DurableInboxSaveBridge.ActiveStore = reconcileOld;
            EventPopup.BindDeferredCarrier(reconcileOld, swapOccurrence, _ => reconcileRemoved++);
            string reconcileRefusal;
            if (!DurableInboxSaveBridge.ReconcileAndInstall(new Resolver(), out reconcileRefusal) ||
                reconcileRemoved != 1 || reconcileOld.Carriers.Count(swapOccurrence) != 0 ||
                ReferenceEquals(DurableInboxSaveBridge.ActiveStore, reconcileOld))
                yield return "L395 reconcile-retained-old-store-carriers: " + reconcileRefusal;
            DurableInboxSaveBridge.ActiveStore = null;
            foreach (var failure in StoreAbandonBarrier(memberA)) yield return failure;
        }

        private static IEnumerable<string> StoreAbandonBarrier(MembershipId member)
        {
            var occurrence = new OccurrenceId("event", "abandon", new[] { "subject" });
            var entry = new InboxEntry(occurrence, member, InboxLifecycle.Queued,
                default(CanonicalChoiceId), 1, 0, new HostOrderKey(1, occurrence.TriggerId));
            var oldStore = new DurableInboxStore(new HostLedger(new[] { entry }, 1));
            DurableInboxSaveBridge.ActiveStore = oldStore;
            int firstRemoved = 0, lateRemoved = 0, futureRemoved = 0;
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                EventPopup.BindDeferredCarrier(oldStore, occurrence, _ =>
                { Interlocked.Increment(ref firstRemoved); entered.Set(); release.Wait(); });
                var replacement = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>()));
                var swap = Task.Run(() => DurableInboxSaveBridge.ActiveStore = replacement);
                if (!entered.Wait(5000)) { release.Set(); yield return "L395 abandoned-store-cleanup-never-entered"; yield break; }
                var late = Task.Run(() => CutsceneMirror.BindReplayCarrier(oldStore, occurrence,
                    _ => Interlocked.Increment(ref lateRemoved)));
                if (!late.Wait(5000)) { release.Set(); yield return "L395 late-bind-to-abandoned-store-did-not-finish"; yield break; }
                if (swap.Wait(100)) yield return "L395 store-swap-returned-before-preexisting-removal-finished";
                release.Set(); swap.Wait();
                CutsceneMirror.BindReplayCarrier(oldStore, occurrence,
                    _ => Interlocked.Increment(ref futureRemoved));
                if (firstRemoved != 1 || lateRemoved != 1 || futureRemoved != 1 ||
                    oldStore.Carriers.Count(occurrence) != 0)
                    yield return "L395 abandoned-store-admitted-late-or-future-carrier";
            }
            DurableInboxSaveBridge.ActiveStore = null;
        }

        private static IEnumerable<string> InFlightSealBarrier(DurableInboxStore store, MembershipId member,
            OccurrenceId occurrence)
        {
            var registry = new DurableCarrierRegistry(); var initial = new FakeCarrier();
            using (var initialEntered = new ManualResetEventSlim(false))
            using (var releaseInitial = new ManualResetEventSlim(false))
            using (var lateEntered = new ManualResetEventSlim(false))
            using (var releaseLate = new ManualResetEventSlim(false))
            {
                initial.OnRemove = () => { initialEntered.Set(); releaseInitial.Wait(); };
                registry.Register(occurrence, DurableCarrierClass.ModQueued, initial);
                bool result = false; string refusal = null;
                var removeTask = Task.Run(() => result = new DurableInboxEngine(store, member, initial, registry)
                    .RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal));
                if (!initialEntered.Wait(5000)) { yield return "L395 seal-barrier-never-entered-initial-remove"; yield break; }
                var late = new FakeCarrier { OnRemove = () => { lateEntered.Set(); releaseLate.Wait(); } };
                var registerTask = Task.Run(() => registry.Register(occurrence, DurableCarrierClass.WireReplay, late));
                if (!lateEntered.Wait(5000)) { releaseInitial.Set(); yield return "L395 late-carrier-did-not-enter-sealed-remove"; yield break; }
                releaseInitial.Set();
                if (removeTask.Wait(100)) yield return "L395 remove-returned-before-in-flight-late-carrier-finished";
                releaseLate.Set(); Task.WaitAll(removeTask, registerTask);
                if (!result || refusal != null || late.Removed != 1 || registry.Count(occurrence) != 0)
                    yield return "L395 in-flight-seal-barrier-did-not-drain-to-stable";
            }
        }

        private static IEnumerable<string> SealRace(DurableInboxStore store, MembershipId member,
            OccurrenceId occurrence)
        {
            var registry = new DurableCarrierRegistry(); var first = new FakeCarrier();
            var late = new FakeCarrier(); var reentrant = new FakeCarrier();
            registry.Register(occurrence, DurableCarrierClass.ModQueued, first);
            first.OnRemove = () => registry.Register(occurrence, DurableCarrierClass.ModDeferred, reentrant);
            var errors = new ConcurrentQueue<Exception>(); string refusal = null; bool removed = false;
            using (var gate = new ManualResetEventSlim(false))
            {
                var remover = Task.Run(() => { try { gate.Wait(); removed = new DurableInboxEngine(store, member,
                    first, registry).RemoveAllCarriers(occurrence, TerminalReason.Invalidated, 5, out refusal); }
                    catch (Exception ex) { errors.Enqueue(ex); } });
                var registrar = Task.Run(() => { try { gate.Wait(); registry.Register(occurrence,
                    DurableCarrierClass.TacticalHeld, late); } catch (Exception ex) { errors.Enqueue(ex); } });
                gate.Set(); Task.WaitAll(remover, registrar);
            }
            if (!errors.IsEmpty || !removed || refusal != null || first.Removed != 1 || late.Removed != 1 ||
                reentrant.Removed != 1 || registry.Count(occurrence) != 0)
                yield return "L395 terminal-seal-did-not-drain-late-reentrant-concurrent-carriers";
        }

        private sealed class FakeCarrier : IDurableWindowCarrierAdapter, IDurableOccurrenceCarrier
        {
            internal int Removed, Callbacks, ThrowsRemaining;
            internal Action OnRemove;
            public void RemoveWithoutCallback(TerminalReason reason)
            {
                if (ThrowsRemaining-- > 0) throw new InvalidOperationException("injected refusal");
                Removed++;
                OnRemove?.Invoke();
            }
            public InboxWindowCheckpoint Capture(OccurrenceId occurrence) => null;
            public bool Present(OccurrenceId occurrence) { Callbacks++; return false; }
            public bool Restore(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint) { Callbacks++; return false; }
            public void Abandon(OccurrenceId occurrence) { Callbacks++; }
            public void FinalizeRestore(OccurrenceId occurrence) { Callbacks++; }
        }

        private sealed class Resolver : IDurableInboxStableResolver
        {
            public bool SubjectExists(string subjectId) => true;
            public bool ChoiceExists(OccurrenceId occurrence, string choiceId) => true;
            public bool ResultExists(OccurrenceId occurrence, string resultId) => true;
            public bool RewardExists(OccurrenceId occurrence, string subjectId, string rewardId) => true;
        }
    }
}
