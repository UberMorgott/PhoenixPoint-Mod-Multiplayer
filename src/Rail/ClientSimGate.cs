using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;
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
    /// N1 — sim gating (law 4b), SECOND narrow seam: the one law governing the equip screens' native
    /// view-model → model flushes.
    ///
    /// While UIStateEditSoldier / UIStateEditVehicle are open, the screen periodically re-flushes its OWN
    /// widget lists into the live model: <c>UpdateStorage</c> Except-diffs the faction <c>ItemStorage</c>
    /// (UIStateEditSoldier.cs:564 / UIStateEditVehicle.cs:384) and <c>UpdateSoldierEquipment</c> /
    /// <c>UpdateVehicleEquipment</c> call <c>GeoCharacter.SetItems</c> (:546 / :410).
    /// <c>UIModuleCharacterProgression.CommitStatChanges</c> (:367) is the THIRD such commit — reached
    /// from UIStateEditSoldier.cs:232, :363 and :715 — and no gate in this repo's history ever covered it.
    ///
    /// THE LAW, one boolean: a flush is legitimate EXCEPT while a mirror apply is on the stack.
    ///   • Outside an apply the screen is native and authoritative on BOTH peers, and the local user's own
    ///     gestures must commit normally. Nothing is gated, so nothing has to be enumerated.
    ///   • Inside one, the widgets hold PRE-apply content by construction, so the flush writes stale UI
    ///     back over state that just arrived: on a client it stomps the mirror within a frame, on the host
    ///     it reverts a just-applied remote intent (RCA 2026-07-18: an intent removed
    ///     PX_Assault_Legs_ItemDef at frame 12189, the open equip screen added it back at frame 12190 —
    ///     inside the 0.5 s diff tick, so the walk saw changed=0 and the removal reached NO peer).
    ///
    /// Why peer, caller and screen all collapse into that single condition: both callers of
    /// <c>RepaintOpenGeoscapeScreen</c> already run inside <c>SyncApplyScope.Enter()</c>
    /// (OpenUiRepaint.cs), so mechanically the ONLY flush that can be stale is one running underneath an
    /// apply. That is why this replaces 8fdfd86's per-frame model gate, 61d0987's client-only gate and
    /// 402e950's hand-listed gesture allow-list at once: each of those was an enumeration that had to be
    /// exhaustive to be correct, and none of them was.
    /// </summary>
    [HarmonyPatch]
    internal static class EquipFlushGate
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UIStateEditSoldier), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateEditVehicle), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateEditSoldier), "UpdateSoldierEquipment");
            yield return AccessTools.Method(typeof(UIStateEditVehicle), "UpdateVehicleEquipment");
            yield return AccessTools.Method(typeof(UIModuleCharacterProgression), "CommitStatChanges");
        }

        private static bool Prefix()
        {
            // MUST be the first check, before the engine is consulted: PhoenixGame.FinishLevel is async,
            // so by the time the level tears down NetworkEngine.Instance is already null and
            // IsActiveSession already false — this gate would swing OPEN for the first time all session
            // exactly when SessionEnd is quiescing the open equip screen. That is how the client came to
            // commit a stale UI→model flush inside CleanupView and NRE the level-switch coroutine
            // (carried over from 4f0b5b5, whose own copy of this check targeted a gate that no longer exists).
            if (SessionEnd.InProgress) return false;
            if (!SyncApplyScope.Active) return true;          // not mirroring: the screen owns the model
            var engine = NetworkEngine.Instance;
            return engine == null || !engine.IsActiveSession; // solo: native, even under a stray scope
        }
    }
}
