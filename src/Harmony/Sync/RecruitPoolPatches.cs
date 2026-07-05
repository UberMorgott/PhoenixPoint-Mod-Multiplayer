using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// PS3 recruit-pool dirty seams (personnel-sync spec §3, taxonomy §6/§8): HOST-only Postfix hooks on
    /// the pool mutators, marking channel #10.
    /// • HAVEN slot — <c>GeoHaven.SpawnNewRecruit/RemoveRecruit/KillRecruit</c> (GeoHaven.cs:788/804/799).
    ///   Hooked at the HAVEN (not the spec table's <c>GeoFaction.GenerateRecruits</c> driver) because the
    ///   channel diffs per SiteId: the hourly driver funnels every spawn through SpawnNewRecruit
    ///   (GeoFaction.cs:1573-1576) and hire/zone-loss funnel through RemoveRecruit, so three seams give
    ///   exact per-haven marks with full coverage — INCLUDING TFTV-driven refreshes, which drive these
    ///   same vanilla methods (taxonomy §6: TFTV never replaces the store).
    /// • NAKED pool — <c>GeoPhoenixFaction.RegenerateNakedRecruits</c> (:627) + <c>HireNakedRecruit</c>
    ///   (:662, the dict-remove side ONLY: the hired soldier's roster landing rides the #9/#6 membership
    ///   seams — one writer per field, no double-handling).
    /// • CAPTURED pool — <c>CaptureUnit</c> (:764) / <c>KillCapturedUnit</c> (:771 — Harvest funnels
    ///   through it :893) + the capacity trim <c>TrimCapturedUnitsToCapacity</c> (:1590), which removes
    ///   units DIRECTLY from <c>_capturedUnits</c> bypassing KillCapturedUnit (decompile 2026-07-06);
    ///   marked only when it actually removed something (it runs inside every UpdateStats).
    ///
    /// All targets are single-overload (decompile-checked 2026-07-06) → name-only resolve stays robust
    /// across game versions (the PersonnelStatePatches discipline). IsHost gates every mark; a HOST
    /// mutation inside a relayed-action apply MUST dirty (authoritative write → mirror back), so no
    /// <c>SyncApplyScope</c> skip. Reflective targets (Prepare false → PatchAll skips silently — bind
    /// evidence logged); best-effort try/catch — never breaks the native mutator.
    /// </summary>
    internal static class RecruitPoolDirty
    {
        internal static MethodBase Resolve(string typeName, string methodName)
        {
            var t = AccessTools.TypeByName(typeName);
            var m = t != null ? AccessTools.Method(t, methodName) : null;
            Debug.Log("[Multiplayer] RecruitPoolPatches: " + typeName + "." + methodName + " "
                      + (m != null ? "bound" : "NOT FOUND — pool dirty seam disabled"));
            return m;
        }

        internal static bool HostActive()
        {
            var engine = NetworkEngine.Instance;
            return engine != null && engine.IsActiveSession && engine.IsHost;
        }

        internal static void MarkHaven(object haven)
        {
            try
            {
                if (!HostActive()) return;
                // SiteId < 0 (unreadable back-reference) is dropped inside the mark — can't be keyed.
                RecruitPoolChannel.MarkHavenDirtyExternal(RecruitPoolReflection.GetHavenSiteId(haven));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolPatches.MarkHaven failed: " + ex.Message); }
        }

        internal static void MarkNaked()
        {
            try { if (HostActive()) RecruitPoolChannel.MarkNakedDirtyExternal(); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolPatches.MarkNaked failed: " + ex.Message); }
        }

        internal static void MarkCaptured()
        {
            try { if (HostActive()) RecruitPoolChannel.MarkCapturedDirtyExternal(); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolPatches.MarkCaptured failed: " + ex.Message); }
        }
    }

    [HarmonyPatch]
    public static class GeoHavenSpawnRecruitPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Entities.GeoHaven", "SpawnNewRecruit");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => RecruitPoolDirty.MarkHaven(__instance);
    }

    [HarmonyPatch]
    public static class GeoHavenRemoveRecruitPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Entities.GeoHaven", "RemoveRecruit");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => RecruitPoolDirty.MarkHaven(__instance);
    }

    [HarmonyPatch]
    public static class GeoHavenKillRecruitPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Entities.GeoHaven", "KillRecruit");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => RecruitPoolDirty.MarkHaven(__instance);
    }

    [HarmonyPatch]
    public static class PhoenixRegenerateNakedRecruitsPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction", "RegenerateNakedRecruits");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => RecruitPoolDirty.MarkNaked();
    }

    [HarmonyPatch]
    public static class PhoenixHireNakedRecruitPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction", "HireNakedRecruit");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => RecruitPoolDirty.MarkNaked();
    }

    [HarmonyPatch]
    public static class PhoenixCaptureUnitPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction", "CaptureUnit");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => RecruitPoolDirty.MarkCaptured();
    }

    [HarmonyPatch]
    public static class PhoenixKillCapturedUnitPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction", "KillCapturedUnit");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => RecruitPoolDirty.MarkCaptured();
    }

    [HarmonyPatch]
    public static class PhoenixTrimCapturedPoolDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = RecruitPoolDirty.Resolve("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction", "TrimCapturedUnitsToCapacity");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __result)
        {
            // Runs inside every UpdateStats — mark only when the trim actually removed units (the
            // returned list is built eagerly; empty = no capacity loss, the common case).
            try
            {
                if (!(__result is IEnumerable removed)) return;
                foreach (var _ in removed) { RecruitPoolDirty.MarkCaptured(); return; }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PhoenixTrimCapturedPoolDirtyPatch failed: " + ex.Message); }
        }
    }
}
