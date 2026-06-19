using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// Feature C — client-only guards active ONLY for the duration of a <see cref="TacticalFireAnimSync"/>
    /// attack-animation replay (the flags are raised in <c>TacticalFireAnimSync.ClientOnFireStart</c>'s
    /// wrapper coroutine and lowered in its finally). They make the replayed <c>FireWeaponAtTargetCrt</c>:
    ///   • CAMERA-SILENT — <see cref="FireCameraHintGuardPatch"/> no-ops every <c>CameraDirector.Hint</c>
    ///     push, so the camera NEVER flies to the shooter (even the <c>ShootingStarted</c> hint the engine
    ///     fires regardless of AttackType). The enemy-turn camera-follow path is untouched (it works via the
    ///     enemy's own local Activate, which is NOT during a replay).
    ///   • PROJECTILE-FREE / ZERO-DAMAGE — <see cref="FireProjectileNeuterPatch"/> skips
    ///     <c>Weapon.FireProjectile</c> (returns null) so NO damage-carrying projectile is instantiated; DAMAGE
    ///     stays owned by tac.damage. <see cref="WaitForProjectilesNeuterPatch"/> then returns an empty
    ///     coroutine so the null-in-list never NPEs and the post-shot damage/casualty events never re-raise on
    ///     the client.
    ///   • GRENADE-DESTROY-SAFE — <see cref="ThrowableDestroyGuardPatch"/> no-ops the throwable's
    ///     <c>weapon.Destroy()</c> (the <c>if (weapon.IsThrowable) weapon.Destroy()</c> step inside
    ///     <c>FireWeaponAtTargetCrt</c>). On the client that <c>Destroy()</c> would call
    ///     <c>InventoryComponent.RemoveItem</c> on the client's OWN grenade item OUTSIDE the host-authoritative
    ///     equip/actor-state reconcile → inventory desync / double-destroy. Skipped during the replay so the
    ///     host stays authoritative and the existing equip-sync owns the grenade's removal on the client.
    /// All four are HARD-GATED on <see cref="TacticalFireAnimSync.ReplayActive"/> (only a client replay sets
    /// it). Belt-and-suspenders, each guard ALSO requires <see cref="FireReplayGate.ClientReplay"/> (replay flag
    /// AND not-host) so the host can NEVER be affected even if the flag were somehow raised host-side. The
    /// host and the client's normal (non-replay) flow are completely unaffected. Auto-register via PatchAll;
    /// reflection targets so a method-rename can't hard-crash bootstrap.
    /// </summary>
    /// <summary>Shared gate for every Feature-C replay guard: the replay flag is the fast primary check; the
    /// <c>!IsHost</c> term is defense-in-depth so a guard can never neuter the host's own authoritative shot.</summary>
    internal static class FireReplayGate
    {
        // True ONLY during a client fire-anim replay. Flag check first (cheap, almost always false); the host
        // check is a never-hit-on-host backstop reading the mod's own engine accessor.
        public static bool ClientReplay => TacticalFireAnimSync.ReplayActive && !IsHost;

        private static bool IsHost
        {
            get { var e = NetworkEngine.Instance; return e != null && e.IsHost; }
        }
    }
    [HarmonyPatch]
    public static class FireCameraHintGuardPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("Base.Cameras.CameraDirector");
            if (t == null) return false;
            var hintType = AccessTools.TypeByName("Base.Cameras.CameraDirectorHint");
            var paramType = AccessTools.TypeByName("Base.Cameras.CameraDirectorParams");
            if (hintType == null || paramType == null) return false;
            // public void Hint(CameraDirectorHint hint, CameraDirectorParams param) — the directorial push
            // that flies the camera. The other Hint(CameraHint, object) overload is left alone.
            _target = AccessTools.Method(t, "Hint", new[] { hintType, paramType });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Skip the camera-directing hint push ONLY while a client fire-animation replay is in flight.
        public static bool Prefix() => !FireReplayGate.ClientReplay;
    }

    /// <summary>Client replay: skip <c>Weapon.FireProjectile(...)</c> → no projectile, no damage. Returns null
    /// for the skipped <c>Projectile</c>; the partner <see cref="WaitForProjectilesNeuterPatch"/> makes the
    /// coroutine's wait-for-hit a no-op so the null is never dereferenced.</summary>
    [HarmonyPatch]
    public static class FireProjectileNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Weapons.Weapon");
            if (t == null) return false;
            // internal Projectile FireProjectile(TacticalActor, TacticalAbilityTarget, ShootAbility, string, int)
            _target = AccessTools.Method(t, "FireProjectile");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!FireReplayGate.ClientReplay) return true;   // host / normal client flow → real shot
            __result = null;   // no projectile spawned during the replay → zero client damage
            return false;
        }
    }

    /// <summary>Client replay: short-circuit <c>Weapon.WaitForProjectilesToHit(List&lt;Projectile&gt;, Action)</c>
    /// to an EMPTY coroutine so (a) the null projectile from the neutered FireProjectile is never dereferenced,
    /// and (b) the onProjectilesHit callback (which re-raises shooting/damage/casualty events) does NOT run on
    /// the client — those are the host's authority and arrive via tac.damage. Reuses <see cref="EmptyCrt"/>.</summary>
    [HarmonyPatch]
    public static class WaitForProjectilesNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Weapons.Weapon");
            if (t == null) return false;
            _target = AccessTools.Method(t, "WaitForProjectilesToHit");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!FireReplayGate.ClientReplay) return true;
            var empty = EmptyCrt.Empty();
            if (empty == null) return true;   // couldn't build empty crt → safest to let it run
            __result = empty;
            return false;   // skip original (no callback, no null-deref)
        }
    }

    /// <summary>Client replay: NO-OP the throwable's <c>weapon.Destroy()</c> that <c>FireWeaponAtTargetCrt</c>
    /// runs for an <c>IsThrowable</c> weapon (decompile <c>TacticalLevelController.cs:~1796</c>). The runtime
    /// type is a <c>Weapon</c>, whose most-derived <c>Destroy()</c> override is
    /// <c>PhoenixPoint.Tactical.Entities.Equipments.TacticalItem.Destroy()</c> — it calls
    /// <c>InventoryComponent.RemoveItem(this)</c>, i.e. it would yank the client's OWN grenade item out of the
    /// inventory OUTSIDE the host-authoritative equip/actor-state reconcile → inventory desync / double-destroy
    /// when the reconcile later removes it too. During a client fire-anim replay we skip the local Destroy and
    /// let the host stay authoritative; the existing equip-sync owns the grenade's removal on the client. The
    /// return value (<c>List&lt;Addon&gt;</c>) is discarded by the calling step, so an empty list is a safe
    /// __result. HARD-GATED on the replay flag + <c>!IsHost</c>, exactly like the other three guards.</summary>
    [HarmonyPatch]
    public static class ThrowableDestroyGuardPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.TacticalItem");
            if (t == null) return false;
            // public override List<Addon> Destroy() — single zero-arg override (the one Weapon resolves to).
            _target = AccessTools.Method(t, "Destroy", new Type[0]);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!FireReplayGate.ClientReplay) return true;   // host / normal flow → real Destroy
            __result = null;   // calling step `weapon.Destroy();` discards the List<Addon> return → null is safe
            return false;      // skip the local inventory-mutating Destroy during the replay
        }
    }
}
