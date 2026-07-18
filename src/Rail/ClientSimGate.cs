using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Sim gating (law 4b), ONE narrow seam: the client's geoscape clock is deliberately NOT frozen
    /// (Timing advances, presentation schedulers run), so the native hourly sim tick
    /// <c>GeoLevelController.LevelHourlyUpdateCrt</c> would locally mutate authoritative state every
    /// geo hour — faction income (<c>ResourceIncome.Apply(Wallet)</c>), haven updates, base production
    /// (<c>UpdateBasesHourly</c>), research wallet drain (<c>UpdateResearch</c>),
    /// <c>Manufacture.Update()</c>, recruit generation, aircraft repair, Pandoran engagement, daily
    /// update. On a projector client (law 3) ALL of that is host-only: rail deltas are the only source
    /// of those values, and a diff-based rail cannot correct a client-local mutation until the host
    /// value itself changes. Skipping the ONE chokepoint keeps every hourly mutator silent while the
    /// clock keeps ticking; the coroutine stays scheduled by returning the same next-hour reschedule
    /// the native method would.
    /// (Research.Update keeps its own gate in ResearchSync — it is also reachable outside this tick.)
    /// </summary>
    [HarmonyPatch(typeof(GeoLevelController), "LevelHourlyUpdateCrt")]
    internal static class ClientSimGate
    {
        private static bool Prefix(Timing timing, ref NextUpdate __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            __result = NextUpdate.After(TimeUtils.GetNextHour(timing));
            return false; // client: hourly sim is host-only; state arrives via the rail
        }
    }

    /// <summary>
    /// Sim gating (law 4b), second narrow seam — APPLY-SCOPE ONLY, never per-frame. The universal
    /// open-UI re-enter (<see cref="OpenUiRepaint"/>) runs the equip screens' ExitState inside
    /// SyncApplyScope, and ExitState flushes the screen's view-model into the live model
    /// (<c>UpdateStorage()</c>'s Except-diff over the faction <c>ItemStorage</c> +
    /// <c>UpdateSoldierEquipment</c>'s <c>GeoCharacter.SetItems</c>) with PRE-apply content — that
    /// flush would stomp the deltas just applied, and no correcting delta would follow (the diff rail
    /// only resends on a host-side change). Outside apply scope everything runs native: the client's
    /// per-frame flush is a cheap local no-op, gestures apply optimistically, and the host echo on
    /// 0xAC overwrites (see EquipSync — the per-frame write-gate that encoded/compared the loadout
    /// every frame is gone).
    /// </summary>
    [HarmonyPatch]
    internal static class ClientApplyScopeEquipFlushGate
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UIStateEditSoldier), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateEditVehicle), "UpdateStorage");
            yield return AccessTools.Method(typeof(GeoCharacter), nameof(GeoCharacter.SetItems));
        }

        private static bool Prefix()
        {
            if (!SyncApplyScope.Active) return true; // native everywhere outside a delta apply
            var engine = NetworkEngine.Instance;
            return engine == null || !engine.IsActiveSession || engine.IsHost; // client mid-apply: stale flush = stomp
        }
    }
}
