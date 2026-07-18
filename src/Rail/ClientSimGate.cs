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
    /// Sim gating (law 4b), second narrow seam — PEER-AGNOSTIC gate on the equip screens' native
    /// view-model→model flushes. While UIStateEditSoldier / UIStateEditVehicle are open, UpdateState
    /// re-flushes the screen's OWN widget lists into the live model (<c>UpdateStorage</c> Except-diffs
    /// the faction <c>ItemStorage</c> — UIStateEditSoldier:564 / UIStateEditVehicle:384;
    /// <c>UpdateSoldierEquipment</c> / <c>UpdateVehicleEquipment</c> call <c>GeoCharacter.SetItems</c>).
    ///
    /// THE RULE: a view→model flush is legitimate ONLY as the commit of a LOCAL user gesture; a
    /// periodic/idle flush is always illegitimate — on host and client alike. A stale flush replays
    /// whatever the widgets still hold, so on a CLIENT it stomps every mirrored 0xAC delta within a
    /// frame, and on the HOST it reverts a just-applied remote intent (RCA 2026-07-18 host log:
    /// intent removed PX_Assault_Legs_ItemDef at frame 12189, the open equip screen added it back at
    /// frame 12190 — ~16 ms, i.e. inside the 0.5 s diff tick, so the DiffEngine saw changed=0 and the
    /// removal never reached ANY peer). Same hazard for scrap/manufacture intents landing while a host
    /// has the screen open. Neither peer can self-correct: the rail only resends on a HOST-side change.
    ///
    /// LOCAL-GESTURE CARVE-OUT: the one flush GROUP that follows EquipSync.MarkGesture passes
    /// (EquipSync.GesturePending, frame-scoped — see there; the native flushes come in pairs within one
    /// frame and in DIFFERENT orders per call site, so the window is per-frame, never per-method).
    /// HOST: both halves pass — that flush IS the authoritative commit of the host's own drag.
    /// CLIENT: UpdateStorage stays blocked even during a gesture — storage side-effects are host-derived
    /// from the intent and mirror back on 0xAC; the client never writes storage. The client's equipment
    /// half passes so its SetItems postfix can send the loadout intent (EquipSync.SetItemsGestureSendPatch).
    /// GeoCharacter.SetItems itself stays UNgated: its non-screen callers (augment optimistic preview,
    /// roster-deploy coroutines, EquipSync's own host apply) are out of this seam's scope.
    /// </summary>
    [HarmonyPatch]
    internal static class EquipFlushGate
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
            // MUST be the first check, before the engine is consulted: PhoenixGame.FinishLevel is async,
            // so by the time the level tears down NetworkEngine.Instance is already null and
            // IsActiveSession already false — this gate would swing OPEN for the first time all session
            // exactly when SessionEnd is quiescing the open equip screen. That is how the client came to
            // commit a stale UI→model flush inside CleanupView and NRE the level-switch coroutine.
            if (SessionEnd.InProgress) return false;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true; // solo: native
            bool storage = __originalMethod.Name == "UpdateStorage";
            if (EquipSync.GesturePending && (engine.IsHost || !storage))
            {
                EquipSync.NoteFlushAdmitted(); // opens the one-frame group window
                return true;                   // local gesture commit — the only legitimate flush
            }
            if (Time.unscaledTime - _lastLog > 5f) // rate-limited: per-frame path must not spam
            {
                _lastLog = Time.unscaledTime;
                Debug.Log("[MP][equip] stale-flush gated " + (engine.IsHost ? "HOST " : "CLIENT ") +
                          __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
            }
            return false; // stale UI flush would stomp authoritative state (law 3) / revert a remote intent
        }
    }
}
