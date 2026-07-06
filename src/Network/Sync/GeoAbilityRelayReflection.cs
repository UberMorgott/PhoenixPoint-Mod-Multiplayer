using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Reflection glue for the generic geoscape ability relay (<see cref="GeoAbilityActivateAction"/>). The mod
    /// has NO compile-time game references, so every game member is resolved by name and cached (mirrors
    /// <see cref="GeoRuntime"/> / <c>VehicleTravelReflection</c>). Two sides:
    ///   • CLIENT <see cref="TryBuildIntent"/> — off the live <c>GeoAbility</c> being activated + the
    ///     <c>GeoAbilityTarget</c> struct the UI built, read the participant (vehicle composite key OR site id),
    ///     the ability's <c>GeoAbilityDef.Guid</c>, and the target descriptor (site / vehicle / position + the
    ///     acting faction's Def.Guid) into a wire action. Returns null (degrade → run native locally, a no-op on
    ///     the frozen client) on any unresolvable member — never throws into the UI.
    ///   • HOST <see cref="Activate"/> — resolve the live actor by the same key, find its <c>GeoAbility</c> whose
    ///     <c>BaseDef.Guid</c> matches, rebuild the <c>GeoAbilityTarget</c> (with the acting-faction override for
    ///     abilities whose target faction is NOT the actor-site's owner, e.g. ActivateBase / Guard), and invoke
    ///     the authoritative <c>GeoAbility.Activate(GeoAbilityTarget)</c>. Runs inside the caller's
    ///     <c>SyncApplyScope</c> (OnActionRequest) so the generic Activate interceptor passes through.
    /// All reflection is null-safe: a missing member DEGRADES (logged) rather than throwing.
    /// </summary>
    public static class GeoAbilityRelayReflection
    {
        private static bool _ready;
        private static Type _geoAbilityType;
        private static Type _geoVehicleType;
        private static Type _geoSiteType;
        private static Type _geoAbilityTargetType;
        private static PropertyInfo _baseDefProp;    // Ability.BaseDef (BaseDef)
        private static PropertyInfo _geoActorProp;   // GeoAbility.GeoActor (GeoActor)
        private static MethodInfo _activateMethod;   // GeoAbility.Activate(GeoAbilityTarget)
        private static FieldInfo _vehicleIdField;    // GeoVehicle.VehicleID (int)
        private static PropertyInfo _ownerProp;      // GeoVehicle.Owner (GeoFaction)
        private static PropertyInfo _factionDefProp; // GeoFaction.Def (GeoFactionDef : BaseDef)
        private static FieldInfo _tgtFactionField;   // GeoAbilityTarget.Faction
        private static FieldInfo _tgtActorField;     // GeoAbilityTarget.Actor
        private static FieldInfo _tgtPositionField;  // GeoAbilityTarget.Position (Vector3)
        private static ConstructorInfo _ctorFromActor; // new GeoAbilityTarget(GeoActor)
        private static ConstructorInfo _ctorFromPos;   // new GeoAbilityTarget(Vector3)
        private static MethodInfo _getAbilitiesGeoAbility; // ActorComponent.GetAbilities<GeoAbility>()
        private static FieldInfo _factionsField;     // GeoLevelController.Factions (bound lazily; needs a live geo)

        private static void Ensure()
        {
            if (_ready) return;
            _geoAbilityType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Abilities.GeoAbility");
            _geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            _geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            _geoAbilityTargetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Abilities.GeoAbilityTarget");
            var geoActorType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoActor");
            var geoFactionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoFaction");
            var actorComponentType = AccessTools.TypeByName("Base.Entities.ActorComponent");
            if (_geoAbilityType == null || _geoVehicleType == null || _geoSiteType == null
                || _geoAbilityTargetType == null || geoActorType == null) return;

            _baseDefProp = AccessTools.Property(_geoAbilityType, "BaseDef");
            _geoActorProp = AccessTools.Property(_geoAbilityType, "GeoActor");
            // Exact param match pins Activate(GeoAbilityTarget) over the obsolete Activate(object) overload.
            _activateMethod = AccessTools.Method(_geoAbilityType, "Activate", new[] { _geoAbilityTargetType });
            _vehicleIdField = AccessTools.Field(_geoVehicleType, "VehicleID");
            _ownerProp = AccessTools.Property(_geoVehicleType, "Owner");
            if (geoFactionType != null) _factionDefProp = AccessTools.Property(geoFactionType, "Def");

            _tgtFactionField = AccessTools.Field(_geoAbilityTargetType, "Faction");
            _tgtActorField = AccessTools.Field(_geoAbilityTargetType, "Actor");
            _tgtPositionField = AccessTools.Field(_geoAbilityTargetType, "Position");
            _ctorFromActor = _geoAbilityTargetType.GetConstructor(new[] { geoActorType });
            _ctorFromPos = _geoAbilityTargetType.GetConstructor(new[] { typeof(Vector3) });

            // ActorComponent.GetAbilities<T>() — the ZERO-arg overload (there is also GetAbilities<T>(object)).
            if (actorComponentType != null)
            {
                foreach (var m in actorComponentType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "GetAbilities" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    {
                        try { _getAbilitiesGeoAbility = m.MakeGenericMethod(_geoAbilityType); }
                        catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection: GetAbilities<GeoAbility> bind failed: " + ex.Message); }
                        break;
                    }
                }
            }

            _ready = _baseDefProp != null && _geoActorProp != null && _activateMethod != null
                     && _vehicleIdField != null && _ownerProp != null && _factionDefProp != null
                     && _tgtFactionField != null && _tgtActorField != null && _tgtPositionField != null
                     && _ctorFromActor != null && _ctorFromPos != null && _getAbilitiesGeoAbility != null;
        }

        // ─── CLIENT: build the relay intent off the live ability + target the UI is activating ────────────────

        /// <summary>Build the wire action from the live <c>GeoAbility</c> and the boxed <c>GeoAbilityTarget</c>
        /// struct passed to <c>Activate</c>. Returns null on any unresolvable member (caller runs the native
        /// activation locally — a no-op on the sim-frozen client, never a wedge).</summary>
        public static GeoAbilityActivateAction TryBuildIntent(object abilityObj, object targetBoxed)
        {
            try
            {
                Ensure();
                if (!_ready || abilityObj == null) return null;

                string abilityGuid = DefReflection.GetGuid(_baseDefProp.GetValue(abilityObj, null));
                if (string.IsNullOrEmpty(abilityGuid)) return null;

                object actorObj = _geoActorProp.GetValue(abilityObj, null);
                if (actorObj == null) return null;

                byte actorKind;
                int actorOwnerId = 0, actorVehicleId = 0, actorSiteId = 0;
                if (_geoVehicleType.IsInstanceOfType(actorObj))
                {
                    actorKind = GeoAbilityActivateAction.ActorVehicle;
                    if (!TryReadVehicleKey(actorObj, out actorOwnerId, out actorVehicleId)) return null;
                }
                else if (_geoSiteType.IsInstanceOfType(actorObj))
                {
                    actorKind = GeoAbilityActivateAction.ActorSite;
                    actorSiteId = GeoSiteReflection.GetSiteId(actorObj);
                    if (actorSiteId < 0) return null;
                }
                else return null;

                byte targetKind = GeoAbilityActivateAction.TargetNone;
                int tSiteId = 0, tOwnerId = 0, tVehId = 0;
                float tx = 0f, ty = 0f, tz = 0f;
                string factionGuid = string.Empty;
                if (targetBoxed != null)
                {
                    object tFaction = _tgtFactionField.GetValue(targetBoxed);
                    if (tFaction != null)
                        factionGuid = DefReflection.GetGuid(_factionDefProp.GetValue(tFaction, null)) ?? string.Empty;

                    object tActor = _tgtActorField.GetValue(targetBoxed);
                    if (tActor != null)
                    {
                        if (_geoVehicleType.IsInstanceOfType(tActor))
                        {
                            if (TryReadVehicleKey(tActor, out tOwnerId, out tVehId))
                                targetKind = GeoAbilityActivateAction.TargetVehicle;
                        }
                        else if (_geoSiteType.IsInstanceOfType(tActor))
                        {
                            tSiteId = GeoSiteReflection.GetSiteId(tActor);
                            if (tSiteId >= 0) targetKind = GeoAbilityActivateAction.TargetSite;
                        }
                    }
                    else if (_tgtPositionField.GetValue(targetBoxed) is Vector3 pos)
                    {
                        targetKind = GeoAbilityActivateAction.TargetPos;
                        tx = pos.x; ty = pos.y; tz = pos.z;
                    }
                }

                return new GeoAbilityActivateAction(actorKind, actorOwnerId, actorVehicleId, actorSiteId,
                    abilityGuid, targetKind, tSiteId, tOwnerId, tVehId, tx, ty, tz, factionGuid);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.TryBuildIntent failed: " + ex.Message);
                return null;
            }
        }

        // ─── HOST: resolve the live actor + ability + target and run the authoritative Activate ───────────────

        public static void Activate(GeoRuntime rt, byte actorKind, int actorOwnerId, int actorVehicleId,
            int actorSiteId, string abilityGuid, byte targetKind, int targetSiteId, int targetOwnerId,
            int targetVehicleId, float tx, float ty, float tz, string factionGuid)
        {
            try
            {
                Ensure();
                if (!_ready) { Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.Activate: reflection not ready (skipped)"); return; }

                object actor = null;
                if (actorKind == GeoAbilityActivateAction.ActorVehicle)
                    actor = GeoVehicleIdentityReflection.ResolveVehicleByKey(rt, GeoVehiclePos.MakeKey(actorOwnerId, actorVehicleId));
                else if (actorKind == GeoAbilityActivateAction.ActorSite)
                    actor = GeoSiteReflection.ResolveSiteById(rt, actorSiteId);
                if (actor == null) { Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.Activate: actor not resolved (kind=" + actorKind + ")"); return; }

                object ability = FindAbilityByDefGuid(actor, abilityGuid);
                if (ability == null) { Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.Activate: ability guid " + abilityGuid + " not found on actor"); return; }

                object target = BuildTarget(rt, actor, targetKind, targetSiteId, targetOwnerId, targetVehicleId, tx, ty, tz, factionGuid);
                if (target == null) { Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.Activate: target not built (kind=" + targetKind + ")"); return; }

                _activateMethod.Invoke(ability, new[] { target });
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.Activate failed: " + ex.Message);
            }
        }

        private static object FindAbilityByDefGuid(object actor, string guid)
        {
            try
            {
                if (_getAbilitiesGeoAbility == null || _baseDefProp == null) return null;
                if (!(_getAbilitiesGeoAbility.Invoke(actor, null) is IEnumerable abilities)) return null;
                foreach (var ab in abilities)
                {
                    if (ab == null) continue;
                    if (DefReflection.GetGuid(_baseDefProp.GetValue(ab, null)) == guid) return ab;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.FindAbilityByDefGuid failed: " + ex.Message); }
            return null;
        }

        private static object BuildTarget(GeoRuntime rt, object actor, byte targetKind, int targetSiteId,
            int targetOwnerId, int targetVehicleId, float tx, float ty, float tz, string factionGuid)
        {
            try
            {
                object boxed;
                switch (targetKind)
                {
                    case GeoAbilityActivateAction.TargetSite:
                    {
                        object site = GeoSiteReflection.ResolveSiteById(rt, targetSiteId);
                        if (site == null) return null;
                        boxed = _ctorFromActor.Invoke(new[] { site });
                        break;
                    }
                    case GeoAbilityActivateAction.TargetVehicle:
                    {
                        object veh = GeoVehicleIdentityReflection.ResolveVehicleByKey(rt, GeoVehiclePos.MakeKey(targetOwnerId, targetVehicleId));
                        if (veh == null) return null;
                        boxed = _ctorFromActor.Invoke(new[] { veh });
                        break;
                    }
                    case GeoAbilityActivateAction.TargetPos:
                        boxed = _ctorFromPos.Invoke(new object[] { new Vector3(tx, ty, tz) });
                        break;
                    default: // TargetNone → self-target from the actor
                        boxed = _ctorFromActor.Invoke(new[] { actor });
                        break;
                }
                if (boxed == null) return null;

                // Acting-faction override: ActivateBase / AncientGuardianGuard build their target with
                // Faction = the Phoenix faction, which the actor-site's ctor-derived faction is NOT (an abandoned
                // base is environment-owned). Position/probe carry no faction ("") → no override.
                if (!string.IsNullOrEmpty(factionGuid) && _tgtFactionField != null)
                {
                    object faction = ResolveFactionByGuid(rt, factionGuid);
                    if (faction != null) _tgtFactionField.SetValue(boxed, faction);
                }
                return boxed;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][geo] GeoAbilityRelayReflection.BuildTarget failed: " + ex.Message);
                return null;
            }
        }

        private static object ResolveFactionByGuid(GeoRuntime rt, string guid)
        {
            if (string.IsNullOrEmpty(guid) || _factionDefProp == null) return null;
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            if (_factionsField == null) _factionsField = AccessTools.Field(geo.GetType(), "Factions");
            if (_factionsField == null || !(_factionsField.GetValue(geo) is IEnumerable facs)) return null;
            foreach (var f in facs)
            {
                if (f == null) continue;
                if (DefReflection.GetGuid(_factionDefProp.GetValue(f, null)) == guid) return f;
            }
            return null;
        }

        /// <summary>Composite (ownerId, vehicleId) key of a live vehicle — identical derivation to the position /
        /// travel mirrors (<c>GeoVehicleIdentityReflection.TryReadKey</c>), so the host resolves the SAME vehicle
        /// via <c>ResolveVehicleByKey</c>.</summary>
        private static bool TryReadVehicleKey(object vehicle, out int ownerId, out int vehicleId)
        {
            ownerId = 0; vehicleId = 0;
            try
            {
                if (_vehicleIdField == null || _ownerProp == null || _factionDefProp == null) return false;
                vehicleId = Convert.ToInt32(_vehicleIdField.GetValue(vehicle));
                object owner = _ownerProp.GetValue(vehicle, null);
                if (owner == null) return false;
                var def = _factionDefProp.GetValue(owner, null) as UnityEngine.Object;
                ownerId = GeoVehiclePos.StableOwnerKey(def != null ? def.name : null);
                return true;
            }
            catch { return false; }
        }
    }
}
