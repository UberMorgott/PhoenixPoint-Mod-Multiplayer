using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// Save-write guard for UNINITIALIZED tactical scene actors (live failure 2026-07-13).
    ///
    /// Root NRE chain (grounded in the decompile):
    ///   • <c>Base.Entities.ActorComponent.SerializationData</c> getter (ActorComponent.cs:56-65) calls
    ///     <c>RecordInstanceData(actorInstanceData)</c> during every savegame write (the serializer reads the
    ///     [SerializeMember] property via reflection — SerializationMember.GetValue).
    ///   • <c>TacticalActorBase.RecordInstanceData</c> (TacticalActorBase.cs:485) unconditionally derefs
    ///     <c>TacticalFaction.TacticalFactionDef</c>. <c>TacticalFaction</c> ({ get; protected set; }, :188) is
    ///     set ONLY via <c>SetFaction</c> (:646), which for scene actors runs at <c>OnEnterPlay</c> (:529).
    ///   • A scene-placed actor that never entered play (the mission's
    ///     "StructuralTarget_ComponentSetDef_1 … does not have valid scene id" shape, ActorComponent.cs:331)
    ///     therefore has <c>TacticalFaction == null</c> → RecordInstanceData NREs → the whole native
    ///     WriteSavegame coroutine chain dies. That killed the co-op tac-entry transfer save
    ///     (<c>coop_tac_xfer</c>) — and would kill a manual/quick save at the same moment identically.
    ///
    /// Guard: prefix on the <c>SerializationData</c> GETTER — for exactly that broken shape (a
    /// <c>TacticalActorBase</c> whose <c>TacticalFaction</c> is null) return null instead of recording.
    /// Null is already a legal getter result natively (the getter returns null when no instance data can be
    /// created, :59-64) and the setter (<c>RestoreInstanceData</c> → InstanceDataComponent.SetInstanceData)
    /// stores whatever value arrives — so the save write completes and healthy actors are byte-identical
    /// (the prefix passes them straight through to the native getter). One warning line per skipped actor.
    /// </summary>
    [HarmonyPatch]
    public static class SaveActorInstanceDataGuardPatch
    {
        private static MethodBase _target;
        private static System.Type _tacticalActorBaseType;   // PhoenixPoint.Tactical.Entities.TacticalActorBase
        private static PropertyInfo _tacticalFactionProp;    // TacticalActorBase.TacticalFaction (protected set)

        public static bool Prepare()
        {
            var actorComponentType = AccessTools.TypeByName("Base.Entities.ActorComponent");
            _tacticalActorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
            if (actorComponentType == null || _tacticalActorBaseType == null) return false;
            _target = AccessTools.PropertyGetter(actorComponentType, "SerializationData");
            _tacticalFactionProp = AccessTools.Property(_tacticalActorBaseType, "TacticalFaction");
            return _target != null && _tacticalFactionProp != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Skip ONLY the guaranteed-NRE shape (uninitialized tactical actor); every other actor runs native.
        public static bool Prefix(object __instance, ref object __result)
        {
            try
            {
                if (__instance == null || !_tacticalActorBaseType.IsInstanceOfType(__instance)) return true;
                if (_tacticalFactionProp.GetValue(__instance, null) != null) return true;   // healthy → native
                var name = (__instance as Component)?.name ?? __instance.ToString();
                Debug.LogWarning("[Multiplayer] save: skipping instance-data for uninitialized scene actor '" +
                                 name + "' (TacticalFaction null — never entered play; native RecordInstanceData would NRE)");
                __result = null;   // legal native getter result; the save write continues past this actor
                return false;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Multiplayer] SaveActorInstanceDataGuardPatch.Prefix failed: " + e.Message);
                return true;   // never block the native getter on a guard failure
            }
        }
    }
}
