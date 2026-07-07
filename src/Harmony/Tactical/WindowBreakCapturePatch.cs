using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// STRUCTURAL-DESTRUCTION mirror — DIRECT geometry-break capture (spec TS6, gap-window-break-geometry).
    /// A window / fragile pane broken by ACTOR BODY PASSAGE (vault / move-through) rides the native
    /// <c>TacticalNavigationComponent.TriggerWindowBreak</c> → <c>DestructableBase.ApplyDamage(origin, direction,
    /// force)</c> path, which calls the break (<c>Explode</c>) STRAIGHT — never touching
    /// <c>DestructableDamageReceiver.ApplyDamage(DamageResult)</c>, so the combat funnel
    /// (<see cref="StructDamageCapturePatch"/>) misses it and the client kept intact glass (cosmetic divergence +
    /// cover/LoS mismatch at that tile). This HOST postfix funnels the break into the SAME 0x96 surface via
    /// <see cref="TacticalStructDamageSync.HostCaptureDirectBreak"/> (guid + origin + guaranteed-break hp); the
    /// client replays it through the ordinary kind-1 receiver path (receiver zeroes → the SAME native break runs)
    /// — NO new surface, NO new codec kind.
    ///
    /// TARGETS: the abstract overload's ONLY concrete break implementations (pinned against the REAL assembly in
    /// <c>WindowBreakBindingPinTests</c>): <c>Breakable.ApplyDamage(Vector3,Vector3,float)</c> → <c>Explode</c>
    /// and <c>Swappables.ApplyDamage(Vector3,Vector3,float)</c> → <c>Explode</c> (glass pane swap-to-broken).
    /// <c>Destructable</c>'s override is a NOT-IMPLEMENTED log stub — nothing to mirror.
    ///
    /// DEDUP / IDEMPOTENCY: the prefix snapshots the target's own native re-break guard (<c>Breakable._broken</c>;
    /// a Swappables' collider already disabled), and the postfix captures ONLY an actual unbroken→broken
    /// transition, so repeated break events on the same pane broadcast once. If the guard read fails (rename), it
    /// degrades to capture-always — the client replay stays idempotent (receiver health-already-zero /
    /// <c>_broken</c> / collider-disabled guards make a re-break a clean no-op). All host/session gating lives in
    /// the sync layer (host + active + deploy-captured + not applying-remote), so this firing on the CLIENT (its
    /// own native window break during a mirrored move) is a clean no-op. Auto-registers via PatchAll;
    /// reflection-target lazily like the sibling tactical patches.
    /// </summary>
    [HarmonyPatch]
    public static class WindowBreakCapturePatch
    {
        private static readonly List<MethodBase> _targets = new List<MethodBase>();
        private static Type _breakableType;      // PhoenixPoint.Tactical.Levels.Destruction.Breakable
        private static Type _swappablesType;     // PhoenixPoint.Tactical.Levels.Destruction.Swappables
        private static Type _colliderType;       // UnityEngine.Collider (PhysicsModule — resolved reflectively)
        private static FieldInfo _fBroken;       // Breakable._broken (bool) — its native re-break guard
        private static PropertyInfo _pColEnabled; // Collider.enabled — Swappables' native re-break guard

        public static bool Prepare()
        {
            _breakableType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Destruction.Breakable");
            _swappablesType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Destruction.Swappables");
            // public override void ApplyDamage(Vector3 origin, Vector3 direction, float force) — EXACT param match
            // (disambiguates from the (Vector3, float, float) radius overload).
            var sig = new[] { typeof(Vector3), typeof(Vector3), typeof(float) };
            _targets.Clear();
            if (_breakableType != null)
            {
                var m = AccessTools.Method(_breakableType, "ApplyDamage", sig);
                if (m != null) _targets.Add(m);
                _fBroken = AccessTools.Field(_breakableType, "_broken");   // optional (dedup only)
            }
            if (_swappablesType != null)
            {
                var m = AccessTools.Method(_swappablesType, "ApplyDamage", sig);
                if (m != null) _targets.Add(m);
            }
            _colliderType = AccessTools.TypeByName("UnityEngine.Collider");
            if (_colliderType != null) _pColEnabled = AccessTools.Property(_colliderType, "enabled");
            return _targets.Count > 0;
        }

        public static IEnumerable<MethodBase> TargetMethods() => _targets;

        /// <summary>The target's OWN native re-break guard, read BEFORE the call: a Breakable that is already
        /// <c>_broken</c> / a Swappables whose collider is already disabled will no-op natively — nothing to mirror.
        /// Any read failure degrades to false (capture-always; the client replay is idempotent anyway).</summary>
        private static bool AlreadyBroken(object inst)
        {
            try
            {
                if (_breakableType != null && _breakableType.IsInstanceOfType(inst))
                    return _fBroken != null && (bool)_fBroken.GetValue(inst);
                if (_swappablesType != null && _swappablesType.IsInstanceOfType(inst) && _pColEnabled != null)
                {
                    var col = (inst as Component)?.GetComponent(_colliderType);
                    if (col != null) return !(bool)_pColEnabled.GetValue(col, null);
                }
            }
            catch { /* degrade: capture-always */ }
            return false;
        }

        // __state = the pane was ALREADY broken before this call (native break no-ops in that case).
        public static void Prefix(object __instance, ref bool __state)
        {
            __state = __instance != null && AlreadyBroken(__instance);
        }

        // __0 = origin (the break point fed to the native break). Capture only an unbroken→broken transition.
        public static void Postfix(object __instance, Vector3 __0, bool __state)
        {
            if (__state) return;   // re-break of an already-broken pane → native no-op → nothing to mirror
            try { TacticalStructDamageSync.HostCaptureDirectBreak(__instance, __0); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] WindowBreakCapturePatch.Postfix failed: " + ex);
            }
        }
    }
}
