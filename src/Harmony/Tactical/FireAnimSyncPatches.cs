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
    ///   • PROJECTILE FLIES, DAMAGE-LESS — <c>Weapon.FireProjectile</c> runs FULLY (muzzle flash, smoke, shell,
    ///     SFX, and a real tracer projectile that flies to the target) so the firing visuals appear; only the
    ///     DAMAGE is suppressed: <see cref="ProjectileDamageNeuterPatch"/> skips <c>ProjectileLogic.AffectTarget</c>
    ///     so <c>_damageAccum</c> stays null → <c>OnTrajectoryEnd</c>'s <c>ApplyAddedDamage()</c> never runs → no
    ///     client-side damage (DAMAGE stays owned by tac.damage / 0x88). <see cref="WaitForProjectilesNeuterPatch"/>
    ///     then returns an empty coroutine so the post-shot damage/casualty events never re-raise on the client.
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

        // True ONLY during a client MELEE-anim replay (TacticalMeleeAnimSync.ClientOnMeleeStart). Same !IsHost
        // backstop so a melee guard can never neuter the host's own authoritative swing.
        public static bool MeleeReplay => TacticalMeleeAnimSync.MeleeReplayActive && !IsHost;

        // Shared predicate for guards that must fire during EITHER replay (e.g. the ammo-charge neuter — both
        // the fire and the melee coroutines spend charges via CommonItemData.ModifyCharges). Fire-only guards
        // (projectile-damage, wait-for-projectiles, throwable-destroy, camera-hint) stay on ClientReplay.
        public static bool AnyReplay => ClientReplay || MeleeReplay;

        // True ONLY while the HOST is executing a RELAYED CLIENT shot (TacticalCombatSync.RelayedShots is populated
        // solely in HostOnAbilityIntent, drained at OnPlayingActionEnd). Used by the camera-hint guard so the host's
        // camera does NOT fly to the client's shooter during a relayed shot; the host's OWN shots are never in
        // RelayedShots, so their cinematic is untouched. IsHost-gated so a client (Count is host-only anyway) no-ops.
        public static bool HostRelayedShotActive => IsHost && TacticalCombatSync.RelayedShots.Count > 0;

        // BUG2 — UNIFIED host camera-follow guard for ALL relayed CLIENT actions (not just shots). A ref-counted
        // window the host raises around EVERY relayed client action it re-executes (move / overwatch / melee /
        // non-shoot ability — each HostOn*Intent's activate.Invoke). Native TacticalAbility.Activate SYNCHRONOUSLY
        // pushes CameraDirector.Hint(AbilityActivated) (TacticalAbility.cs:1104, gated TrackWithCamera) inside that
        // Invoke; HostRelayedShotActive above is populated ONLY for ShootAbility, so without this window a relayed
        // MOVE/OVERWATCH/MELEE flies the host camera to the client's actor and steals control. The host's OWN actions
        // never enter HostOn*Intent, so their cinematic is untouched. Ref-counted (depth) so a nested/re-entrant apply
        // pops safely; ExitHostApply floors at 0 so an unbalanced pop can never wedge the flag permanently true.
        private static int _hostApplyDepth;
        public static void EnterHostApply() { _hostApplyDepth++; }
        public static void ExitHostApply() { if (_hostApplyDepth > 0) _hostApplyDepth--; }
        public static bool HostApplyingClientAction => IsHost && _hostApplyDepth > 0;

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

        // Skip the camera-directing hint push while EITHER a client fire-animation replay is in flight OR the host is
        // executing a relayed CLIENT shot OR the host is applying ANY relayed client action (BUG2 — move/overwatch/
        // melee/non-shoot, whose synchronous Activate camera hint would otherwise fly the host camera to the client's
        // actor and steal control). The host's OWN actions never enter the HostApplyingClientAction window → full cinematic.
        public static bool Prefix() => !(FireReplayGate.ClientReplay || FireReplayGate.HostRelayedShotActive || FireReplayGate.HostApplyingClientAction);
    }

    /// <summary>Client replay: let <c>Weapon.FireProjectile</c> run FULLY (real tracer + flash/smoke/shell/SFX so
    /// the firing visuals appear), but skip the projectile's DAMAGE by Prefix-no-opping
    /// <c>ProjectileLogic.AffectTarget(CastHit, Vector3)</c> — the SOLE call that builds <c>_damageAccum</c>
    /// (decompile <c>ProjectileLogic.cs:384,433-440</c>). With <c>AffectTarget</c> skipped, <c>_damageAccum</c>
    /// stays null, so <c>OnTrajectoryEnd</c>'s <c>if (_damageAccum != null) … ApplyAddedDamage()</c>
    /// (decompile <c>ProjectileLogic.cs:404,424</c>) never runs → ZERO client-side damage. DAMAGE stays the
    /// host's authority and arrives via tac.damage (opcode 0x88). Single private method, no overloads, so the
    /// name-only <c>AccessTools.Method</c> binds unambiguously (verified against the decompile). HARD-GATED on
    /// <see cref="FireReplayGate.ClientReplay"/> exactly like the other guards — the host's own projectile keeps
    /// its damage.</summary>
    [HarmonyPatch]
    public static class ProjectileDamageNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Weapons.ProjectileLogic");
            if (t == null) return false;
            // private void AffectTarget(CastHit hit, Vector3 dir) — single overload, name-only match is exact.
            _target = AccessTools.Method(t, "AffectTarget");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix()
        {
            // host / normal client flow → real damage-accum; client replay → skip so _damageAccum stays null.
            return !FireReplayGate.ClientReplay;
        }
    }

    /// <summary>Client replay (FIX #2a): NEUTER the ammo-charge decrement that the now-fully-running
    /// <c>Weapon.FireProjectile</c> performs — <c>base.CommonItemData.ModifyCharges(-AmmoChargesPerProjectile)</c>
    /// (decompile <c>Weapon.cs:530</c>, the SOLE charge-spend inside FireProjectile; the <c>ChargesMax &lt;= 0</c>
    /// branch at <c>:525-528</c> returns before it). Since FIX #4 let the projectile fly for real, that decrement
    /// now also runs on the client replay, draining the client's (host-authoritative) ammo with no sync surface to
    /// refill it → ammo drifts low. Prefix-no-op <c>CommonItemData.ModifyCharges(int, bool)</c> (single method, no
    /// overloads → name-only <c>AccessTools.Method</c> binds exactly) so NO charge mutation happens during the
    /// replay. SCOPING: <c>FireReplayGate.ClientReplay</c> is true ONLY inside the <c>ReplayFireCrt</c> window, so
    /// the only ModifyCharges calls it can suppress are the replay's own fire decrements — unrelated charge changes
    /// (reload, item use, host's own shots) are never in that window. Ammo stays host-authoritative, frozen at the
    /// snapshot value until a future ammo-sync surface (TODO — NOT built here). HARD-GATED exactly like the other
    /// guards (replay flag + <c>!IsHost</c>).</summary>
    [HarmonyPatch]
    public static class FireAmmoChargeNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Entities.CommonItemData");
            if (t == null) return false;
            // public bool ModifyCharges(int chargesDelta, bool canCreateMagazines = false) — single overload.
            _target = AccessTools.Method(t, "ModifyCharges");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // host / normal client flow → real charge change; client replay → skip so ammo stays host-authoritative.
        // Gated on AnyReplay (fire OR melee): BOTH coroutines spend charges via this SAME ModifyCharges (fire =
        // Weapon.cs:530, melee = BashAbility.BashCrt:525). One shared neuter, no duplicate patch on the method.
        public static bool Prefix(ref bool __result)
        {
            if (!FireReplayGate.AnyReplay) return true;
            __result = false;   // ModifyCharges returns false when it makes no change → safe no-op result
            return false;       // skip the original decrement during the replay
        }
    }

    /// <summary>Client replay: short-circuit <c>Weapon.WaitForProjectilesToHit(List&lt;Projectile&gt;, Action)</c>
    /// to an EMPTY coroutine so the onProjectilesHit callback (which re-raises shooting/damage/casualty events)
    /// does NOT run on the client — those are the host's authority and arrive via tac.damage. The projectile now
    /// flies for real (damage-less, see <see cref="ProjectileDamageNeuterPatch"/>); we still skip the wait so the
    /// host stays the sole authority for the post-shot events. Reuses <see cref="EmptyCrt"/>.</summary>
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
