using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
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
    /// Sim gating (law 4b), second narrow seam: the equip screens' private <c>UpdateStorage()</c>
    /// (UIStateEditSoldier:564 / UIStateEditVehicle:384) rewrites the LIVE faction/site
    /// <c>ItemStorage</c> from the screen's own view-model list (Except-diff both ways) — on a client
    /// that stomps the mirrored storage with stale UI content on every UpdateState/ExitState,
    /// INCLUDING the OpenUiRepaint Exit→Enter (which runs inside SyncApplyScope — hence blocked
    /// unconditionally, not scope-gated). Storage changes are host-derived from the loadout intent
    /// (<see cref="EquipSync"/>) and mirror back via the GeoItemDict rail; the client never writes
    /// storage. The paired soldier-model write (<c>GeoCharacter.SetItems</c>) is gated in
    /// EquipSync.SetItemsCapturePatch, which also turns it into the intent.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientEquipStorageGate
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UIStateEditSoldier), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateEditVehicle), "UpdateStorage");
        }

        private static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            return engine == null || !engine.IsActiveSession || engine.IsHost; // client: storage is host-derived
        }
    }
}
