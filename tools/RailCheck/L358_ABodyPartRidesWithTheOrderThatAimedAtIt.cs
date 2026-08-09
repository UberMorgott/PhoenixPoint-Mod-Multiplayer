using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L358 — THE LIMB THE SHOT WAS AIMED AT REACHES THE OTHER PEER, IN BOTH RECEIVER SHAPES.
    ///
    /// A7's item address had ONE anchor, <c>Item.InventoryComponent</c>:45, and a body part has none — it hangs
    /// in an <c>ItemSlot</c> (<c>TacticalItem.ParentItemSlot</c>:121) and never entered an inventory. So
    /// <c>TacticalAbilityTarget.TacticalItem</c> did not ride for any limb, and the compensation in
    /// <c>Read</c> only refills it when the <c>DamageReceiver</c> IS the limb.
    ///
    /// IT IS NOT, ON THE PATH THAT MATTERS. <c>Weapon.GetShootTargets</c>:841-844 — the <c>ShootAbility</c>
    /// SnapToBodyparts path — sets <c>DamageReceiver</c> to the target ACTOR and early-exits at :853. (Its two
    /// siblings do compensate: <c>Weapon.cs</c>:887-888 and <c>GetShootTarget</c>:790-795 set both fields off
    /// one receiver.) So a shot aimed at a head arrived naming the whole actor, with no slot and no item, 31
    /// times in one captured mission.
    ///
    /// WHAT IS AND IS NOT AT STAKE, stated honestly: HEALTH does not diverge here. Damage is authoritative and
    /// crosses on its own record. What is lost is INTENT — the client aimed at a head and the host replays the
    /// order against whatever <c>GetWorkingPosition</c>:181 picks on its own screen — and the presentation that
    /// follows it.
    ///
    /// THE FIX IS A SECOND RUNG ON THE SAME ADDRESS, not a second scheme: (actor key, KindBodyPart, def guid,
    /// slot name), resolved by the resolver that already exists for exactly this pair
    /// (<c>TacticalDamageSync.ResolveItem</c> → <c>CharacterBodyState.GetItem</c>:178-181). The wire therefore
    /// grew one string per item field — and a field written and not read does not lose the field, it MISALIGNS
    /// every byte after it, which is why arm (a) executes the alignment rather than reading the code.
    ///
    /// Falsify (each verified RED, then restored): drop the slot <c>ReadString</c> from <c>ResolveItem</c> →
    /// (a) address-misaligned; drop the slot <c>Write</c> → (a) reader-does-not-consume-it; delete the
    /// body-part rung from <c>ItemAddress</c> → (b) no-second-rung; route the body-part kind through
    /// <c>ContainerOf</c> instead of the shared resolver → (b) third-scheme; delete the DamageReceiver
    /// compensation from <c>Read</c> → (c).
    /// </summary>
    internal static class L358_ABodyPartRidesWithTheOrderThatAimedAtIt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var codec = typeof(TacAbilityTargetCodec);
            var asm = codec.Assembly;
            var address = codec.GetMethod("ItemAddress", All);
            var resolve = codec.GetMethod("ResolveItem", All);
            var read = codec.GetMethod("Read", All);
            if (address == null || resolve == null || read == null)
            {
                yield return "L358 premise-changed: TacAbilityTargetCodec.ItemAddress / ResolveItem / Read no longer " +
                             "all resolve. They are the whole of A7's item rider — without them nothing carries " +
                             "WHICH limb an order was aimed at, and every arm below would pass while every shot " +
                             "replays against a different body part on every other screen.";
                yield break;
            }

            // ── (a) EXECUTED: THE BODY-PART ADDRESS SURVIVES THE WIRE INTACT ─────────
            // Not "is the field there" — is every byte of it consumed. The reader and the writer disagreeing by
            // one string is not a lost limb, it is a misparsed order.
            byte[] wire;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(TacAbilityTargetCodec.BitTacticalItem);
                    w.Write(4242);                                   // owner key: unresolvable here, by design
                    w.Write(TacticalInventorySync.KindBodyPart);
                    w.Write("def-limb-guid");
                    w.Write(-1);                                     // charge: not a body part's field
                    w.Write(0);                                      // ordinal: likewise
                    w.Write("HEAD_SLOT");
                }
                wire = ms.ToArray();
            }
            string threw = null;
            long left = -1;
            var unresolved = new List<string>();
            using (var ms = new MemoryStream(wire))
            using (var rd = new BinaryReader(ms, Encoding.UTF8))
            {
                try { TacAbilityTargetCodec.Read(rd, null, unresolved); left = ms.Length - ms.Position; }
                catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name; Console.Error.WriteLine("TEMP-TRACE L358 >>> " + ex); }
            }
            if (threw != null)
                yield return "L358 address-misaligned: a target carrying a BODY-PART item address threw on decode (" +
                             threw + "). The reader is consuming a different number of fields than the writer emits, " +
                             "so it is not the limb that is lost — it is every byte after it, and the order is " +
                             "replayed against garbage.";
            else if (left != 0)
                yield return "L358 reader-does-not-consume-it: " + left + " byte(s) of a body-part item address were " +
                             "left unread. A payload is a stream: whatever this reader skipped, the NEXT field on " +
                             "the same wire will read as its own, silently.";
            else if (unresolved.Count == 0)
                yield return "L358 unresolvable-limb-is-silent: a body-part address whose owner this peer cannot " +
                             "resolve produced no sentence at all. A limb that quietly comes back null is the " +
                             "original 31-times-a-mission defect with the log line removed.";

            // ── (b) THE SECOND RUNG EXISTS AND REUSES THE ONE RESOLVER ───────────────
            // CalleeSequence, not Callees: ParentItemSlot and GetAllDirectItems live in the GAME assembly and
            // Callees filters to ours, so the one-assembly walker cannot see either of them at all.
            var minted = Program.CalleeSequence(address);
            if (!minted.Any(c => c.Name == "get_ParentItemSlot"))
                yield return "L358 no-second-rung: ItemAddress never reads TacticalItem.ParentItemSlot, so its only " +
                             "anchor is Item.InventoryComponent again — which a body part does not have. Every limb " +
                             "then fails to ride and the receiving peer re-derives an aim point of its own.";
            var resolved = Program.Callees(resolve, asm).ToList();
            var shared = typeof(TacticalDamageSync).GetMethod("ResolveItem", All);
            if (shared == null || !resolved.Any(c => c.MetadataToken == shared.MetadataToken && c.Module == shared.Module))
                yield return "L358 third-scheme: the target codec's ResolveItem does not go through " +
                             "TacticalDamageSync.ResolveItem, which is the game's own CharacterBodyState.GetItem " +
                             "(slotName, def) lookup and already the damage rail's answer for this exact pair. A " +
                             "second way to turn (slot, def) into an item is a second answer that can drift from it.";

            // ── (c) THE OTHER RECEIVER SHAPE STILL COMPENSATES ───────────────────────
            // When the host DID name the limb as the DamageReceiver, Read replays Weapon.GetShootTarget:792-795's
            // own rule off it. That path is not replaced by the rung above; both shapes occur.
            if (!Program.CalleeSequence(read).Any(c => c.Name == "GetAllDirectItems"))
                yield return "L358 compensation-gone: Read no longer re-derives the aimed-at item from the " +
                             "DamageReceiver's slot. That is the OTHER receiver shape — Weapon.cs:887-888 and " +
                             "GetShootTarget:790-795 name the limb as the receiver rather than as the item — and it " +
                             "is not covered by the ride, so removing it re-opens half the hole.";
        }
    }
}
