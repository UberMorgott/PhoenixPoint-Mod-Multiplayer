using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L170 — AN OPEN CONTAINER PANEL SURVIVES A VEHICLE, AND IS STILL ACTUALLY REBUILT.
    ///
    /// THE REPORT (2026-08-07, Player.log:32072-32083 and :32098/:32126/:32193). The owner bought a VEHICLE.
    /// Inside a mission a peer opened its inventory and the mirrored repaint threw
    /// <c>NullReferenceException</c> at <c>UIInventoryList.GetFirstAvailableSlot</c>, caught by
    /// <c>TacticalUiRepaint</c>'s own catch. Twenty seconds later the NATIVE screen threw three more times,
    /// UNCAUGHT, at <c>UIInventoryList.GetTotalItemWeight</c> — twice from the player's own drag/drop
    /// (<c>SlotEndDragHandler</c> → <c>TryMoveItem</c> → <c>RefreshUI</c>) and once from merely opening the
    /// panel. TFTV is in every one of the first stacks and is NOT the cause: its
    /// <c>UIInventoryList_UpdateList_patch.Prefix</c> reproduces native <c>UpdateList</c>:595-597
    /// instruction for instruction, and would throw identically with TFTV uninstalled.
    ///
    /// WHAT IS NULL AND WHY. <c>UIStateInventory.GetVehicleReadyItems</c>:624-632 returns
    /// <c>new List&lt;TacticalItem&gt; { GetVehicleWeapon, GetVehicleHull, GetVehicleEngine }</c> and every
    /// one of those three is a <c>FirstOrDefault</c> (:621, :611, :606). A vehicle with no weapon mounted
    /// yields a THREE-ENTRY LIST WHOSE FIRST ENTRY IS NULL — by design, with no complaint. The game never
    /// lets that list reach a <c>UIInventoryList</c> whole: <c>UpdateSecondaryDataCommand.Execute</c>:182
    /// and <c>UpdatePrimaryDataCommand.Execute</c>:131 split it into three ONE-ITEM sequences, each wrapped
    /// in <c>.Where(x =&gt; x != null)</c>, and hand them to <c>UpdateVehicleSecondaryData</c>:783 /
    /// <c>UpdateVehicleData</c>:735 — three separate per-slot lists, not the flat ready list.
    /// <c>RepaintContainerView</c> called the FLAT <c>UpdateSecondaryData</c>:767 for a vehicle, so the raw
    /// list went through <c>UIInventoryList.Init</c>:447 → <c>SetItems</c>, whose :701 dereferences
    /// <c>item.ItemDef</c> on the null. That is our bug, not the game's and not TFTV's.
    ///
    /// AND IT POISONED THE PANEL FOR THE PLAYER'S OWN HANDS, which is why three UNCAUGHT native throws
    /// followed a caught one. <c>SetItems</c>:120 assigns <c>UnfilteredItems = items</c> BEFORE
    /// <c>ApplyFilter</c>:125 throws, so the null-bearing list stayed installed on <c>SecondaryReadyList</c>
    /// after the exception unwound into our catch — and every later native
    /// <c>GetSecondaryWeight</c>:972 → <c>GetTotalItemWeight</c>:406 walked it and NRE'd again. Nothing
    /// cleans it up: the vehicle branch at :182 does not touch <c>SecondaryReadyList</c> at all. One wrong
    /// call from us, then a broken screen in the owner's hands until he left the mission. No separate guard
    /// is needed for the native path — it was never independently broken.
    ///
    /// ARM (a) IS THE OUTCOME, BOTH WAYS, and it runs the real method rather than reading its shape. Fed the
    /// exact list a bare vehicle produces — <c>{ null, item, null }</c> — <c>VehicleSlot</c> must yield a
    /// sequence with NO NULL IN IT for the empty slots (nothing null reaches a list, so nothing throws) and
    /// must still yield the REAL ITEM for the filled one (the panel is rebuilt, not silently blanked). The
    /// empty answer must also be EMPTY-NOT-NULL: <c>UpdateVehicleData</c>:749-763 skips a list whose argument
    /// is null, so a null answer would leave the slot painting the PREVIOUS vehicle's weapon — a stale panel
    /// dressed up as a fix, which is the failure this law's own title forbids.
    ///
    /// ARM (b) IS THE ROUTING. <c>RepaintContainerView</c> must reach both vehicle entry points. Without it
    /// arm (a) is a rule about a helper nobody calls.
    ///
    /// ARM (c) IS THE POSITIVE CONTROL, taken from the GAME's IL rather than assumed: the two native
    /// commands must still be the ones that split a vehicle onto the per-slot lists. The instant the game
    /// stops doing that, our mirror of it is wrong in some new way and arms (a)/(b) are enforcing a shape
    /// the game abandoned.
    ///
    /// Falsify: make <c>VehicleSlot</c> pass the raw list through → <c>null-reaches-list</c>; make it drop a
    /// non-null item → <c>slot-blanked</c>; return null instead of an empty sequence →
    /// <c>slot-skipped</c>; send a vehicle back through the flat <c>UpdateSecondaryData</c>/<c>UpdateData</c>
    /// → <c>vehicle-unrouted</c>; change what the native commands call → <c>POSITIVE CONTROL</c>; rename any
    /// member → <c>premise-changed</c>.
    /// </summary>
    internal static class L170_VehicleSecondarySurvivesTheRepaint
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(TacticalUiRepaint);
            var game = typeof(UIModuleSoldierEquip).Assembly;

            var vehicleSlot = repaint.GetMethod("VehicleSlot", All);
            var container = repaint.GetMethod("RepaintContainerView", All);
            var updateVehicle = typeof(UIModuleSoldierEquip).GetMethod("UpdateVehicleData", All);
            var updateVehicleSecondary = typeof(UIModuleSoldierEquip).GetMethod("UpdateVehicleSecondaryData", All);
            var vehicleReady = typeof(UIStateInventory).GetMethod("GetVehicleReadyItems", All);
            var primaryCommand = typeof(UIStateInventory).GetNestedType("UpdatePrimaryDataCommand", All)?.GetMethod("Execute", All);
            var secondaryCommand = typeof(UIStateInventory).GetNestedType("UpdateSecondaryDataCommand", All)?.GetMethod("Execute", All);

            if (vehicleSlot == null || container == null || updateVehicle == null ||
                updateVehicleSecondary == null || vehicleReady == null ||
                primaryCommand == null || secondaryCommand == null)
            {
                yield return "L170 premise-changed: one of TacticalUiRepaint.{VehicleSlot," +
                             "RepaintContainerView}, UIModuleSoldierEquip.{UpdateVehicleData," +
                             "UpdateVehicleSecondaryData}, UIStateInventory.GetVehicleReadyItems or the " +
                             "Execute of UIStateInventory.{UpdatePrimaryDataCommand,UpdateSecondaryDataCommand} " +
                             "no longer resolves. The vehicle inventory path has been reshaped and this law is " +
                             "asserting something about a shape it no longer has — re-read how a vehicle's " +
                             "three equipment slots reach a UIInventoryList before trusting the repaint.";
                yield break;
            }

            // The exact list a bare vehicle produces: GetVehicleReadyItems:626-631 keeps the three
            // FirstOrDefault results in slot order, nulls and all.
            var item = MakeItem();
            if (item == null)
            {
                yield return "L170 premise-changed: cannot materialise a TacticalItem to stand in for a " +
                             "mounted vehicle module, so the both-ways arm cannot run. Without it this law " +
                             "would only ever prove that nulls are dropped — never that a real weapon still " +
                             "reaches the slot — and a VehicleSlot that returned nothing at all would pass.";
                yield break;
            }
            var bareVehicle = new List<ICommonItem> { null, item, null };

            foreach (var index in new[] { 0, 1, 2 })
            {
                var answer = Invoke(vehicleSlot, bareVehicle, index);
                if (answer == null)
                {
                    yield return "L170 slot-skipped: VehicleSlot returned NULL for vehicle equipment slot " +
                                 index + " instead of an empty sequence. UpdateVehicleData:749-763 and " +
                                 "UpdateVehicleSecondaryData:792-806 skip a list entirely when its argument " +
                                 "is null — they only Deinit+Init it when something, even nothing, is passed. " +
                                 "So an empty slot would keep painting whatever the PREVIOUS vehicle had " +
                                 "mounted there, and a peer would be looking at another vehicle's weapon with " +
                                 "no way to tell. Empty is not null and the two are not interchangeable here.";
                    continue;
                }
                var got = answer.ToList();
                if (got.Any(x => x == null))
                    yield return "L170 null-reaches-list: VehicleSlot let a NULL through for vehicle " +
                                 "equipment slot " + index + ". GetVehicleReadyItems:624-632 puts one entry " +
                                 "per slot from a FirstOrDefault (:621/:611/:606), so an unmounted weapon IS " +
                                 "a null and this is the ordinary case, not an error case. A null that " +
                                 "reaches UIInventoryList.SetItems dereferences item.ItemDef at :701 and " +
                                 "throws — and worse, SetItems:120 has already installed the poisoned list by " +
                                 "then, so every later native GetSecondaryWeight:972 → GetTotalItemWeight:406 " +
                                 "throws too, in the player's own hands, uncaught, until he leaves the mission.";
                if (index == 1 && (got.Count != 1 || !ReferenceEquals(got[0], item)))
                    yield return "L170 slot-blanked: VehicleSlot did not return the MOUNTED module for slot 1 " +
                                 "(got " + got.Count + " item(s)). Dropping nulls is only half the outcome — " +
                                 "the panel has to be REBUILT, and a helper that answers empty for every slot " +
                                 "would satisfy the no-null arm while quietly painting a vehicle with no " +
                                 "weapon, no hull and no engine on every peer that mirrors it.";
                if (index != 1 && got.Count != 0)
                    yield return "L170 slot-blanked: VehicleSlot invented " + got.Count + " item(s) for empty " +
                                 "vehicle equipment slot " + index + ". An unmounted slot must come back " +
                                 "empty, the way the game's own .Where(x => x != null) at " +
                                 "UpdateSecondaryDataCommand.Execute:182 leaves it.";
            }

            // ── (b) THE ROUTING: a vehicle goes to the per-slot entry points ──
            var repaintCalls = Program.Callees(container, game).ToList();
            foreach (var entry in new[] { updateVehicle, updateVehicleSecondary })
                if (!repaintCalls.Any(c => Same(c, entry)))
                    yield return "L170 vehicle-unrouted: TacticalUiRepaint.RepaintContainerView never calls " +
                                 "UIModuleSoldierEquip." + entry.Name + ", so a vehicle's three equipment " +
                                 "slots are being handed to the FLAT ready list the way they were when the " +
                                 "owner's mission threw. The flat call is right for a soldier and wrong for a " +
                                 "vehicle: it skips the game's own per-slot split and its null strip in one " +
                                 "move, and it also drops weapon, hull and engine into a single list that was " +
                                 "never meant to hold them.";

            // ── (c) POSITIVE CONTROL: the game still splits vehicles this way ──
            foreach (var pair in new[]
                     {
                         new KeyValuePair<MethodBase, MethodInfo>(primaryCommand, updateVehicle),
                         new KeyValuePair<MethodBase, MethodInfo>(secondaryCommand, updateVehicleSecondary),
                     })
                if (!Program.Callees(pair.Key, game).Any(c => Same(c, pair.Value)))
                    yield return "L170 POSITIVE CONTROL: the game's own " + pair.Key.DeclaringType?.Name +
                                 ".Execute no longer calls UIModuleSoldierEquip." + pair.Value.Name + ". Our " +
                                 "repaint mirrors those two commands deliberately — the per-slot split AND " +
                                 "the .Where(x => x != null) that comes with it are copied from them — so if " +
                                 "the game has moved to some other way of feeding a vehicle's equipment " +
                                 "slots, arms (a) and (b) are now enforcing a shape nothing else in the game " +
                                 "has, and the repaint needs re-deriving from whatever replaced them.";
        }

        /// <summary>A stand-in for a mounted vehicle module. Never touched, only compared by reference — the
        /// arm needs an ICommonItem that is not null, and constructing a real one would need a live battle.</summary>
        private static ICommonItem MakeItem()
        {
            try { return FormatterServices.GetUninitializedObject(typeof(TacticalItem)) as ICommonItem; }
            catch (Exception) { return null; }
        }

        private static IEnumerable<ICommonItem> Invoke(MethodInfo vehicleSlot, List<ICommonItem> ready, int index)
        {
            try { return vehicleSlot.Invoke(null, new object[] { ready, index }) as IEnumerable<ICommonItem>; }
            catch (Exception) { return null; }
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
