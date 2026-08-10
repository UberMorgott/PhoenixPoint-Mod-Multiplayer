using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    internal static class L388_ADeparturePrunesBeforeItInvalidates
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class Carrier : IDurableOccurrenceCarrier
        {
            internal int Removed;
            internal TerminalReason? Reason;
            public void RemoveWithoutCallback(TerminalReason reason) { Removed++; Reason = reason; }
        }
        private sealed class ThrowOnceCarrier : IDurableOccurrenceCarrier
        {
            internal int Calls;
            public void RemoveWithoutCallback(TerminalReason reason)
            { Calls++; if (Calls == 1) throw new InvalidOperationException("transient teardown"); }
        }

        internal static IEnumerable<string> Check()
        {
            var a = new MembershipId("a", 1); var b = new MembershipId("b", 1);
            var c = new MembershipId("c", 1); var d = new MembershipId("d", 1);
            var occurrence = new OccurrenceId("DeploymentPreparing", "depart", new[] { "S#7", "M#7" });
            var order = new HostOrderKey(1, occurrence.TriggerId);
            var checkpoint = new InboxWindowCheckpoint("deployment", "U#1", "read");
            var members = new[] {
                new KeyValuePair<MembershipId, MemberPresence>(a, MemberPresence.Active),
                new KeyValuePair<MembershipId, MemberPresence>(b, MemberPresence.Active),
                new KeyValuePair<MembershipId, MemberPresence>(c, MemberPresence.NonGeoscape),
                new KeyValuePair<MembershipId, MemberPresence>(d, MemberPresence.NonGeoscape) };
            DurableInboxStore Make() => new DurableInboxStore(new HostLedger(new[] {
                new InboxEntry(occurrence, a, InboxLifecycle.Open, default(CanonicalChoiceId), 2, 0, order),
                new InboxEntry(occurrence, b, InboxLifecycle.Open, default(CanonicalChoiceId), 2, 0, order),
                new InboxEntry(occurrence, c, InboxLifecycle.Suspended, default(CanonicalChoiceId), 2, 0, order,
                    InboxSuspensionReason.PriorityPreemption, checkpoint),
                new InboxEntry(occurrence, d, InboxLifecycle.Deferred, default(CanonicalChoiceId), 2, 0, order)
            }, 1, members));
            var before = new DeploymentSourceSnapshot(new Dictionary<string, IEnumerable<string>> {
                ["V#1"] = new[] { "U#1", "U#2" }, ["V#2"] = new[] { "U#3" } });
            var after = new DeploymentSourceSnapshot(new Dictionary<string, IEnumerable<string>> {
                ["V#2"] = new[] { "U#3" } });

            var store = Make(); var repainted = new List<MembershipId>(); string refusal; ulong terminal;
            var engine = new DurableSourceRevalidationEngine(store, (o, m) => repainted.Add(m));
            if (!engine.TryRevalidate(occurrence, "V#1", before, after, false, out terminal, out refusal) ||
                terminal != 0 || refusal != null || store.Ledger.CommittedRevision != 2 ||
                repainted.Count != 2 || !repainted.Contains(a) || !repainted.Contains(b) ||
                store.Ledger.Get(occurrence, a).Lifecycle != InboxLifecycle.Open ||
                store.Ledger.Get(occurrence, b).Lifecycle != InboxLifecycle.Open ||
                store.Ledger.Get(occurrence, c).Lifecycle != InboxLifecycle.Suspended ||
                !checkpoint.Equals(store.Ledger.Get(occurrence, c).Checkpoint) ||
                store.Ledger.Get(occurrence, d).Lifecycle != InboxLifecycle.Queued ||
                store.Ledger.AllEntries.Any(x => x.PreparationRevision != 1 || x.Lifecycle == InboxLifecycle.Removed))
                yield return "L388 partial-departure-did-not-update-every-copy-repaint-every-open-and-retain-occurrence";

            var unrelated = Make(); int unrelatedRepaints = 0;
            if (new DurableSourceRevalidationEngine(unrelated, (o, m) => unrelatedRepaints++)
                    .TryRevalidate(occurrence, "V#9", before, after, false, out terminal, out refusal) ||
                unrelated.Ledger.CommittedRevision != 1 || unrelatedRepaints != 0)
                yield return "L388 unrelated-departure-mutated-the-occurrence";

            // POSITIVE CONTROL: a claimed departure whose source is still in the authoritative after-set
            // must be actively refused. This catches a wholesale "every travel is accepted" adapter.
            var control = Make();
            if (new DurableSourceRevalidationEngine(control, (o, m) => { })
                    .TryRevalidate(occurrence, "V#1", before, before, false, out terminal, out refusal) ||
                string.IsNullOrEmpty(refusal) || control.Ledger.CommittedRevision != 1)
                yield return "L388 control-not-red: still-present-source-was-accepted-as-departed";

            foreach (var unique in new[] { false, true })
            {
                var terminalStore = Make(); var carrier = new Carrier();
                terminalStore.Carriers.Register(occurrence, DurableCarrierClass.NativeCurrent, carrier);
                var remaining = unique ? after : new DeploymentSourceSnapshot(
                    new Dictionary<string, IEnumerable<string>>());
                var departed = unique ? "V#1" : "V#2";
                var terminalBefore = unique ? before : after;
                var terminalEngine = new DurableSourceRevalidationEngine(terminalStore, (o, m) => { });
                if (!terminalEngine.TryRevalidate(occurrence, departed, terminalBefore, remaining, unique,
                        out terminal, out refusal) || terminal != 2 || refusal != null ||
                    terminalStore.Ledger.AllEntries.Any(x => x.Lifecycle != InboxLifecycle.Removed ||
                        x.TerminalReason != TerminalReason.SourceInvalidated || x.TombstoneRevision != terminal) ||
                    carrier.Removed != 1 || carrier.Reason != TerminalReason.SourceInvalidated)
                    yield return unique ? "L388 uniquely-bound-source-was-not-tombstoned-before-silent-removal" :
                        "L388 last-source-was-not-tombstoned-before-silent-removal";
            }

            var sourceEngine = typeof(DurableSourceRevalidationEngine);
            var revalidate = sourceEngine.GetMethod("TryRevalidateBatch", All);
            var remove = typeof(DurableInboxEngine).GetMethod("RemoveAllCarriers", All);
            var travel = typeof(VehicleSync).GetNestedType("TravelCapturePatch", All);
            var repaint = typeof(TravelRepaintPatch).GetMethod("Postfix", All);
            var rail = typeof(GenericApplier).GetMethod("FlushDepartureRevalidation", All);
            var capture = typeof(MissionSync).GetMethod("CaptureVehicleDeparture", All);
            var commit = typeof(MissionSync).GetMethod("CommitCapturedVehicleDeparture", All);
            var applyScheduled = typeof(MissionSync).GetMethod("ApplyScheduledSourceRevalidationDeltas", All);
            var install = typeof(DurableInboxStore).GetMethod("InstallSourceRevalidationBatch", All);
            var fireSource = typeof(UiEventMap).GetMethod("FirePreparationEdit", All);
            var repaintSource = typeof(MissionSync).GetMethod("RepaintSourceRevalidation", All);
            var hasSettled = typeof(GenericApplier).GetMethod("HasSettledDeparture", All);
            var markSettled = typeof(GenericApplier).GetMethod("MarkDepartureWatermarksSettled", All);
            var travelPrefix = travel?.GetMethod("Prefix", All);
            var travelFinalizer = travel?.GetMethod("Finalizer", All);
            var abandonCapture = typeof(MissionSync).GetMethod("AbandonCapturedVehicleDeparture", All);
            var advanceGeneration = typeof(DepartureGenerationRail).GetMethod("TryAdvance", All);
            var generationSnapshot = typeof(DepartureGenerationRail).GetMethod("Snapshot", All);
            var diffEmit = typeof(DiffEngine).GetMethod("Emit", All);
            var applyGenerationTail = typeof(GenericApplier).GetMethod("ApplyDepartureGenerationTail", All);
            var installGeneration = typeof(GenericApplier).GetMethod("InstallDepartureGeneration", All);
            var uniqueField = typeof(PhoenixPoint.Common.Levels.Missions.TacMissionTypeDef)
                .GetField("DeployOnlyFromActiveVehicle", All);
            var uniqueBinding = typeof(MissionSync).GetMethod("UniqueSourceBinding", All);
            if (revalidate == null || remove == null || travel == null || repaint == null || rail == null ||
                capture == null || commit == null || travelPrefix == null || applyScheduled == null || install == null || fireSource == null || repaintSource == null ||
                hasSettled == null || markSettled == null || travelFinalizer == null || abandonCapture == null ||
                advanceGeneration == null || generationSnapshot == null || diffEmit == null ||
                applyGenerationTail == null || installGeneration == null ||
                !UniqueControl(uniqueField, uniqueBinding) || !Calls(capture, uniqueBinding) ||
                !Calls(capture, typeof(DeploymentSourceSnapshot).GetMethod("ContainsSource", All)) ||
                !Calls(revalidate, typeof(DurableSourceRevalidationEngine).GetMethod("RetryTerminalTeardown", All)) ||
                !Calls(typeof(DurableSourceRevalidationEngine).GetMethod("RetryTerminalTeardown", All), remove) ||
                !Calls(travelPrefix, capture) || !Calls(repaint, commit) || !Calls(commit, revalidate) ||
                Calls(rail, revalidate) || !Calls(rail, applyScheduled) || !Calls(applyScheduled, install) ||
                !Calls(applyScheduled, repaintSource) || !Calls(repaintSource, fireSource) ||
                !Calls(applyScheduled, hasSettled) || !Calls(commit, advanceGeneration) ||
                !Calls(travelFinalizer, abandonCapture) || Calls(travelFinalizer, advanceGeneration) ||
                !Calls(diffEmit, generationSnapshot) || !Calls(applyGenerationTail, installGeneration))
                yield return "L388 host-postwrite-and-client-rail-do-not-converge-on-one-reducer-adapter-or-task7-removal";

            var host = Make(); SourceRevalidationBatchDelta batch; var batchEngine =
                new DurableSourceRevalidationEngine(host, (o, m) => { });
            if (!batchEngine.TryRevalidateBatch(new[] { new SourceRevalidationPlan(occurrence, "V#1", before, after, false) },
                    out batch, out refusal) || batch == null || batch.AuthorityRevision != 2 || batch.Items.Count != 1)
                yield return "L388 host-did-not-produce-one-exact-batch-authority-delta";
            var encoded = DurableInboxCodec.EncodeSourceRevalidation(batch); SourceRevalidationBatchDelta decoded;
            var peer = Make();
            if (encoded[0] != DurableInboxCodec.DeploymentTransitionOp || encoded[1] != 3 ||
                !DurableInboxCodec.TryDecodeSourceRevalidation(encoded, out decoded, out refusal) ||
                decoded.DepartedSource != batch.DepartedSource ||
                decoded.DepartureWatermark != batch.DepartureWatermark ||
                !peer.InstallSourceRevalidationBatch(decoded, out refusal) || peer.Ledger.CommittedRevision != 2 ||
                peer.Ledger.AllEntries.Any(x => x.PreparationRevision != 1) ||
                peer.InstallSourceRevalidationBatch(decoded, out refusal) ||
                !peer.IsSourceRevalidationBatchInstalled(decoded))
                yield return "L388 bounded-schema3-delta-did-not-install-only-at-the-exact-next-revision";
            var malformed = encoded.Concat(new byte[] { 1 }).ToArray();
            if (DurableInboxCodec.TryDecodeSourceRevalidation(malformed, out decoded, out refusal) ||
                MissionSync.IsAuthoritativeTransitionSender(9, 8) || MissionSync.IsAuthoritativeTransitionSender(null, 9))
                yield return "L388 malformed-or-nonhost-source-delta-was-accepted";

            var divergent = new DurableInboxStore(new HostLedger(Make().Ledger.AllEntries, 10, members));
            if (!divergent.InstallSourceRevalidationBatch(batch, out refusal) ||
                divergent.Ledger.CommittedRevision != 11 || divergent.Ledger.AllEntries.Any(x =>
                    x.PreparationRevision != 1 || x.PreparationAuthorityRevision != batch.AuthorityRevision))
                yield return "L388 client-local-ledger-revision-was-confused-with-host-authority-watermark";

            var sibling = new OccurrenceId("DeploymentPreparing", "depart-sibling", new[] { "S#7", "M#7" });
            var siblingOrder = new HostOrderKey(1, sibling.TriggerId);
            var batchMembers = members.Take(2).ToArray();
            DurableInboxStore BatchStore() => new DurableInboxStore(new HostLedger(new[] {
                new InboxEntry(occurrence,a,InboxLifecycle.Open,default(CanonicalChoiceId),2,0,order),
                new InboxEntry(occurrence,b,InboxLifecycle.Queued,default(CanonicalChoiceId),2,0,order),
                new InboxEntry(sibling,a,InboxLifecycle.Suspended,default(CanonicalChoiceId),2,0,siblingOrder,
                    InboxSuspensionReason.PriorityPreemption,checkpoint),
                new InboxEntry(sibling,b,InboxLifecycle.Deferred,default(CanonicalChoiceId),2,0,siblingOrder)
            },1,batchMembers));
            var plans = new[] { new SourceRevalidationPlan(occurrence,"V#1",before,after,false),
                new SourceRevalidationPlan(sibling,"V#1",before,after,false) };
            var atomic = BatchStore(); atomic.WriteRecord = _ => false;
            if (new DurableSourceRevalidationEngine(atomic,(o,m)=>{}).TryRevalidateBatch(plans,out batch,out refusal) ||
                atomic.Ledger.CommittedRevision != 1 || atomic.Ledger.AllEntries.Any(x=>x.PreparationRevision!=0))
                yield return "L388 failed-batch-writer-left-a-partial-sibling-transition";
            atomic = BatchStore();
            if (!new DurableSourceRevalidationEngine(atomic,(o,m)=>{}).TryRevalidateBatch(plans,out batch,out refusal) ||
                atomic.Ledger.CommittedRevision != 2 || atomic.Ledger.AllEntries.Any(x=>x.PreparationRevision!=1) ||
                atomic.Ledger.Get(sibling,a).Lifecycle!=InboxLifecycle.Suspended ||
                atomic.Ledger.Get(sibling,b).Lifecycle!=InboxLifecycle.Queued)
                yield return "L388-related-occurrences-were-split-instead-of-one-atomic-revision";

            var concurrent=BatchStore();int wins=0;var errors=new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            System.Threading.Tasks.Parallel.Invoke(
                ()=>{try{SourceRevalidationBatchDelta x;string w;if(new DurableSourceRevalidationEngine(concurrent,(o,m)=>{})
                    .TryRevalidateBatch(new[]{plans[0]},out x,out w))System.Threading.Interlocked.Increment(ref wins);}catch(Exception ex){errors.Enqueue(ex);}},
                ()=>{try{SourceRevalidationBatchDelta x;string w;if(new DurableSourceRevalidationEngine(concurrent,(o,m)=>{})
                    .TryRevalidateBatch(new[]{plans[1]},out x,out w))System.Threading.Interlocked.Increment(ref wins);}catch(Exception ex){errors.Enqueue(ex);}});
            if(!errors.IsEmpty||wins!=2||concurrent.Ledger.CommittedRevision!=3||
                concurrent.Ledger.AllEntries.Any(x=>x.PreparationRevision!=1))
                yield return "L388 concurrent-distinct-departures-were-not-serialized-without-loss";
            var reentrant=BatchStore();bool nested=false;reentrant.WriteRecord=_=>{
                SourceRevalidationBatchDelta x;string w;nested=new DurableSourceRevalidationEngine(reentrant,(o,m)=>{})
                    .TryRevalidateBatch(new[]{plans[1]},out x,out w);return true;};
            if(!new DurableSourceRevalidationEngine(reentrant,(o,m)=>{}).TryRevalidateBatch(
                    new[]{plans[0]},out batch,out refusal)||nested||reentrant.Ledger.CommittedRevision!=2||
                reentrant.Ledger.AllEntries.Where(x=>x.Occurrence.Equals(sibling)).Any(x=>x.PreparationRevision!=0))
                yield return "L388 reentrant-departure-entered-the-active-atomic-writer";

            var buffered=BatchStore();var oldActive=DurableInboxSaveBridge.ActiveStore;
            try
            {
                DepartureGenerationRail.Clear();ulong ordinaryGeneration,dwiGeneration;
                if(!DepartureGenerationRail.TryAdvance("V#1",out ordinaryGeneration)||ordinaryGeneration!=1||
                    !DepartureGenerationRail.TryAdvance("V#1",out dwiGeneration)||dwiGeneration!=2)
                    yield return "L388 ordinary-departure-did-not-advance-the-same-host-generation-as-dwi";
                DurableInboxSaveBridge.ActiveStore=buffered;
                int sourceRepaints=0;MissionSync.SourceRevalidationRepaintProbe=()=>sourceRepaints++;
                var newer=new SourceRevalidationBatchDelta(25,"V#1",2,new[]{new SourceRevalidationItem(
                    occurrence,false,2,20)});
                var unrelatedReady=new SourceRevalidationBatchDelta(30,"V#9",1,new[]{new SourceRevalidationItem(
                    sibling,false,1,0)});
                var predecessor=new SourceRevalidationBatchDelta(20,"V#1",1,new[]{new SourceRevalidationItem(
                    occurrence,false,1,0)});
                MissionSync.ScheduleSourceRevalidationDelta(newer);
                MissionSync.ScheduleSourceRevalidationDelta(unrelatedReady);
                if(MissionSync.ApplyScheduledSourceRevalidationDeltas()!=0||sourceRepaints!=0||
                    buffered.Ledger.AllEntries.Any(x=>x.PreparationRevision!=0))
                    yield return "L388 delta-before-rail-mutated-or-repainted-the-predeparture-graph";
                GenericApplier.InstallDepartureGeneration("V#8",1);
                if(MissionSync.ApplyScheduledSourceRevalidationDeltas()!=0||sourceRepaints!=0)
                    yield return "L388 unrelated-rail-departure-unlocked-a-buffered-delta";
                GenericApplier.InstallDepartureGeneration("V#9",1);
                if(MissionSync.ApplyScheduledSourceRevalidationDeltas()!=1||sourceRepaints!=1||
                    buffered.Ledger.Get(sibling,a).PreparationAuthorityRevision!=30||
                    buffered.Ledger.Get(occurrence,a).PreparationRevision!=0)
                    yield return "L388 future-head-blocked-an-unrelated-ready-delta";
                MissionSync.ScheduleSourceRevalidationDelta(predecessor);
                GenericApplier.InstallDepartureGeneration("V#1",1);
                GenericApplier.FlushDepartureRevalidation();
                if(sourceRepaints!=2||buffered.Ledger.Get(occurrence,a).PreparationRevision!=1||
                    buffered.Ledger.Get(occurrence,a).PreparationAuthorityRevision!=20)
                    yield return "L388 reversed-consecutive-deltas-did-not-unblock-on-the-rail-scan";
                GenericApplier.InstallDepartureGeneration("V#1",2);
                GenericApplier.FlushDepartureRevalidation();
                if(sourceRepaints!=3||buffered.Ledger.Get(occurrence,a).PreparationRevision!=2||
                    buffered.Ledger.Get(occurrence,a).PreparationAuthorityRevision!=25)
                    yield return "L388 second-rapid-departure-did-not-wait-for-its-own-rail-watermark";
                MissionSync.ScheduleSourceRevalidationDelta(newer); // exact duplicate, then a newer dependent delta
                MissionSync.ScheduleSourceRevalidationDelta(new SourceRevalidationBatchDelta(40,"V#1",3,new[]{
                    new SourceRevalidationItem(occurrence,false,3,25)}));
                GenericApplier.InstallDepartureGeneration("V#1",3);
                if(MissionSync.ApplyScheduledSourceRevalidationDeltas()!=1||sourceRepaints!=4||
                    buffered.Ledger.Get(occurrence,a).PreparationRevision!=3||
                    buffered.Ledger.Get(occurrence,a).PreparationAuthorityRevision!=40)
                    yield return "L388 duplicate-blocked-or-reapplied-before-a-newer-delta";
            }
            finally { MissionSync.SourceRevalidationRepaintProbe=null;DurableInboxSaveBridge.ActiveStore=oldActive;
                MissionSync.ClearScheduledSourceRevalidationDeltas(); DepartureGenerationRail.Clear(); }

            DepartureGenerationRail.Clear(); GenericApplier.ClearDepartureWatermarks(); ulong saturated;
            for(int i=0;i<DepartureGenerationRail.MaxVehicles;i++)
            { if(!DepartureGenerationRail.TryAdvance("V#sat-"+i,out saturated))
                yield return "L388 host-departure-generation-bound-rejected-a-slot-before-capacity"; }
            if(DepartureGenerationRail.TryAdvance("V#over-bound",out saturated))
                yield return "L388 host-departure-generation-storage-exceeded-its-bound";
            for(int i=0;i<DepartureGenerationRail.MaxVehicles;i++)
                GenericApplier.InstallDepartureGeneration("V#sat-"+i,1);
            GenericApplier.InstallDepartureGeneration("V#over-bound",1);
            if(!GenericApplier.HasSettledDeparture("V#sat-0",1)||
                GenericApplier.HasSettledDeparture("V#over-bound",1))
                yield return "L388 client-generation-saturation-evicted-a-potentially-buffered-token";
            DepartureGenerationRail.Clear(); GenericApplier.ClearDepartureWatermarks();

            var maxPrepEntry=new InboxEntry(occurrence,a,InboxLifecycle.Open,default(CanonicalChoiceId),2,0,order,
                preparationRevision:ulong.MaxValue);
            var maxPrep=new DurableInboxStore(new HostLedger(new[]{maxPrepEntry},1,
                new[]{new KeyValuePair<MembershipId,MemberPresence>(a,MemberPresence.Active)}));
            var maxDelta=new SourceRevalidationBatchDelta(2,"V#1",1,new[]{new SourceRevalidationItem(
                occurrence,false,0,0)});
            if(maxPrep.InstallSourceRevalidationBatch(maxDelta,out refusal)||maxPrep.Ledger.CommittedRevision!=1)
                yield return "L388 preparation-max-boundary-wrapped-or-partially-committed";
            var maxLocal=new DurableInboxStore(new HostLedger(new[]{new InboxEntry(occurrence,a,
                InboxLifecycle.Open,default(CanonicalChoiceId),2,0,order)},ulong.MaxValue,
                new[]{new KeyValuePair<MembershipId,MemberPresence>(a,MemberPresence.Active)}));
            if(maxLocal.InstallSourceRevalidationBatch(new SourceRevalidationBatchDelta(2,"V#1",1,new[]{
                new SourceRevalidationItem(occurrence,false,1,0)}),out refusal))
                yield return "L388 local-ledger-max-boundary-wrapped";
            var maxLifecycle=new DurableInboxStore(new HostLedger(new[]{new InboxEntry(occurrence,a,
                InboxLifecycle.Open,default(CanonicalChoiceId),ulong.MaxValue,0,order)},1,
                new[]{new KeyValuePair<MembershipId,MemberPresence>(a,MemberPresence.Active)}));
            if(maxLifecycle.InstallSourceRevalidationBatch(new SourceRevalidationBatchDelta(2,"V#1",1,new[]{
                new SourceRevalidationItem(occurrence,true,0,0)}),out refusal)||
                maxLifecycle.Ledger.Get(occurrence,a).Lifecycle!=InboxLifecycle.Open)
                yield return "L388 lifecycle-max-boundary-wrapped-or-partially-tombstoned";

            var retryStore=Make(); var throwOnce=new ThrowOnceCarrier();
            retryStore.Carriers.Register(occurrence,DurableCarrierClass.NativeCurrent,throwOnce);
            var empty=new DeploymentSourceSnapshot(new Dictionary<string,IEnumerable<string>>());
            if(!new DurableSourceRevalidationEngine(retryStore,(o,m)=>{}).TryRevalidateBatch(
                    new[]{new SourceRevalidationPlan(occurrence,"V#1",before,empty,false)},out batch,out refusal)||
                retryStore.Ledger.AllEntries.Any(x=>x.Lifecycle!=InboxLifecycle.Removed||
                    x.TerminalReason!=TerminalReason.SourceInvalidated||x.TombstoneRevision!=2)||throwOnce.Calls!=1||
                DurableSourceRevalidationEngine.RetryTerminalTeardown(retryStore,new[]{occurrence})!=1||throwOnce.Calls!=2)
                yield return "L388 transient-carrier-failure-lost-authority-or-was-not-retryable";
            HostLedger restored; var ledgerBytes=retryStore.Ledger.EncodeCanonical();
            if(!DurableInboxCodec.TryDecodeLedger(ledgerBytes,out restored,out refusal)||
                restored.AllEntries.Any(x=>x.TerminalReason!=TerminalReason.SourceInvalidated||x.TombstoneRevision!=2))
                yield return "L388 source-invalidated-tombstone-did-not-survive-save-load";
        }

        private static bool Calls(MethodBase from, MethodBase target)
        {
            if (from == null || target == null) return false;
            return Program.Callees(from, typeof(DurableInboxEngine).Assembly)
                .Any(x => x.MetadataToken == target.MetadataToken && x.Module == target.Module);
        }
        private static bool NamesToken(MethodBase method, int metadataToken)
        {
            byte[] il; try { il = method?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false; var token = BitConverter.GetBytes(metadataToken);
            for (int i = 0; i <= il.Length - 4; i++)
                if (il[i] == token[0] && il[i+1] == token[1] && il[i+2] == token[2] && il[i+3] == token[3]) return true;
            return false;
        }
        private static bool UniqueControl(FieldInfo field, MethodInfo method)
        {
            if (field == null || method == null) return false;
            try
            {
                var match = new OccurrenceId("DeploymentPreparing","unique",new[]{"S#1","V#1"});
                var miss = new OccurrenceId("DeploymentPreparing","unique-miss",new[]{"S#1","V#2"});
                return (bool)method.Invoke(null,new object[]{true,(OccurrenceId?)match,"V#1"}) &&
                    !(bool)method.Invoke(null,new object[]{true,(OccurrenceId?)miss,"V#1"}) &&
                    !(bool)method.Invoke(null,new object[]{false,(OccurrenceId?)match,"V#1"});
            }
            catch { return false; }
        }
    }
}
