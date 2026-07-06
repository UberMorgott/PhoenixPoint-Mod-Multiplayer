using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the U vehicle-LOADOUT tail on the mid-session vehicle channel (#6): reads a
    /// PHOENIX aircraft's ordered weapon/module slot def guids on the host and value-stamps them onto a
    /// mirrored craft on the client. The loadout affects the interception outcome (audit item U), so a
    /// frozen client that never re-equips its aircraft would compute stale interception maths.
    ///
    /// Verified against the decompile (2026-07-06,
    /// PhoenixPoint.Geoscape.Entities.GeoVehicle / …Interception.Equipments.GeoVehicleEquipment):
    ///   • <c>GeoVehicle.Weapons</c> / <c>GeoVehicle.Modules</c> (IEnumerable&lt;GeoVehicleEquipment&gt;,
    ///     GeoVehicle.cs:258/260) — backed by <c>_weapons</c>/<c>_modules : List&lt;GeoVehicleEquipment&gt;</c>
    ///     whose entries may be NULL (an empty slot, placed by <c>AddNullWeapon</c>/<c>AddNullModule</c>).
    ///   • <c>GeoVehicleEquipment.EquipmentDef : GeoVehicleEquipmentDef</c> (:12) — the slotted def
    ///     (its <c>BaseDef.Guid</c> is the wire value; "" = an empty/null slot).
    ///   • rebuild path (mirrors native load/UseLoadout GeoVehicle.cs:858): <c>ClearEquipments()</c> then per
    ///     slot in order <c>AddEquipment(GeoVehicleEquipmentDef)</c> (:843, auto-routes weapon↔module by
    ///     <c>IsWeapon</c> and re-applies module bonuses) / <c>AddNullWeapon()</c> / <c>AddNullModule()</c> (:812/817).
    ///
    /// Owner scope = the shared PHOENIX faction only (the crew tail's scope, spec §1): a non-Phoenix craft's
    /// loadout is never read or written. CLIENT apply is VALUE-ONLY + idempotent: it first compares the live
    /// loadout to the mirrored one and no-ops when identical (the resident-style rail re-emits every flush —
    /// a rebuild every hour would needlessly churn equipment objects the open interception UI may hold).
    /// Every path is try/caught — best-effort, never throws into game code.
    /// </summary>
    public static class GeoVehicleLoadoutReflection
    {
        private static bool _probed;
        private static PropertyInfo _ownerProp;      // GeoVehicle.Owner (GeoActor)
        private static PropertyInfo _weaponsProp;    // GeoVehicle.Weapons (IEnumerable<GeoVehicleEquipment>)
        private static PropertyInfo _modulesProp;    // GeoVehicle.Modules
        private static PropertyInfo _equipmentDefProp; // GeoVehicleEquipment.EquipmentDef
        private static MethodInfo _clearEquipments;  // GeoVehicle.ClearEquipments()
        private static MethodInfo _addEquipmentDef;  // GeoVehicle.AddEquipment(GeoVehicleEquipmentDef)
        private static MethodInfo _addNullWeapon;    // GeoVehicle.AddNullWeapon()
        private static MethodInfo _addNullModule;    // GeoVehicle.AddNullModule()

        private static void Ensure(object vehicle)
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var vt = vehicle?.GetType() ?? AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
                var equipDefType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Interception.Equipments.GeoVehicleEquipmentDef");
                var equipType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Interception.Equipments.GeoVehicleEquipment");
                if (vt == null) return;
                _ownerProp = AccessTools.Property(vt, "Owner");
                _weaponsProp = AccessTools.Property(vt, "Weapons");
                _modulesProp = AccessTools.Property(vt, "Modules");
                _clearEquipments = AccessTools.Method(vt, "ClearEquipments");
                _addNullWeapon = AccessTools.Method(vt, "AddNullWeapon");
                _addNullModule = AccessTools.Method(vt, "AddNullModule");
                if (equipDefType != null)
                    _addEquipmentDef = AccessTools.Method(vt, "AddEquipment", new[] { equipDefType });   // the def overload (exact param match)
                if (equipType != null)
                    _equipmentDefProp = AccessTools.Property(equipType, "EquipmentDef");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleLoadoutReflection.Ensure failed: " + ex.Message); }
        }

        private static bool ReadReady => _weaponsProp != null && _modulesProp != null && _equipmentDefProp != null;

        /// <summary>HOST (#6 loadout poll): the ordered weapon-slot + module-slot def guids of a PHOENIX
        /// aircraft ("" for an empty/null slot). False for a non-Phoenix craft (loadout scope = the shared
        /// faction only) or when the equipment lists are unreachable — the caller then tracks no loadout.</summary>
        public static bool TryReadLoadout(GeoRuntime rt, object vehicle, out string[] weapons, out string[] modules)
        {
            weapons = null;
            modules = null;
            try
            {
                if (vehicle == null) return false;
                Ensure(vehicle);
                if (!ReadReady) return false;
                var fac = rt?.PhoenixFaction();
                if (fac == null) return false;
                object owner = _ownerProp?.GetValue(vehicle, null);
                if (!ReferenceEquals(owner, fac)) return false;   // Phoenix-owned aircraft only
                weapons = ReadSlotGuids(_weaponsProp.GetValue(vehicle, null) as IEnumerable);
                modules = ReadSlotGuids(_modulesProp.GetValue(vehicle, null) as IEnumerable);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoVehicleLoadoutReflection.TryReadLoadout failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Ordered def guids of one equipment list ("" = a null/empty slot).</summary>
        private static string[] ReadSlotGuids(IEnumerable equipments)
        {
            var res = new List<string>();
            if (equipments == null) return res.ToArray();
            foreach (var eq in equipments)
            {
                if (eq == null) { res.Add(""); continue; }   // empty slot placeholder
                var def = _equipmentDefProp.GetValue(eq, null);
                res.Add(DefReflection.GetGuid(def) ?? "");
            }
            return res.ToArray();
        }

        /// <summary>CLIENT (#6 loadout tail): value-stamp the aircraft's <c>_weapons</c>/<c>_modules</c> to
        /// the mirrored slot guids. No-op when the live loadout already matches (idempotent, no churn). A
        /// slot whose def guid doesn't resolve on this client degrades to an EMPTY slot (never a throw).</summary>
        public static void ApplyLoadout(GeoRuntime rt, object vehicle, string[] weapons, string[] modules)
        {
            if (vehicle == null || weapons == null || modules == null) return;
            try
            {
                Ensure(vehicle);
                if (!ReadReady || _clearEquipments == null || _addEquipmentDef == null
                    || _addNullWeapon == null || _addNullModule == null)
                {
                    Debug.Log("[Multiplayer] GeoVehicleLoadoutReflection: loadout apply unavailable (equipment API unresolved) — skipped");
                    return;
                }
                // Value-only: skip when the live loadout already equals the mirrored one (churn-free re-emit).
                var curW = ReadSlotGuids(_weaponsProp.GetValue(vehicle, null) as IEnumerable);
                var curM = ReadSlotGuids(_modulesProp.GetValue(vehicle, null) as IEnumerable);
                if (GeoVehicleLoadout.SameLoadout(curW, curM, weapons, modules)) return;

                _clearEquipments.Invoke(vehicle, null);
                int added = 0, empty = 0, unresolved = 0;
                foreach (var g in weapons)
                {
                    if (string.IsNullOrEmpty(g)) { _addNullWeapon.Invoke(vehicle, null); empty++; continue; }
                    var def = DefReflection.GetDefByGuid(g);
                    if (def == null) { _addNullWeapon.Invoke(vehicle, null); unresolved++; continue; }
                    _addEquipmentDef.Invoke(vehicle, new[] { def }); added++;
                }
                foreach (var g in modules)
                {
                    if (string.IsNullOrEmpty(g)) { _addNullModule.Invoke(vehicle, null); empty++; continue; }
                    var def = DefReflection.GetDefByGuid(g);
                    if (def == null) { _addNullModule.Invoke(vehicle, null); unresolved++; continue; }
                    _addEquipmentDef.Invoke(vehicle, new[] { def }); added++;
                }
                Debug.Log("[Multiplayer] GeoVehicleLoadoutReflection.ApplyLoadout reconciled (+" + added
                          + " equip/" + empty + " empty" + (unresolved > 0 ? "/" + unresolved + " unresolved→empty" : "")
                          + ") w=" + weapons.Length + " m=" + modules.Length);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleLoadoutReflection.ApplyLoadout failed: " + ex.Message); }
        }
    }
}
