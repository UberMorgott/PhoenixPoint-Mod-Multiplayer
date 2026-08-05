using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L112 — TWO ITEMS OF THE SAME DEF IN ONE CONTAINER MUST RESOLVE TO DISTINCT ITEMS.
    ///
    /// A7's item address was <c>(actorKey, kind, defGuid)</c>, so a soldier carrying two magazines of one
    /// type had ONE address for both and every peer resolved whichever its own list held first. That is not
    /// a cosmetic tie: a full clip and a spent one share a def, so a mirrored reload could load the wrong
    /// one on the receiving peer and the two boards then disagreed about how many rounds a weapon had. A6's
    /// batch apply had the same hole one level up — it matched pool items by def guid alone, so a batch
    /// moving ONE of two magazines moved whichever this peer found first.
    ///
    /// THE FIX IS THE ADDRESS, NOT THE LIST ORDER. Container order is still deliberately unsynchronised —
    /// forcing it would mean removing and re-adding items nobody touched, which for an
    /// <c>EquipmentComponent</c> means unmounting a weapon mid-mission. Instead the address gained the two
    /// fields that actually tell same-def items apart, both defined ONCE in
    /// <c>TacticalInventorySync</c> and shared by both riders:
    ///   • <c>ChargeOf</c> — <c>CommonItemData.CurrentCharges</c>, the per-item state;
    ///   • <c>OrdinalOf</c> — position among the items sharing that (def, charge).
    ///
    /// WHAT IS ASSERTED IS THE RESOLUTION, not the shape of the key. The arm BUILDS a container holding
    /// four items — two same-def/same-charge, one same-def/different-charge, one different-def — writes
    /// each one's address the way the rider does, resolves each address back, and requires that all four
    /// come back DISTINCT and each to ITSELF. Deleting either field from the address fails it: without the
    /// charge the two charge-classes collide, without the ordinal the two identical clips do.
    ///
    /// It also holds the layout codec to the same rule (the charge must survive
    /// <c>WriteLayout</c>/<c>ReadLayout</c> — a field written and not read shifts every later field and the
    /// batch lands as garbage with no exception) and requires <c>ApplyLayout</c> to actually consult it,
    /// because an address nobody reads is a comment.
    /// </summary>
    internal static class L112_ItemAddressOrdinal
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var inv = typeof(TacticalInventorySync);
            var chargeOf = inv.GetMethod("ChargeOf", All);
            var ordinalOf = inv.GetMethod("OrdinalOf", All);
            var resolveIn = inv.GetMethod("ResolveIn", All);
            if (chargeOf == null || ordinalOf == null || resolveIn == null)
            {
                yield return "L112 rule-gone: TacticalInventorySync.ChargeOf / OrdinalOf / ResolveIn no longer all " +
                             "exist. Those three ARE the item address rule, shared by A6's batch apply and A7's " +
                             "target codec so the two cannot drift — without them the address is back to a def " +
                             "guid, and two magazines of one type are one address again";
                yield break;
            }

            // ── (a) THE RESOLUTION, executed on a real container ──
            string built = null;
            object container = null;
            object[] items = null;
            try
            {
                container = FormatterServices.GetUninitializedObject(typeof(InventoryComponent));
                var list = new List<Item>();
                typeof(InventoryComponent).GetField("Items", All).SetValue(container, list);
                var defX = Def("def-X");
                var defY = Def("def-Y");
                // A and C are INDISTINGUISHABLE by every field the address can see — they are what the
                // ordinal is for. B shares their def but not their charge. D shares nothing.
                items = new[] { Item(defX, 5, container), Item(defX, 2, container),
                                Item(defX, 5, container), Item(defY, 5, container) };
                foreach (var it in items) list.Add((Item)it);
            }
            catch (Exception ex) { built = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            if (built != null)
            {
                yield return "L112 fixture-unbuildable: the harness could not stand up a container of items (" +
                             built + "), so the one thing this law exists to prove — that two same-def items " +
                             "resolve apart — is UNCHECKED rather than satisfied. Re-point the fixture at the " +
                             "game's current Item/InventoryComponent shape";
                yield break;
            }

            var resolved = new List<object>();
            string threw = null;
            try
            {
                foreach (var it in items)
                {
                    string guid = ((Item)it).ItemDef.Guid;
                    int charge = (int)chargeOf.Invoke(null, new[] { it });
                    int ordinal = (int)ordinalOf.Invoke(null, new[] { it });
                    resolved.Add(resolveIn.Invoke(null, new object[] { container, guid, charge, ordinal }));
                }
            }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            if (threw != null)
                yield return "L112 address-throws: writing or resolving an item address threw (" + threw + "). " +
                             "Every mirrored reload, grenade and weapon-switch carries one, so a throwing address " +
                             "is a rider that stops crossing at all";
            else
            {
                for (int i = 0; i < items.Length; i++)
                    if (!ReferenceEquals(resolved[i], items[i]))
                        yield return "L112 address-resolves-elsewhere: item #" + i + " does not resolve back to " +
                                     "ITSELF — the address written for it names a different item on the very " +
                                     "container it came from. Two identical magazines and one half-empty one is " +
                                     "the ordinary case, not an edge: a mirrored reload then loads the wrong clip " +
                                     "and the two boards disagree about the weapon's ammo for the rest of the " +
                                     "mission";
                var distinct = resolved.Where(o => o != null).Distinct().Count();
                if (distinct != items.Length)
                    yield return "L112 same-def-items-collide: " + items.Length + " items in one container " +
                                 "resolve to only " + distinct + " distinct item(s). The address has stopped " +
                                 "telling same-def items apart — which is exactly the (actorKey, kind, defGuid) " +
                                 "ceiling this law replaced, where the far side resolved 'the first one' and the " +
                                 "peers picked different clips";
            }

            // ── (b) THE CHARGE SURVIVES THE LAYOUT WIRE ──
            var write = inv.GetMethod("WriteLayout", All);
            var read = inv.GetMethod("ReadLayout", All);
            var slotType = inv.GetNestedType("Slot", All);
            if (write == null || read == null || slotType == null)
                yield return "L112 layout-codec-gone: WriteLayout / ReadLayout / Slot no longer resolve, so " +
                             "nothing checks that the per-item charge reaches the far side. A charge written and " +
                             "not read shifts every later field and the whole batch decodes as garbage, silently";
            else
            {
                string bad = null;
                try
                {
                    var listType = typeof(List<>).MakeGenericType(slotType);
                    var slots = (System.Collections.IList)Activator.CreateInstance(listType);
                    var s = Activator.CreateInstance(slotType);
                    slotType.GetField("ActorKey", All).SetValue(s, 9);
                    slotType.GetField("Kind", All).SetValue(s, (byte)0);
                    var defs = (List<string>)slotType.GetField("ItemDefs", All).GetValue(s);
                    var charges = (List<int>)slotType.GetField("ItemCharges", All).GetValue(s);
                    defs.Add("def-X"); charges.Add(5);
                    defs.Add("def-X"); charges.Add(2);
                    slots.Add(s);
                    byte[] bytes;
                    using (var ms = new MemoryStream())
                    {
                        var w = new BinaryWriter(ms, Encoding.UTF8);
                        write.Invoke(null, new object[] { w, slots, 1, 0f, false, false });
                        w.Flush();
                        bytes = ms.ToArray();
                    }
                    using (var ms = new MemoryStream(bytes))
                    {
                        var rd = new BinaryReader(ms, Encoding.UTF8);
                        var args = new object[] { rd, null, null, null, null };
                        var back = (System.Collections.IList)read.Invoke(null, args);
                        var gotCharges = (List<int>)slotType.GetField("ItemCharges", All).GetValue(back[0]);
                        var gotDefs = (List<string>)slotType.GetField("ItemDefs", All).GetValue(back[0]);
                        if (!gotDefs.SequenceEqual(new[] { "def-X", "def-X" })) bad = "the item defs did not survive";
                        else if (!gotCharges.SequenceEqual(new[] { 5, 2 }))
                            bad = "the charges came back as [" + string.Join(",", gotCharges.Select(c => c.ToString())) +
                                  "] instead of [5,2]";
                    }
                }
                catch (Exception ex) { bad = "the codec THREW (" + (ex.InnerException ?? ex).GetType().Name + ")"; }
                if (bad != null)
                    yield return "L112 layout-charge-lost: the per-item charge does not survive the layout wire — " +
                                 bad + ". Both magazines then look identical to the receiver and a batch moving " +
                                 "one of them moves whichever it finds first";
            }

            // ── (c) THE APPLY ACTUALLY CONSULTS IT ──
            var apply = inv.GetMethod("ApplyLayout", All);
            if (apply == null)
                yield return "L112 apply-gone: TacticalInventorySync.ApplyLayout no longer resolves, so whether the " +
                             "batch apply uses the item address is unprovable";
            else if (!Program.Callees(apply, inv.Assembly).Any(c => c.Name == "ChargeOf"))
                yield return "L112 apply-ignores-the-charge: ApplyLayout never calls ChargeOf, so it is back to " +
                             "matching pool items by def guid alone. The address on the wire got wider and the " +
                             "code that reads it did not — a batch moving one of two magazines still moves " +
                             "whichever this peer happens to hold first";
        }

        private static ItemDef Def(string guid)
        {
            var d = (ItemDef)FormatterServices.GetUninitializedObject(typeof(ItemDef));
            AccessTools.Field(typeof(Base.Defs.BaseDef), "Guid").SetValue(d, guid);
            return d;
        }

        /// <summary>A TacticalItem with nothing but the three things the address rule reads: its def, its
        /// charge and its owning container. A real one needs a live Unity scene the harness has no business
        /// building.</summary>
        private static Item Item(ItemDef def, int charges, object container)
        {
            var data = FormatterServices.GetUninitializedObject(typeof(CommonItemData));
            AccessTools.Field(typeof(CommonItemData), "_charges").SetValue(data, charges);
            var item = (TacticalItem)FormatterServices.GetUninitializedObject(typeof(TacticalItem));
            AccessTools.Field(typeof(TacticalItem), "_commonItemData").SetValue(item, data);
            item.BaseDef = def;
            AccessTools.Field(typeof(PhoenixPoint.Common.Entities.Items.Item), "<InventoryComponent>k__BackingField")
                       .SetValue(item, container);
            return item;
        }
    }
}
