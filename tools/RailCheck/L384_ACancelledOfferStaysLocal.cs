using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;
using HarmonyLib;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    internal static class L384_ACancelledOfferStaysLocal
    {
        internal static IEnumerable<string> Check()
        {
            var a = new MembershipId("a", 1); var b = new MembershipId("b", 1);
            var o = new OccurrenceId("Modal:GeoAmbushBrief", "cancel-local", new[] { "mission" });
            var order = new HostOrderKey(1, o.TriggerId);
            var store = new DurableInboxStore(new HostLedger(new[] {
                new InboxEntry(o,a,InboxLifecycle.Open,default(CanonicalChoiceId),1,0,order),
                new InboxEntry(o,b,InboxLifecycle.Read,default(CanonicalChoiceId),1,0,order)},1,
                new[] { new KeyValuePair<MembershipId,MemberPresence>(a,MemberPresence.Active),
                    new KeyValuePair<MembershipId,MemberPresence>(b,MemberPresence.Active)}));
            if (!WindowQueueSync.CancelDurableMissionOffer(store,a,o) ||
                store.Ledger.Get(o,a).Lifecycle != InboxLifecycle.Dismissed ||
                store.Ledger.Get(o,b).Lifecycle != InboxLifecycle.Read)
                yield return "L384 cancel-was-not-one-members-local-lifecycle";
            var arbitrary = new OccurrenceId("ordinary", "not-offer", new[] { "mission" });
            var prep = new OccurrenceId("DeploymentPreparing", "not-offer-prep", new[] { "mission" });
            var arbitraryStore = new DurableInboxStore(new HostLedger(new[] {
                new InboxEntry(arbitrary,a,InboxLifecycle.Open,default(CanonicalChoiceId),1,0,
                    new HostOrderKey(1,arbitrary.TriggerId)),
                new InboxEntry(prep,a,InboxLifecycle.Open,default(CanonicalChoiceId),1,0,
                    new HostOrderKey(2,prep.TriggerId))},2,
                new[] { new KeyValuePair<MembershipId,MemberPresence>(a,MemberPresence.Active)}));
            if (WindowQueueSync.CancelDurableMissionOffer(arbitraryStore,a,arbitrary) ||
                WindowQueueSync.CancelDurableMissionOffer(arbitraryStore,a,prep) ||
                arbitraryStore.Ledger.AllEntries.Any(x=>x.Lifecycle==InboxLifecycle.Dismissed))
                yield return "L384 cancel-accepted-an-arbitrary-or-preparation-occurrence";
            var o2=new OccurrenceId("Modal:GeoAmbushBrief","second",new[]{"mission"});
            var multi=new DurableInboxStore(new HostLedger(new[]{
                new InboxEntry(o,a,InboxLifecycle.Open,default(CanonicalChoiceId),1,0,order),
                new InboxEntry(o2,a,InboxLifecycle.Open,default(CanonicalChoiceId),1,0,new HostOrderKey(2,o2.TriggerId))},2,
                new[]{new KeyValuePair<MembershipId,MemberPresence>(a,MemberPresence.Active)}));
            if(!WindowQueueSync.DurableMissionOfferBindingMatches(multi,a,o,"mission")||
                !WindowQueueSync.DurableMissionOfferBindingMatches(multi,a,o2,"mission")||
                WindowQueueSync.DurableMissionOfferBindingMatches(multi,a,o,"foreign"))
                yield return "L384 exact-native-binding-was-replaced-by-ambiguous-subject-search";
            int orderedActions=0,orderedCloses=0;
            bool runNative=WindowQueueSync.FinishDurableMissionOfferAnswer(PhoenixPoint.Common.Utils.ModalResult.Confirm,
                ()=>{orderedActions++;return false;},()=>orderedCloses++);
            if(runNative||orderedActions!=1||orderedCloses!=0)
                yield return "L384 failed-start-was-closed-or-ran-native-before-successor-persistence";
            WindowQueueSync.FinishDurableMissionOfferAnswer(PhoenixPoint.Common.Utils.ModalResult.Cancel,
                ()=>{orderedActions++;return true;},()=>orderedCloses++);
            if(orderedActions!=2||orderedCloses!=1)
                yield return "L384 local-cancel-did-not-commit-before-callback-free-close";
            var calls = typeof(WindowQueueSync).GetMethod("CancelDurableMissionOffer",
                System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            if (calls == null || calls.GetMethodBody().GetILAsByteArray().Length == 0)
                yield return "L384 premise-changed: local cancel seam disappeared";
            var patch = typeof(MissionSync).Assembly.GetType("Multiplayer.Network.Sync.PerPeerModalAnswer");
            var prefix = patch?.GetMethod("Prefix", System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            if (prefix == null || !typeof(MissionSync).GetMethods(
                    System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)
                    .Any(x=>x.Name=="HandleDurableMissionOfferAnswer"))
                yield return "L384 native-ModalResultCallback-cancel-is-not-wired-to-durable-local-cancel";
            var finishPatch=typeof(WindowQueueSync).GetNestedType("FinishDialogAnswer",
                System.Reflection.BindingFlags.NonPublic);
            var finishPrefix=finishPatch?.GetMethod("Prefix",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            if(finishPrefix==null||finishPrefix.ReturnType!=typeof(bool))
                yield return "L384 native-FinishDialog-ordering-adapter-is-not-block-first";
            var parameters=finishPrefix?.GetParameters();
            var nativeFinish=AccessTools.Method(typeof(UIStateGeoModal),nameof(UIStateGeoModal.FinishDialog));
            var finishQuery=AccessTools.Method(typeof(GeoscapeView),nameof(GeoscapeView.FinishQueriedState));
            var invoke=AccessTools.Method(typeof(PhoenixPoint.Common.Utils.DialogCallback),"Invoke");
            var il=nativeFinish?.GetMethodBody()?.GetILAsByteArray();
            if(parameters==null||parameters.Length!=2||parameters[0].ParameterType!=typeof(UIStateGeoModal)||
                parameters[0].Name!="__instance"||parameters[1].ParameterType!=typeof(PhoenixPoint.Common.Utils.ModalResult)||
                parameters[1].Name!="res"||
                finishPatch.GetCustomAttributes(typeof(HarmonyPatch),false).Length==0||il==null||
                TokenAt(il,finishQuery?.MetadataToken??0)<0||TokenAt(il,invoke?.MetadataToken??0)<0||
                TokenAt(il,finishQuery.MetadataToken)>=TokenAt(il,invoke.MetadataToken))
                yield return "L384 FinishDialog patch-target-or-native-finish-before-callback-premise-changed";
            int routed=0;
            if(MissionSync.RunNativeMissionOfferAnswer(true,true,true,true,()=>{routed++;return true;})||routed!=1||
                !MissionSync.RunNativeMissionOfferAnswer(false,true,true,true,()=>{routed++;return true;})||routed!=1||
                !MissionSync.RunNativeMissionOfferAnswer(true,false,true,true,()=>{routed++;return true;})||routed!=1||
                !MissionSync.RunNativeMissionOfferAnswer(true,true,true,false,()=>{routed++;return true;})||routed!=1||
                !MissionSync.RunNativeMissionOfferAnswer(true,true,true,true,()=>{routed++;return false;})||routed!=2||
                MissionSync.RunNativeMissionOfferAnswer(true,true,false,false,null))
                yield return "L384 native-answer-routing-did-not-suppress-only-the-durable-session-offer";
            // POSITIVE CONTROL: globally tombstoning both copies is observably not this result.
            if (store.Ledger.AllEntries.All(x => x.Lifecycle == InboxLifecycle.Removed))
                yield return "L384 control-not-red: global removal looked local";
        }
        private static int TokenAt(byte[] il,int token)
        { var b=System.BitConverter.GetBytes(token); for(int i=0;i<=il.Length-4;i++)if(il[i]==b[0]&&il[i+1]==b[1]&&il[i+2]==b[2]&&il[i+3]==b[3])return i;return -1; }
    }
}

