using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;
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
    /// N1 — sim gating (law 4b), SECOND narrow seam: the equip screens' STORAGE write-back, the ONE half
    /// of those screens with no model funnel underneath it.
    ///
    /// <c>UIStateEditSoldier.UpdateStorage</c> (:564-578) / <c>UIStateEditVehicle.UpdateStorage</c>
    /// (:384-398) rebuild an <c>ItemStorage</c> out of the widget's storage list, Except-diff it against
    /// the faction's real one and then <c>RemoveItems</c>/<c>AddItems</c> the difference — a direct
    /// authoritative write. (The loadout half of the same screens bottoms out in
    /// <c>GeoCharacter.SetItems</c> and is captured THERE, block-first, by EquipSync.SetItemsCapturePatch;
    /// listing its callers here as well is the enumeration this seam has been shedding since 402e950.)
    ///
    /// Third row, same shape, same law: <c>UIStateVehicleRoster.UpdateAircraftStorage</c> (:278-290) does
    /// the identical Except-diff against the faction's <c>AircraftItemStorage</c>. It is the storage half
    /// of the aircraft loadout gesture whose model half (<c>GeoVehicle.ReplaceEquipments</c>) is captured
    /// block-first by VehicleSync — the host's own replay is the real write there too, and both halves have
    /// to be blocked or the client keeps a divergent storage the rail can never correct.
    ///
    /// Two arms, one line:
    ///   • CLIENT: never, apply or no apply. Storage is a pure mirror (law 3) — the client's own
    ///     storage↔soldier drag goes to the host as an OpSetItems, the host's UpdateStorage-equivalent
    ///     (PopItem/AddItem in HandleIntent) is the real write, and the delta brings it back. Letting this
    ///     run is precisely how a client's storage edits stayed local and reached NO peer.
    ///   • HOST inside an apply: the widgets hold PRE-apply content by construction, so the flush reverts
    ///     the delta that just landed (RCA 2026-07-18: an intent removed PX_Assault_Legs_ItemDef at frame
    ///     12189, the open screen put it back at 12190 — inside the 0.5 s tick, so the walk saw changed=0
    ///     and the removal reached NO peer). Outside an apply the host screen owns the model, natively.
    /// </summary>
    [HarmonyPatch]
    internal static class EquipStorageGate
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UIStateEditSoldier), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateEditVehicle), "UpdateStorage");
            yield return AccessTools.Method(typeof(UIStateVehicleRoster), "UpdateAircraftStorage");
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
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true; // solo: the screen owns the model
            return engine.IsHost && !SyncApplyScope.Active;
        }
    }

    /// <summary>
    /// Same law, the THIRD commit on those screens: <c>UIModuleCharacterProgression.CommitStatChanges</c>
    /// (:367), reached from UIStateEditSoldier.cs:232, :363 and :715. Only the apply arm applies here — the
    /// client's own stat clicks are captured by PersonnelSync, not blocked wholesale.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleCharacterProgression), "CommitStatChanges")]
    internal static class StatCommitApplyGate
    {
        private static bool Prefix()
        {
            if (SessionEnd.InProgress) return false;         // see EquipStorageGate for why this is first
            if (!SyncApplyScope.Active) return true;          // not mirroring: the screen owns the model
            var engine = NetworkEngine.Instance;
            return engine == null || !engine.IsActiveSession; // solo: native, even under a stray scope
        }
    }

    /// <summary>
    /// N1's apply arm at the MODEL chokepoint, covering every caller at once. The equip screens are not
    /// the only route: the augment screens revert THROUGH the same model write on exit —
    /// <c>UIStateBionics.ExitState</c>:93 → <c>UIModuleBionics.Deinit</c>:119 →
    /// <c>RevertUnconfirmedChanges</c>:127-129 → <c>GeoCharacter.SetItems(CharacterOriginalItems)</c> —
    /// so the universal repaint's fallback re-enter (OpenUiRepaint, inside SyncApplyScope) overwrote a
    /// just-applied augment delta with the PRE-augment snapshot in the same frame (RCA 2026-07-24).
    /// Same law, zero enumeration: inside an apply, ANY view→model item flush is stale by construction
    /// (verified: no rail code calls SetItems under the scope — GenericApplier writes the lists via
    /// ApplyList, host intent replays run outside it). Outside an apply, gestures/staging stay native on
    /// the host and are captured block-first on the client (EquipSync.SetItemsCapturePatch), whose prefix
    /// hands this one the decision by returning true for the whole in-apply case.
    /// </summary>
    [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.SetItems))]
    internal static class SetItemsApplyGate
    {
        private static bool Prefix()
        {
            if (!SyncApplyScope.Active) return true;          // native: gestures, host replays, solo
            var engine = NetworkEngine.Instance;
            return engine == null || !engine.IsActiveSession; // solo: native, even under a stray scope
        }
    }

    /// <summary>
    /// FOURTH seam, same law, at the POWER funnel: <c>GeoPhoenixFacility.SetPowered</c>
    /// (GeoPhoenixFacility.cs:317) is the ONE writer of <c>_isPowered</c> — every power mutator on a
    /// client routes through it: the UI toggle (UIModuleBaseLayout.TogglePower:856, captured as an
    /// intent by FacilitySync), the auto-router <c>GeoPhoenixBase.RoutePower</c> (:594/:616, reached
    /// from :555 post-load and :706 on a power-output change) and the auto-(un)plug on facility state
    /// change (:800-822). One gate in the funnel instead of a guard per caller: the client never writes
    /// authoritative state (law 3), and <c>_isPowered</c> is a rail-covered leaf that arrives as a delta.
    /// Blocking the client's own RoutePower is CORRECT, not collateral: the save it loaded came from the
    /// host with the routing already decided, and any later re-route is the host's to make and ship.
    /// The apply arm stays OPEN (a rail path that ever reaches the setter must land), and the setter is
    /// void — nothing dereferences a blocked return.
    /// </summary>
    [HarmonyPatch(typeof(GeoPhoenixFacility), nameof(GeoPhoenixFacility.SetPowered))]
    internal static class FacilityPowerGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        private static bool Prefix(GeoPhoenixFacility __instance, bool powered)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            bool allow = SyncApplyScope.Active;

            // Diagnostic (power retest 2026-07-29): WHO tried to write power on a client, and whether it
            // got through. Client branch only, log-once per distinct message — a RoutePower loop over N
            // facilities collapses to one line per (caller, facility, value) and cannot spam a frame.
            string caller = "?";
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var names = new List<string>();
                for (int i = 0; i < st.FrameCount && names.Count < 3; i++)
                {
                    var n = st.GetFrame(i)?.GetMethod()?.Name;
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
                caller = string.Join("<", names.ToArray());
            }
            catch { }
            string msg = (allow ? "ALLOW(apply) " : "BLOCK ") + (__instance?.Def != null ? __instance.Def.name : "?") +
                         " fid=" + (__instance != null ? __instance.FacilityId : 0) + " powered=" + powered + " from=" + caller;
            if (_logged.Add(msg)) Debug.Log("[MP][diag] SetPowered " + msg);
            return allow;
        }
    }

    /// <summary>
    /// FIFTH seam, same law, at the GEOSCAPE MOVEMENT funnel: <c>GeoNavComponent.Navigate</c>
    /// (GeoNavComponent.cs:152). It is the ONE entry point for geoscape navigation — every geoscape
    /// caller in the game passes a <c>List&lt;Vector3&gt;</c> and lands here (GeoVehicle.cs:388 the
    /// mid-flight-join resume, :529/:541 StartTravel, GeoBehemothActor.cs:325/:351/:365/:706,
    /// GeoscapeRaid.cs:125, plus the internal resume GeoNavComponent.cs:185); the inherited
    /// <c>Navigate(Vector3)</c> overload (NavigationComponent.cs:162) is tactical-only. One prefix
    /// therefore covers the class, with no reach into tactical nav.
    ///
    /// <c>NavigateRoutine</c> (GeoNavComponent.cs:86-143) is an AUTHORITATIVE writer, not presentation:
    /// it sets <c>Travelling</c> (:88/:94/:138) and <c>RangeRemaining</c> (:116/:135) — both rail-covered
    /// leaves — and fires <c>Arrived</c> (:139) → <c>GeoVehicle.OnArrived</c>:327-350 →
    /// <c>CurrentSite.VehicleArrived</c> + <c>OnArrivedAtDestination</c>, i.e. gameplay outcomes on a
    /// projector client (law 3). It is reachable on a client today: a client that joins mid-flight loads
    /// a save with <c>Travelling</c> set, and <c>GeoVehicle.OnLevelStart</c>:385-388 re-issues Navigate —
    /// after which the client flies its own timeline and the diff rail can never correct it (the diff is
    /// host-now vs host-before, so a client-local mutation is permanent). Blocked, the client's aircraft
    /// is a pure projection of the host's <c>Surface.position</c>/<c>.rotation</c> deltas instead.
    ///
    /// Phrased on the RAIL's own knowledge, not on a type list: only an actor the rail actually mirrors
    /// is gated (<c>IdentityResolver.RootRef</c> != null). Every faction's GeoVehicle is a root
    /// (IdentityResolver.cs:218-221 walks <c>map.Vehicles</c> wholesale), so no aircraft is ever frozen
    /// WITHOUT also being mirrored; <c>GeoBehemothActor</c> is not a root (RootRef's default arm,
    /// IdentityResolver.cs:129) so behemoths keep dead-reckoning exactly as before — and the day one
    /// becomes a root, this gate extends itself. Known ceiling, presentation only: the flying animator
    /// pose is set by nav (<c>InitiateTravelling</c> → GeoVehicle.cs:583, cleared at :344), so a mirrored
    /// client shows the landed pose while the icon steps at the walk cadence. The gameplay half of
    /// <c>Travelling</c> (VehicleLeft + CurrentSite=null, GeoVehicle.cs:209-217) still lands, via the leaf.
    /// </summary>
    [HarmonyPatch]
    internal static class GeoNavigateGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        // Exact parameter match — AccessTools.Method does no widening, and the base type declares a
        // same-named Vector3 overload that must NOT be caught here.
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GeoNavComponent), nameof(GeoNavComponent.Navigate), new[] { typeof(List<Vector3>) });

        private static bool Prefix(GeoNavComponent __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            if (SyncApplyScope.Active) return true;                                      // an apply may rewire nav
            var root = IdentityResolver.RootRef(__instance?.NavActor?.Actor);
            if (root == null) return true; // not rail-mirrored — leave its local dead-reckoning alone

            // Never silent (the dominant bug class): say which actor's navigation was refused and why.
            // Log-once per root — Navigate is per route leg, not per frame, but the join resume retries.
            if (_logged.Add(root))
                Debug.Log("[MP][diag] GeoNavigateGate BLOCK " + root + " — client nav is host-mirrored (Surface.position/.rotation deltas)");
            return false;
        }
    }

    /// <summary>
    /// SIXTH seam, same law, at the GEOSCAPE EVENT funnel: <c>GeoscapeEventSystem.OnGeoscapeEvent</c>
    /// (GeoscapeEventSystem.cs:606) is the ONE place a geoscape event is ever raised. Both routes land
    /// here — the eventus handler registration (:542 / unregister :547) and the direct
    /// <c>TriggerGeoscapeEvent</c> path (:319-329, calling it at :328) — and <c>OnEventTriggered</c>
    /// (:638), which mints the record and fires <c>GeoscapeEventRaised</c>, is reachable only FROM it
    /// (:622). One prefix therefore covers the class.
    ///
    /// The client's geoscape clock is deliberately not frozen, so its own timers
    /// (<c>Update</c>:550 → <c>CompleteTimer</c>:568) and arrival/visit handlers (:412, :421) run and
    /// can roll a DIFFERENT random encounter than the host did. That mints an authoritative record on
    /// a projector client (law 3) which the diff rail can never correct — the diff is host-now vs
    /// host-before, so a record only the client has is never mentioned. Blocked, every record on a
    /// client arrives as a delta, which is exactly what <see cref="EventPopup.Backlog"/> derives its
    /// window queue from.
    ///
    /// <c>SuppressEvents</c> cannot serve here: it is a rail-MIRRORED leaf
    /// (docs/rail-baseline.txt:254) carrying the host's <c>false</c>, so a local write is overwritten
    /// by the next delta. The apply arm stays OPEN (a rail path that reaches the funnel must land) and
    /// the method is void — nothing dereferences a blocked return.
    /// Parameter types are named EXACTLY: <c>AccessTools</c>/<c>HarmonyPatch</c> do no widening, and a
    /// base-typed guess resolves to null, which PatchAll turns into one swallowed warning (RailCheck L23).
    /// </summary>
    [HarmonyPatch(typeof(PhoenixPoint.Geoscape.Events.GeoscapeEventSystem), "OnGeoscapeEvent",
                  new[] { typeof(Base.Eventus.BaseEventData), typeof(Base.Eventus.BaseEventContext) })]
    internal static class GeoscapeEventRaiseGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        private static bool Prefix(Base.Eventus.BaseEventData eventData)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            if (SyncApplyScope.Active) return true;                                      // an apply may legitimately reach it
            string id = (eventData as PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventData)?.EventID ?? "?";
            if (_logged.Add(id))
                Debug.Log("[MP][events] client-local raise of '" + id + "' BLOCKED — the host's record arrives via the rail");
            return false;
        }
    }
}
