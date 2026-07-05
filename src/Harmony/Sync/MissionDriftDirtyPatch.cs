using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// WA-2 MISSION-DRIFT dirty hook (spec 2026-07-05 §5, audit gap 1c LOW-MED). Channel #5 snapshots a
    /// site's <c>GeoMissionRecord</c> only when a GeoMap site event fires — but an ONGOING updateable mission
    /// mutates its runtime bits BETWEEN events (haven-defense Attacker/DefenderDeployment tick down hourly,
    /// GeoHavenDefenseMission.cs:47-108), so the client's mirrored brief drifts stale until mission end.
    ///
    /// ONE chokepoint: the private <c>GeoUpdateableMission.Update(Timing)</c> driver
    /// (GeoUpdateableMission.cs:79-89 — every subclass's <c>OnUpdateMission</c> runs through it, and it is
    /// the exact raiser of <c>OnMissionUpdated</c>). HOST-only Postfix → resolve the owning site
    /// (<c>GeoMission.Site</c>) → throttled (≤1/s per site, <see cref="MissionDriftThrottle"/>) dirty-mark
    /// via <c>GeoSiteChannel.MarkSiteDirtyExternal</c>. ZERO wire change: the ordinary flush re-reads the
    /// LIVE mission values (GeoSiteReflection.ReadMissionRecord reads the deployment properties fresh each
    /// snapshot; the client's ApplyMission refreshes the mutable bits on a same-class/same-def record).
    /// Pure observe — native mission ticking untouched; client sim is frozen so its missions never tick
    /// (and IsHost gates the mark anyway). Best-effort try/catch; reflective target (Prepare false → skipped).
    /// </summary>
    [HarmonyPatch]
    public static class MissionDriftDirtyPatch
    {
        private static MethodBase _target;

        // 1 s per site: a burst of same-hour mission ticks collapses into one snapshot (last-wins wire).
        internal static readonly MissionDriftThrottle Throttle = new MissionDriftThrottle(1.0);

        public static bool Prepare()
        {
            var updT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoUpdateableMission");
            var timingT = AccessTools.TypeByName("Base.Core.Timing");
            // EXACT param match (harmony-accesstools-exact-param-match): Update(Timing) private.
            if (updT != null && timingT != null) _target = AccessTools.Method(updT, "Update", new[] { timingT });
            // Binding evidence in Player.log: Prepare=false skips SILENTLY otherwise — a miss here would be
            // indistinguishable from "no drift" in a soak (fail-open by design, but never invisibly).
            Debug.Log("[Multiplayer] MissionDriftDirtyPatch.Prepare: GeoUpdateableMission.Update(Timing) "
                      + (_target != null ? "bound" : "NOT FOUND — mission-drift re-snapshot disabled"));
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                var site = GeoSiteReflection.GetMissionSite(__instance);
                int siteId = GeoSiteReflection.GetSiteId(site);
                if (siteId < 0) return;
                // Unity 2019.4: realtimeSinceStartupAsDouble does not exist (2020.2+) — float widened
                // to double, ample for a 1 s throttle (the TimeSyncManager precedent).
                if (!Throttle.ShouldMark(siteId, Time.realtimeSinceStartup)) return;
                GeoSiteChannel.MarkSiteDirtyExternal(siteId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MissionDriftDirtyPatch.Postfix failed: " + ex.Message); }
        }
    }
}
