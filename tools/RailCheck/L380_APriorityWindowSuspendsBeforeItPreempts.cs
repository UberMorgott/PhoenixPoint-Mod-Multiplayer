using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.View;
using HarmonyLib;
using Base.Core;
using PhoenixPoint.Geoscape.Events;

namespace RailCheck
{
    internal static class L380_APriorityWindowSuspendsBeforeItPreempts
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("reader", 2);
            var ordinary = new OccurrenceId("ordinary", "ordinary-17", new[] { "site" });
            var priority = new OccurrenceId("ambush", "ambush-4", new[] { "mission" });
            var entries = new[] { Entry(ordinary, member, InboxLifecycle.Open, 1),
                                  Entry(priority, member, InboxLifecycle.Queued, 2) };
            var store = Store(entries, member);
            var checkpoint = new InboxWindowCheckpoint("result-phase", "choice-3", "paragraph-2",
                "EV-17", "HOST TITLE", "HOST NARRATIVE", 15, "mission-data");
            var carrier = new Carrier(store, ordinary, checkpoint);
            var engine = new DurableInboxEngine(store, member, carrier);

            if (engine.TryPreempt(ordinary, priority, true, typeof(UIStateResearch)) || carrier.Presented != 0)
                yield return "L380 priority-bypassed-geoscape-gate";
            if (!engine.TryPreempt(ordinary, priority, true, typeof(UIStateNothingSelected)))
                yield return "L380 registered-priority-did-not-preempt-on-map";
            var suspended = store.Ledger.Get(ordinary, member);
            if (suspended.Lifecycle != InboxLifecycle.Suspended ||
                suspended.SuspensionReason != InboxSuspensionReason.PriorityPreemption ||
                !checkpoint.Equals(suspended.Checkpoint) || carrier.CallbackCount != 0 || !carrier.SawDurableBeforePresent)
                yield return "L380 ordinary-was-not-durably-suspended-before-callback-free-replacement";

            HostLedger decoded; string refusal;
            if (!DurableInboxCodec.TryDecodeLedger(store.Ledger.EncodeCanonical(), out decoded, out refusal) ||
                !checkpoint.Equals(decoded.Get(ordinary, member).Checkpoint))
                yield return "L380 suspended-checkpoint-was-not-durable: " + refusal;
            var currentCanonical = legacyCanonical(entries[0], member);
            var oldCanonical = new byte[currentCanonical.Length - 19];
            Buffer.BlockCopy(currentCanonical, 9, oldCanonical, 0, oldCanonical.Length);
            HostLedger oldDecoded; string oldRefusal;
            if (!DurableInboxCodec.TryDecodeLedger(oldCanonical, out oldDecoded, out oldRefusal) ||
                oldDecoded.Get(ordinary, member).Lifecycle != InboxLifecycle.Open)
                yield return "L380 schema1-canonical-ledger-did-not-migrate: " + oldRefusal;
            var collisionEntry = new InboxEntry(ordinary, member, InboxLifecycle.Open, default(CanonicalChoiceId),
                1, 0, new HostOrderKey(1, ordinary.TriggerId));
            var collisionCurrent = new HostLedger(new[] { collisionEntry }, 0x02D2,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) }).EncodeCanonical();
            var collisionLegacy = new byte[collisionCurrent.Length - 19];
            Buffer.BlockCopy(collisionCurrent, 9, collisionLegacy, 0, collisionLegacy.Length);
            HostLedger collisionDecoded; string collisionRefusal;
            if (!DurableInboxCodec.TryDecodeLedger(collisionLegacy, out collisionDecoded, out collisionRefusal) ||
                collisionDecoded.CommittedRevision != 0x02D2)
                yield return "L380 legacy-revision-02d2-collided-with-new-framing: " + collisionRefusal;
            var magicCurrent = new HostLedger(new[] { collisionEntry }, 0x33495744,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) }).EncodeCanonical();
            var magicLegacy = new byte[magicCurrent.Length - 19];
            Buffer.BlockCopy(magicCurrent, 9, magicLegacy, 0, magicLegacy.Length);
            HostLedger magicDecoded; string magicRefusal;
            if (!DurableInboxCodec.TryDecodeLedger(magicLegacy, out magicDecoded, out magicRefusal) ||
                magicDecoded.CommittedRevision != 0x33495744)
                yield return "L380 legacy-revision-equal-to-magic-collided-with-framed-ledger: " + magicRefusal;
            var unknownSchema = (byte[])currentCanonical.Clone(); unknownSchema[4] = 99;
            HostLedger ignoredUnknown; string unknownRefusal;
            if (DurableInboxCodec.TryDecodeLedger(unknownSchema, out ignoredUnknown, out unknownRefusal))
                yield return "L380 unknown-canonical-schema-was-accepted";

            var legacyLedger = new HostLedger(new[] { Entry(ordinary, member, InboxLifecycle.Open, 1) }, 2,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) });
            DurableInboxRestore legacy; string legacyRefusal;
            var legacyRoot = DurableInboxSaveCodec.CreateSchema1ForMigrationTest(legacyLedger);
            if (!DurableInboxSaveCodec.TryRestore(legacyRoot, null, out legacy, out legacyRefusal) ||
                legacy.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Open ||
                legacy.Ledger.Get(ordinary, member).Checkpoint != null)
                yield return "L380 schema1-root-did-not-migrate: " + legacyRefusal;
            var v2 = DurableInboxSaveCodec.Create(store.Ledger, Array.Empty<DurableInboxJournalRecord>(),
                DurableInboxCanonicalState.Empty);
            DurableInboxRestore v2Restore = null; string v2Refusal = null;
            if (v2.Schema != DurableInboxSaveCodec.Schema || !DurableInboxSaveCodec.TryRestore(v2, null, out v2Restore, out v2Refusal) ||
                !checkpoint.Equals(v2Restore.Ledger.Get(ordinary, member).Checkpoint))
                yield return "L380 schema2-checkpoint-root-did-not-roundtrip: " + v2Refusal;

            DismissPriority(store, priority, member);
            if (!engine.TryResumeSuspended(true, typeof(UIStateNothingSelected)) || carrier.CallbackCount != 0 ||
                !checkpoint.Equals(carrier.Restored) || !carrier.RestoredOccurrence.Equals(ordinary) ||
                store.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Open)
                yield return "L380 ordinary-did-not-resume-same-occurrence-phase-selection-and-read-checkpoint";

            var invalidStore = Store(entries, member); var invalidCarrier = new Carrier(invalidStore, ordinary, checkpoint);
            var invalidEngine = new DurableInboxEngine(invalidStore, member, invalidCarrier);
            invalidEngine.TryPreempt(ordinary, priority, true, typeof(UIStateNothingSelected));
            DismissPriority(invalidStore, priority, member); invalidStore.SetUnservable(new[] { ordinary });
            if (invalidEngine.TryResumeSuspended(true, typeof(UIStateNothingSelected)) || invalidCarrier.Restored != null ||
                invalidStore.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Removed)
                yield return "L380 invalidated-suspended-window-resumed";

            if (DurableWindowRegistry.PriorityOf(new OccurrenceId("DeploymentPreparing", "d", new[] { "m" })) != DurableWindowPriority.Deployment ||
                DurableWindowRegistry.PriorityOf(priority) != DurableWindowPriority.Priority ||
                DurableWindowRegistry.PriorityOf(ordinary) != DurableWindowPriority.Ordinary ||
                DurableWindowRegistry.PriorityOf(new OccurrenceId("Modal:" + ModalType.GeoAmbushOutcome,
                    "outcome", new[] { "m" })) != DurableWindowPriority.Ordinary)
                yield return "L380 closed-priority-order-is-not-deployment-then-registered-then-ordinary";
            var nativePaging = EventPopup.NativeCheckpoint(3, true, -1);
            var nativeChoice = EventPopup.NativeCheckpoint(1, false, -1);
            var nativeResult = EventPopup.NativeCheckpoint(2, false, 4);
            if (nativePaging.ContentPhase != "paging" || nativePaging.ReadCheckpoint != "3" ||
                nativeChoice.ContentPhase != "choices" || nativeResult.ContentPhase != "result" ||
                nativeResult.Selection != "4")
                yield return "L380 native-event-checkpoint-lost-phase-selection-or-read-position";
            var nativeModal = GeoModalMirror.CaptureCheckpoint(new UIStateGeoModal(ModalType.GeoAmbushBrief));
            if (nativeModal == null || nativeModal.ContentPhase != "modal" ||
                nativeModal.Selection != ((int)ModalType.GeoAmbushBrief).ToString())
                yield return "L380 native-modal-adapter-did-not-capture-exact-family";

            var failedStore = Store(entries, member); var failedCarrier = new Carrier(failedStore, ordinary, null);
            var failedEngine = new DurableInboxEngine(failedStore, member, failedCarrier);
            if (failedEngine.TryPreempt(ordinary, priority, true, typeof(UIStateNothingSelected)) ||
                failedStore.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Open ||
                failedStore.Ledger.Get(priority, member).Lifecycle != InboxLifecycle.Queued)
                yield return "L380 capture-failure-still-preempted";

            var casStore = Store(entries, member); var casCarrier = new Carrier(casStore, ordinary, checkpoint);
            var casEngine = new DurableInboxEngine(casStore, member, casCarrier);
            casEngine.TryPreempt(ordinary, priority, true, typeof(UIStateNothingSelected));
            DismissPriority(casStore, priority, member);
            casStore.WriteRecord = _ => false;
            if (casEngine.TryResumeSuspended(true, typeof(UIStateNothingSelected)) || casCarrier.Abandoned == 0 ||
                casCarrier.Finalized != 0 || casStore.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Suspended)
                yield return "L380 restore-cas-failure-left-native-open-or-regressed-durable-state";

            var throwStore = Store(entries, member); var throwCarrier = new Carrier(throwStore, ordinary, checkpoint)
                { ThrowPresent = true };
            if (new DurableInboxEngine(throwStore, member, throwCarrier).TryPreempt(ordinary, priority, true,
                    typeof(UIStateNothingSelected)) || throwStore.Ledger.Get(priority, member).Lifecycle != InboxLifecycle.Queued ||
                throwStore.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Open ||
                !checkpoint.Equals(throwCarrier.Restored))
                yield return "L380 throwing-present-did-not-restore-ordinary-and-leave-priority-queued";
            var otherOpen = new OccurrenceId("ordinary", "ordinary-18", new[] { "site-2" });
            var multiEntries = new[] { Entry(ordinary, member, InboxLifecycle.Open, 1),
                Entry(otherOpen, member, InboxLifecycle.Open, 2), Entry(priority, member, InboxLifecycle.Queued, 3) };
            var multiStore = new DurableInboxStore(new HostLedger(multiEntries, 3,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) }));
            var multiCarrier = new Carrier(multiStore, ordinary, checkpoint);
            if (new DurableInboxEngine(multiStore, member, multiCarrier).TryPresentNext(true,
                    typeof(UIStateNothingSelected)) || multiCarrier.Presented != 0)
                yield return "L380 multiple-open-corruption-was-not-refused-before-native-mutation";
            var reentrantStore = Store(entries, member); var reentrantCarrier = new Carrier(reentrantStore, ordinary, checkpoint);
            var reentrantEngine = new DurableInboxEngine(reentrantStore, member, reentrantCarrier);
            reentrantEngine.TryPreempt(ordinary, priority, true, typeof(UIStateNothingSelected));
            DismissPriority(reentrantStore, priority, member);
            reentrantCarrier.OnRestore = () =>
            {
                var expectedReentrant = reentrantStore.Ledger; var currentReentrant = expectedReentrant.Get(ordinary, member);
                ulong rev = currentReentrant.LifecycleRevision + 1;
                var removedReentrant = new InboxEntry(ordinary, member, InboxLifecycle.Removed,
                    currentReentrant.Choice, rev, rev, currentReentrant.HostOrderKey);
                reentrantStore.Commit(expectedReentrant, expectedReentrant.Replace(removedReentrant)
                    .WithAuthority(expectedReentrant.CommittedRevision + 1, expectedReentrant.Members));
            };
            if (reentrantEngine.TryResumeSuspended(true, typeof(UIStateNothingSelected)) ||
                reentrantStore.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Removed)
                yield return "L380 reentrant-remove-during-restore-was-regressed-to-open";
            var presentDismissStore = Store(entries, member);
            var presentDismissCarrier = new Carrier(presentDismissStore, ordinary, checkpoint);
            presentDismissCarrier.OnPresent = () => DismissPriority(presentDismissStore, priority, member);
            if (new DurableInboxEngine(presentDismissStore, member, presentDismissCarrier).TryPreempt(ordinary,
                    priority, true, typeof(UIStateNothingSelected)) ||
                presentDismissStore.Ledger.Get(priority, member).Lifecycle != InboxLifecycle.Dismissed ||
                presentDismissStore.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Open)
                yield return "L380 synchronous-present-dismiss-was-regressed-or-ordinary-not-restored";
            var presentRemoveStore = Store(entries, member);
            var presentRemoveCarrier = new Carrier(presentRemoveStore, ordinary, checkpoint);
            presentRemoveCarrier.OnPresent = () => RemoveOccurrence(presentRemoveStore, ordinary, member);
            if (new DurableInboxEngine(presentRemoveStore, member, presentRemoveCarrier).TryPreempt(ordinary,
                    priority, true, typeof(UIStateNothingSelected)) ||
                presentRemoveStore.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Removed ||
                presentRemoveStore.Ledger.Get(priority, member).Lifecycle != InboxLifecycle.Queued)
                yield return "L380 synchronous-present-remove-was-regressed";
            var ordinaryRequest = new GeoscapeViewStateSwitchRequest(new UIStateGeoModal(ModalType.GeoAmbushOutcome), 99);
            var priorityRequest = new GeoscapeViewStateSwitchRequest(new UIStateGeoModal(ModalType.GeoAmbushBrief), 0);
            WindowOrder.BindDurable(ordinaryRequest, ordinary); WindowOrder.BindDurable(priorityRequest, priority);
            var previousActiveStore = DurableInboxSaveBridge.ActiveStore;
            DurableInboxSaveBridge.ActiveStore = store;
            var nativePending = new List<GeoscapeViewStateSwitchRequest> { ordinaryRequest, priorityRequest };
            if (!ReferenceEquals(WindowOrder.DurablePriorityHead(nativePending), priorityRequest))
                yield return "L380 priority-behind-native-head-was-not-selected-by-global-durable-order";
            var headlessQuery = new GeoscapeViewSwitchQuery(null, null);
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest")
                .SetValue(headlessQuery, ordinaryRequest);
            var headlessPending = (IList<GeoscapeViewStateSwitchRequest>)WindowOrder.RequestsField.GetValue(headlessQuery);
            headlessPending.Add(priorityRequest); int beforeAlreadyCurrent = headlessPending.Count;
            if (!WindowQueueSync.AlreadyCurrent(headlessQuery, ordinaryRequest, ordinary) ||
                !EventPopup.IsExpectedCurrent(headlessQuery, ordinaryRequest) ||
                EventPopup.IsExpectedCurrent(headlessQuery, priorityRequest) ||
                headlessPending.Count != beforeAlreadyCurrent)
                yield return "L380 already-current-failed-present-path-reconstructed-or-duplicated-native-carrier";
            OccurrenceId rebound;
            WindowOrder.BindDurable(ordinaryRequest, otherOpen);
            if (!WindowOrder.TryGetDurable(ordinaryRequest, out rebound) || !rebound.Equals(otherOpen))
                yield return "L380 duplicate-native-binding-did-not-replace-exactly-once";
            var nativeCarrier = typeof(WindowQueueSync).GetNestedType("NativeCarrier",
                System.Reflection.BindingFlags.NonPublic);
            var silentEvent = typeof(WindowQueueSync).GetNestedType("DurableEventSilentExitPatch",
                System.Reflection.BindingFlags.NonPublic);
            var silentModal = typeof(WindowQueueSync).GetNestedType("DurableModalSilentExitPatch",
                System.Reflection.BindingFlags.NonPublic);
            if (nativeCarrier == null || nativeCarrier.GetMethod("Restore") == null ||
                silentEvent?.GetMethod("Finalizer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) == null ||
                silentModal?.GetMethod("Finalizer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) == null)
                yield return "L380 runtime-native-carrier-or-exception-safe-callback-suppression-hook-missing";
            if (!WindowQueueSync.SuppressDurableCallback(true) || WindowQueueSync.SuppressDurableCallback(false))
                yield return "L380 callback-suppression-degenerated-to-a-constant";
            var nativeRecord = new GeoscapeEventRecord("event", default(TimeUnit));
            if (WindowQueueSync.BeginSilentEventExit(nativeRecord, false).HasValue ||
                nativeRecord.State != GeoscapeEventRecordState.Triggered)
                yield return "L380 nonsilent-event-exit-mutated-native-record";
            var savedRecordState = WindowQueueSync.BeginSilentEventExit(nativeRecord, true);
            if (nativeRecord.State != GeoscapeEventRecordState.Completed)
                yield return "L380 silent-event-prefix-did-not-suppress-native-completion-callback";
            try { throw new InvalidOperationException("exercise finalizer"); }
            catch (InvalidOperationException) { }
            finally { WindowQueueSync.EndSilentEventExit(nativeRecord, savedRecordState); }
            if (nativeRecord.State != GeoscapeEventRecordState.Triggered)
                yield return "L380 event-finalizer-did-not-restore-record-after-exception";
            var nativeModalState = new UIStateGeoModal(ModalType.GeoAmbushBrief);
            var handledField = AccessTools.Field(typeof(UIStateGeoModal), "_handledByDialog");
            if (WindowQueueSync.BeginSilentModalExit(nativeModalState, false).HasValue ||
                (bool)handledField.GetValue(nativeModalState))
                yield return "L380 nonsilent-modal-exit-mutated-handler-state";
            var savedHandled = WindowQueueSync.BeginSilentModalExit(nativeModalState, true);
            if (!(bool)handledField.GetValue(nativeModalState))
                yield return "L380 silent-modal-prefix-did-not-suppress-dialog-callback";
            try { throw new InvalidOperationException("exercise modal finalizer"); }
            catch (InvalidOperationException) { }
            finally { WindowQueueSync.EndSilentModalExit(nativeModalState, savedHandled); }
            if ((bool)handledField.GetValue(nativeModalState))
                yield return "L380 modal-finalizer-did-not-restore-handler-after-exception";
            var modalOccurrence = new OccurrenceId("Modal:" + ModalType.GeoAmbushBrief, "modal-trigger",
                new[] { "mission-stable" });
            if (DurableWindowRegistry.MatchPriorityOccurrence(new DurableInboxStore(new HostLedger(
                    Array.Empty<InboxEntry>(), 1, new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) })),
                    modalOccurrence.EventId, modalOccurrence.TriggerId, "mission-stable").HasValue)
                yield return "L380 request-before-ledger-priority-modal-was-not-parked";
            var modalStore = new DurableInboxStore(new HostLedger(new[] { new InboxEntry(modalOccurrence, member,
                InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0,
                new HostOrderKey(1, modalOccurrence.TriggerId)) }, 1,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) }));
            if (!DurableWindowRegistry.MatchPriorityOccurrence(modalStore, modalOccurrence.EventId,
                    modalOccurrence.TriggerId, "mission-stable").HasValue ||
                DurableWindowRegistry.MatchPriorityOccurrence(modalStore, modalOccurrence.EventId,
                    modalOccurrence.TriggerId, "wrong-subject").HasValue)
                yield return "L380 ledger-before-request-modal-did-not-match-exact-family-trigger-and-subject";
            DurableInboxSaveBridge.ActiveStore = previousActiveStore;

            // POSITIVE CONTROL: any answer/dismiss callback here would make preemption authoritative locally.
            if (carrier.CallbackCount != 0) yield return "L380 control-not-red: callback-ran";
        }

        private static DurableInboxStore Store(InboxEntry[] entries, MembershipId member) =>
            new DurableInboxStore(new HostLedger(entries, 2,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) }));
        private static byte[] legacyCanonical(InboxEntry entry, MembershipId member) =>
            new HostLedger(new[] { entry }, 2,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) }).EncodeCanonical();
        private static InboxEntry Entry(OccurrenceId o, MembershipId m, InboxLifecycle lifecycle, ulong ordinal) =>
            new InboxEntry(o, m, lifecycle, default(CanonicalChoiceId), 1, 0, new HostOrderKey(ordinal, o.TriggerId));
        private static void DismissPriority(DurableInboxStore store, OccurrenceId occurrence, MembershipId member)
        {
            var expected = store.Ledger; var current = expected.Get(occurrence, member);
            var next = expected.Replace(current.WithLifecycle(InboxLifecycle.Dismissed, current.LifecycleRevision + 1))
                .WithAuthority(expected.CommittedRevision + 1, expected.Members);
            if (!store.Commit(expected, next)) throw new InvalidOperationException("law dismissal failed");
        }
        private static void RemoveOccurrence(DurableInboxStore store, OccurrenceId occurrence, MembershipId member)
        {
            var expected = store.Ledger; var current = expected.Get(occurrence, member);
            ulong rev = current.LifecycleRevision + 1;
            var removed = new InboxEntry(occurrence, member, InboxLifecycle.Removed, current.Choice, rev, rev,
                current.HostOrderKey);
            if (!store.Commit(expected, expected.Replace(removed)
                .WithAuthority(expected.CommittedRevision + 1, expected.Members)))
                throw new InvalidOperationException("law removal failed");
        }

        private sealed class Carrier : IDurableWindowCarrierAdapter
        {
            private readonly DurableInboxStore _store; private readonly OccurrenceId _ordinary;
            private readonly InboxWindowCheckpoint _captured;
            internal Carrier(DurableInboxStore store, OccurrenceId ordinary, InboxWindowCheckpoint captured)
            { _store = store; _ordinary = ordinary; _captured = captured; }
            internal int Presented; internal int CallbackCount => 0; internal bool SawDurableBeforePresent;
            internal InboxWindowCheckpoint Restored; internal OccurrenceId RestoredOccurrence;
            internal bool ThrowPresent; internal int Abandoned, Finalized;
            internal Action OnRestore;
            internal Action OnPresent;
            public InboxWindowCheckpoint Capture(OccurrenceId occurrence) => _captured;
            public bool Present(OccurrenceId occurrence)
            { if (ThrowPresent) throw new InvalidOperationException("mutation"); Presented++; SawDurableBeforePresent = _store.Ledger.Get(_ordinary, _store.Ledger.AllEntries[0].Membership)
                    .Lifecycle == InboxLifecycle.Suspended; OnPresent?.Invoke(); return true; }
            public bool Restore(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint)
            { RestoredOccurrence = occurrence; Restored = checkpoint; OnRestore?.Invoke(); return true; }
            public void Abandon(OccurrenceId occurrence) { Abandoned++; }
            public void FinalizeRestore(OccurrenceId occurrence) { Finalized++; }
        }
    }
}
