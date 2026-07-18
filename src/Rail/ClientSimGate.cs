using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

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
    /// Sim gating (law 4b), second narrow seam — UNCONDITIONAL client gate on the equip screens'
    /// native view-model→model flushes. While UIStateEditSoldier / UIStateEditVehicle are open,
    /// UpdateState re-flushes the screen's OWN widget lists into the live model
    /// (<c>UpdateStorage</c> Except-diffs the faction <c>ItemStorage</c> —
    /// UIStateEditSoldier:564 / UIStateEditVehicle:384; <c>UpdateSoldierEquipment</c> /
    /// <c>UpdateVehicleEquipment</c> call <c>GeoCharacter.SetItems</c>) — on a client that stomps
    /// every mirrored 0xAC delta within a frame, and the diff rail never corrects it (it only
    /// resends on a HOST-side change; RCA 2026-07-18 live logs: deltas applied, then overwritten
    /// locally). Gesture carve-out: the ONE model flush after a user gesture passes — it commits
    /// the gesture optimistically and its SetItems postfix sends the loadout intent
    /// (EquipSync.SetItemsGestureSendPatch), which clears the flag; later frames block again.
    /// UpdateStorage stays blocked even then — storage side-effects are host-derived from the
    /// intent and mirror back on 0xAC; the client never writes storage.
    /// GeoCharacter.SetItems itself stays UNgated: its non-screen client callers (augment
    /// optimistic preview, roster-deploy coroutines) are out of this seam's scope.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientEquipFlushGate
    {
        private static float _lastLog = -999f;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UIStateEditSoldier), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateEditVehicle), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateEditSoldier), "UpdateSoldierEquipment");
            yield return AccessTools.Method(typeof(UIStateEditVehicle), "UpdateVehicleEquipment");
        }

        // ponytail: cost ceiling = these bool checks + one float compare per call — NEVER encode or
        // diff the loadout in here (the deleted model-level gate did, per frame, and froze the game).
        private static bool Prefix(MethodBase __originalMethod)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // host/solo: native
            if (EquipSync.GesturePending && __originalMethod.Name != "UpdateStorage") return true; // gesture flush → intent send
            if (Time.unscaledTime - _lastLog > 5f) // rate-limited: per-frame path must not spam
            {
                _lastLog = Time.unscaledTime;
                Debug.Log("[MP][equip] CLIENT stale-flush gated " +
                          __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
            }
            return false; // client: stale UI flush would stomp mirrored state (law 3)
        }
    }
}
