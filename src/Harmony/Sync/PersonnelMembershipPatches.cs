using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// PS1 roster-MEMBERSHIP dirty seams (personnel-sync spec §3, taxonomy §8): HOST-only Postfix hooks on
    /// the four container mutators — <c>GeoVehicle.AddCharacter/RemoveCharacter(GeoCharacter)</c>
    /// (GeoVehicle.cs:759/766) and <c>GeoSite.AddCharacter/RemoveCharacter(GeoCharacter)</c>
    /// (GeoSite.cs:983/989). EVERY membership path funnels through these (hire → AddRecruitToContainerFinal
    /// → AddCharacter; transfer = Remove(src)+Add(dst) — there is no dedicated transfer method), so four
    /// seams cover the whole assignment surface. Each fire marks that soldier's GeoUnitId dirty on channel
    /// #9 (<see cref="PersonnelChannel.MarkSoldierDirtyExternal"/> — coalesced, full-set flush); a VEHICLE
    /// container change additionally converges through the ~4 Hz #6 crew poll (GeoVehicleChannel.HostObserve
    /// value-compares crew each walk — no extra hook needed, ≤1 poll interval of latency).
    ///
    /// No <c>SyncApplyScope.IsApplying</c> skip: the client never runs these natives (its applies are
    /// value-only list edits), and a HOST mutation inside a relayed-action apply MUST dirty (authoritative
    /// write → mirror back). IsHost gates everything. Reflective targets (Prepare false → PatchAll skips
    /// silently — bind evidence logged); best-effort try/catch — never breaks the native mutator.
    /// </summary>
    internal static class PersonnelMembershipDirty
    {
        internal static MethodBase Resolve(string typeName, string methodName)
        {
            var containerT = AccessTools.TypeByName(typeName);
            var charT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            // EXACT param match (harmony-accesstools-exact-param-match): AddCharacter(GeoCharacter) etc.
            var m = containerT != null && charT != null
                ? AccessTools.Method(containerT, methodName, new[] { charT }) : null;
            Debug.Log("[Multiplayer] PersonnelMembershipPatches: " + typeName + "." + methodName + " "
                      + (m != null ? "bound" : "NOT FOUND — membership dirty seam disabled"));
            return m;
        }

        internal static void Mark(object character)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                // Id 0 (read miss) still marks: the flush is full-set, so the trigger matters, not the id.
                PersonnelChannel.MarkSoldierDirtyExternal(PersonnelReflection.ReadUnitId(character));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelMembershipPatches.Mark failed: " + ex.Message); }
        }
    }

    [HarmonyPatch]
    public static class GeoVehicleAddCharacterDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelMembershipDirty.Resolve("PhoenixPoint.Geoscape.Entities.GeoVehicle", "AddCharacter");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __0) => PersonnelMembershipDirty.Mark(__0);
    }

    [HarmonyPatch]
    public static class GeoVehicleRemoveCharacterDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelMembershipDirty.Resolve("PhoenixPoint.Geoscape.Entities.GeoVehicle", "RemoveCharacter");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __0) => PersonnelMembershipDirty.Mark(__0);
    }

    [HarmonyPatch]
    public static class GeoSiteAddCharacterDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelMembershipDirty.Resolve("PhoenixPoint.Geoscape.Entities.GeoSite", "AddCharacter");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __0) => PersonnelMembershipDirty.Mark(__0);
    }

    [HarmonyPatch]
    public static class GeoSiteRemoveCharacterDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelMembershipDirty.Resolve("PhoenixPoint.Geoscape.Entities.GeoSite", "RemoveCharacter");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __0) => PersonnelMembershipDirty.Mark(__0);
    }
}
