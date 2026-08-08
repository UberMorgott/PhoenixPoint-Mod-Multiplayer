using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L366 — A LOADOUT INTENT BUILT FROM THE NATIVE CAPACITY-SHAPED SLOT LIST IS ACCEPTED AGAINST A COMPACT
    /// LIVE LIST, AND A NO-OP EXIT FLUSH SHIPS NOTHING.
    ///
    /// Two shapes describe the same loadout and the mod compared them as if they were one number. The native
    /// screen builds one entry per SLOT, null for an empty one
    /// (<c>UIStateVehicleRoster.UpdateVehicleEquipments</c>:246-272), while a save-loaded vehicle's own list
    /// is COMPACT — <c>GeoVehicle.AddNullWeapon</c> has no caller but <c>ReplaceEquipments</c> (decompile
    /// GeoVehicle.cs:884-897), so nothing puts a null in it until some UI does.
    ///
    /// Measured, live: 1 weapon + 2 empty module slots against a host list holding 1 item ⇒ "slot count off
    /// by 2", i.e. the host refused a perfectly legal edit and called it a stale mirror. The same asymmetry
    /// broke the client's own no-op guard from the other side: a compact canon can NEVER be byte-equal to a
    /// capacity-shaped body, so every roster exit shipped a loadout intent for an aircraft nobody had edited.
    ///
    /// Both arms drive the REAL functions with plain guid strings — a live <c>GeoVehicleEquipment</c> needs a
    /// DefRepository, the same reason <c>TakeSlots</c> is generic (L32).
    ///
    /// ARMS
    ///   (a) <c>capacity-shaped-refused</c> — the measured case must validate clean.
    ///   (b) <c>overfill-accepted</c> — POSITIVE CONTROL: where the host CAN see capacity (a live list that
    ///       still carries an empty slot), one slot too many must still be refused, or arm (a) is just a
    ///       deleted guard.
    ///   (c) <c>no-op-exit-ships</c> — the capacity-shaped body and the compact canon must compare EQUAL for
    ///       an unedited aircraft.
    ///   (d) <c>real-edit-suppressed</c> — POSITIVE CONTROL for (c): a genuine edit must still compare
    ///       UNEQUAL, or the client would suppress every loadout change instead of only the empty ones.
    ///
    /// Falsify: restore <c>want.Count - live.Count</c> unconditionally → (a) red; make CapacityDelta always
    /// return 0 → (b) red; drop <c>filledOnly</c> from the no-op compare → (c) red; make the compare
    /// constant-true → (d) red.
    /// </summary>
    internal static class L366_ACapacityShapedLoadoutFitsACompactVehicle
    {
        private const string W = "weapon-guid";
        private const string M = "module-guid";
        private static readonly string[] None = new string[0];
        private static string Self(string s) => s;   // the slot list IS its guid list in this harness

        internal static IEnumerable<string> Check()
        {
            var legal = new VehicleSync.Facts
            {
                Resolved = true, OwnedByPlayer = true, CanRedirect = true, TargetResolved = true,
                TargetIsIdleCurrentSite = false, Docked = true, SlotCountDelta = 0,
                SiteExplorable = true, CanExploreSites = true, HasCrew = true, AlreadyExploring = false,
            };
            if (VehicleSync.Validate(VehicleSync.OpSetEquipment, legal) != null)
            {
                yield return "L366 premise-changed: VehicleSync.Validate refuses even a fully legal " +
                             "setEquipment gesture, so this law cannot tell a slot-count refusal from any other " +
                             "one. Re-point it at whatever validates a loadout now; do not delete it — without " +
                             "it the host goes back to refusing every edit to any aircraft it has not itself " +
                             "opened the roster on, and calling that a stale mirror.";
                yield break;
            }

            // (a) THE MEASURED CASE. Wire: 1 filled weapon slot, 3 module slots of which 1 is filled.
            //     Host: a save-loaded vehicle, compact — 1 weapon, 1 module, no empty slots to read.
            var f = legal;
            f.SlotCountDelta = VehicleSync.CapacityDelta(1, new[] { W }, Self) +
                               VehicleSync.CapacityDelta(3, new[] { M }, Self);
            var why = VehicleSync.Validate(VehicleSync.OpSetEquipment, f);
            if (why != null)
                yield return "L366 capacity-shaped-refused: the native screen's capacity-shaped slot list was " +
                             "refused against a compact save-loaded vehicle — \"" + why + "\". Capacity is a " +
                             "PREFAB fact (UIVehicleEquipmentInventoryList.Slots) that no def and no model " +
                             "member carries, so a live list with no empty slot tells the host NOTHING about " +
                             "capacity and it must not subtract two different units.";

            // (b) POSITIVE CONTROL: a live list that DOES carry an empty slot is capacity-shaped, and there
            //     the arithmetic is a real comparison again.
            f = legal;
            f.SlotCountDelta = VehicleSync.CapacityDelta(3, new[] { W, "", "" }, Self) +
                               VehicleSync.CapacityDelta(3, new[] { M, "" }, Self);   // host has 2 slots, wire claims 3
            if (VehicleSync.Validate(VehicleSync.OpSetEquipment, f) == null)
                yield return "L366 overfill-accepted: a request naming one slot more than a capacity-shaped " +
                             "live list has was accepted. The abstention in CapacityDelta is for the case where " +
                             "the host cannot SEE capacity — where it can, an over-long request is still a stale " +
                             "mirror or a def/mod mismatch and must be refused.";

            // (c) The client's own no-op test, over the two shapes of an UNEDITED aircraft.
            var body = VehicleSync.EncodeSlots(new[] { W, "" }, new[] { M, "", "" }, Self, filledOnly: true);
            var canon = VehicleSync.EncodeSlots(new[] { W }, new[] { M }, Self, filledOnly: true);
            if (!RailMeta.BytesEqual(body, canon))
                yield return "L366 no-op-exit-ships: the capacity-shaped body and the compact canon of the SAME " +
                             "loadout do not compare equal, so the client ships a loadout intent on every roster " +
                             "exit even when nothing was edited (UIStateVehicleRoster.ExitState:128-131 flushes " +
                             "unconditionally) — and the host answers each one.";

            // (d) POSITIVE CONTROL for (c): the compare must still see a genuine edit.
            var edited = VehicleSync.EncodeSlots(new[] { W, "" }, new[] { M, "other-guid", "" }, Self, filledOnly: true);
            if (RailMeta.BytesEqual(edited, canon))
                yield return "L366 real-edit-suppressed: a loadout with an extra module compares EQUAL to the " +
                             "vehicle's own, so the no-op guard would swallow every real edit and the player's " +
                             "change would never leave the machine.";
        }
    }
}
