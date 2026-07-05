using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the mid-session vehicle-creation channel (#6). The mod has NO compile-time game
    /// references, so every member is resolved by name and cached (mirrors <see cref="GeoSiteReflection"/> /
    /// <c>GeoVehicleMirror</c>). Two sides:
    ///   • HOST <see cref="TryReadIdentity"/> — off a live <c>GeoVehicle</c>, read the owner faction's
    ///     <c>Def.Guid</c> and the vehicle's spawn <c>ComponentSet.SetDef.Guid</c> (Base.Core.ComponentSet.cs:12),
    ///     combined with the composite key + placement the poll already computed, into a wire
    ///     <see cref="GeoVehicleIdentity"/>.
    ///   • CLIENT <see cref="SpawnMirrorVehicle"/> — resolve the owning faction (by def guid) + the spawn
    ///     <c>ComponentSetDef</c> (by guid), spawn an INERT vehicle via
    ///     <c>ActorSpawner.SpawnActor&lt;GeoVehicle&gt;(setDef, null, callEnterPlayOnActor:false)</c> — the SAME
    ///     template the native <c>GeoFaction.CreateVehicle</c> instantiates (GeoFaction.cs:2006), but WITHOUT
    ///     <c>DoEnterPlay</c> so no <c>Owner.RegisterVehicle</c> / faction-controller / travel producer / event
    ///     wiring runs (GeoVehicle.cs:398-403). It is stamped with the host's <c>Owner</c> + <c>VehicleID</c> +
    ///     initial placement PLUS the minimal enter-play state the native selection UI reads (Stats /
    ///     RangeRemaining / loadout / Navigation.Init — the mirror is click-selectable in co-op), added to
    ///     <c>GeoMap.Vehicles</c> (bypassing <c>RegisterActor</c> — GeoMap.cs:371-376 — pure mirror, the same
    ///     no-cascade discipline as <see cref="GeoSiteReflection.SpawnMirrorSite"/>) and registered in the owner
    ///     faction cache (<c>GeoMap.UpdateVehicleOwner</c>). The client clock is frozen anyway, so even the view's
    ///     own updateables never advance; this is display-only. Idempotent by composite key.
    ///   • CLIENT <see cref="DespawnVehicle"/> — tombstoned key → remove the client's live vehicle (native
    ///     <c>GeoVehicle.Destroy()</c> for a join-save-loaded one, symmetric no-cascade teardown for a mirror).
    ///
    /// The composite key equals the position/travel/explore mirrors' key: (<c>StableOwnerKey(owner def name)</c>,
    /// <c>VehicleID</c>) — so once spawned, 0xA5/0xA6/0xA7 resolve and drive it. All reflection is null-safe: a
    /// missing member DEGRADES (logged) rather than throwing; a partial resolve skips the spawn (never NREs).
    /// </summary>
    public static class GeoVehicleIdentityReflection
    {
        private static bool _ready;
        private static FieldInfo _mapField;         // GeoLevelController.Map (GeoMap)
        private static FieldInfo _factionsField;    // GeoLevelController.Factions (IEnumerable<GeoFaction>)
        private static PropertyInfo _vehiclesProp;  // GeoMap.Vehicles (IList<GeoVehicle>)
        private static Type _geoVehicleType;        // PhoenixPoint.Geoscape.Entities.GeoVehicle
        private static FieldInfo _vehicleIdField;   // GeoVehicle.VehicleID (public int)
        private static PropertyInfo _ownerProp;     // GeoVehicle.Owner (GeoFaction; setter only sets _owner + visuals)
        private static PropertyInfo _surfaceProp;   // GeoVehicle.Surface (heading transform)
        private static MethodInfo _refreshVisibility;// GeoVehicle.RefreshVisibility()
        private static PropertyInfo _factionDefProp;// GeoFaction.Def (GeoFactionDef : BaseDef)
        private static Type _componentSetType;      // Base.Core.ComponentSet
        private static PropertyInfo _setDefProp;    // ComponentSet.SetDef (ComponentSetDef)
        private static MethodInfo _spawnActorGeoVehicle; // ActorSpawner.SpawnActor<GeoVehicle>(BaseDef, ActorInstanceData, bool)
        // Minimal enter-play stamp (mirrors GeoFaction.CreateVehicle:2010-2012 + GeoVehicle.PrepareEnterPlay:395):
        private static PropertyInfo _vehicleDefProp;    // GeoVehicle.VehicleDef (GeoVehicleDef)
        private static FieldInfo _statsField;           // GeoVehicle.Stats (GeoVehicleStats, public field)
        private static FieldInfo _baseStatsField;       // GeoVehicleDef.BaseStats (GeoVehicleStats)
        private static MethodInfo _statsClone;          // GeoVehicleStats.Clone()
        private static FieldInfo _maxRangeField;        // GeoVehicleStats.MaximumRange (EarthUnits)
        private static PropertyInfo _rangeRemainingProp;// GeoVehicle.RangeRemaining (EarthUnits)
        private static PropertyInfo _loadoutManagerProp;// GeoFaction.GeoLoadoutManager
        private static MethodInfo _getVehicleLoadout;   // GeoLoadoutManager.GetGeoVehicleLoadout(GeoVehicleDef)
        private static MethodInfo _useLoadout;          // GeoVehicle.UseLoadout(GeoVehicleLoadoutDef)
        private static FieldInfo _navigationField;      // GeoVehicle.Navigation (GeoNavComponent, public field)
        private static MethodInfo _navInit;             // GeoNavComponent.Init(INavigatableGeoActor)
        // Tombstone despawn + owner faction cache:
        private static MethodInfo _updateVehicleOwner;  // GeoMap.UpdateVehicleOwner(GeoVehicle, GeoFaction, GeoFaction)
        private static MethodInfo _vehicleDestroy;      // GeoVehicle.Destroy() (native full teardown, GeoVehicle.cs:593)
        private static PropertyInfo _inPlayProp;        // ActorComponent.InPlay (ran DoEnterPlay ⇒ full native destroy path)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var geoLevelType = geo.GetType();
            _mapField = AccessTools.Field(geoLevelType, "Map");
            _factionsField = AccessTools.Field(geoLevelType, "Factions");
            if (_mapField == null) return;
            var map = _mapField.GetValue(geo);
            if (map == null) return;
            _vehiclesProp = AccessTools.Property(map.GetType(), "Vehicles");
            _updateVehicleOwner = AccessTools.Method(map.GetType(), "UpdateVehicleOwner");

            _geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (_geoVehicleType == null) return;
            _vehicleIdField = AccessTools.Field(_geoVehicleType, "VehicleID");
            _ownerProp = AccessTools.Property(_geoVehicleType, "Owner");
            _surfaceProp = AccessTools.Property(_geoVehicleType, "Surface");
            _refreshVisibility = AccessTools.Method(_geoVehicleType, "RefreshVisibility", Type.EmptyTypes);
            _vehicleDestroy = AccessTools.Method(_geoVehicleType, "Destroy", Type.EmptyTypes);
            _inPlayProp = AccessTools.Property(_geoVehicleType, "InPlay");   // inherited from ActorComponent

            // Enter-play stamp members (all best-effort — a miss degrades that piece only, logged at use site).
            _vehicleDefProp = AccessTools.Property(_geoVehicleType, "VehicleDef");
            _statsField = AccessTools.Field(_geoVehicleType, "Stats");
            _rangeRemainingProp = AccessTools.Property(_geoVehicleType, "RangeRemaining");
            _useLoadout = AccessTools.Method(_geoVehicleType, "UseLoadout");
            _navigationField = AccessTools.Field(_geoVehicleType, "Navigation");
            if (_vehicleDefProp != null)
                _baseStatsField = AccessTools.Field(_vehicleDefProp.PropertyType, "BaseStats");
            if (_baseStatsField != null)
            {
                _statsClone = AccessTools.Method(_baseStatsField.FieldType, "Clone", Type.EmptyTypes);
                _maxRangeField = AccessTools.Field(_baseStatsField.FieldType, "MaximumRange");
            }
            var navActorIface = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.INavigatableGeoActor");
            if (_navigationField != null && navActorIface != null)
                _navInit = AccessTools.Method(_navigationField.FieldType, "Init", new[] { navActorIface });

            // GeoFaction.Def + GeoLoadoutManager — resolved off a live faction (mirrors GeoSiteReflection). The
            // faction-def property is a READY-GATE member (see below); with an empty Factions list it stays null
            // and Ensure retries on the next call instead of latching a dead channel.
            if (_factionsField != null && _factionsField.GetValue(geo) is IEnumerable facs)
                foreach (var f in facs)
                {
                    if (f == null) continue;
                    _factionDefProp = AccessTools.Property(f.GetType(), "Def");
                    _loadoutManagerProp = AccessTools.Property(f.GetType(), "GeoLoadoutManager");
                    break;
                }
            if (_loadoutManagerProp != null)
                _getVehicleLoadout = AccessTools.Method(_loadoutManagerProp.PropertyType, "GetGeoVehicleLoadout");

            // ComponentSet.SetDef → the spawn ComponentSetDef guid (Base.Core.ComponentSet.cs:12).
            _componentSetType = AccessTools.TypeByName("Base.Core.ComponentSet");
            if (_componentSetType != null)
                _setDefProp = AccessTools.Property(_componentSetType, "SetDef");

            // ActorSpawner.SpawnActor<T>(BaseDef, ActorInstanceData, bool callEnterPlayOnActor) closed to GeoVehicle
            // (Base.Entities/ActorSpawner.cs:12). callEnterPlayOnActor:false → inert spawn (no DoEnterPlay).
            var actorSpawnerType = AccessTools.TypeByName("Base.Entities.ActorSpawner");
            if (actorSpawnerType != null)
            {
                var spawnActorOpen = actorSpawnerType.GetMethod("SpawnActor", BindingFlags.Public | BindingFlags.Static);
                if (spawnActorOpen != null && spawnActorOpen.IsGenericMethodDefinition)
                {
                    try { _spawnActorGeoVehicle = spawnActorOpen.MakeGenericMethod(_geoVehicleType); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection: SpawnActor<GeoVehicle> bind failed (spawn disabled): " + ex.Message); }
                }
            }

            // Core gate: id resolution + list access + owner-faction guid resolution. _factionDefProp binds off
            // the FIRST live faction — latching ready while it is still null (early Ensure, empty Factions list)
            // would leave the channel silently dead for the whole process (owner guids never resolve); gating on
            // it makes Ensure retry the faction bind next call. Pure predicate = unit-tested ReflectionReady.
            // Spawn members remain best-effort (a miss disables spawn only).
            _ready = GeoVehicleIdentityTracker.ReflectionReady(
                _vehicleIdField != null, _vehiclesProp != null, _ownerProp != null, _factionDefProp != null);
        }

        private static object GetMap(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null || _mapField == null) return null;
            try { return _mapField.GetValue(geo); }
            catch { return null; }
        }

        /// <summary>Read a live vehicle's Owner-faction Def.Guid + spawn ComponentSet.SetDef.Guid and combine them
        /// with the (already-computed) composite key + placement into a wire identity. False on any missing guid.</summary>
        public static bool TryReadIdentity(GeoRuntime rt, object vehicle, GeoVehiclePos placement, out GeoVehicleIdentity id)
        {
            id = default(GeoVehicleIdentity);
            if (vehicle == null) return false;
            try
            {
                Ensure(rt);
                if (!_ready) return false;

                // Owner faction Def.Guid.
                string ownerGuid = null;
                object owner = _ownerProp?.GetValue(vehicle, null);
                if (owner != null && _factionDefProp != null)
                    ownerGuid = DefReflection.GetGuid(_factionDefProp.GetValue(owner, null));
                if (string.IsNullOrEmpty(ownerGuid)) return false;   // can't resolve the faction on the client → skip

                // Spawn ComponentSet.SetDef.Guid.
                string setGuid = null;
                if (_componentSetType != null && _setDefProp != null && vehicle is Component vc && vc != null)
                {
                    var cs = vc.GetComponent(_componentSetType);
                    if (cs != null) setGuid = DefReflection.GetGuid(_setDefProp.GetValue(cs, null));
                }
                if (string.IsNullOrEmpty(setGuid)) return false;     // no spawn template → skip

                id = new GeoVehicleIdentity(placement.OwnerId, placement.VehicleId, ownerGuid, setGuid,
                                            placement.QX, placement.QY, placement.QZ, placement.QW,
                                            placement.X, placement.Y, placement.Z);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.TryReadIdentity failed: " + ex.Message); return false; }
        }

        /// <summary>The composite keys of every live vehicle (client idempotence + host seed). Empty when not in
        /// geoscape. Key = (StableOwnerKey(owner def name), VehicleID) — identical to the position mirror.</summary>
        public static HashSet<long> ResolveLiveKeys(GeoRuntime rt)
        {
            var keys = new HashSet<long>();
            try
            {
                Ensure(rt);
                if (!_ready) return keys;
                var map = GetMap(rt);
                if (map == null || !(_vehiclesProp?.GetValue(map, null) is IEnumerable vehicles)) return keys;
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    if (TryReadKey(v, out long key)) keys.Add(key);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.ResolveLiveKeys failed: " + ex.Message); }
            return keys;
        }

        /// <summary>Composite key of one live vehicle, matching the position mirror's key derivation.</summary>
        private static bool TryReadKey(object vehicle, out long key)
        {
            key = 0;
            try
            {
                if (_vehicleIdField == null) return false;
                int id = Convert.ToInt32(_vehicleIdField.GetValue(vehicle));
                int ownerId = 0;
                object owner = _ownerProp?.GetValue(vehicle, null);
                if (owner != null && _factionDefProp != null)
                {
                    var def = _factionDefProp.GetValue(owner, null) as UnityEngine.Object;
                    ownerId = GeoVehiclePos.StableOwnerKey(def != null ? def.name : null);
                }
                key = GeoVehiclePos.MakeKey(ownerId, id);
                return true;
            }
            catch { return false; }
        }

        /// <summary>CLIENT: spawn an INERT mirror vehicle for a host-created craft this sim-frozen client never
        /// made, so 0xA5/0xA6/0xA7 resolve + drive it. Idempotent (a live key → no-op). Best-effort: any reflection
        /// miss logs + returns without spawning; NEVER throws into the apply loop. Runs inside the caller's
        /// <c>SyncApplyScope</c> (OnStateSync) so no interceptor re-broadcasts.</summary>
        public static void SpawnMirrorVehicle(GeoRuntime rt, GeoVehicleIdentity identity)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return;
                if (_spawnActorGeoVehicle == null)
                {
                    Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: SpawnActor<GeoVehicle> unresolved (skipped)");
                    return;
                }

                // Idempotent: never double-add a vehicle whose composite key already resolves on this client.
                if (ResolveLiveKeys(rt).Contains(identity.Key)) return;

                object faction = ResolveFactionByGuid(rt, identity.OwnerFactionDefGuid);
                if (faction == null)
                {
                    Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: owner faction guid " + identity.OwnerFactionDefGuid + " not resolved (skipped)");
                    return;
                }
                object setDef = DefReflection.GetDefByGuid(identity.VehicleSetDefGuid);
                if (setDef == null)
                {
                    Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: vehicle set-def guid " + identity.VehicleSetDefGuid + " not resolved (skipped)");
                    return;
                }

                // INERT spawn: callEnterPlayOnActor:false → Instantiate + SetActorRootParent (view/mesh created +
                // parented into the globe), but NO DoEnterPlay (no RegisterVehicle / controller / producers).
                object vehicle = _spawnActorGeoVehicle.Invoke(null, new object[] { setDef, null, false });
                if (vehicle == null)
                {
                    Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: SpawnActor<GeoVehicle> returned null (skipped)");
                    return;
                }

                // Owner (its setter only sets _owner + refreshes owner visuals — no cascade) + host VehicleID.
                try { _ownerProp.SetValue(vehicle, faction, null); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: set Owner failed: " + ex.Message); }
                try { _vehicleIdField.SetValue(vehicle, identity.VehicleId); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: stamp VehicleID failed: " + ex.Message); }

                // Initial placement (pivot localRotation + heading euler) so it appears in the right spot at once.
                if (vehicle is Component comp && comp != null && comp.transform != null)
                {
                    comp.transform.localRotation = new Quaternion(identity.QX, identity.QY, identity.QZ, identity.QW);
                    if (_surfaceProp?.GetValue(vehicle, null) is Transform surface && surface != null)
                        surface.localEulerAngles = new Vector3(identity.X, identity.Y, identity.Z);
                }

                // ── Minimal ENTER-PLAY stamp ─────────────────────────────────────────────────────────────────
                // The mirror is click-SELECTABLE (visible, Owner = the shared viewer faction), and the native
                // selection UI reads state only DoEnterPlay-era code sets — a bare inert spawn NREs it. Stamp the
                // exact pieces the UI reads, mirroring GeoFaction.CreateVehicle (GeoFaction.cs:2010-2012) and
                // GeoVehicle.PrepareEnterPlay (GeoVehicle.cs:395), WITHOUT DoEnterPlay itself — no
                // Owner.RegisterVehicle event wiring / VehicleFactionController / travel or explore producers, and
                // nothing here schedules on the (frozen) client Timing:
                //   • Stats = VehicleDef.BaseStats.Clone() — UIStateVehicleSelected → SetVehicleInfo /
                //     CalculateRemainingPossibleRange dereference Stats (RequiresRepair → Stats.HitPoints).
                //   • RangeRemaining = Stats.MaximumRange — travel-range ring + reachable-site markers.
                //   • UseLoadout(Owner.GeoLoadoutManager.GetGeoVehicleLoadout(VehicleDef)) — equipment strip;
                //     pure local list + stat-bonus mutation (GeoVehicle.AddEquipment), no producers.
                //   • Navigation.Init(this) — binds NavActor + default pathfinder only (GeoNavComponent.cs:32-43);
                //     DrawPathToSite → Navigation.FindPath NREs un-Init'd. Nothing starts navigating.
                object stats = null;
                try
                {
                    object vehicleDef = _vehicleDefProp?.GetValue(vehicle, null);
                    object baseStats = vehicleDef != null ? _baseStatsField?.GetValue(vehicleDef) : null;
                    stats = baseStats != null ? _statsClone?.Invoke(baseStats, null) : null;
                    if (stats != null && _statsField != null)
                    {
                        _statsField.SetValue(vehicle, stats);
                        if (_maxRangeField != null && _rangeRemainingProp != null)
                            _rangeRemainingProp.SetValue(vehicle, _maxRangeField.GetValue(stats), null);
                    }
                    else
                        Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: Stats stamp unresolved (mirror may NRE native selection UI)");
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: Stats stamp failed: " + ex.Message); }
                try
                {
                    object loadoutMgr = _loadoutManagerProp?.GetValue(faction, null);
                    object vehicleDef = _vehicleDefProp?.GetValue(vehicle, null);
                    if (loadoutMgr != null && vehicleDef != null && _getVehicleLoadout != null && _useLoadout != null)
                        _useLoadout.Invoke(vehicle, new[] { _getVehicleLoadout.Invoke(loadoutMgr, new[] { vehicleDef }) });
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: UseLoadout failed: " + ex.Message); }
                try
                {
                    object nav = _navigationField?.GetValue(vehicle);
                    if (nav != null && _navInit != null) _navInit.Invoke(nav, new[] { vehicle });
                    else Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: Navigation.Init unresolved (path-draw may NRE)");
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: Navigation.Init failed: " + ex.Message); }

                // Register WITHOUT cascade: add directly to GeoMap.Vehicles (bypasses RegisterActor — pure mirror),
                // then write the owner-faction cache via the PUBLIC native GeoMap.UpdateVehicleOwner(v, null, owner)
                // (GeoMap.cs:524-528) — GeoFaction.Vehicles IS Map.VehiclesByOwner (that cache, GeoFaction.cs:137),
                // and UIStateVehicleSelected.OnVehichleChanged keys its keep-selected check on it. Also raises
                // Map.VehicleChanged so open aircraft lists repaint natively.
                try
                {
                    var map = GetMap(rt);
                    if (map != null && _vehiclesProp.GetValue(map, null) is IList list)
                    {
                        list.Add(vehicle);
                        try { _updateVehicleOwner?.Invoke(map, new object[] { vehicle, null, faction }); }
                        catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: UpdateVehicleOwner failed: " + ex.Message); }
                    }
                    else
                        Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: GeoMap.Vehicles not an IList (not registered)");
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: Vehicles.Add failed: " + ex.Message); }

                // Force the native visibility/marker so it renders (owner-visuals were set by the Owner setter).
                try { _refreshVisibility?.Invoke(vehicle, null); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle: RefreshVisibility failed: " + ex.Message); }

                Debug.Log("[Multiplayer][geo] SpawnMirrorVehicle: spawned inert mirror vehicle key=" + identity.Key.ToString("X")
                          + " id=" + identity.VehicleId + " ownerGuid=" + identity.OwnerFactionDefGuid + " setGuid=" + identity.VehicleSetDefGuid);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.SpawnMirrorVehicle failed: " + ex.Message); }
        }

        /// <summary>CLIENT: despawn the live vehicle whose composite key was TOMBSTONED by the host (craft
        /// destroyed/lost — without this the client mirror ghosts in GeoMap.Vehicles forever). Two teardown paths,
        /// each matching how the vehicle ENTERED this client:
        ///   • <c>InPlay</c> (join-save-loaded, ran full native DoEnterPlay) → native <c>GeoVehicle.Destroy()</c>
        ///     (GeoVehicle.cs:593-604: OnVehicleDestroyed → OnExitPlay unregisters faction events + GeoMap actor/
        ///     cache/list + raises VehicleChanged → Object.Destroy) — identical to the host's own destroy.
        ///   • inert mirror (spawned here, never registered) → symmetric no-cascade teardown: remove from
        ///     GeoMap.Vehicles, undo the owner cache via UpdateVehicleOwner(v, owner, null) (raises VehicleChanged
        ///     so the native UI deselects/repaints), destroy the GameObject.
        /// Idempotent (no live key → no-op); best-effort; never throws into the apply loop.</summary>
        public static void DespawnVehicle(GeoRuntime rt, long key)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return;
                var map = GetMap(rt);
                if (map == null || !(_vehiclesProp?.GetValue(map, null) is IList list)) return;

                object victim = null;
                foreach (var v in list)
                {
                    if (v == null) continue;
                    if (TryReadKey(v, out long k) && k == key) { victim = v; break; }
                }
                if (victim == null) return;   // already gone / never spawned here → tombstone no-op

                bool inPlay = false;
                try { inPlay = _inPlayProp != null && (bool)_inPlayProp.GetValue(victim, null); } catch { }
                if (inPlay && _vehicleDestroy != null)
                {
                    _vehicleDestroy.Invoke(victim, null);   // full native teardown (it removes itself from the map)
                }
                else
                {
                    // List first, so the VehicleChanged raised by the cache write already sees the vehicle gone.
                    list.Remove(victim);
                    object owner = null;
                    try { owner = _ownerProp?.GetValue(victim, null); } catch { }
                    try { _updateVehicleOwner?.Invoke(map, new object[] { victim, owner, null }); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.DespawnVehicle: UpdateVehicleOwner failed: " + ex.Message); }
                    if (victim is Component comp && comp != null)
                        UnityEngine.Object.Destroy(comp.gameObject);
                }
                Debug.Log("[Multiplayer][geo] DespawnVehicle: tombstoned vehicle removed key=" + key.ToString("X") + " inPlay=" + inPlay);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoVehicleIdentityReflection.DespawnVehicle failed: " + ex.Message); }
        }

        /// <summary>Find the live <c>GeoFaction</c> whose <c>Def.Guid</c> equals <paramref name="guid"/>, or null.</summary>
        private static object ResolveFactionByGuid(GeoRuntime rt, string guid)
        {
            if (string.IsNullOrEmpty(guid) || _factionsField == null || _factionDefProp == null) return null;
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            if (!(_factionsField.GetValue(geo) is IEnumerable facs)) return null;
            foreach (var f in facs)
            {
                if (f == null) continue;
                if (DefReflection.GetGuid(_factionDefProp.GetValue(f, null)) == guid) return f;
            }
            return null;
        }
    }
}
