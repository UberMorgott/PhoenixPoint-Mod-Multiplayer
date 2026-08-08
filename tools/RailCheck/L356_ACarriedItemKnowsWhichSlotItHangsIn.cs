using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities.Addons;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L356 — A CARRIED ITEM'S ADDRESS NAMES THE SLOT IT HANGS IN, AND IT IS NEVER EMPTY.
    ///
    /// THE 0% DELIVERY. This repo believed, in code and in doctrine, that a <c>TacticalItem</c> forwards
    /// <c>GetSlotName()</c> to its slot. It does not. <c>DamageReceiverImplementation.GetSlotName</c>:71-74 is
    /// the method that forwards to <c>_itemSlot</c>; <c>TacticalItem.GetSlotName</c>:634-637 is
    /// <c>return "";</c>, hardcoded, and <c>TacticalItem</c> does not delegate to its damage implementation for
    /// it. So <c>TacticalActorKey.SlotOf</c> answered "" — "the whole actor" — for every weapon and every limb,
    /// <c>TacticalDamageSync.ItemGuidOf</c> saw an empty slot and REFUSED to ship, and not one item-damage
    /// record crossed for the entire life of that arc. The four refusal lines in the 2026-08-08 capture looked
    /// like four events; they were four DEFS collapsed by one <c>SayOnce</c>.
    ///
    /// L322 was GREEN throughout. It asserted that the seam is installed, that <c>OnDamageApplied</c> CALLS
    /// <c>ItemGuidOf</c>, that <c>ApplyDamage</c> reads two strings and CALLS <c>ResolveItem</c> — every
    /// mechanism, and the mechanism all worked. What nothing asked was the OUTCOME: what the address a real
    /// item produces actually IS. That question is this law, and it is asked by BUILDING an item in a slot and
    /// reading the answer.
    ///
    /// THE POSITIVE CONTROL IS THE WHOLE POINT: the naive form — <c>receiver.GetSlotName()</c>, which is what
    /// the code did — must come back "" and turn arm (c) RED. A law that cannot tell the shipped rule from the
    /// one that delivered nothing for a month is not a law.
    ///
    /// Falsify (each verified RED, then restored): make <c>SlotOf</c> return <c>receiver.GetSlotName()</c> for
    /// every shape → (a) item-address-empty + (b) round-trip; delete the <c>ParentItemSlot</c> rung → same;
    /// return the item's own def name instead of the slot's → (b) slot-name-not-the-slots.
    /// </summary>
    internal static class L356_ACarriedItemKnowsWhichSlotItHangsIn
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal const string SlotName = "RANGED_ITEM_SLOT";
        internal const string ItemGuid = "def-rifle-guid";

        internal static IEnumerable<string> Check()
        {
            var slotOf = typeof(TacticalActorKey).GetMethod("SlotOf", All);
            if (slotOf == null || slotOf.ReturnType != typeof(string))
            {
                yield return "L356 premise-changed: TacticalActorKey.SlotOf(IDamageReceiver)->string no longer " +
                             "resolves. It is the ONE place the rail turns a damage receiver into an address, " +
                             "shared by the damage capture, the status target tag, the resnapshot's item block and " +
                             "the ability-target rider — without it every arm below would pass vacuously while " +
                             "carried-item damage crosses nowhere.";
                yield break;
            }

            TacticalItem item; ItemSlot slot; string built;
            Limb(out item, out slot, out built);
            if (built != null)
            {
                yield return "L356 fixture-unbuildable: the harness could not stand up a TacticalItem hanging in an " +
                             "ItemSlot (" + built + "), so the one thing this law exists to prove — that a real " +
                             "item's address is not empty — is UNCHECKED rather than satisfied. Re-point the " +
                             "fixture at the game's current Addon/ItemSlot shape.";
                yield break;
            }

            // ── (a) POSITIVE CONTROL, AND IT IS THE DEFECT ITSELF ────────────────────
            // The naive address is what this repo shipped: ask the receiver directly. It MUST come back empty,
            // both because that is the game's documented behaviour (TacticalItem.cs:634-637 is `return "";`) and
            // because arm (b) is worthless if the rule it checks cannot be told apart from this one.
            string naive = ((IDamageReceiver)item).GetSlotName() ?? "<null>";
            if (naive != "")
                yield return "L356 control-not-red: the naive address — receiver.GetSlotName(), which is precisely " +
                             "what shipped and relayed zero item damage — came back '" + naive + "' instead of " +
                             "empty. Either TacticalItem stopped hardcoding \"\" (re-read the decompile: the rung " +
                             "in SlotOf may now be redundant) or this fixture is not a real item. Until it is " +
                             "resolved, arm (b) cannot tell the fixed rule from the broken one.";

            // ── (b) THE ADDRESS A REAL ITEM PRODUCES ─────────────────────────────────
            string got = null, threw = null;
            try { got = (string)slotOf.Invoke(null, new object[] { item }); }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            if (threw != null)
                yield return "L356 address-throws: SlotOf threw on an ordinary item in a slot (" + threw + "). Every " +
                             "hit, every status and every mirrored order goes through it, so a throwing address is " +
                             "not a lost record — it is a lost battle.";
            else if (string.IsNullOrEmpty(got))
                yield return "L356 item-address-empty: SlotOf answers \"\" for a TacticalItem hanging in slot '" +
                             SlotName + "'. \"\" is the wire's word for THE WHOLE ACTOR, so ItemGuidOf refuses to " +
                             "ship the record at all — this is the 0%-delivery defect verbatim, and it was green " +
                             "under every mechanism law in L322.";
            else if (got != SlotName)
                yield return "L356 slot-name-not-the-slots: SlotOf answers '" + got + "' for an item hanging in " +
                             "slot '" + SlotName + "'. The far side resolves it with the game's own " +
                             "CharacterBodyState.GetItem(slotName, def), which matches on ItemSlotDef.SlotName and " +
                             "nothing else, so any other string resolves to nothing on every peer.";

            // ── (c) THE ROUND TRIP, THROUGH THE GAME'S OWN MATCH ─────────────────────
            // CharacterBodyState.GetItem:178-181 is GetSlot(name)?.GetAllDirectItems().First(i => i.ItemDef == def).
            // The actor→slot hop is L66c's (CharacterBodyState.GetSlot); what is asked here is the half this
            // address adds: does (that slot name, that def) come back to THIS item?
            string tripped = null;
            TacticalItem back = null;
            try
            {
                var named = slot.GetSlotName() == got ? slot : null;
                back = named == null ? null : named.GetAllDirectItems().FirstOrDefault(i => i.ItemDef == item.ItemDef);
            }
            catch (Exception ex) { tripped = (ex.InnerException ?? ex).GetType().Name; }
            if (tripped != null)
                yield return "L356 round-trip-throws: resolving the address back through the slot threw (" + tripped +
                             "), so this law can say nothing about whether it round-trips.";
            else if (!ReferenceEquals(back, item))
                yield return "L356 address-resolves-elsewhere: the address SlotOf minted does not resolve back to " +
                             "the item it was minted for. A damaged rifle's hit points then land on some other " +
                             "item — or on nothing — on every peer but the sender's.";

            // ── (d) ONE RULE, NOT FOUR COPIES ────────────────────────────────────────
            // Every caller that needs a receiver's slot must ask SlotOf. A second hand-rolled GetSlotName() call
            // on the ship path is a fifth answer to the same question and is how this bug survived.
            var asm = typeof(TacticalActorKey).Assembly;
            foreach (var site in new[]
                     {
                         Tuple.Create((MethodBase)typeof(TacticalDamageSync).GetMethod("OnDamageApplied", All),
                                      "the host's damage capture"),
                         Tuple.Create((MethodBase)typeof(TacticalStatusSet).GetMethod("TargetTag", All),
                                      "the status target tag"),
                     })
            {
                if (site.Item1 == null)
                {
                    yield return "L356 caller-gone: " + site.Item2 + " no longer resolves, so whether it derives a " +
                                 "receiver's slot through the ONE rule is unprovable.";
                    continue;
                }
                if (!Program.Callees(site.Item1, asm).Any(c => c.Name == "SlotOf"))
                    yield return "L356 second-answer: " + site.Item2 + " does not go through TacticalActorKey.SlotOf " +
                                 "(" + site.Item1.Name + "). " +
                                 "It then calls IDamageReceiver.GetSlotName() itself, which is \"\" for every " +
                                 "TacticalItem — the same 0%-delivery bug on a second path, and it will be found the " +
                                 "same way this one was: months later, from a log that looked like four events.";
            }
        }

        /// <summary>A real <c>TacticalItem</c> hanging in a real <c>ItemSlot</c>, with nothing on either but the
        /// fields the address rule reads: the slot's def (its <c>SlotName</c> and its <c>DamageHandler</c>), the
        /// item's def, and the parent/child link between them. A live one needs a Unity scene, an addons manager
        /// and a rig root — none of which this harness has any business building. Shared with L322 and L358 so
        /// there is one fixture and not three that can drift.</summary>
        internal static void Limb(out TacticalItem item, out ItemSlot slot, out string error)
        {
            item = null; slot = null; error = null;
            try
            {
                var slotDef = (ItemSlotDef)FormatterServices.GetUninitializedObject(typeof(ItemSlotDef));
                AccessTools.Field(typeof(ItemSlotDef), "SlotName").SetValue(slotDef, SlotName);
                AccessTools.Field(typeof(ItemSlotDef), "DamageHandler").SetValue(slotDef, DamageHandler.AttachedItem);

                slot = (ItemSlot)FormatterServices.GetUninitializedObject(typeof(ItemSlot));
                slot.BaseDef = slotDef;
                AccessTools.Field(typeof(Addon.AddonSlotImpl), "_weakAddons").SetValue(slot, new List<Addon>());

                var itemDef = (ItemDef)FormatterServices.GetUninitializedObject(typeof(ItemDef));
                AccessTools.Field(typeof(Base.Defs.BaseDef), "Guid").SetValue(itemDef, ItemGuid);

                item = (TacticalItem)FormatterServices.GetUninitializedObject(typeof(TacticalItem));
                item.BaseDef = itemDef;
                AccessTools.Field(typeof(Addon), "_parentSlot").SetValue(item, slot);
                slot.StrongAddon = item;
            }
            catch (Exception ex)
            {
                var real = ex.InnerException ?? ex;
                error = real.GetType().Name + ": " + real.Message;
                item = null; slot = null;
            }
        }
    }
}
