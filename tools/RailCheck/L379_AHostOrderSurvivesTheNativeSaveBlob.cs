using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Base.Serialization.General;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;

namespace RailCheck
{
    /// <summary>L379 / DWI-04 — native Contents + configured save-transfer blob preserve and replay full host order.</summary>
    internal static class L379_AHostOrderSurvivesTheNativeSaveBlob
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("player");
            var first = new OccurrenceId("event", "trigger-b", new[] { "subject" });
            var second = new OccurrenceId("event", "trigger-a", new[] { "subject" });
            var snapshot = new HostLedger(new[]
            {
                new InboxEntry(first, member, InboxLifecycle.Open, default(CanonicalChoiceId), 3, 0, new HostOrderKey(3, first.TriggerId)),
                new InboxEntry(second, member, InboxLifecycle.Queued, default(CanonicalChoiceId), 3, 0, new HostOrderKey(3, second.TriggerId))
            }, 3, new[] { member });
            var store = new DurableInboxStore(snapshot);
            var expected = store.Ledger;
            if (!store.Commit(expected, expected.WithAuthority(4, expected.Members)))
                yield return "L379 journal-setup-commit-failed";
            expected = store.Ledger;
            if (!store.Commit(expected, expected.WithAuthority(5, expected.Members)))
                yield return "L379 second-journal-setup-commit-failed";

            DurableInboxSaveBridge.ActiveStore = store;
            var contents = DurableInboxSaveBridge.Append(new object[] { "native-data" }).ToArray();
            if (contents.OfType<DurableInboxSaveRoot>().Count() != 1 || contents.Count(x => Equals(x, "native-data")) != 1)
                yield return "L379 native-contents-did-not-append-exactly-one-root";
            var root = contents.OfType<DurableInboxSaveRoot>().Single();
            DurableInboxRestore restored; string refusal;
            if (!DurableInboxSaveCodec.TryRestore(root, null, out restored, out refusal))
                yield return "L379 root-refused-its-own-save: " + refusal;
            else
            {
                var order = restored.Ledger.EntriesFor(member).Select(x => x.HostOrderKey).ToArray();
                if (restored.SnapshotLedger.CommittedRevision != 3 || restored.Ledger.CommittedRevision != 5 ||
                    order.Length != 2 || order[0].TriggerId != "trigger-a" || order[1].TriggerId != "trigger-b")
                    yield return "L379 validated-journal-was-not-replayed-over-snapshot";
                if (restored.Journal.Count != 2 || restored.Journal.Any(x => !x.Names(first) || !x.Names(second)))
                    yield return "L379 decoded-journal-lost-occurrence-subject-identities";
            }

            foreach (var failure in PendingBoundaryChecks(store, root)) yield return failure;
            foreach (var failure in CompetingBridgeChecks(store, root)) yield return failure;
            foreach (var failure in NativePipelineChecks(root)) yield return failure;
            foreach (var failure in JournalAdjacencyChecks(snapshot)) yield return failure;

            var corrupt = store.CreateSaveRoot(); corrupt.Snapshot[0] ^= 0xff;
            DurableInboxSaveBridge.ActiveStore = store;
            bool corruptRefused = false;
            try { DurableInboxSaveBridge.Extract(new object[] { corrupt }).ToArray(); }
            catch (InvalidDataException) { corruptRefused = true; }
            if (!corruptRefused) yield return "L379 corrupt-root-did-not-fail-closed";
            if (DurableInboxSaveBridge.ActiveStore != null || DurableInboxSaveBridge.PendingRestore != null)
                yield return "L379 corrupt-extraction-left-stale-active-or-pending-state";

            // POSITIVE CONTROL: scalar ordinal loses the deterministic TriggerId tie-break.
            if (snapshot.AllEntries.Select(x => x.HostOrderKey.CampaignOrdinal).Distinct().Count() != 1 ||
                snapshot.AllEntries.Select(x => x.HostOrderKey).Distinct().Count() != 2)
                yield return "L379 control-not-red: scalar-order-loss-was-not-demonstrated";
        }

        private static IEnumerable<string> PendingBoundaryChecks(DurableInboxStore oldStore, DurableInboxSaveRoot root)
        {
            DurableInboxSaveBridge.ActiveStore = oldStore;
            var nativeOnly = DurableInboxSaveBridge.Extract(new object[] { "native", root }).ToArray();
            if (nativeOnly.Length != 1 || DurableInboxSaveBridge.ActiveStore != null || DurableInboxSaveBridge.PendingRestore == null)
                yield return "L379 set-read-prefix-did-not-stage-without-early-install";
            var resavedWhilePending = DurableInboxSaveBridge.Append(new object[] { "native-again" }).OfType<DurableInboxSaveRoot>().Single();
            if (!ReferenceEquals(resavedWhilePending, root))
                yield return "L379 pending-load-was-replaced-by-empty-root-before-task15-reconciliation";
            var patch = typeof(DurableInboxSetReadObjectsPatch);
            if (AccessTools.Method(patch, "Postfix") != null)
                yield return "L379 set-read-postfix-falsely-claims-level-objects-exist";

            DurableInboxSaveBridge.ActiveStore = oldStore;
            DurableInboxSaveBridge.Extract(new object[] { "pre-dwi-native" }).ToArray();
            if (DurableInboxSaveBridge.ActiveStore != null || DurableInboxSaveBridge.PendingRestore == null ||
                DurableInboxSaveBridge.PendingRestore.Ledger.EntryCount != 0)
                yield return "L379 missing-root-did-not-clear-stale-state-and-stage-empty-ledger";
        }

        private static IEnumerable<string> NativePipelineChecks(DurableInboxSaveRoot root)
        {
            if (typeof(DurableInboxSaveRoot).GetCustomAttributes(typeof(SerializeTypeAttribute), false).Length != 1)
                yield return "L379 root-is-not-a-native-serialize-type";
            var get = AccessTools.Method(typeof(GeoLevelSavegame), nameof(GeoLevelSavegame.GetObjectsToWrite));
            var set = AccessTools.Method(typeof(GeoLevelSavegame), nameof(GeoLevelSavegame.SetReadObjects));
            if (get == null || get.ReturnType != typeof(IEnumerable<object>) || set == null ||
                set.GetParameters().Single().ParameterType != typeof(IEnumerable<object>))
                yield return "L379 grounded-geolevel-save-provider-signatures-drifted";
            if (AccessTools.Method(typeof(DurableInboxGetObjectsToWritePatch), "Postfix") == null ||
                AccessTools.Method(typeof(DurableInboxSetReadObjectsPatch), "Prefix") == null ||
                AccessTools.Method(typeof(DurableInboxNativeSerializer), "RoundTripConfigured") == null)
                yield return "L379 configured-native-provider-or-serializer-seam-is-missing";

            byte[] hostBlob;
            DurableInboxSaveRoot decoded;
            string serializerError = null;
            try { decoded = DurableInboxNativeSerializer.RoundTripConfigured(root, RailMeta.SerializerOverride, out hostBlob, headless: true); }
            catch (Exception ex) { decoded = null; hostBlob = null; serializerError = ex.ToString().Replace('\r', ' ').Replace('\n', ' '); }
            if (serializerError != null)
            { yield return "L379 actual-phoenix-value-serializer-unavailable-headless: " + serializerError; yield break; }
            if (decoded == null ||
                decoded.Magic != root.Magic || decoded.Schema != root.Schema || decoded.SnapshotRevision != root.SnapshotRevision ||
                !decoded.Snapshot.SequenceEqual(root.Snapshot) || decoded.Journal.Length != root.Journal.Length ||
                decoded.Journal.Where((x, i) => !x.SequenceEqual(root.Journal[i])).Any())
                yield return "L379 executable-configured-serializer-or-same-blob-roundtrip-failed";
            var source = DurableInboxSaveBlobTransit.OpenExactReadSavegameBinaryBlob(hostBlob, ".b");
            if (!ReferenceEquals(hostBlob, source.Data) || source.FileExtension != ".b")
                yield return "L379 read-savegame-binary-blob-was-copied-or-reencoded-before-client-level-source";
            var coordinator = typeof(Multiplayer.Network.SaveTransferCoordinator);
            var host = MoveNextOf(coordinator.GetMethod("HostSerializeAndSendCrt", BindingFlags.Instance | BindingFlags.NonPublic));
            var client = MoveNextOf(coordinator.GetMethod("PrepareEntryFromBlobCrt", BindingFlags.Instance | BindingFlags.NonPublic));
            if (!CallsNamed(host, "ReadSavegameBinary") || !CallsNamed(host, "SendBlob") ||
                !Calls(client, AccessTools.Method(typeof(DurableInboxSaveBlobTransit), "OpenExactReadSavegameBinaryBlob")))
                yield return "L379 shipped-read-savegame-binary-to-client-level-source-dataflow-is-broken";
            var corrupt = hostBlob.ToArray(); corrupt[corrupt.Length / 2] ^= 0x5a;
            bool corruptionRefused = false;
            try
            {
                var corruptRoot = DurableInboxNativeSerializer.DeserializeConfigured(corrupt, RailMeta.SerializerOverride, headless: true);
                DurableInboxRestore ignored; string why;
                corruptionRefused = corruptRoot == null || !DurableInboxSaveCodec.TryRestore(corruptRoot, null, out ignored, out why);
            }
            catch { corruptionRefused = true; }
            if (!corruptionRefused) yield return "L379 corrupted-native-serializer-bytes-decoded-as-a-root";
        }

        private static IEnumerable<string> CompetingBridgeChecks(DurableInboxStore store, DurableInboxSaveRoot root)
        {
            DurableInboxSaveBridge.ActiveStore = store;
            var errors = new ConcurrentQueue<Exception>();
            using (var gate = new ManualResetEventSlim(false))
            {
                var tasks = new[]
                {
                    Task.Run(() => Run(() => DurableInboxSaveBridge.Append(new object[] { "native" }).ToArray())),
                    Task.Run(() => Run(() => DurableInboxSaveBridge.Extract(new object[] { "native", root }).ToArray())),
                    Task.Run(() => Run(() => { string why; DurableInboxSaveBridge.ReconcileAndInstall(new AllowAllResolver(), out why); }))
                };
                gate.Set(); Task.WaitAll(tasks);
                void Run(Action action) { try { gate.Wait(); for (int i = 0; i < 32; i++) action(); } catch (Exception ex) { errors.Enqueue(ex); } }
            }
            if (!errors.IsEmpty) yield return "L379 synchronized-bridge-threw-under-append-extract-reconcile-contention";
            DurableInboxSaveBridge.Extract(new object[] { "native", root }).ToArray();
            string finalRefusal;
            bool installed = DurableInboxSaveBridge.ReconcileAndInstall(new AllowAllResolver(), out finalRefusal);
            if (!installed || DurableInboxSaveBridge.PendingRestore == null || DurableInboxSaveBridge.ActiveStore == null ||
                DurableInboxSaveBridge.ActiveStore.Ledger.CommittedRevision != 5)
                yield return "L379 bridge-contention-finished-without-a-coherent-staged-and-active-state";
        }

        private static IEnumerable<string> JournalAdjacencyChecks(HostLedger snapshot)
        {
            var gapLedger = snapshot.WithAuthority(5, snapshot.Members);
            var gapPayload = DurableInboxSaveCodec.EncodeJournalCandidate(gapLedger, DurableInboxCanonicalState.Empty);
            var gapRecord = new DurableInboxJournalRecord(5, gapPayload, gapLedger.AllEntries.Select(x => x.Occurrence));
            var gapRoot = DurableInboxSaveCodec.Create(snapshot, new[] { gapRecord }, DurableInboxCanonicalState.Empty);
            DurableInboxRestore restored; string refusal;
            if (DurableInboxSaveCodec.TryRestore(gapRoot, null, out restored, out refusal))
                yield return "L379 crc-valid-journal-gap-r3-to-r5-was-accepted";

            var maxSnapshot = snapshot.WithAuthority(ulong.MaxValue, snapshot.Members);
            var maxPayload = DurableInboxSaveCodec.EncodeJournalCandidate(maxSnapshot, DurableInboxCanonicalState.Empty);
            var maxRecord = new DurableInboxJournalRecord(ulong.MaxValue, maxPayload,
                maxSnapshot.AllEntries.Select(x => x.Occurrence));
            var overflowRoot = DurableInboxSaveCodec.Create(maxSnapshot, new[] { maxRecord }, DurableInboxCanonicalState.Empty);
            if (DurableInboxSaveCodec.TryRestore(overflowRoot, null, out restored, out refusal))
                yield return "L379 max-revision-journal-overflow-boundary-was-accepted";
        }

        private sealed class AllowAllResolver : IDurableInboxStableResolver
        {
            public bool SubjectExists(string id) => true;
            public bool ChoiceExists(OccurrenceId occurrence, string id) => true;
            public bool ResultExists(OccurrenceId occurrence, string id) => true;
            public bool RewardExists(OccurrenceId occurrence, string subjectId, string id) => true;
        }

        private static MethodBase MoveNextOf(MethodInfo method)
        {
            if (method == null) return null;
            foreach (var type in method.DeclaringType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.IndexOf(method.Name, StringComparison.Ordinal) >= 0)
                    return type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return method;
        }

        private static bool CallsNamed(MethodBase caller, string name) => caller != null &&
            PatchProcessor.GetOriginalInstructions(caller).Any(x => x.operand is MethodBase m && m.Name == name);
        private static bool Calls(MethodBase caller, MethodBase target) => caller != null && target != null &&
            PatchProcessor.GetOriginalInstructions(caller).Any(x => x.operand is MethodBase m &&
                m.MetadataToken == target.MetadataToken && m.Module == target.Module);

    }
}
