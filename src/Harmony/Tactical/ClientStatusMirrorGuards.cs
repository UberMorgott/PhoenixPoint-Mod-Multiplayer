using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// FEATURE B — CLIENT-ONLY guards that keep a host-mirrored status INERT on the co-op mirror (no gameplay
    /// effect re-runs on the client; host is authoritative). The state spine (<see cref="TacticalActorStateSync"/>)
    /// mirrors every host status whose healthbar visibility is not Hidden so its ICON draws (icon draw is free
    /// once the status object sits in StatusComponent.Statuses).
    ///
    /// INERTNESS — TWO complementary mechanisms (grounded in the decompile):
    ///   1. APPLY-TIME (the spine's job, NOT a patch): the spine pre-sets <c>Status.Applied = true</c> BEFORE
    ///      driving ApplyStatus, so every subclass OnApply takes the engine's OWN deserialize/"load a saved
    ///      status" path (the pervasive `bool applied = base.Applied; … if (!applied) {side-effects}` /
    ///      `if (CurrentlyDeserializing) return;` idiom) and SKIPS its gameplay side effects — stat-mod add,
    ///      faction flip (MindControl), AP drain (Stun), DoT arm (TacEffectStatus) — while still landing the
    ///      status object in the list (icon) + raising the healthbar events. This is robust by construction:
    ///      it reuses the engine's vetted inert-reattach path rather than guessing which methods to skip.
    ///   2. PER-TURN + REMOVAL (these patches): even with apply-time inertness, a few methods would still mutate
    ///      state if the engine reached them on the client mirror:
    ///        • TacEffectStatus.StartTurn/EndTurn → ApplyEffect → DoT DAMAGE every turn (NO deserialize guard).
    ///        • OnUnapply on stat-style statuses (e.g. TacStatsModifyStatus.OnUnapply) UNCONDITIONALLY reverts
    ///          its modifier (RemoveStatModification) — but the mirror never APPLIED it → would corrupt a stat.
    ///      So these prefixes SKIP StartTurn/EndTurn/ApplyEffect/OnUnapply for a spine-created mirror. (OnApply/
    ///      AfterApply are deliberately NOT patched — skipping them would drop the Applied=true plumbing the icon
    ///      depends on; apply-time inertness already covers them.)
    ///
    /// SCOPE (hard): every guard early-returns (lets the original run) UNLESS this instance is a co-op CLIENT
    /// inside a mirrored tactical mission (<see cref="TacticalDeploySync.IsClientMirroring"/>) AND the status is
    /// mirror-managed. The host and single-player are NEVER affected. Fail-OPEN on any unexpected error.
    /// Auto-registers via PatchAll.
    /// </summary>
    public static class ClientStatusMirrorGuards
    {
        // The set of status instances the spine created as INERT mirrors (reference identity). Entries are
        // removed on mirror-unapply; the whole set is cleared on mission exit via Reset().
        private static readonly HashSet<object> _mirrored =
            new HashSet<object>(ReferenceEqualityComparer.Instance);

        // True while the spine is INSIDE its UnapplyStatus call for a mirror (so OnUnapply is skipped during the
        // spine-driven removal even before we unregister). Thread-static defense-in-depth, like
        // TacticalActorStateSync._applyingRemote.
        [ThreadStatic] private static bool _unapplyInProgress;

        // ─── spine API (called by TacticalActorStateSync only) ───────────────────────────────────────────

        public static bool UnapplyInProgress { get => _unapplyInProgress; set => _unapplyInProgress = value; }

        /// <summary>Register a status instance as an inert mirror (after the spine's ApplyStatus created it).</summary>
        public static void RegisterMirror(object status) { if (status != null) _mirrored.Add(status); }

        /// <summary>Forget an inert-mirror instance (after the spine's UnapplyStatus removed it).</summary>
        public static void UnregisterMirror(object status) { if (status != null) _mirrored.Remove(status); }

        /// <summary>True if this status instance is a spine-created inert mirror.</summary>
        public static bool IsMirror(object status) => status != null && _mirrored.Contains(status);

        /// <summary>Clear all mirror tracking (mission exit / re-deploy). Idempotent.</summary>
        public static void Reset() { _mirrored.Clear(); _unapplyInProgress = false; }

        // ─── PURE skip decision ──────────────────────────────────────────────────────────────────────────

        /// <summary>PURE: should a guarded effect method be SKIPPED for this status? Skip iff we are a co-op
        /// client mirror AND the status is mirror-managed (a tracked inert mirror, or we are mid spine-unapply).
        /// Off-client / host / single-player / a non-mirror status → never skip (let native run). Split into
        /// booleans so the decision is testable without engine types.</summary>
        public static bool ShouldSkip(bool isClientMirroring, bool isMirrorContext)
            => isClientMirroring && isMirrorContext;

        // The live wiring of the pure decision.
        private static bool SkipFor(object status)
        {
            try
            {
                bool mirrorContext = _unapplyInProgress || IsMirror(status);
                return ShouldSkip(TacticalDeploySync.IsClientMirroring, mirrorContext);
            }
            catch { return false; }   // fail-open: never wedge a native status method
        }

        // ─── Harmony patches (StartTurn / EndTurn / ApplyEffect / OnUnapply) ──────────────────────────────

        private static MethodBase DeclaredMethod(Type t, string name, Type[] args)
        {
            try
            {
                return args == null ? AccessTools.DeclaredMethod(t, name)
                                    : AccessTools.DeclaredMethod(t, name, args);
            }
            catch { return null; }
        }

        /// <summary>All loaded subtypes of <paramref name="baseTypeName"/> (inclusive) that DECLARE the given
        /// method — so a guard binds the real override at every level of the chain (base-only would miss a
        /// non-chaining override). Deduped by MethodBase.</summary>
        private static IEnumerable<MethodBase> DeclaringMethodsInHierarchy(string baseTypeName, string method, Type[] args)
        {
            var seen = new HashSet<MethodBase>();
            var baseType = AccessTools.TypeByName(baseTypeName);
            if (baseType == null) { Debug.LogError("[Multipleer][tac] mirror-guard: base type not found: " + baseTypeName); yield break; }
            foreach (var t in EnumerateSubtypes(baseType))
            {
                var m = DeclaredMethod(t, method, args);
                if (m != null && seen.Add(m)) yield return m;
            }
        }

        private static IEnumerable<Type> EnumerateSubtypes(Type baseType)
        {
            yield return baseType;
            Type[] types;
            try { types = baseType.Assembly.GetTypes(); }      // all TacStatus subclasses live in Assembly-CSharp
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            if (types == null) yield break;
            foreach (var t in types)
            {
                if (t == null || t == baseType) continue;
                if (baseType.IsAssignableFrom(t)) yield return t;
            }
        }

        private const string TacStatusName = "PhoenixPoint.Tactical.Entities.Statuses.TacStatus";
        private const string TacEffectStatusName = "PhoenixPoint.Tactical.Entities.Statuses.TacEffectStatus";

        // OnUnapply() — skip so a mirror's removal never reverts a gameplay effect it never applied
        // (e.g. TacStatsModifyStatus.OnUnapply → RemoveStatModification, which has no deserialize guard).
        // The status is still removed from the list + the icon clears (StatusComponent.RemoveStatus +
        // OnStatusUnapplied/OnStatusesChanged run in the component, OUTSIDE OnUnapply).
        [HarmonyPatch]
        public static class OnUnapplyGuard
        {
            public static IEnumerable<MethodBase> TargetMethods()
                => DeclaringMethodsInHierarchy(TacStatusName, "OnUnapply", new Type[0]);
            public static bool Prefix(object __instance) => !SkipFor(__instance);
        }

        // StartTurn() — virtual on TacStatus, override on TacEffectStatus/subclasses (drives ApplyEffect).
        [HarmonyPatch]
        public static class StartTurnGuard
        {
            public static IEnumerable<MethodBase> TargetMethods()
                => DeclaringMethodsInHierarchy(TacStatusName, "StartTurn", new Type[0]);
            public static bool Prefix(object __instance) => !SkipFor(__instance);
        }

        // EndTurn() — virtual on TacStatus, override on TacEffectStatus/subclasses (drives ApplyEffect at end).
        [HarmonyPatch]
        public static class EndTurnGuard
        {
            public static IEnumerable<MethodBase> TargetMethods()
                => DeclaringMethodsInHierarchy(TacStatusName, "EndTurn", new Type[0]);
            public static bool Prefix(object __instance) => !SkipFor(__instance);
        }

        // ApplyEffect() — the DoT/Bleed/Stun damage application (TacEffectStatus + subclasses). Belt-and-braces:
        // EndTurn() calls it UNCONDITIONALLY (no deserialize guard), so skipping ApplyEffect for a mirror
        // guarantees no DoT damage runs on the client even if a turn tick reaches a mirrored status.
        [HarmonyPatch]
        public static class ApplyEffectGuard
        {
            public static IEnumerable<MethodBase> TargetMethods()
                => DeclaringMethodsInHierarchy(TacEffectStatusName, "ApplyEffect", new Type[0]);
            public static bool Prefix(object __instance) => !SkipFor(__instance);
        }
    }

    /// <summary>Reference-equality comparer so the mirror set tracks status instances by identity.</summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
