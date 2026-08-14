using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Items;

namespace RailCheck
{
    /// <summary>
    /// L443 — A RESEARCHED ITEM IS MANUFACTURABLE ON EVERY PEER, NOT ONLY ON THE HOST.
    ///
    /// <c>ItemManufacturing.ManufacturableItems</c> is NOT a <c>[SerializeMember]</c> (only <c>_queue</c> is),
    /// so the generic DTO rail never walks it — <c>docs/rail-baseline.txt:214</c> reads
    /// <c>ItemManufacturing [direct] covered=1/1</c>, and that one is the queue. The producing chain is
    /// host-only: <c>ManufactureResearchReward.GiveReward → AddAvailableItem</c>, reached from
    /// <c>ResearchElement.Complete → ApplyRewards</c>, which a client never runs because research arrives as a
    /// LEAF FIELD WRITE of <c>_state</c> that bypasses the setter. So the unlock has exactly one carrier: the
    /// 0xAD manufacture snapshot. Lose it and every client keeps the STARTING item set for the whole campaign
    /// while the host builds from the researched one — no exception, no log line, and the queue rebuild
    /// silently falls back to <c>new ManufacturableItem(def)</c>, dropping any live cost multiplier with it.
    ///
    /// ARMS
    ///   (a) <c>POSITIVE CONTROL / codec</c> — the real <c>EncodeSnapshot</c> is INVOKED and its bytes are
    ///       decoded here: the queue block must still round-trip in order, and the unlocked-set block must
    ///       follow it with the exact guids handed in. Deleting the trailing write, or writing a wrong count,
    ///       is caught by the wire itself rather than by a shape assertion that can agree with a broken codec.
    ///   (b) <c>unlock-block-is-not-trailing</c> — the unlocked set must sit AFTER the queue block. That
    ///       ordering is the back-compat contract the decoder leans on (it reads the block only while bytes
    ///       remain); moving it forward silently mis-parses every payload an older peer sends.
    ///   (c) <c>apply-drops-the-unlocks</c> — <c>ApplySnapshot</c> must reach BOTH
    ///       <c>ItemManufacturing.AddAvailableItem</c> and <c>RemoveAvailableItem</c>. Add alone leaves a
    ///       <c>RemoveReward</c> un-mirrored (a client keeps building something the host revoked); remove
    ///       alone is the original bug.
    ///   (d) <c>change-key-is-blind-to-unlocks</c> — <c>PushQueueSnapshot</c> must read
    ///       <c>ManufacturableItems</c>. The send is coalesced by a change key over the QUEUE; if an unlock
    ///       does not move that key, a research completion that adds no queue element is never sent at all and
    ///       arm (a) proves a payload nobody transmits.
    ///   (e) <c>apply-does-not-repaint</c> — the apply must still reach <c>RepaintManufacturingUi</c>.
    ///       <c>UIModuleManufacturing</c> is pull-model: an unlock landing behind an OPEN manufacturing screen
    ///       is invisible until the player leaves and re-enters, which postulate 1 calls a defect.
    ///
    /// Falsify: drop the trailing block from <c>EncodeSnapshot</c> → (a); write it before the queue → (b);
    /// delete either reconcile call in <c>ApplySnapshot</c> → (c); fold only the queue into the change key →
    /// (d); drop the repaint → (e).
    /// </summary>
    internal static class L443_AResearchedItemIsManufacturableOnEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(ManufactureSync);
            var encode = sync.GetMethod("EncodeSnapshot", All);
            var apply = sync.GetMethod("ApplySnapshot", All);
            var push = sync.GetMethod("PushQueueSnapshot", All);
            var repaint = sync.GetMethod("RepaintManufacturingUi", All);
            var gameAsm = typeof(ItemManufacturing).Assembly;

            if (encode == null || apply == null || push == null || repaint == null ||
                encode.GetParameters().Length != 3 ||
                encode.GetParameters()[2].ParameterType != typeof(List<string>))
            {
                yield return "L443 premise-changed: ManufactureSync's snapshot seam no longer resolves " +
                             "(EncodeSnapshot(uint, List<KeyValuePair<string,float>>, List<string>) / " +
                             "ApplySnapshot / PushQueueSnapshot / RepaintManufacturingUi). The unlocked " +
                             "manufacturable set has NO other carrier — ManufacturableItems is not a " +
                             "[SerializeMember], so the generic rail cannot see it — so re-point this law at " +
                             "whatever carries the set now; do not delete it, because losing the carrier " +
                             "leaves every client on the STARTING item list for the whole campaign with " +
                             "nothing in any log";
                yield break;
            }

            // ═══ (a)+(b) the REAL codec, executed and decoded ═══
            var entries = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("Q#one", 12f),
                new KeyValuePair<string, float>("Q#two", 34f),
            };
            var available = new List<string> { "U#researched", "U#starting" };
            byte[] wire = null;
            string threw = null;
            try { wire = (byte[])encode.Invoke(null, new object[] { 7u, entries, available }); }
            catch (Exception ex)
            { threw = ((ex as TargetInvocationException)?.InnerException ?? ex).GetType().Name; }
            if (threw != null)
            {
                yield return "L443 premise-changed: ManufactureSync.EncodeSnapshot THREW headless (" + threw +
                             ") — a snapshot encoder that can throw takes the host's whole manufacture " +
                             "surface down mid-session";
                yield break;
            }

            var decodedQueue = new List<KeyValuePair<string, float>>();
            List<string> decodedAvailable = null;
            uint seq = 0;
            string decodeError = null;
            try
            {
                using (var ms = new MemoryStream(wire))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    seq = r.ReadUInt32();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                        decodedQueue.Add(new KeyValuePair<string, float>(r.ReadString(), r.ReadSingle()));
                    if (ms.Position < ms.Length)
                    {
                        int m = r.ReadUInt16();
                        decodedAvailable = new List<string>(m);
                        for (int i = 0; i < m; i++) decodedAvailable.Add(r.ReadString());
                        if (ms.Position != ms.Length) decodeError = "trailing bytes after the unlocked set";
                    }
                }
            }
            catch (Exception ex) { decodeError = ex.GetType().Name; }

            if (decodeError != null)
                yield return "L443 POSITIVE CONTROL: the 0xAD snapshot no longer decodes as " +
                             "[u32 seq][u16 n][guid,f32 xn][u16 m][guid xm] (" + decodeError + "). Both peers " +
                             "read this surface with one hand-written codec; a shape change that only one " +
                             "side knows about is a silent mis-parse of every manufacture delta.";
            else if (seq != 7u || decodedQueue.Count != entries.Count ||
                     decodedQueue.Where((e, i) => e.Key != entries[i].Key || e.Value != entries[i].Value).Any())
                yield return "L443 POSITIVE CONTROL: the QUEUE block no longer round-trips in order through " +
                             "EncodeSnapshot. The queue is an un-keyable list addressed by POSITION — order " +
                             "is the identity, and a client intent carries the index it saw.";
            else if (decodedAvailable == null)
                yield return "L443 unlock-block-missing: EncodeSnapshot emits no unlocked-manufacturable " +
                             "block, so a research reward (ManufactureResearchReward.GiveReward → " +
                             "AddAvailableItem, host-only) reaches no client. Every client is stuck on the " +
                             "STARTING item set for the rest of the campaign.";
            else if (!decodedAvailable.SequenceEqual(available))
                yield return "L443 unlock-block-corrupt: the unlocked-manufacturable block does not carry the " +
                             "guids it was given (got [" + string.Join(",", decodedAvailable.ToArray()) +
                             "]). The client resolves each guid through DefRepository and hands the def to " +
                             "AddAvailableItem — a wrong or truncated set unlocks the wrong items.";

            // ═══ (c) the apply reconciles BOTH directions ═══
            var applyCallees = Program.Callees(apply, gameAsm).Select(m => m.Name).ToList();
            if (!applyCallees.Contains("AddAvailableItem"))
                yield return "L443 apply-drops-the-unlocks: ManufactureSync.ApplySnapshot never reaches " +
                             "ItemManufacturing.AddAvailableItem, so the mirrored set is decoded and thrown " +
                             "away. The queue rebuild then falls back to new ManufacturableItem(def) for " +
                             "every researched item, losing any host-applied cost multiplier with it.";
            if (!applyCallees.Contains("RemoveAvailableItem"))
                yield return "L443 apply-drops-the-unlocks: ManufactureSync.ApplySnapshot never reaches " +
                             "ItemManufacturing.RemoveAvailableItem. The reconcile is one-way: a revoked " +
                             "unlock (ManufactureRemoveReward) stays on the client forever, which is a client " +
                             "queueing something the host will reject on every intent.";

            // ═══ (d) an unlock alone must move the change key ═══
            if (!Program.Callees(push, gameAsm).Any(m => m.Name == "get_ManufacturableItems"))
                yield return "L443 change-key-is-blind-to-unlocks: ManufactureSync.PushQueueSnapshot no longer " +
                             "reads ManufacturableItems. The send is coalesced by a change key; a research " +
                             "completion that adds no queue element does not move a queue-only key, so the " +
                             "unlock is never transmitted at all and the codec arm above proves a payload " +
                             "nobody sends.";

            // ═══ (e) and it repaints the open screen ═══
            if (!Program.Callees(apply, sync.Assembly).Any(m => m.MetadataToken == repaint.MetadataToken))
                yield return "L443 apply-does-not-repaint: ManufactureSync.ApplySnapshot no longer reaches " +
                             "RepaintManufacturingUi. UIModuleManufacturing is pull-model (it subscribes to " +
                             "no ItemManufacturing event), so a newly unlocked item landing behind an OPEN " +
                             "manufacturing screen stays invisible until the player leaves and re-enters — " +
                             "postulate 1 calls that a defect, not a cosmetic issue.";
        }
    }
}
