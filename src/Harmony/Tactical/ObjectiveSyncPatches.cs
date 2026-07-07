using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// LIVE mission-objective mirror chokepoints (surface <c>tac.objective</c> 0x99). Five narrow patches
    /// (three objective-state ones below, plus the two scripted ZONE-UNLOCK chokepoints — audit D20 —
    /// <see cref="UnlockZonesEffectApplySyncPatch"/> / <see cref="ObjectivesUnlockZonesSyncPatch"/>):
    ///   • <see cref="ObjectivesEvaluateSyncPatch"/> — <c>ObjectivesManager.Evaluate()</c>: HOST postfix
    ///     (diff + broadcast; every <c>FactionObjective.State</c> write funnels through this method — the
    ///     private setter is only reachable from <c>FactionObjective.Evaluate</c>) + CLIENT-mirror PREFIX
    ///     suppress (the frozen client must NOT locally re-evaluate objectives: it would overwrite the host
    ///     stamps with locally-diverged results AND run completion logic — NextOnSuccess/NextOnFail adds).
    ///   • <see cref="ObjectivesAddSyncPatch"/> — <c>ObjectivesManager.Add(FactionObjective)</c>: HOST postfix,
    ///     the mid-mission SCRIPTED-add chokepoint (adds outside Evaluate). Not suppressed on the client:
    ///     mission-SETUP adds run pre-mirror-arm and build the client's baseline list; post-arm the suppressed
    ///     Evaluate never reaches AddRange, and the 0x99 mirror-append bypasses Add() entirely.
    ///   • <see cref="GameOverConditionEvaluateSuppressPatch"/> — <c>GameOverCondition.EvaluateObjectives()</c>:
    ///     CLIENT-mirror prefix suppress (constant-false). With live host stamps the client's own 1s poll would
    ///     otherwise read "all victory objectives Achieved" and fire a LOCAL GameOver — mission END stays owned
    ///     by TS4 (0x95), completion logic stays host-owned (sync canon: client is display-only).
    /// All host gating (host + active session + deploy-captured + player faction + change-detected) lives in
    /// <see cref="TacticalObjectiveSync"/>, so a single-player / pre-deploy call is a clean no-op. TFTV
    /// compatibility: resolve-by-name, <c>Prepare()</c> skips cleanly when a type/member is missing; TFTV
    /// patches vanilla EvaluateObjective bodies, which only ever run on the HOST here. Auto-registers via
    /// <c>MultiplayerMain.PatchAll(GetExecutingAssembly())</c>; reflection-target lazily like the sibling patches.
    /// </summary>
    [HarmonyPatch]
    public static class ObjectivesEvaluateSyncPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.FactionObjectives.ObjectivesManager");
            if (t == null) return false;
            // public void Evaluate()  (ObjectivesManager.cs:74)
            _target = AccessTools.Method(t, "Evaluate");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // CLIENT mirror: skip local objective evaluation (pure mirror — host stamps own the states).
        public static bool Prefix() => !TacticalDeploySync.IsClientMirroring;

        // HOST: diff + broadcast (gated + change-detected inside; runs even when the prefix skipped → no-op there).
        public static void Postfix(object __instance)
        {
            try { TacticalObjectiveSync.HostOnObjectivesChanged(__instance); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ObjectivesEvaluateSyncPatch.Postfix failed: " + ex);
            }
        }
    }

    /// <summary>Mid-mission SCRIPTED-add chokepoint — see the file summary.</summary>
    [HarmonyPatch]
    public static class ObjectivesAddSyncPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.FactionObjectives.ObjectivesManager");
            var obj = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.FactionObjectives.FactionObjective");
            if (t == null || obj == null) return false;
            // public void Add(FactionObjective objective)  (ObjectivesManager.cs:36) — EXACT param match.
            _target = AccessTools.Method(t, "Add", new[] { obj });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try { TacticalObjectiveSync.HostOnObjectivesChanged(__instance); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ObjectivesAddSyncPatch.Postfix failed: " + ex);
            }
        }
    }

    /// <summary>
    /// Scripted ZONE-UNLOCK chokepoint #1 (audit D20): HOST postfix on <c>UnlockZonesEffect.OnApply</c> —
    /// the scripted-effect path that unlocks deploy/exit-zone <c>TacticalObjectiveLock</c>s mid-battle. The
    /// unlocked <c>LockTagDefs</c> mirror as ZONE_UNLOCK records on <c>tac.objective</c> 0x99 (same
    /// scripted-state family, no separate rail); the client re-runs the same unlock walk display-side.
    /// All gating (host + active + deploy-captured + non-empty) lives in
    /// <see cref="TacticalObjectiveSync.HostOnZonesUnlocked"/>. Never suppresses the native effect.
    /// </summary>
    [HarmonyPatch]
    public static class UnlockZonesEffectApplySyncPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Effects.UnlockZonesEffect");
            if (t == null) return false;
            // protected override void OnApply(EffectTarget target)  (UnlockZonesEffect.cs:13)
            _target = AccessTools.Method(t, "OnApply");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try
            {
                // UnlockZonesEffectDef.LockTagDefs (public field on the effect's def).
                object def = AccessTools.Property(__instance.GetType(), "UnlockZonesEffectDef")?.GetValue(__instance, null);
                var tags = def != null ? AccessTools.Field(def.GetType(), "LockTagDefs")?.GetValue(def) : null;
                TacticalObjectiveSync.HostOnZonesUnlocked(tags as System.Collections.IEnumerable);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] UnlockZonesEffectApplySyncPatch.Postfix failed: " + ex);
            }
        }
    }

    /// <summary>
    /// Scripted ZONE-UNLOCK chokepoint #2 (audit D20): HOST postfix on the private
    /// <c>ObjectivesManager.UnlockZones(TacticalLevelLockTagDef[])</c> — the objective-COMPLETION unlock path
    /// (ObjectivesManager.cs:116). On the client this path is dead (Evaluate is suppressed), so the host must
    /// mirror it. Same broadcast funnel as chokepoint #1; an empty tag array no-ops inside.
    /// </summary>
    [HarmonyPatch]
    public static class ObjectivesUnlockZonesSyncPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.FactionObjectives.ObjectivesManager");
            if (t == null) return false;
            // private void UnlockZones(TacticalLevelLockTagDef[] unlockTags)
            _target = AccessTools.Method(t, "UnlockZones");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = the TacticalLevelLockTagDef[] unlockTags argument.
        public static void Postfix(object __0)
        {
            try { TacticalObjectiveSync.HostOnZonesUnlocked(__0 as System.Collections.IEnumerable); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ObjectivesUnlockZonesSyncPatch.Postfix failed: " + ex);
            }
        }
    }

    /// <summary>CLIENT-mirror suppress of the native game-over evaluator — see the file summary.</summary>
    [HarmonyPatch]
    public static class GameOverConditionEvaluateSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.GameOverConditions.GameOverCondition");
            if (t == null) return false;
            // public bool EvaluateObjectives()  (GameOverCondition.cs:80)
            _target = AccessTools.Method(t, "EvaluateObjectives");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref bool __result)
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / non-mirror → native check runs
            __result = false;   // client mirror: no local victory/defeat resolution — TS4 (0x95) owns the end
            return false;
        }
    }
}
