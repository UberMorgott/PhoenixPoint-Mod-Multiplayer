using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection boundary for the BEHEMOTH mirror (WA-1, gap 5a). Game members resolved by name + cached
    /// (mirrors <see cref="GeoVehicleIdentityReflection"/>); conventions in <see cref="GeoBehemothState"/>.
    ///   • HOST <see cref="TryReadHost"/> — read <c>GeoLevel.AlienFaction.Behemoth</c> (the ONLY native
    ///     registry of the actor — it lives outside <c>GeoMap.Vehicles</c>) into a reserved-key
    ///     <see cref="GeoVehiclePos"/> record (pivot localRotation + Surface heading — the exact fields the
    ///     shared <c>GeoNavComponent.NavigateRoutine</c> writes for the behemoth too) + its
    ///     <c>CurrentBehemothStatus</c> byte.
    ///   • CLIENT <see cref="ApplyPresence"/> — spawn-if-absent an INERT mirror via the native template:
    ///     <c>ActorSpawner.SpawnActor&lt;GeoBehemothActor&gt;(FesteringSkiesSettings.BehemothDef, null,
    ///     callEnterPlayOnActor:false)</c> — the SAME def+call the native <c>GeoAlienFaction.SpawnBehemoth</c>
    ///     uses (GeoAlienFaction.cs:1333) but WITHOUT <c>DoEnterPlay</c>/<c>OnLevelStart</c>/<c>OnFactionsReady</c>,
    ///     so NO producers run on the frozen client (no target-picking, no SubmergeCrt, no navigation — the
    ///     behemoth "AI" is host-only). It is then registered via the PUBLIC native
    ///     <c>GeoAlienFaction.RegisterBehemoth</c> so the accessor + native FS UI (tooltip, disruption meter,
    ///     BehemothSpawned listeners) see it — that registration is what makes the mirror resolvable by the
    ///     0xA5 walk and by <see cref="Despawn"/>. Status stamps are VALUE-ONLY (private status field +
    ///     VisualsRoot active + Animator int — the native display contract), idempotent per re-emission.
    ///   • CLIENT <see cref="Despawn"/> — tombstoned key → native <c>GeoBehemothActor.Destroy()</c>
    ///     (OnDespawned → OnExitPlay → UnregisterBehemoth → Object.Destroy — the same teardown the host's
    ///     <c>RemoveBehemoth</c> drives), with a manual unregister+destroy fallback if the native path throws.
    /// All null-safe/best-effort: a missing member degrades (logged) rather than throwing.
    /// </summary>
    public static class GeoBehemothReflection
    {
        private static bool _ready;
        private static PropertyInfo _alienFactionProp;   // GeoLevelController.AlienFaction (GeoAlienFaction)
        private static PropertyInfo _behemothProp;       // GeoAlienFaction.Behemoth (GeoBehemothActor)
        private static MethodInfo _registerBehemoth;     // GeoAlienFaction.RegisterBehemoth(GeoBehemothActor)
        private static MethodInfo _unregisterBehemoth;   // GeoAlienFaction.UnregisterBehemoth(GeoBehemothActor)
        private static Type _behemothType;               // PhoenixPoint.Geoscape.Entities.GeoBehemothActor
        private static PropertyInfo _bSurface;           // GeoBehemothActor.Surface (Transform — heading)
        private static PropertyInfo _bStatus;            // GeoBehemothActor.CurrentBehemothStatus (enum, host read)
        private static FieldInfo _bStatusField;          // GeoBehemothActor._currentBehemothStatus (client value-write)
        private static FieldInfo _bVisualsRoot;          // GeoBehemothActor.VisualsRoot (GameObject)
        private static FieldInfo _bAnimator;             // GeoBehemothActor.Animator (Animator)
        private static MethodInfo _bDestroy;             // GeoBehemothActor.Destroy()
        private static FieldInfo _fsSettings;            // GeoLevelController.FesteringSkiesSettings (public field)
        private static FieldInfo _behemothDef;           // FesteringSkiesSettingsDef.BehemothDef (ComponentSetDef)
        private static MethodInfo _spawnActorBehemoth;   // ActorSpawner.SpawnActor<GeoBehemothActor>(BaseDef, ActorInstanceData, bool)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var geoType = geo.GetType();
            _alienFactionProp = AccessTools.Property(geoType, "AlienFaction");
            _fsSettings = AccessTools.Field(geoType, "FesteringSkiesSettings");
            if (_fsSettings != null) _behemothDef = AccessTools.Field(_fsSettings.FieldType, "BehemothDef");

            _behemothType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoBehemothActor");
            if (_behemothType != null)
            {
                _bSurface = AccessTools.Property(_behemothType, "Surface");
                _bStatus = AccessTools.Property(_behemothType, "CurrentBehemothStatus");
                _bStatusField = AccessTools.Field(_behemothType, "_currentBehemothStatus");
                _bVisualsRoot = AccessTools.Field(_behemothType, "VisualsRoot");
                _bAnimator = AccessTools.Field(_behemothType, "Animator");
                _bDestroy = AccessTools.Method(_behemothType, "Destroy", Type.EmptyTypes);
            }
            if (_alienFactionProp != null)
            {
                var facType = _alienFactionProp.PropertyType;
                _behemothProp = AccessTools.Property(facType, "Behemoth");
                _registerBehemoth = AccessTools.Method(facType, "RegisterBehemoth");
                _unregisterBehemoth = AccessTools.Method(facType, "UnregisterBehemoth");
            }
            var actorSpawnerType = AccessTools.TypeByName("Base.Entities.ActorSpawner");
            if (actorSpawnerType != null && _behemothType != null)
            {
                var open = actorSpawnerType.GetMethod("SpawnActor", BindingFlags.Public | BindingFlags.Static);
                if (open != null && open.IsGenericMethodDefinition)
                {
                    try { _spawnActorBehemoth = open.MakeGenericMethod(_behemothType); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection: SpawnActor<GeoBehemothActor> bind failed (spawn disabled): " + ex.Message); }
                }
            }
            // Core gate: accessor + status read. Spawn/despawn members stay best-effort (a miss degrades that
            // piece only, logged at use).
            _ready = _alienFactionProp != null && _behemothProp != null && _behemothType != null && _bStatus != null;
        }

        /// <summary>The live behemoth actor (<c>GeoLevel.AlienFaction.Behemoth</c>) as a Component, or null
        /// (no FS activity this campaign / not in geoscape / destroyed).</summary>
        private static Component ResolveLive(GeoRuntime rt)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return null;
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                object faction = _alienFactionProp.GetValue(geo, null);
                if (faction == null) return null;
                var comp = _behemothProp.GetValue(faction, null) as Component;
                return comp != null ? comp : null;   // Unity-null → null
            }
            catch { return null; }
        }

        /// <summary>CLIENT: the live behemoth for the 0xA5 apply path (composite-key lookup extension).</summary>
        public static bool TryResolveLive(out Component behemoth)
        {
            behemoth = ResolveLive(GeoRuntime.Instance);
            return behemoth != null;
        }

        /// <summary>HOST (per 0xA5 poll): the behemoth's reserved-key placement record + status byte. False when
        /// no behemoth is in the session (key simply absent from the walk).</summary>
        public static bool TryReadHost(GeoRuntime rt, out GeoVehiclePos rec, out byte status)
        {
            rec = default(GeoVehiclePos); status = 0;
            try
            {
                var comp = ResolveLive(rt);
                if (comp == null) return false;
                Transform pivot = comp.transform;
                if (pivot == null) return false;
                Quaternion q = pivot.localRotation;
                if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)) return false;
                Vector3 heading = Vector3.zero;
                if (_bSurface?.GetValue(comp, null) is Transform surface && surface != null)
                    heading = surface.localEulerAngles;
                status = (byte)Convert.ToInt32(_bStatus.GetValue(comp, null));
                rec = new GeoVehiclePos(GeoBehemothState.OwnerId, GeoBehemothState.VehicleId,
                                        heading.x, heading.y, heading.z, q.x, q.y, q.z, q.w);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.TryReadHost failed: " + ex.Message); return false; }
        }

        /// <summary>CLIENT (interp driver): write one interpolated placement to the behemoth's transforms —
        /// pivot localRotation + its OWN Surface heading property (the vehicle mirror's cached GeoVehicle
        /// property cannot be invoked on a GeoBehemothActor). Mirrors <c>GeoVehicleMirror.WritePlacement</c>.</summary>
        public static bool WritePlacement(Component comp, VehicleInterpolator.Sample s)
        {
            if (comp == null) return false;
            try
            {
                Transform pivot = comp.transform;
                if (pivot == null) return false;
                pivot.localRotation = new Quaternion(s.QX, s.QY, s.QZ, s.QW);
                if (_bSurface?.GetValue(comp, null) is Transform surface && surface != null)
                    surface.localEulerAngles = new Vector3(s.X, s.Y, s.Z);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.WritePlacement failed: " + ex.Message); return false; }
        }

        /// <summary>CLIENT (channel #6 apply): behemoth sentinel identity → spawn an inert mirror when absent
        /// (idempotent ADOPT when one is already live from the join save — never a duplicate), then stamp the
        /// status display values. Runs inside the caller's <c>SyncApplyScope</c>.</summary>
        public static void ApplyPresence(GeoRuntime rt, GeoVehicleIdentity identity)
        {
            try
            {
                if (!GeoBehemothState.TryParseStatus(identity, out byte status)) return;
                Ensure(rt);
                if (!_ready) return;
                var live = ResolveLive(rt);
                if (live == null)
                    live = SpawnMirror(rt, identity);
                if (live == null) return;
                StampStatus(live, status);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.ApplyPresence failed: " + ex.Message); }
        }

        private static Component SpawnMirror(GeoRuntime rt, GeoVehicleIdentity identity)
        {
            if (_spawnActorBehemoth == null)
            {
                Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.SpawnMirror: SpawnActor<GeoBehemothActor> unresolved (skipped)");
                return null;
            }
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            object settings = _fsSettings?.GetValue(geo);
            object def = settings != null ? _behemothDef?.GetValue(settings) : null;
            if (def == null)
            {
                Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.SpawnMirror: FesteringSkiesSettings.BehemothDef unresolved (skipped)");
                return null;
            }

            // INERT spawn: Instantiate + SetActorRootParent only — no DoEnterPlay/OnLevelStart/OnFactionsReady,
            // so no producers/AI ever run on the frozen client (native spawn: GeoAlienFaction.cs:1333-1336).
            object actor = _spawnActorBehemoth.Invoke(null, new object[] { def, null, false });
            if (!(actor is Component comp) || comp == null)
            {
                Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.SpawnMirror: SpawnActor<GeoBehemothActor> returned null (skipped)");
                return null;
            }

            // Initial placement from the identity so it appears in the right spot before the next 0xA5 record.
            try
            {
                comp.transform.localRotation = new Quaternion(identity.QX, identity.QY, identity.QZ, identity.QW);
                if (_bSurface?.GetValue(comp, null) is Transform surface && surface != null)
                    surface.localEulerAngles = new Vector3(identity.X, identity.Y, identity.Z);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.SpawnMirror: placement stamp failed: " + ex.Message); }

            // Register via the PUBLIC native RegisterBehemoth: sets AlienFaction.Behemoth (what the 0xA5 apply
            // + Despawn resolve) and fires BehemothSpawned so native FS UI reacts. The handlers it subscribes
            // only ever fire from producers — none run on the frozen client.
            try
            {
                object faction = _alienFactionProp.GetValue(geo, null);
                if (faction != null && _registerBehemoth != null)
                    _registerBehemoth.Invoke(faction, new object[] { comp });
                else
                    Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.SpawnMirror: RegisterBehemoth unresolved (mirror not adopted by faction)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.SpawnMirror: RegisterBehemoth failed: " + ex.Message); }

            Debug.Log("[Multiplayer][geo] SpawnMirror: spawned inert behemoth mirror key=" + GeoBehemothState.Key.ToString("X"));
            return comp;
        }

        /// <summary>Value-only display stamp: status field + VisualsRoot active (Dormant/Dead hidden — the
        /// native submerge contract) + Animator State int (Idle=0/Moving=1). Never calls the native status
        /// setter (its transitions start navigation/producers).</summary>
        private static void StampStatus(Component behemoth, byte status)
        {
            try
            {
                if (_bStatusField != null)
                    _bStatusField.SetValue(behemoth, Enum.ToObject(_bStatusField.FieldType, (int)status));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.StampStatus: status field failed: " + ex.Message); }
            try
            {
                if (_bVisualsRoot?.GetValue(behemoth) is GameObject visuals && visuals != null)
                    visuals.SetActive(GeoBehemothState.VisualsVisible(status));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.StampStatus: visuals failed: " + ex.Message); }
            try
            {
                if (_bAnimator?.GetValue(behemoth) is Animator animator && animator != null)
                    animator.SetInteger("State", GeoBehemothState.AnimatorState(status));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.StampStatus: animator failed: " + ex.Message); }
        }

        /// <summary>CLIENT (channel #6 tombstone): despawn the live behemoth (host lost/removed it). Native
        /// <c>Destroy()</c> first (full teardown incl. UnregisterBehemoth — same path the host's RemoveBehemoth
        /// drives); manual unregister+destroy fallback if it throws. Idempotent: none live → no-op.</summary>
        public static void Despawn(GeoRuntime rt)
        {
            try
            {
                Ensure(rt);
                var live = ResolveLive(rt);
                if (live == null) return;   // already gone / never present → tombstone no-op
                try
                {
                    if (_bDestroy == null) throw new MissingMethodException("GeoBehemothActor.Destroy unresolved");
                    _bDestroy.Invoke(live, null);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.Despawn: native Destroy failed (" + ex.Message + ") — manual teardown");
                    try
                    {
                        var geo = rt?.GeoLevel();
                        object faction = geo != null ? _alienFactionProp?.GetValue(geo, null) : null;
                        // UnregisterBehemoth reads the faction's Behemoth property internally — only safe while
                        // it still points at our victim.
                        if (faction != null && _unregisterBehemoth != null
                            && ReferenceEquals(_behemothProp?.GetValue(faction, null), live))
                            _unregisterBehemoth.Invoke(faction, new object[] { live });
                    }
                    catch (Exception ex2) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.Despawn: manual unregister failed: " + ex2.Message); }
                    if (live != null && live.gameObject != null)
                        UnityEngine.Object.Destroy(live.gameObject);
                }
                Debug.Log("[Multiplayer][geo] Despawn: behemoth mirror removed (tombstone)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoBehemothReflection.Despawn failed: " + ex.Message); }
        }
    }
}
