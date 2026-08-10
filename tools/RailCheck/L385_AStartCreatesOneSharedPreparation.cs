using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    internal static class L385_AStartCreatesOneSharedPreparation
    {
        internal static IEnumerable<string> Check()
        {
            var a=new MembershipId("a"); var b=new MembershipId("b"); var offer=new OccurrenceId("Modal:GeoAmbushBrief","offer",new[]{"m"});
            var prep=new OccurrenceId("DeploymentPreparing","prep",new[]{"m"}); var order=new HostOrderKey(1,offer.TriggerId);
            var members=new[]{a,b};
            DurableInboxStore Make()=>new DurableInboxStore(new HostLedger(new[]{
                new InboxEntry(offer,a,InboxLifecycle.Dismissed,default(CanonicalChoiceId),2,0,order),
                new InboxEntry(offer,b,InboxLifecycle.Open,default(CanonicalChoiceId),1,0,order)},1,members));
            var failed=Make(); var carrier=new Carrier(); failed.Carriers.Register(offer,DurableCarrierClass.NativeCurrent,carrier);
            failed.ValidateCandidate=_=>false; ulong rev;
            bool before=failed.TryStartDeployment(offer,prep,_=>true,out rev);
            failed.ValidateCandidate=_=>true; failed.WriteRecord=_=>false;
            bool after=failed.TryStartDeployment(offer,prep,_=>true,out rev);
            if(before||after||!failed.Ledger.Contains(offer)||failed.Ledger.Contains(prep)||failed.Carriers.Count(offer)!=1||carrier.Removed!=0)
                yield return "L385 failed-store-consumed-the-offer";
            var store=Make(); var errors=new ConcurrentQueue<Exception>(); var wins=new ConcurrentQueue<bool>();
            Parallel.For(0,32,_=>{try{ulong r;wins.Enqueue(store.TryStartDeployment(offer,prep,x=>true,out r));}catch(Exception ex){errors.Enqueue(ex);}});
            var p=store.Ledger.AllEntries.Where(x=>x.Occurrence.Equals(prep)).ToArray(); var old=store.Ledger.AllEntries.Where(x=>x.Occurrence.Equals(offer)).ToArray();
            if(!errors.IsEmpty||wins.Any(x=>!x)||p.Length!=2||p.Any(x=>x.Lifecycle!=InboxLifecycle.Queued||!x.Predecessor.HasValue||!x.Predecessor.Value.Equals(offer))||
                old.Any(x=>x.Lifecycle!=InboxLifecycle.Removed||x.TerminalReason!=TerminalReason.Superseded||!x.SupersededBy.HasValue||!x.SupersededBy.Value.Equals(prep)))
                yield return "L385 start-was-not-one-atomic-linked-successor-for-all-current-epochs";
            if(store.Ledger.CommittedRevision!=2||store.Journal.Count!=1||p.Any(x=>x.HostOrderKey.CampaignOrdinal!=2||x.HostOrderKey.TriggerId!=prep.TriggerId)||
                old.Any(x=>x.TombstoneRevision!=2))
                yield return "L385 transition-did-not-use-one-journal-revision-and-exact-authoritative-order";
            HostLedger decoded; string why;
            if(!DurableInboxCodec.TryDecodeLedger(store.Ledger.EncodeCanonical(),out decoded,out why)||decoded.AllEntries.Any(x=>x.Occurrence.Equals(prep)&&!x.Predecessor.HasValue))
                yield return "L385 predecessor-link-was-not-durable";
            DurableInboxRestore restored;
            if(!DurableInboxSaveCodec.TryRestore(store.CreateSaveRoot(),null,out restored,out why)||
                restored.Ledger.AllEntries.Any(x=>x.Occurrence.Equals(prep)&&!x.Predecessor.HasValue))
                yield return "L385 schema8-root-did-not-roundtrip-transition-links";
            var legacySource=Make();
            if(!DurableInboxSaveCodec.TryRestore(DurableInboxSaveCodec.CreateSchema7ForMigrationTest(legacySource.Ledger),null,out restored,out why)||
                restored.Ledger.AllEntries.Any(x=>x.Predecessor.HasValue||x.SupersededBy.HasValue))
                yield return "L385 schema7-linkless-root-did-not-migrate";
            var race=Make(); var prep2=new OccurrenceId("DeploymentPreparing","prep-other",new[]{"m"}); bool first=false,second=false;
            Parallel.Invoke(()=>{ulong r;first=race.TryStartDeployment(offer,prep,_=>true,out r);},
                ()=>{ulong r;second=race.TryStartDeployment(offer,prep2,_=>true,out r);});
            var authoritative=race.Ledger.AllEntries.Where(x=>x.Predecessor.HasValue&&x.Predecessor.Value.Equals(offer)).Select(x=>x.Occurrence).Distinct().ToArray();
            if(first==second||authoritative.Length!=1||race.Journal.Count!=1||race.Ledger.CommittedRevision!=2||
                (first&&!authoritative[0].Equals(prep))||(second&&!authoritative[0].Equals(prep2)))
                yield return "L385 different-concurrent-starts-did-not-elect-exactly-one-authoritative-successor";
            bool danglingRejected=false;
            try { new HostLedger(new[]{new InboxEntry(prep,a,InboxLifecycle.Queued,default(CanonicalChoiceId),1,0,
                new HostOrderKey(2,prep.TriggerId),predecessor:offer)},2,members); }
            catch(ArgumentException){danglingRejected=true;}
            if(!danglingRejected) yield return "L385 host-ledger-accepted-a-dangling-predecessor-link";
            // POSITIVE CONTROL: inheriting the predecessor dismissal would dismiss A's fresh successor.
            if(p.Single(x=>x.Membership.Equals(a)).Lifecycle==InboxLifecycle.Dismissed) yield return "L385 control-not-red: predecessor-state-was-inherited";
        }
        private sealed class Carrier:IDurableOccurrenceCarrier
        { internal int Removed; public void RemoveWithoutCallback(TerminalReason reason){Removed++;} }
    }
}

