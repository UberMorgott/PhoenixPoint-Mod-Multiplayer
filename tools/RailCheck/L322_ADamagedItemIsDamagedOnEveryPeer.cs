using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L322 — AN ITEM DAMAGED ON THE HOST IS DAMAGED IDENTICALLY ON EVERY PEER.
    ///
    /// THE REPORT (2026-08-08, 3-instance battle). A worm crawled up and exploded beside a soldier. The HOST
    /// showed hit-point damage, an infection status, AND a message that the WEAPON IN HIS HANDS was damaged.
    /// The clients got the first two. The third never crossed and never could have, because a whole RECEIVER
    /// SHAPE had no seam.
    ///
    /// <c>IDamageReceiver</c> has three shapes in this game and this arc captured two: the actor
    /// (<c>ActorDamageSeam</c> on <c>TacticalActorBase.ApplyDamageInternal</c>) and the body-part
    /// <c>ItemSlot</c> (<c>SlotDamageSeam</c>). The third is a <c>TacticalItem</c> that holds its OWN hit
    /// points — every weapon, every equipment and every body part mounted in a slot whose
    /// <c>ItemSlotDef.DamageHandler</c> is <c>AttachedItem</c>. <c>TacticalItem.RegisterStats</c>:259-266 gives
    /// it a <c>&lt;slot&gt;_Health</c> stat of its own and <c>TacticalItem.ApplyDamage</c>:294-316 falls
    /// through to <c>DamageReceiverImplementation.ApplyDamage</c>:103-110, which is what an explosion actually
    /// subtracts from. NOTHING patched that method, so the host lost the health, ran
    /// <c>OnHealthReachedZero</c>:662-665 → <c>SetToDisabled</c>, and raised
    /// <c>TacticalActorNotificationsComp.OnItemBroken</c>:68-76 — and the client, whose own damage computation
    /// is deleted one level up at <c>DamageAccumulation.ApplyAddedDamage</c>:550, could not even recompute it.
    /// Host-only state with no carrier: permanent for the rest of the mission.
    ///
    /// THE ADDRESS IS THE OTHER HALF, AND IT IS WHY THIS IS NOT A ONE-LINE PATCH. A <c>TacticalItem</c> reports
    /// the SAME <c>GetSlotName()</c> as the slot it hangs in (<c>DamageReceiverImplementation</c>:71-74 forwards
    /// to <c>_itemSlot</c>), so the rail's existing (actor key, slot name) address — law L66c — names the SLOT,
    /// and a record shipped under it would subtract a damaged rifle's hit points from the ARM holding it. The
    /// item therefore carries one more field, its def GUID, and the far side resolves the pair with the game's
    /// own two-part lookup <c>CharacterBodyState.GetItem(slotName, itemDef)</c>:177-180.
    ///
    /// AND THE CAPTURE MUST BE CONDITIONAL. <c>TacticalItem.ApplyDamage</c> ROUTES before it applies (:301-313):
    /// a <c>Slot</c> handler hands the result to <c>ItemSlot.ApplyDamage</c> and a <c>ParentItem</c> handler to
    /// the owning item, both of which have their own capture. Shipping those would send one hit twice under two
    /// addresses. <c>TacticalDamageSync.ItemAppliesItsOwnDamage</c> is that routing condition as a pure
    /// function, and arm (a) executes every corner of it rather than trusting a comment.
    ///
    /// Falsify (each verified RED, then restored): delete the <c>OnDamageApplied</c> call from
    /// <c>ItemDamageSeam.Postfix</c> — the defect verbatim — → (b) item-damage-never-captured; make
    /// <c>ItemAppliesItsOwnDamage</c> return <c>true</c> always → (a) routed-damage-shipped-twice ×2;
    /// return <c>hasParentSlot</c> → (a) own-damage-unshipped; stop calling it from the postfix →
    /// (b) routing-decision-unreached; drop <c>ItemGuidOf</c> from <c>OnDamageApplied</c> →
    /// (c) item-rides-as-its-slot; drop the <c>ResolveItem</c> branch from <c>ApplyDamage</c> →
    /// (c) item-record-lands-on-the-slot; drop the third <c>ReadString</c> → (c) address-half-read;
    /// drop the item block from the resnapshot → (d).
    /// </summary>
    internal static class L322_ADamagedItemIsDamagedOnEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalDamageSync);
            var asm = sync.Assembly;
            var decide = sync.GetMethod("ItemAppliesItsOwnDamage", All);
            var shipped = sync.GetMethod("OnDamageApplied", All);
            var applied = sync.GetMethod("ApplyDamage", All);
            var resnapped = sync.GetMethod("ApplyResnap", All);
            var seam = asm.GetType("Multiplayer.Tactical.ItemDamageSeam");

            if (decide == null || decide.ReturnType != typeof(bool) || decide.GetParameters().Length != 2 ||
                shipped == null || applied == null || resnapped == null || seam == null)
            {
                yield return "L322 premise-changed: TacticalDamageSync.ItemAppliesItsOwnDamage(bool,DamageHandler)" +
                             "->bool, OnDamageApplied, ApplyDamage, ApplyResnap or the ItemDamageSeam patch class " +
                             "no longer resolves. A carried item's hit points have no other carrier — nothing else " +
                             "in this repo ships or reconciles them — so every arm below would pass vacuously " +
                             "while a weapon damaged on the host stays whole on every other screen.";
                yield break;
            }

            // ── (a) THE ROUTING DECISION, ON ALL FOUR CORNERS ────────────────────────
            Func<bool, DamageHandler, bool> real =
                (s, h) => (bool)decide.Invoke(null, new object[] { s, h });
            foreach (var v in Corners(real, "L322")) yield return v;

            // ── (b) THE CAPTURE EXISTS, ON THE GAME'S OWN METHOD ─────────────────────
            var patched = seam.GetCustomAttributes(typeof(HarmonyPatch), false)
                              .Cast<HarmonyPatch>()
                              .Select(a => a.info)
                              .FirstOrDefault(i => i != null && i.declaringType == typeof(TacticalItem) &&
                                                   i.methodName == "ApplyDamage");
            if (patched == null)
                yield return "L322 seam-off-the-item-method: ItemDamageSeam does not declare " +
                             "[HarmonyPatch(typeof(TacticalItem), \"ApplyDamage\")]. That method is the ONLY " +
                             "funnel where a carried item's own hit points move — ItemSlot.ApplyDamage covers " +
                             "the DamageHandler.Slot limbs and nothing else covers this — so a seam anywhere " +
                             "else captures a different receiver or none.";

            var postfix = seam.GetMethod("Postfix", All);
            var prefix = seam.GetMethod("Prefix", All);
            if (postfix == null || prefix == null)
            {
                yield return "L322 seam-half-gone: ItemDamageSeam is missing its " +
                             (postfix == null ? "Postfix (the host capture)" : "Prefix (the client neuter)") +
                             ". One half ships the host's item damage; the other stops a client computing its " +
                             "own. Without the first the report comes back verbatim; without the second a " +
                             "damage-over-time tick lands twice on every client.";
            }
            else
            {
                var post = Program.Callees(postfix, asm).ToList();
                if (!post.Any(c => c.MetadataToken == shipped.MetadataToken && c.Module == shipped.Module))
                    yield return "L322 item-damage-never-captured: ItemDamageSeam.Postfix does not reach " +
                                 "TacticalDamageSync.OnDamageApplied. This is the reported defect verbatim — the " +
                                 "host subtracts the weapon's hit points, disables it and raises its own " +
                                 "'EquipmentDamaged' notification, and not one byte about it reaches the wire.";
                if (!post.Any(c => c.MetadataToken == decide.MetadataToken && c.Module == decide.Module))
                    yield return "L322 routing-decision-unreached: ItemDamageSeam.Postfix does not call " +
                                 "ItemAppliesItsOwnDamage, so the corner table in arm (a) proves a function the " +
                                 "capture does not consult. TacticalItem.ApplyDamage:301-313 routes a Slot or " +
                                 "ParentItem handler's damage elsewhere and returns — capturing those ships one " +
                                 "hit twice, under two different addresses, and the far side applies both.";
                if (!Program.Callees(prefix, asm).Any(c => c.Name == "DamageIsSomebodyElses"))
                    yield return "L322 item-neuter-unasked: ItemDamageSeam.Prefix never consults " +
                                 "DamageIsSomebodyElses, the one predicate that answers 'may this peer compute " +
                                 "damage'. A client would then apply its own item damage beside the host's " +
                                 "shipped record — and inside a mirror apply it would refuse the host's, which " +
                                 "is the same defect with the sign flipped.";
            }

            // ── (c) THE RECORD CARRIES THE ITEM, AND THE FAR SIDE USES IT ────────────
            if (!Program.Callees(shipped, asm).Any(c => c.Name == "ItemGuidOf"))
                yield return "L322 item-rides-as-its-slot: OnDamageApplied no longer derives the item's def " +
                             "guid. A TacticalItem reports the same GetSlotName() as the slot it hangs in " +
                             "(DamageReceiverImplementation:71-74), so without that third field the record names " +
                             "the SLOT and the far side subtracts a damaged rifle's hit points from the arm " +
                             "holding it — worse than not crossing at all.";

            var seq = Program.CalleeSequence(applied);
            int codec = seq.FindIndex(c => c.Name == "Read" && c.DeclaringType == typeof(DamageResultCodec));
            if (codec < 0)
                yield return "L322 apply-reads-no-result: ApplyDamage no longer decodes a DamageResult, so this " +
                             "law can say nothing about the address that precedes it.";
            else
            {
                if (seq.Take(codec).Count(c => c.Name == "ReadString") < 2)
                    yield return "L322 address-half-read: ApplyDamage reads fewer than two strings before the " +
                                 "DamageResult. The record carries (key, slot name, item def guid); reading one " +
                                 "string fewer than the host wrote does not lose the item — it MISALIGNS every " +
                                 "field after it, so the whole hit decodes as garbage.";
                if (!seq.Skip(codec).Any(c => c.Name == "ResolveItem"))
                    yield return "L322 item-record-lands-on-the-slot: ApplyDamage never calls ResolveItem, so a " +
                                 "record naming an item is resolved through ResolveReceiver like a limb — which " +
                                 "returns the ItemSlot of that name. The damage then lands on the arm and the " +
                                 "weapon stays pristine, which is the original report plus a second divergence.";
            }

            // ── (d) THE RECOVERY PATH CARRIES IT TOO ─────────────────────────────────
            // HandleResnapRequest hands its body to Send as a lambda, so the call lives in a compiler-generated
            // closure — the honest question is "does this type write the item set anywhere" (L131's shape).
            if (!WritesAnywhere(sync, "DamageableItems", asm))
                yield return "L322 resnap-carries-no-items: nothing in TacticalDamageSync enumerates an actor's " +
                             "damageable items for the resnapshot. 0x84 is a stream of discrete events with no " +
                             "re-emit — the resnapshot is the ONLY repair for a lost record — so leaving item " +
                             "health out of it makes exactly one dropped packet a permanently pristine weapon.";
            if (!Program.Callees(resnapped, asm).Any(c => c.Name == "ResolveItem"))
                yield return "L322 resnap-applies-no-items: ApplyResnap reads the host's item health and does " +
                             "nothing with it. The recovery path then repairs every field of that soldier except " +
                             "the one the lost record was about.";

            // ── (e) POSITIVE CONTROL ─────────────────────────────────────────────────
            if (!Corners((s, h) => true, "CONTROL").Any())
                yield return "L322 control-not-red: the naive seam (capture EVERY call to TacticalItem" +
                             ".ApplyDamage, routed or not) passed the corner table, so arm (a) cannot tell the " +
                             "shipped rule from one that relays each limb hit twice.";
        }

        /// <summary>The routing table of <c>TacticalItem.ApplyDamage</c>:301-313, as the capture must read it.
        /// Only the fall-through — no parent slot, or an <c>AttachedItem</c> one — moves THIS item's hit
        /// points; the other two handlers move somebody else's and are captured by that somebody's seam.</summary>
        private static List<string> Corners(Func<bool, DamageHandler, bool> decide, string tag)
        {
            var found = new List<string>();
            if (!decide(false, DamageHandler.AttachedItem))
                found.Add(tag + " orphan-item-unshipped: an item with NO parent slot was not shipped. " +
                          "ApplyDamage:301 skips the whole routing block when ParentItemSlot is null and " +
                          "applies to itself, so that hit moved this item's hit points and nothing else " +
                          "will ever report it.");
            if (!decide(true, DamageHandler.AttachedItem))
                found.Add(tag + " own-damage-unshipped: an item in an AttachedItem slot was not shipped. That " +
                          "is the weapon in the soldier's hands — the whole reported defect — and its health " +
                          "lives on the ITEM, not on the slot, so no other seam can carry it.");
            if (decide(true, DamageHandler.Slot))
                found.Add(tag + " routed-damage-shipped-twice: an item whose slot handles its own damage was " +
                          "shipped from here as well. ApplyDamage:303-307 forwarded the whole result to " +
                          "ItemSlot.ApplyDamage, which SlotDamageSeam already captured, so the far side applies " +
                          "one limb hit twice — once to the slot and once to an item that never lost anything.");
            if (decide(true, DamageHandler.ParentItem))
                found.Add(tag + " routed-damage-shipped-twice (ParentItem): an item that forwarded its damage " +
                          "to its owner (ApplyDamage:308-312) was shipped from here too. The owner's own pass " +
                          "through this same seam ships it properly; this one names an item whose hit points " +
                          "did not move.");
            return found;
        }

        /// <summary>Does this type reach <paramref name="callee"/> from ANY of its methods, closures
        /// included — the honest question for a body handed to <c>Send</c> as a lambda.</summary>
        private static bool WritesAnywhere(Type owner, string callee, Assembly asm)
        {
            foreach (var t in new[] { owner }.Concat(owner.GetNestedTypes(All)))
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                   .Concat(t.GetConstructors(All)))
                    if (Program.Callees(m, asm).Any(c => c.Name == callee)) return true;
            return false;
        }
    }
}
