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
    /// FIFTH seam, same law, at the GEOSCAPE ARRIVAL funnel: <c>GeoVehicle.OnArrived</c>
    /// (GeoVehicle.cs:327-350), the single delegate the nav routine invokes when a leg finishes
    /// (<c>Arrived?.Invoke</c>, GeoNavComponent.cs:139; wired once at GeoVehicle.cs:321).
    ///
    /// This gate USED to sit one level up, on <c>GeoNavComponent.Navigate(List&lt;Vector3&gt;)</c>, which
    /// froze the client's navigation outright and left its aircraft a pure projection of the host's
    /// <c>SurfacePos</c>/<c>SurfaceRot</c>/<c>Rot</c> deltas — i.e. an icon that STEPS at the rail's walk
    /// cadence (the ceiling this doc used to name out loud). That was gating the DERIVATION to stop the
    /// OUTCOME. Native geoscape motion is closed-form and reproducible — <c>NavigateRoutine</c> recomputes
    /// <c>num = totalTime.Ratio01(startTime, Timing.Now)</c> → <c>Slerp(start,end,num)</c> →
    /// <c>PivotTransform.localRotation</c> from scratch EVERY frame (:104-116, rescheduled :126) and
    /// <c>RangeRemaining</c> likewise closed-form (:116), off a def-fixed speed with no RNG and a clock the
    /// client already tracks. So the client can fly the aircraft itself, exactly as the game itself does
    /// when it restores a flight from a save with no departure time recorded
    /// (<c>GeoVehicle.OnLevelStart</c>:385-388 re-issues <c>Navigate</c> from the destination sites, and
    /// <c>CalculatePath</c>:68-84 re-seeds from the actor's CURRENT WorldPosition). The gate therefore drops
    /// to the outcome, and only the outcome.
    ///
    /// What must not run on a projector client (law 3) is ALL of this method's body, so the prefix skips it
    /// whole rather than picking lines: <c>CurrentSite =</c> (:337), <c>_destinationSites.RemoveAt(0)</c>
    /// (:340), <c>CurrentSite.VehicleArrived(this)</c> (:347) and <c>OnArrivedAtDestination</c> (:348 →
    /// <c>CanRedirect = true</c>, <c>ArrivedAtDestinationEvent</c>). Every one of those is a rail-covered
    /// leaf or a host-computed event; all of them arrive as deltas. Skipping whole also disarms
    /// <c>:335 throw new Exception("Sites desynchronized?!?")</c> — the client's own nav can legitimately
    /// finish a leg AFTER the host's <c>DestinationSites</c> delta already trimmed the list, which is
    /// exactly the mismatch that throw fires on, and it would surface inside a coroutine.
    ///
    /// The client's flying ANIMATOR pose is set locally by its own nav (<c>InitiateTravelling</c> →
    /// GeoVehicle.cs:581) and this gate blocks the native reset at :344, so the landing half is re-derived
    /// where every other mirrored-input derivation lives: <c>GenericApplier</c>'s order-leaf re-seed, next
    /// to the pivot seed. Presentation stays complete without letting an outcome through here.
    ///
    /// Phrased on the RAIL's own knowledge, not a type list: only an actor the rail actually mirrors is
    /// gated (<c>IdentityResolver.RootRef</c> != null). Every faction's GeoVehicle is a root
    /// (IdentityResolver.cs:218-221 walks <c>map.Vehicles</c> wholesale), so no aircraft's arrival is ever
    /// refused WITHOUT its order being mirrored. There is deliberately NO <c>SyncApplyScope</c> exemption:
    /// arrival is never a rail apply's job, the re-seed only starts a coroutine that fires this later on the
    /// game loop, and an open apply arm would let the outcome through on exactly the path that re-seeds.
    /// </summary>
    [HarmonyPatch]
    internal static class VehicleArrivalGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        // Exact parameter match — AccessTools.Method does no widening, and a null here makes PatchAll throw
        // a warning MultiplayerMain swallows, killing every later patch in the same pass (RailCheck L23).
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GeoVehicle), "OnArrived", new[] { typeof(Vector3), typeof(bool) });

        private static bool Prefix(GeoVehicle __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            var root = IdentityResolver.RootRef(__instance);
            if (root == null) return true; // not rail-mirrored — its arrival is nobody else's truth

            // Never silent (the dominant bug class): say whose arrival outcome was refused and why.
            // Log-once per root — arrival is per route leg, not per frame.
            if (_logged.Add(root))
                Debug.Log("[MP][diag] VehicleArrivalGate BLOCK " + root + " — arrival outcome is host-only; " +
                          "the client flew the leg itself and takes CurrentSite/DestinationSites/CanRedirect from the rail");
            return false;
        }
    }

    /// <summary>
    /// NINTH seam, same law, at the SITE-EXPLORATION OUTCOME: <c>GeoFaction.OnVehicleSiteExplored</c>
    /// (GeoFaction.cs:1919), the ONE subscriber of <c>GeoVehicle.SiteExplored</c> that mutates anything
    /// (wired :2058, dropped :2088; the only other subscriber is UIStateVehicleSelected.cs:706, pure UI).
    /// No type overrides it, so this single prefix covers the class.
    ///
    /// The exact twin of <see cref="VehicleArrivalGate"/>, and it exists for the same reason: the client now
    /// RUNS the exploration itself. Progress is closed-form off mirrored inputs
    /// (<c>GeoActorProgressionVisualController.Progression</c> recomputes
    /// <c>(Timing.Now - Start)/(End - Start)</c> in <c>Update()</c> every frame, :25-35/:53-56), so
    /// <c>GenericApplier.ReseedExploration</c> hands the game's own <c>ExploreCurrentSite</c>:448 the
    /// mirrored ORDER and the client's fill runs smoothly instead of appearing only when the host's counter
    /// completed. That local timer must not also produce the RESULT: when it expires,
    /// <c>SiteExplorationCompleted</c>:474 invokes <c>SiteExplored</c>, and this method's body
    /// (<c>SetInspected(this, true)</c> + <c>UpdateVehicleSite</c>) is authoritative state the diff can never
    /// correct — the diff is host-now vs host-before. The host's own SetInspected arrives as a delta.
    /// Skipping the body whole rather than picking lines, exactly as the arrival gate does.
    ///
    /// Phrased on the RAIL's own knowledge, not a type list: only an actor the rail actually mirrors is
    /// gated (<c>IdentityResolver.RootRef</c> != null). Deliberately NO <c>SyncApplyScope</c> exemption —
    /// the re-seed only STARTS a timer that fires this later on the game loop, so an open apply arm would
    /// admit the outcome on precisely the path that re-seeds. Void method; nothing dereferences the skip.
    /// </summary>
    [HarmonyPatch(typeof(GeoFaction), "OnVehicleSiteExplored")]
    internal static class SiteExploredOutcomeGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        private static bool Prefix(GeoVehicle vehicle)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            var root = IdentityResolver.RootRef(vehicle);
            if (root == null) return true; // not rail-mirrored — its exploration result is nobody else's truth

            // Never silent (the dominant bug class): say whose exploration outcome was refused and why.
            // Log-once per root — exploration is a per-site event, not a per-frame one.
            if (_logged.Add(root))
                Debug.Log("[MP][diag] SiteExploredOutcomeGate BLOCK " + root + " — the exploration RESULT is host-only; " +
                          "the client derived the progress bar itself and takes the site's inspected state from the rail");
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
    /// client arrives as a delta, and every WINDOW arrives as a 0xB6 raise
    /// (<see cref="EventPopup.HandleInbound"/>) — the client raises nothing of its own.
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

    /// <summary>
    /// EIGHTH seam, same law, over the AIRCRAFT-GESTURE class as a whole. A geoscape ability is a player
    /// gesture that calls a native mutator on the actor, and on a rail-mirrored <c>GeoVehicle</c> that write
    /// is authoritative — the diff is host-now vs host-before, so a client-local one is never corrected.
    /// <see cref="ClientSimGate"/> only ever gated the hourly TICK, which is why every one of these ran
    /// locally on a client for free.
    ///
    /// The set is not open-ended and is not guessed: <c>GeoVehicle</c> methods reachable from a
    /// <c>GeoAbility.ActivateInternal</c> are exactly four (RailCheck L42 sweeps the game assembly for them
    /// and fails if a fifth appears). <c>StartTravel</c> and <c>StartExploringCurrentSite</c> carry INTENTS
    /// (<see cref="VehicleSync"/>) because the player expects the order to happen. The rows here are the ones
    /// that get a GATE instead, each for a stated reason:
    ///   • <c>ScheduleRepair</c> (GeoVehicle.cs:743, sole caller EmergencyRepairAbility.cs:40) — writes
    ///     <c>_maintenancePointsToRepair</c> (a covered leaf) and <c>_scheduledRepair</c>, and its arguments
    ///     are derived from the ABILITY def, not from the vehicle, so an intent would have to ship a def
    ///     address the host must then trust. Blocked here; the host's own repair mirrors as ordinary state.
    ///   • <c>StartCollectingFromCurrentSite</c> / <c>EndCollectingFromCurrentSite</c> (:432/:440) — both
    ///     write <c>GeoHarvestingSite.AddHarvestingForce</c> (GeoHarvestingSite.cs:83), whose
    ///     <c>HarvestingForce</c> is a covered leaf. BOTH halves are gated, never just the start: End is also
    ///     called from <c>TeleportToSite</c>:506 and <c>StartTravel</c>:524/:538, so gating the start alone
    ///     would leave a client subtracting a force it never added.
    /// Every one of these is void or its return is discarded by the ability, so nothing dereferences a
    /// blocked call. Host and solo stay fully native; the apply arm stays open (a rail path that reaches one
    /// must land).
    /// </summary>
    [HarmonyPatch]
    internal static class VehicleGestureGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        // Named as the game names them; a drifted name yields null, which RailCheck L23 reports as an
        // unbound handle rather than a silently absent gate.
        internal static readonly string[] GatedGestures =
            { "ScheduleRepair", "StartCollectingFromCurrentSite", "EndCollectingFromCurrentSite" };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in GatedGestures)
                yield return AccessTools.Method(typeof(GeoVehicle), name);
        }

        private static bool Prefix(GeoVehicle __instance, MethodBase __originalMethod)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            if (SyncApplyScope.Active) return true;                                      // an apply may reach it

            string who = (__originalMethod == null ? "?" : __originalMethod.Name) + " " +
                         (IdentityResolver.RootRef(__instance) ?? "V#?");
            if (_logged.Add(who))
                Debug.Log("[MP][vehicle] CLIENT gesture " + who + " BLOCKED — it writes rail-covered aircraft/site " +
                          "state the host owns; the host's own result arrives as a delta");
            return false;
        }
    }

    /// <summary>
    /// NINTH seam, same law, over the FACTION-SPAWNER gestures — the two rows RailCheck L42 found the moment
    /// its receiver filter stopped being the single type <c>GeoVehicle</c> (2026-08-08, widened alongside
    /// <c>HavenMissionGate</c>). Both are reached from a player ability with one click and both MINT A ROOT:
    ///   • <c>CreateScanner</c> (GeoFaction.cs:2161, from <c>ScanAbility.ActivateInternal</c>) —
    ///     <c>ActorSpawner.SpawnActor&lt;GeoScanner&gt;</c> + <c>DoEnterPlay</c> + <c>OnLevelStart</c>.
    ///   • <c>CreateAncientSiteProbe</c> (:2152, from <c>AncientSiteProbeAbility</c>) — the same shape for
    ///     <c>GeoAncientSiteProbe</c>.
    /// A root the CLIENT spawns is the worst case the diff has: it is host-now vs host-before, so an actor
    /// only the client has is never mentioned again and the CRC backstop can do nothing but report it — the
    /// identical failure <c>HavenMissionGate</c> exists for, one entity type over. The host's own scanner
    /// arrives as an ordinary structural create, so a client that presses Scan sees the scanner appear the
    /// moment the host runs the same click. Both methods are void; nothing dereferences a blocked call.
    ///
    /// A GATE and not an intent, deliberately: unlike a launch, neither gesture is one the player is waiting
    /// on a screen for, and the ability's own side effect (<c>GeoPhoenixFaction.AncientSiteProbes--</c>,
    /// AncientSiteProbeAbility.cs:41) is a covered leaf the next diff overwrites from the host.
    /// </summary>
    [HarmonyPatch]
    internal static class FactionSpawnerGestureGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        // Named as the game names them; a drifted name yields null, which RailCheck L23 reports as an unbound
        // handle rather than a silently absent gate.
        internal static readonly string[] GatedGestures = { "CreateScanner", "CreateAncientSiteProbe" };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in GatedGestures)
                yield return AccessTools.Method(typeof(GeoFaction), name);
        }

        private static bool Prefix(GeoFaction __instance, MethodBase __originalMethod)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            if (SyncApplyScope.Active) return true;                                      // an apply may reach it

            string who = (__originalMethod == null ? "?" : __originalMethod.Name) + " " +
                         (__instance?.Def == null ? "?" : __instance.Def.name);
            if (_logged.Add(who))
                Debug.Log("[MP][faction] CLIENT gesture " + who + " BLOCKED — it SPAWNS a geoscape actor, and a " +
                          "root only this peer has is one the host-now-vs-host-before diff can never mention " +
                          "again; the host's own runs the same spawn and it arrives as a structural create");
            return false;
        }
    }

    /// <summary>
    /// SEVENTH seam, same law, at the AUTO-MANUFACTURE funnel: <c>GeoFaction.UpdateManufacturing</c>
    /// (GeoFaction.cs:2106) is the ONE writer — both callers route through it, <c>RegisterVehicle</c>
    /// (:2069-2072) and <c>UnregisterVehicle</c> (:2095-2098). One gate in the funnel instead of a guard
    /// per caller, as with <see cref="FacilityPowerGate"/>.
    ///
    /// It is reachable on a client and it WRITES: <c>_automanufactureVehicles</c> is set from
    /// <c>Def.ManufacturesVehicles</c> in <c>OnLevelStart</c> (:392) on BOTH peers, so a client applying
    /// a <c>"V#"</c> structural destroy (c289fea) runs the faction's own
    /// <c>Manufacture.ManufactureItem</c>/<c>Cancel</c> loop (:2112-2134) and enqueues a replacement
    /// aircraft locally. The queue is host-computed state that rides the 0xAD order channel and the value
    /// rail, so the client has no business deriving it.
    ///
    /// CLIENT: never, apply or no apply — the same arm shape <see cref="EquipStorageGate"/> uses, and for
    /// the same reason: there is no rail path that legitimately reaches this funnel on a client (the
    /// appliers write the queue, they never recompute it), so an OPEN apply arm would only re-admit the
    /// exact write the structural applier triggers. Host and solo stay fully native. Void method —
    /// nothing dereferences a blocked return.
    /// </summary>
    [HarmonyPatch(typeof(GeoFaction), "UpdateManufacturing")]
    internal static class AutomanufactureGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        private static bool Prefix(GeoFaction __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native

            // Never silent (the dominant bug class): name the faction whose replacement order was refused.
            // Log-once per faction — the funnel fires per vehicle registered or unregistered.
            string who = __instance?.Def == null ? "?" : __instance.Def.name;
            if (_logged.Add(who))
                Debug.Log("[MP][diag] AutomanufactureGate BLOCK " + who +
                          " — the client never derives a manufacturing queue; the host's queue arrives via the rail");
            return false;
        }
    }
}
