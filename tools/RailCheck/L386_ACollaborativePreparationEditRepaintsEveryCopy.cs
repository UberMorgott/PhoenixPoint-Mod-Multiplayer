using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    internal static class L386_ACollaborativePreparationEditRepaintsEveryCopy
    {
        private const BindingFlags All=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        internal static IEnumerable<string> Check()
        {
            var a=new MembershipId("a");var b=new MembershipId("b");
            var occurrence=new OccurrenceId("DeploymentPreparing","prep-edit",new[]{"S#8","M#8"});
            var order=new HostOrderKey(1,occurrence.TriggerId);
            var checkpoint=new InboxWindowCheckpoint("deployment","U#4","roster");
            var members=new[]{a,
                b};
            DurableInboxStore Make()=>new DurableInboxStore(new HostLedger(new[]{
                new InboxEntry(occurrence,a,InboxLifecycle.Open,default(CanonicalChoiceId),2,0,order),
                new InboxEntry(occurrence,b,InboxLifecycle.Suspended,default(CanonicalChoiceId),2,0,order,
                    InboxSuspensionReason.PriorityPreemption,checkpoint)},1,members));

            var host=Make();var peer=Make();PreparationEditDelta emitted=null;int native=0,repaints=0;
            var engine=new DurablePreparationEditEngine(host,d=>{emitted=d;repaints++;});string refusal;
            if(!engine.TryApply(new DurablePreparationEditContext(occurrence,0),()=>true,
                    ()=>{native++;return true;},()=>{},new[]{"U#4","V#2"},out refusal)||native!=1||repaints!=1||emitted==null||
                emitted.PreparationRevision!=1||emitted.AuthoritativeLedgerRevision!=2||
                !emitted.TouchedStableIdentities.SequenceEqual(new[]{"U#4","V#2"})||
                host.Ledger.AllEntries.Any(x=>x.PreparationRevision!=1||x.PreparationAuthorityRevision!=2)||
                host.Ledger.Get(occurrence,a).Lifecycle!=InboxLifecycle.Open||
                host.Ledger.Get(occurrence,b).Lifecycle!=InboxLifecycle.Suspended||
                !checkpoint.Equals(host.Ledger.Get(occurrence,b).Checkpoint))
                yield return "L386 host-edit-was-not-atomically-versioned-once-and-repainted";
            var payload=DurableInboxCodec.EncodePreparationEdit(emitted);PreparationEditDelta decodedDelta;
            int peerRepaints=0;
            if(payload[0]!=DurableInboxCodec.DeploymentTransitionOp||payload[1]!=2||
                !DurableInboxCodec.TryDecodePreparationEdit(payload,out decodedDelta,out refusal)||
                !MissionSync.ApplyPreparationEditDelta(peer,decodedDelta,d=>peerRepaints++)||peerRepaints!=1||
                peer.Ledger.AllEntries.Any(x=>x.PreparationRevision!=1||x.PreparationAuthorityRevision!=2)||
                peer.Ledger.Get(occurrence,a).Lifecycle!=InboxLifecycle.Open||
                peer.Ledger.Get(occurrence,b).Lifecycle!=InboxLifecycle.Suspended||
                !checkpoint.Equals(peer.Ledger.Get(occurrence,b).Checkpoint))
                yield return "L386 authoritative-state-delta-did-not-update-the-second-store-and-preserve-copies";
            if(!MissionSync.IsAuthoritativeTransitionSender(9,9)||MissionSync.IsAuthoritativeTransitionSender(9,8)||
                MissionSync.IsAuthoritativeTransitionSender(null,9))
                yield return "L386 preparation-state-delta-did-not-retain-host-authentication";
            PreparationEditDelta second=null;
            if(!new DurablePreparationEditEngine(peer,d=>second=d).TryApply(
                    new DurablePreparationEditContext(occurrence,1),()=>true,()=>true,()=>{},new[]{"U#5"},out refusal)||
                second==null||second.PreparationRevision!=2)
                yield return "L386 cross-peer-second-edit-could-not-use-the-repainted-authoritative-revision";

            int stale=0;
            if(engine.TryApply(new DurablePreparationEditContext(occurrence,0),()=>true,()=>{stale++;return true;},()=>{},null,out refusal)||stale!=0)
                yield return "L386 stale-revision-reached-native-mutation";
            var wrongOccurrence=new OccurrenceId("DeploymentPreparing","foreign",new[]{"S#9"});
            if(!DeploymentWindowClose.AuthorizePreparationMember(host,a,new DurablePreparationEditContext(occurrence,1))||
                DeploymentWindowClose.AuthorizePreparationMember(host,b,new DurablePreparationEditContext(occurrence,1))||
                DeploymentWindowClose.AuthorizePreparationMember(host,a,new DurablePreparationEditContext(wrongOccurrence,1))||
                DeploymentWindowClose.AuthorizePreparationMember(host,a,new DurablePreparationEditContext(occurrence,0)))
                yield return "L386 sender-membership-open-entitlement-or-exact-revision-was-not-authorized-strictly";
            var failed=Make();failed.ValidateCandidate=_=>false;int failedNative=0;
            if(new DurablePreparationEditEngine(failed,_=>{}).TryApply(new DurablePreparationEditContext(occurrence,0),
                    ()=>true,()=>{failedNative++;return true;},()=>{},null,out refusal)||failedNative!=0||failed.Ledger.CommittedRevision!=1||
                failed.Ledger.AllEntries.Any(x=>x.PreparationRevision!=0))
                yield return "L386 commit-preflight-failure-allowed-native-mutation";
            foreach(var capacityFailure in new Action<DurableInboxStore>[] {
                s=>s.PreparationMaterializationProbe=()=>{throw new Exception("materialize");},
                s=>s.PreparationCapacityProbe=()=>{throw new Exception("capacity");}})
            {
                var preflight=Make();capacityFailure(preflight);int preflightNative=0;
                if(new DurablePreparationEditEngine(preflight,_=>{}).TryApply(
                       new DurablePreparationEditContext(occurrence,0),()=>true,
                       ()=>{preflightNative++;return true;},()=>{},new[]{"V#2","U#4","V#2"},out refusal)||
                   preflightNative!=0||preflight.Ledger.CommittedRevision!=1)
                    yield return "L386 materialization-or-capacity-failure-reached-native-mutation";
            }
            var rollbackStore=Make();int nativeState=7,rollbacks=0;
            if(new DurablePreparationEditEngine(rollbackStore,_=>{}).TryApply(new DurablePreparationEditContext(occurrence,0),
                    ()=>true,()=>{nativeState=99;throw new Exception("partial");},()=>{nativeState=7;rollbacks++;},null,out refusal)||
                nativeState!=7||rollbacks!=1||rollbackStore.Ledger.CommittedRevision!=1||
                rollbackStore.Ledger.AllEntries.Any(x=>x.PreparationRevision!=0))
                yield return "L386 partial-native-throw-was-not-rolled-back-before-ledger-remained-unchanged";

            var race=Make();int mutations=0,wins=0;var errors=new ConcurrentQueue<Exception>();
            Parallel.For(0,2,_=>{try{string why;if(new DurablePreparationEditEngine(race,d=>{}).TryApply(
                new DurablePreparationEditContext(occurrence,0),()=>true,()=>{Interlocked.Increment(ref mutations);return true;},()=>{},null,out why))
                    Interlocked.Increment(ref wins);}catch(Exception ex){errors.Enqueue(ex);}});
            if(!errors.IsEmpty||wins!=1||mutations!=1||race.Ledger.AllEntries.Any(x=>x.PreparationRevision!=1))
                yield return "L386 keyed-gate-did-not-serialize-two-real-shaped-mutations";
            var sourceCharacters=new List<string>{"U#4"};var destinationCharacters=new List<string>();
            try{PersonnelSync.RunReassignSteps(()=>sourceCharacters.Remove("U#4"),()=>
                {destinationCharacters.Add("U#4");throw new Exception("destination partial");});}catch(Exception)
            {PersonnelSync.RunReassignRollback(()=>destinationCharacters.Contains("U#4"),
                ()=>destinationCharacters.Remove("U#4"),()=>sourceCharacters.Contains("U#4"),
                ()=>sourceCharacters.Add("U#4"));}
            if(!sourceCharacters.SequenceEqual(new[]{"U#4"})||destinationCharacters.Count!=0)
                yield return "L386 actual-reassign-plan-seam-did-not-restore-source-after-partial-add";
            var equipmentBefore=new[]{new SnapshotItem("rifle",7),new SnapshotItem("medkit",2)};
            var equipmentRestored=EquipSync.ClonePreparationSnapshot(equipmentBefore,
                x=>new SnapshotItem(x.Id,x.Charges));
            equipmentBefore[0].Charges=0;
            if(equipmentRestored.Count!=2||equipmentRestored[0].Id!="rifle"||equipmentRestored[0].Charges!=7||
                ReferenceEquals(equipmentRestored[0],equipmentBefore[0]))
                yield return "L386 actual-equip-snapshot-seam-did-not-preserve-content-charges-and-copy-identity";
            var nestedStore=Make();bool nestedAccepted=true;int nestedNative=0;
            var nestedEngine=new DurablePreparationEditEngine(nestedStore,_=>{});
            if(!nestedEngine.TryApply(new DurablePreparationEditContext(occurrence,0),()=>true,()=>
                {
                    nestedNative++;
                    var before=nestedStore.Ledger;
                    nestedAccepted=nestedStore.Commit(before,before.WithAuthority(before.CommittedRevision+1,before.Members));
                    return true;
                },()=>{},new[]{"V#2","U#4","V#2"},out refusal)||nestedAccepted||nestedNative!=1||
                nestedStore.Ledger.CommittedRevision!=2||
                nestedStore.Ledger.AllEntries.Any(x=>x.PreparationRevision!=1))
                yield return "L386 reentrant-store-write-was-not-rejected-before-it-could-be-lost";
            var recursiveStore=Make();DurablePreparationEditEngine recursiveEngine=null;
            bool recursiveAccepted=true;int recursiveOuterNative=0,recursiveInnerNative=0;
            recursiveEngine=new DurablePreparationEditEngine(recursiveStore,_=>{});
            if(!recursiveEngine.TryApply(new DurablePreparationEditContext(occurrence,0),()=>true,()=>
                {
                    recursiveOuterNative++;
                    string innerWhy;
                    recursiveAccepted=recursiveEngine.TryApply(new DurablePreparationEditContext(occurrence,0),
                        ()=>true,()=>{recursiveInnerNative++;return true;},()=>{},null,out innerWhy);
                    return true;
                },()=>{},new[]{"U#4"},out refusal)||recursiveAccepted||recursiveOuterNative!=1||
                recursiveInnerNative!=0||recursiveStore.Ledger.CommittedRevision!=2||
                recursiveStore.Ledger.AllEntries.Any(x=>x.PreparationRevision!=1))
                yield return "L386 recursive-preparation-engine-cleared-or-bypassed-the-outer-transition-guard";
            var reentrant=Make();int reentrantNative=0;DurablePreparationEditEngine reentrantEngine=null;
            reentrantEngine=new DurablePreparationEditEngine(reentrant,d=>{string why;reentrantEngine.TryApply(
                new DurablePreparationEditContext(occurrence,0),()=>true,()=>{reentrantNative++;return true;},()=>{},null,out why);});
            if(!reentrantEngine.TryApply(new DurablePreparationEditContext(occurrence,0),()=>true,()=>
                {reentrantNative++;return true;},()=>{},null,out refusal)||reentrantNative!=1)
                yield return "L386 repaint-reentrancy-applied-the-same-revision-twice";

            HostLedger decoded;
            if(!DurableInboxCodec.TryDecodeLedger(host.Ledger.EncodeCanonical(),out decoded,out refusal)||
                decoded.AllEntries.Any(x=>x.PreparationRevision!=1||x.PreparationAuthorityRevision!=2))
                yield return "L386 ledger-schema6-did-not-roundtrip-preparation-state";
            var ledgerBytes=host.Ledger.EncodeCanonical();
            var message=new InboxMessage(InboxMessageKind.TransportAck,occurrence,
                new CanonicalResultId(occurrence,"r"),Array.Empty<CanonicalRewardItemId>(),a,order,2,0,
                InboxLifecycle.Open,default(CanonicalChoiceId));
            var messageBytes=DurableInboxCodec.Encode(message);InboxMessage ignoredMessage;
            var schema4=(byte[])messageBytes.Clone();schema4[0]=4;
            if(ledgerBytes[4]!=6||messageBytes[0]!=5||DurableInboxCodec.TryDecode(schema4,out ignoredMessage,out refusal))
                yield return "L386 message-v5-and-ledger-v6-schemas-were-not-split-or-schema4-was-accepted";

            var context=new DurablePreparationEditContext(occurrence,17);byte[] contextBytes;
            using(var ms=new MemoryStream()){using(var w=new BinaryWriter(ms,System.Text.Encoding.UTF8,true))DurablePreparationEditContext.Write(w,context);contextBytes=ms.ToArray();}
            DurablePreparationEditContext got;
            using(var empty=new MemoryStream())using(var emptyReader=new BinaryReader(empty))
                if(DurablePreparationEditContext.TryReadTrailing(emptyReader,out got))
                    yield return "L386 missing-context-was-not-backward-compatible-outside-a-bound-preparation";
            using(var ms=new MemoryStream(contextBytes,false))using(var r=new BinaryReader(ms))
                if(!DurablePreparationEditContext.TryReadTrailing(r,out got)||!got.Occurrence.Equals(occurrence)||got.ExpectedRevision!=17)
                    yield return "L386 bounded-edit-context-did-not-roundtrip";
            foreach(var malformed in new[]{contextBytes.Take(contextBytes.Length-1).ToArray(),new byte[]{0xD6,0,0}})
            {bool rejected=false;try{using(var ms=new MemoryStream(malformed,false))using(var r=new BinaryReader(ms))DurablePreparationEditContext.TryReadTrailing(r,out got);}catch(Exception){rejected=true;}
             if(!rejected)yield return "L386 malformed-or-truncated-context-was-accepted";}
            bool oversizedRejected=false;
            try{var huge=new DurablePreparationEditContext(new OccurrenceId("DeploymentPreparing","huge",new[]{new string('x',4097)}),0);
                using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms))DurablePreparationEditContext.Write(w,huge);}
            catch(Exception){oversizedRejected=true;}
            if(!oversizedRejected)yield return "L386 oversized-context-identity-was-accepted";

            var equipOp=typeof(EquipSync).GetField("OpSetItems",All);var personnelOp=typeof(PersonnelSync).GetField("OpReassign",All);
            if(equipOp==null||personnelOp==null||(byte)equipOp.GetRawConstantValue()!=8||(byte)personnelOp.GetRawConstantValue()!=4)
                yield return "L386 existing-edit-opcodes-were-replaced";
            var fire=typeof(UiEventMap).GetMethod("FirePreparationEdit",All);
            var repaint=typeof(DeploymentWindowClose).GetMethod("RepaintDeploymentScreen",All);
            var nativeRefresh=typeof(DeploymentWindowClose).GetMethod("RunNativePreparationRefresh",All);
            var mark=typeof(OpenUiRepaint).GetMethod("MarkPreparationDirty",All);
            var engineApply=typeof(DurablePreparationEditEngine).GetMethod("TryApply",All);
            var writeContext=typeof(DurablePreparationEditContext).GetMethod("Write",All);
            var reassignPatch=typeof(PersonnelSync).GetNestedType("HostPreparationReassignPatch",All);
            var siteAdd=typeof(PersonnelSync).GetNestedType("SiteAddGuardPatch",All);
            var vehicleAdd=typeof(PersonnelSync).GetNestedType("VehicleAddGuardPatch",All);
            var capture=typeof(DeploymentWindowClose).GetMethod("TryCapturePreparationEditContext",All);
            var selectedField=typeof(DeploymentRosterRefresh).GetField("SelectedField",All);
            var setupField=typeof(DeploymentWindowClose).GetField("SetUpInitial",All);
            if(fire==null||repaint==null||nativeRefresh==null||mark==null||capture==null||
                !Calls(fire,typeof(UiEventMap).GetMethod("Fire",All))||!Calls(fire,mark)||!Calls(repaint,nativeRefresh))
                yield return "L386 premise-changed: callback-free-repaint-routing-drifted";
            if(!AnyCalls(typeof(EquipSync),engineApply)||!AnyCalls(typeof(PersonnelSync),engineApply)||
                !AnyCalls(typeof(EquipSync),writeContext)||!AnyCalls(typeof(PersonnelSync),writeContext)||reassignPatch==null||
                siteAdd?.GetMethod("Postfix",All)!=null||vehicleAdd?.GetMethod("Postfix",All)!=null)
                yield return "L386 premise-changed: existing-native-edit-funnel-wiring-drifted";
            if(selectedField?.GetValue(null)==null||setupField?.GetValue(null)==null)
                yield return "L386 premise-changed: native-selection-prune-or-validity-tail-drifted";
            var nativeOrder=new List<string>();
            if(!DeploymentWindowClose.RunNativePreparationRefresh(()=>nativeOrder.Add("roster-init"),
                    ()=>nativeOrder.Add("setup-initial"))||!nativeOrder.SequenceEqual(new[]{"roster-init","setup-initial"})||
                DeploymentWindowClose.RunNativePreparationRefresh(null,()=>{})||
                DeploymentWindowClose.RunNativePreparationRefresh(()=>{},null))
                yield return "L386 native-roster-and-button-validity-tail-was-not-executed-once-in-order";
            if(!Program.Callees(capture,typeof(EquipSync).Assembly).Any(x=>x.Name=="CurrentRequest"))
                yield return "L386 context-is-not-limited-to-the-actually-bound-window";
            var selection=new List<string>{"keep","drop"};DeploymentRosterRefresh.PruneSelection(selection,
                new HashSet<string>(new[]{"keep"}));
            if(selection.Count!=1||selection[0]!="keep")yield return "L386 native-preparation-selection-was-regenerated-or-not-pruned";
            // POSITIVE CONTROL: an entitlement-local revision would leave one copy at zero.
            if(host.Ledger.AllEntries.Count(x=>x.PreparationRevision==1)!=2)
                yield return "L386 control-not-red: one-copy-update-looked-global";
        }
        private static bool Calls(MethodInfo from,MethodInfo to){if(from==null||to==null)return false;var il=from.GetMethodBody()?.GetILAsByteArray();if(il==null)return false;
            var token=BitConverter.GetBytes(to.MetadataToken);for(int i=0;i<=il.Length-4;i++)if(il[i]==token[0]&&il[i+1]==token[1]&&il[i+2]==token[2]&&il[i+3]==token[3])return true;return false;}
        private static bool AnyCalls(Type type,MethodInfo target){if(type==null||target==null)return false;
            if(type.GetMethods(All).Any(m=>Calls(m,target)))return true;return type.GetNestedTypes(All).Any(t=>AnyCalls(t,target));}
        private sealed class SnapshotItem{internal SnapshotItem(string id,int charges){Id=id;Charges=charges;}
            internal string Id;internal int Charges;}
    }
}
