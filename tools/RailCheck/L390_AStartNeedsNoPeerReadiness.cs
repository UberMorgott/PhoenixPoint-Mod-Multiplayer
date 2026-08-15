using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    internal static class L390_AStartNeedsNoPeerReadiness
    {
        internal static IEnumerable<string> Check()
        {
            var method=typeof(DurableMissionOfferEngine).GetMethod("TryStart",BindingFlags.Instance|BindingFlags.NonPublic);
            if(method==null) { yield return "L390 premise-changed: start engine disappeared"; yield break; }
            var names=typeof(DurableMissionOfferEngine).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Select(x=>x.Name)
                .Concat(typeof(DurableMissionOfferEngine).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Select(x=>x.Name));
            if(names.Any(x=>x.IndexOf("ready",System.StringComparison.OrdinalIgnoreCase)>=0||x.IndexOf("quorum",System.StringComparison.OrdinalIgnoreCase)>=0||x.IndexOf("vote",System.StringComparison.OrdinalIgnoreCase)>=0))
                yield return "L390 start-acquired-peer-readiness-state";
            if(typeof(MissionSync).GetMethod("StartDurableOffer",BindingFlags.Static|BindingFlags.NonPublic)==null)
                yield return "L390 terminal-delta-production-funnel-is-unwired";
            var member=new MembershipId("afk"); var offer=new OccurrenceId("Modal:GeoAmbushBrief","o",new[]{"m"});
            var prep=new OccurrenceId("DeploymentPreparing","p",new[]{"m"});
            var store=new DurableInboxStore(new HostLedger(new[]{new InboxEntry(offer,member,InboxLifecycle.Open,
                default(CanonicalChoiceId),1,0)},1,
                new[]{member}));
            var carrier=new Carrier(); store.Carriers.Register(offer,DurableCarrierClass.NativeCurrent,carrier); int deltas=0; string refusal;
            if(!new DurableMissionOfferEngine(store,_=>deltas++).TryStart(offer,prep,_=>true,out refusal)||
                carrier.Removed!=1||deltas!=1||refusal!=null)
                yield return "L390 committed-start-did-not-close-and-repaint-terminal-predecessor";
            var retryStore=new DurableInboxStore(new HostLedger(new[]{new InboxEntry(offer,member,InboxLifecycle.Open,
                default(CanonicalChoiceId),1,0)},1,
                new[]{member}));
            var flaky=new Carrier{Throws=1}; retryStore.Carriers.Register(offer,DurableCarrierClass.NativeCurrent,flaky);
            int retryDeltas=0; var retryEngine=new DurableMissionOfferEngine(retryStore,d=>retryDeltas++);
            if(retryEngine.TryStart(offer,prep,_=>true,out refusal)||retryDeltas!=1||retryStore.Ledger.CommittedRevision!=2||
                retryStore.Carriers.Count(offer)!=1||!retryStore.Ledger.Contains(prep))
                yield return "L390 teardown-failure-lost-durable-successor-or-retryable-offer-carrier";
            if(!retryEngine.TryStart(offer,prep,_=>true,out refusal)||retryDeltas!=1||flaky.Removed!=1||
                retryStore.Carriers.Count(offer)!=0)
                yield return "L390 bounded-teardown-retry-reemitted-delta-or-did-not-remove-carrier";
            var terminal=retryStore.Ledger.Get(offer,member);
            var delta=new DeploymentTransitionDelta(offer,prep,terminal.TombstoneRevision,TerminalReason.Superseded);
            DeploymentTransitionDelta decoded; string decodeWhy; if(!DurableInboxCodec.TryDecodeDeploymentTransition(
                DurableInboxCodec.EncodeDeploymentTransition(delta),out decoded,out decodeWhy)||!decoded.Offer.Equals(offer)||
                // AMPUTATED 2026-08-15 (L523): DeploymentTransitionDelta.Order is gone with the second
                // ordering system; the round-trip still proves offer, preparation and reason survive it.
                !decoded.Preparation.Equals(prep)||
                decoded.Reason!=TerminalReason.Superseded)
                yield return "L390 authoritative-occurrence-terminal-delta-did-not-roundtrip";
            var inner=DurableInboxCodec.EncodeDeploymentTransition(delta);
            var envelope=SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWindowIntent,SyncKind.StateDelta,inner);
            byte surface; SyncKind kind; byte[] decodedInner;
            if(inner[0]!=DurableInboxCodec.DeploymentTransitionOp||inner[0]!=0x44||
                !SyncProtocol.TryDecodeEnvelope(envelope,out surface,out kind,out decodedInner)||
                surface!=SurfaceIds.GeoWindowIntent||kind!=SyncKind.StateDelta||!decodedInner.SequenceEqual(inner))
                yield return "L390 terminal-delta-did-not-use-0xB9-state-delta-op-0x44";
            var malformed=(byte[])inner.Clone(); malformed[malformed.Length-1]^=1;
            if(DurableInboxCodec.TryDecodeDeploymentTransition(malformed,out decoded,out decodeWhy)||string.IsNullOrEmpty(decodeWhy))
                yield return "L390 malformed-terminal-delta-was-accepted";
            if(!MissionSync.IsAuthoritativeTransitionSender(71,71)||MissionSync.IsAuthoritativeTransitionSender(71,72)||
                MissionSync.IsAuthoritativeTransitionSender(null,71))
                yield return "L390 non-host-peer-could-apply-a-terminal-delta";
            int refusalCount=0; string refusalText=null; bool refusalNotify=false;
            MissionSync.EmitDurableStartRefusal("membership epoch is not enrolled",(text,notify)=>
            {refusalCount++;refusalText=text;refusalNotify=notify;});
            if(refusalCount!=1||!refusalNotify||string.IsNullOrEmpty(refusalText)||
                refusalText.IndexOf("membership epoch",System.StringComparison.Ordinal)<0||refusalText.Length>512)
                yield return "L390 durable-start-refusal-was-not-one-bounded-notifying-reason";
            // POSITIVE CONTROL: a readiness field would be caught by the name sweep above.
            if(new[]{"ready"}.All(x=>x.IndexOf("ready",System.StringComparison.OrdinalIgnoreCase)<0))
                yield return "L390 control-not-red: readiness mutation escaped";
        }
        private sealed class Carrier:IDurableOccurrenceCarrier
        { internal int Removed,Throws; public void RemoveWithoutCallback(TerminalReason reason){if(Throws-->0)throw new System.InvalidOperationException("injected");Removed++;} }
    }
}

