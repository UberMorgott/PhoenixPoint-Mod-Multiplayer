using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Interception.Equipments;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// AIRCRAFT gesture family (surface 0xB5, law 1) — RCA gap A2, "the client cannot send an aircraft
    /// anywhere". One op per MODEL FUNNEL a player gesture on an aircraft can reach, each addressed by the
    /// vehicle's ROOT KEY ("V#&lt;id&gt;@&lt;ownerFactionGuid&gt;", IdentityResolver.cs:126) — never by a
    /// local reference and never by a payload-carried id:
    ///   • <c>travelTo</c> — <c>GeoVehicle.StartTravel(List&lt;GeoSite&gt;)</c> (GeoVehicle.cs:518). THE
    ///     route funnel: the player's own order (MoveVehicleAbility.ActivateInternal:63), both
    ///     <c>TravelTo</c> overloads (:556/:572) and every AI/raid route (VehicleFactionController:169,
    ///     GeoscapeRaid:96/:110/:145, Bomb/Destroy/Infest/Recon/SupplyRaid, GeoLevelController:1382) all
    ///     bottom out there. The wire carries the DESTINATION site only; the HOST re-derives the route
    ///     from its own navigation graph.
    ///   • <c>setEquipment</c> — <c>GeoVehicle.ReplaceEquipments</c> (GeoVehicle.cs:795), sole caller
    ///     <c>UIStateVehicleRoster.UpdateVehicleEquipments</c>:273. The RCA drafted
    ///     <c>mountModule</c>/<c>unmountModule</c>; there is NO per-slot native entry point to hook —
    ///     the screen commits the WHOLE weapon+module slot list in one call — so one op over the slot
    ///     list is the funnel-faithful shape, exactly like <c>EquipSync.OpSetItems</c> for a soldier.
    ///     The gesture's other half, the <c>AircraftItemStorage</c> write-back
    ///     (<c>UIStateVehicleRoster.UpdateAircraftStorage</c>:278-290), is blocked on a client by
    ///     <see cref="EquipStorageGate"/> — same law, same seam list.
    ///   • <c>exploreSite</c> — <c>GeoVehicle.StartExploringCurrentSite</c> (GeoVehicle.cs:414), sole caller
    ///     <c>ExploreSiteAbility.ActivateInternal</c>:14. The wire carries the vehicle key ALONE: the site
    ///     explored is that vehicle's own <c>CurrentSite</c>, which the host reads off its own graph. Left
    ///     un-captured it was a client running a per-vehicle <c>Timing.Start</c> (:451) against its own
    ///     unfrozen clock, i.e. exploring a site with no aircraft of the host's there.
    ///
    /// The sibling gestures the same abilities reach — <c>ScheduleRepair</c>,
    /// <c>Start/EndCollectingFromCurrentSite</c> — take a GATE instead of an op, argued at
    /// <see cref="VehicleGestureGate"/>. RailCheck L42 discovers the whole set from the game's own ability
    /// metadata, so neither list can silently fall behind the game.
    ///
    /// CREW BOARDING IS NOT HERE, deliberately. <c>GeoVehicle.AddCharacter/RemoveCharacter</c> (:759/:766)
    /// already have block-first capture in <see cref="PersonnelSync"/> (PersonnelSync.cs:347-366 →
    /// op=4 reassign [charId][dstRef]), because boarding is a roster TRANSFER between two containers and
    /// the site half of that same gesture lives on <c>GeoSite.AddCharacter</c>. A second path for the
    /// vehicle half would be two mechanisms for one gesture — and the pair encoding
    /// (PersonnelSync.cs:332-339) only works while there is exactly one.
    ///
    /// ONE validator shape and ONE apply shape, per op: resolve the root key → <see cref="Validate"/>
    /// (PURE, over REPLICATED facts only, so RailCheck L32 drives the real function headless) → the
    /// native call → one APPLIED line. Every refusal goes through <see cref="Reject"/> with the
    /// vehicle's own subtree as the re-emit scope, so a rejected gesture reconverges instead of
    /// throwing: two peers ordering the same aircraft inside one RTT is the NORMAL case.
    ///
    /// Why these two writes must be blocked at all: <c>DestinationSites</c>, <c>Modules</c> and
    /// <c>Weapons</c> are all COVERED rail state (docs/rail-baseline.txt:622/:625/:640), and the diff is
    /// host-now vs host-before — a client-local write to a covered field is a permanent divergence the
    /// rail can never correct.
    /// </summary>
    public static class VehicleSync
    {
        internal const byte OpTravelTo = 1;      // [vehicleRef][siteRef]
        internal const byte OpSetEquipment = 2;  // [vehicleRef][wN:u16][defGuid × N][mN:u16][defGuid × M] ("" = empty slot)
        internal const byte OpExploreSite = 3;   // [vehicleRef] — the site is the vehicle's OWN CurrentSite, host-read

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpTravelTo] = HandleTravelTo,
                [OpSetEquipment] = HandleSetEquipment,
                [OpExploreSite] = HandleExploreSite,
            };
            IntentRail.Register(SurfaceIds.GeoVehicleIntent, "vehicle", ops);
        }

        /// <summary>Full session teardown: drop the re-flush memo (see <see cref="AlreadySent"/>).</summary>
        internal static void Reset() => _sent.Clear();

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>The DiffEngine root-key scope a rejected op re-emits (IntentRail.Reject narrows the
        /// re-emit with it). Never blank for a resolvable gesture: an unscoped reject falls back to the
        /// whole covered graph, which converges but costs a full walk.</summary>
        internal static string Scope(string vehicleRef) => string.IsNullOrEmpty(vehicleRef) ? null : vehicleRef;

        private static void Reject(ulong peer, string vehicleRef, string why) =>
            IntentRail.Reject(SurfaceIds.GeoVehicleIntent, peer, (vehicleRef ?? "V#?") + " — " + why, Scope(vehicleRef));

        // ─── THE ONE VALIDATOR (pure — RailCheck L32 drives it) ─────────────

        /// <summary>Every fact an op is allowed to be validated against: all of it is either REPLICATED
        /// state read off the HOST's own graph, or a resolution result. Nothing here comes off the wire
        /// except the two root keys that produced <see cref="Resolved"/>/<see cref="TargetResolved"/> —
        /// a client's mirror can be arbitrarily stale, so the host repeats the native gates itself.</summary>
        internal struct Facts
        {
            internal bool Resolved;                 // the vehicle root key resolved to a live GeoVehicle
            internal bool OwnedByPlayer;            // vehicle.Owner == geo.PhoenixFaction (not an alien/haven aircraft)
            internal bool CanRedirect;              // travelTo: MoveVehicleAbility.GetDisabledStateInternal:20
            internal bool TargetResolved;           // travelTo: the destination site key resolved
            internal bool TargetIsIdleCurrentSite;  // travelTo: MoveVehicleAbility.GetTargetDisabledStateInternal:35
            internal bool Docked;                   // setEquipment: UIModuleVehicleCycle.InPhoenixBase:135-149
            internal int SlotCountDelta;            // setEquipment: requested slots - the vehicle's real slots
            internal bool SiteExplorable;           // exploreSite: ExploreSiteAbility.GetTargetDisabledStateInternal:42-49 (CurrentSite exists, ExplorationTime != 0, not already inspected)
            internal bool CanExploreSites;          // exploreSite: GeoVehicle.StartExploringCurrentSite:421 (CanExploreSites:282)
            internal bool HasCrew;                  // exploreSite: ExploreSiteAbility.GetDisabledStateInternal:24 (NoSoldiersInVehicle)
            internal bool AlreadyExploring;         // exploreSite: ExploreSiteAbility.ActivateInternal:12 (!IsExploringSite)
        }

        /// <summary>THE docking predicate, and the only copy of it. <c>UIModuleVehicleCycle.InPhoenixBase</c>
        /// (UIModuleVehicleCycle.cs:135-149) verbatim, but read off the LIVE entity instead of the screen's
        /// <c>VehicleDisplayData</c> snapshot. The host's <see cref="Facts.Docked"/> and the client's capture
        /// seam both call THIS, so the gate a peer's screen obeys and the gate the host validates with cannot
        /// drift apart — the drift IS the bug (live session 2026-08-08 12:22:20: peer3 shipped a loadout for
        /// an aircraft parked at a mission site, the host refused it, and the player's only feedback was the
        /// slot snapping back).</summary>
        internal static bool IsDocked(GeoVehicle vehicle)
        {
            var site = vehicle == null ? null : vehicle.CurrentSite;   // null = in transit
            return site != null && site.Type == GeoSiteType.PhoenixBase && site.State == GeoSiteState.Functioning;
        }

        /// <summary>One wording for one refusal: <see cref="Validate"/> returns it to the host's reject and
        /// the client's own pre-send guard shows it, so the player and the log read the same sentence.</summary>
        internal const string NotDockedReason =
            "aircraft is not docked at a functioning Phoenix base — its loadout cannot be changed";

        /// <summary>null = accept, otherwise the human reason the gesture was refused. A reason is never
        /// blank — a silently eaten click is the bug class this family exists to kill. The default arm
        /// refuses, so an op REGISTERED without a validator case here cannot slip through unchecked
        /// (RailCheck L32 unvalidated-op drives every registered op through this).</summary>
        internal static string Validate(byte op, Facts f)
        {
            if (!f.Resolved)
                return "no such aircraft on the host (destroyed, or a stale mirror)";
            if (!f.OwnedByPlayer)
                return "not a Phoenix aircraft — only the shared player faction's vehicles take orders from a peer";
            switch (op)
            {
                case OpTravelTo:
                    if (!f.TargetResolved) return "destination site does not exist on the host — stale mirror";
                    if (!f.CanRedirect) return "aircraft cannot be redirected right now (committed to a raid or mission)";
                    if (f.TargetIsIdleCurrentSite) return "already parked at that site with no route — nothing to order";
                    return null;
                case OpSetEquipment:
                    if (!f.Docked) return NotDockedReason;
                    if (f.SlotCountDelta != 0)
                        return "slot count off by " + f.SlotCountDelta + " — stale mirror or def/mod mismatch";
                    return null;
                case OpExploreSite:
                    if (!f.SiteExplorable)
                        return "the aircraft's current site cannot be explored (no site, nothing to find, or already inspected) — stale mirror";
                    if (!f.CanExploreSites) return "this aircraft carries nothing that can explore a site";
                    if (!f.HasCrew) return "no soldiers aboard — the exploration gesture needs a crew";
                    if (f.AlreadyExploring) return "already exploring that site — nothing to order";
                    return null;
                default:
                    return "op " + op + " is registered on the vehicle surface but has no validator";
            }
        }

        /// <summary>Assign the requested slot defs out of the host's OWN instance pools, vehicle-first.
        /// PURE, and generic over the instance type so RailCheck L32 drives the REAL function with plain
        /// guid strings (a live <c>GeoVehicleEquipment</c> needs a DefRepository). Consumes from both
        /// pools: what is left in <paramref name="onVehicle"/> afterwards was UNEQUIPPED and goes back to
        /// storage, and <paramref name="fromStorage"/> marks which taken instance must leave storage.
        /// Vehicle-first is not a preference, it is the no-churn rule: a slot whose def did not change
        /// keeps its own instance (with its damage and ammo), so a re-flush moves nothing.
        /// The wire carries DEFS, never HP/ammo — the host hands over the real instances, so a stale or
        /// forged number cannot repair or reload anything (native's own UI rebuild loses stored items'
        /// HP by recreating them, UIStateVehicleRoster.cs:284; the rail deliberately does not).</summary>
        internal static string TakeSlots<T>(IList<string> want, List<T> onVehicle, List<T> inStorage,
                                           Func<T, string> guidOf, List<T> taken, List<bool> fromStorage)
            where T : class
        {
            foreach (var guid in want)
            {
                if (string.IsNullOrEmpty(guid)) { taken.Add(null); fromStorage.Add(false); continue; } // empty slot
                int i = onVehicle.FindIndex(x => x != null && guidOf(x) == guid);
                if (i >= 0) { taken.Add(onVehicle[i]); fromStorage.Add(false); onVehicle.RemoveAt(i); continue; }
                i = inStorage.FindIndex(x => x != null && guidOf(x) == guid);
                if (i < 0)
                    return "def " + guid + " is on neither the aircraft nor the host's aircraft storage — " +
                           "stale mirror (scrapped, or another peer took it)";
                taken.Add(inStorage[i]); fromStorage.Add(true); inStorage.RemoveAt(i);
            }
            return null;
        }

        // ─── CLIENT: the two capture seams (law 4a), block-first ────────────

        /// <summary>Route capture at the MODEL funnel. Block-first via
        /// <see cref="IntentRail.ShouldRunNative"/>: the client's click never writes
        /// <c>_destinationSites</c> (a covered LeafList) and never navigates — the aircraft moves when the
        /// host's <c>SurfacePos</c>/<c>SurfaceRot</c> deltas arrive (404d696). Parameter types are named
        /// EXACTLY: <c>StartTravel</c> is overloaded (<c>List&lt;Vector3&gt;</c>, GeoVehicle.cs:532) and
        /// Harmony does no widening — a base-typed guess resolves to null, which PatchAll turns into one
        /// swallowed warning (RailCheck L23).</summary>
        [HarmonyPatch(typeof(GeoVehicle), nameof(GeoVehicle.StartTravel), new[] { typeof(List<GeoSite>) })]
        internal static class TravelCapturePatch
        {
            private static bool Prefix(GeoVehicle __instance, List<GeoSite> path)
            {
                MissionSync.CaptureVehicleDeparture(__instance); // immutable BEFORE, host-only
                // WHO SENT IT (host-only ledger, read by the tab-pause rule in TimeSync). Attributed to
                // the host by default and OVERWRITTEN by HandleTravelTo below when this StartTravel is
                // the host replaying a client's order — that call frames this very prefix.
                var net = NetworkEngine.Instance;
                if (net != null && net.IsHost) AircraftDispatch.Note(__instance, AircraftDispatch.HostPeer);
                return CaptureTravel(__instance, path);
            }
            private static Exception Finalizer(GeoVehicle __instance, Exception __exception)
            {
                if (__exception != null) MissionSync.AbandonCapturedVehicleDeparture(__instance);
                return __exception;
            }
        }


        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        private static bool CaptureTravel(GeoVehicle vehicle, List<GeoSite> path)
        {
            if (IntentRail.ShouldRunNative()) return true;
            string vehicleRef = IdentityResolver.RootRef(vehicle);
            try
            {
                // A client's own sim/AI route — a raid, a faction controller, the mid-flight resume. A
                // projector must not run it (the route is host-computed and arrives as mirrored motion)
                // and must not ORDER it either: nobody asked for it. Blocked, logged once, never sent.
                var geo = GeoLevel();
                if (geo == null || !ReferenceEquals(vehicle.Owner, geo.PhoenixFaction))
                {
                    if (_logged.Add("route:" + vehicleRef))
                        Debug.Log("[MP][vehicle] CLIENT sim route of " + vehicleRef + " BLOCKED — not a player gesture; the host's route arrives as mirrored motion");
                    return false;
                }
                // The route the client computed came off ITS mirror; only the DESTINATION travels and the
                // host re-derives the path from its own graph (law 3 — the wire carries no derived state).
                var dst = path == null || path.Count == 0 ? null : path[path.Count - 1];
                string siteRef = IdentityResolver.RootRef(dst);
                if (siteRef == null)
                {
                    Debug.LogWarning("[MP][vehicle] CLIENT route of " + vehicleRef + " DROPPED — unaddressable destination (" +
                                     (dst == null ? "empty path" : "site id " + dst.SiteId) + ")");
                    OpenUiRepaint.MarkDirty();
                    return false;
                }
                IntentRail.Send(SurfaceIds.GeoVehicleIntent, OpTravelTo, "travel " + vehicleRef + " -> " + siteRef,
                    w => { w.Write(vehicleRef); w.Write(siteRef); });
            }
            catch (Exception ex)
            {
                // Nothing was written and nothing was sent: no delta will ever repaint the order the UI
                // already drew, so reconverge from the un-mutated local model like the reject path does.
                Debug.LogError("[MP][vehicle] CLIENT route capture failed for " + vehicleRef + " — reconverging local UI: " + ex);
                OpenUiRepaint.MarkDirty();
            }
            return false;
        }

        /// <summary>Loadout capture at the MODEL funnel (GeoVehicle.cs:795). Same posture as
        /// <c>EquipSync.SetItemsCapturePatch</c>: the client's slot widgets stay staged until the host's
        /// echo repaints them, and <c>_weapons</c>/<c>_modules</c> (covered EntityLists) are never written
        /// locally.</summary>
        [HarmonyPatch(typeof(GeoVehicle), nameof(GeoVehicle.ReplaceEquipments))]
        internal static class EquipCapturePatch
        {
            private static bool Prefix(GeoVehicle __instance, List<GeoVehicleEquipment> weapons,
                                       List<GeoVehicleEquipment> modules)
                => CaptureEquip(__instance, weapons, modules);
        }

        private static bool CaptureEquip(GeoVehicle vehicle, List<GeoVehicleEquipment> weapons,
                                        List<GeoVehicleEquipment> modules)
        {
            if (IntentRail.ShouldRunNative()) return true;
            string vehicleRef = IdentityResolver.RootRef(vehicle);
            try
            {
                var geo = GeoLevel();
                if (geo == null || !ReferenceEquals(vehicle.Owner, geo.PhoenixFaction))
                {
                    if (_logged.Add("equip:" + vehicleRef))
                        Debug.Log("[MP][vehicle] CLIENT loadout write on " + vehicleRef + " BLOCKED — not a player aircraft, nothing to relay");
                    return false;
                }
                byte[] body = EncodeSlots(weapons, modules, GuidOfSlot);
                byte[] canon = EncodeSlots(vehicle.Weapons, vehicle.Modules, GuidOfSlot);
                // "Changes nothing" is a question about the FILLED slots, and only those: the UI's list
                // carries one entry per SLOT (a null per empty one, UIStateVehicleRoster
                // .UpdateVehicleEquipments:246-272) while the vehicle's own list is COMPACT after a load
                // (GeoVehicle.AddNullWeapon has no caller but ReplaceEquipments, decompile
                // GeoVehicle.cs:884-897). Compared raw, a compact canon can never be byte-equal to a
                // capacity-shaped body, so every roster exit shipped an intent for an aircraft nobody had
                // edited — the nonce=1 in the join log.
                if (RailMeta.BytesEqual(EncodeSlots(weapons, modules, GuidOfSlot, filledOnly: true),
                                        EncodeSlots(vehicle.Weapons, vehicle.Modules, GuidOfSlot, filledOnly: true)))
                    return false; // this flush changes nothing — zero traffic
                // THE GATE, taken one round trip early and from the SAME predicate the host validates with.
                // It has to live HERE and not on the screen because the screen is not the only caller:
                // UIStateVehicleRoster.ExitState:128-131 flushes UpdateVehicleEquipmentAndStorage on EVERY
                // exit, ungated by its own _inPhoenixBase, and ItemScrappedHandler:183 flushes too — so a
                // guard on the roster widgets would still ship an intent the host must refuse. One guard at
                // the funnel covers every caller, this mod's and any other's.
                // Refusing AFTER the byte-equal check is what keeps it quiet: the no-op exit flush of an
                // aircraft nobody edited is byte-equal and returns above, so the notice fires only for an
                // edit that really was refused.
                if (!IsDocked(vehicle))
                {
                    Debug.Log("[MP][vehicle] CLIENT loadout write on " + vehicleRef +
                              " REFUSED locally — " + NotDockedReason);
                    SessionNotifier.ShowToast(NotDockedReason);   // native NotificationController toast
                    // Nothing was written (law 3: the native op never ran) and nothing was sent, so no delta
                    // will ever repaint the slot the UI already drew. Reconverging from the un-mutated model
                    // puts it back AND makes the next flush byte-equal, so the notice cannot repeat itself.
                    OpenUiRepaint.MarkDirty();
                    return false;
                }
                if (AlreadySent(vehicleRef, body, canon)) return false;
                IntentRail.Send(SurfaceIds.GeoVehicleIntent, OpSetEquipment,
                    "loadout " + vehicleRef + " bytes=" + body.Length,
                    w => { w.Write(vehicleRef); w.Write(body); });
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][vehicle] CLIENT loadout capture failed for " + vehicleRef + " — reconverging local UI: " + ex);
                OpenUiRepaint.MarkDirty();
            }
            return false;
        }

        /// <summary>Site-exploration capture at the MODEL funnel — <c>GeoVehicle.StartExploringCurrentSite</c>
        /// (GeoVehicle.cs:414), whose ONE caller is <c>ExploreSiteAbility.ActivateInternal</c>
        /// (ExploreSiteAbility.cs:14), i.e. it is the gesture. Untreated it was a PLAYER gesture running
        /// LOCALLY on a projector: it schedules a per-vehicle <c>Timing.Start</c> (:451) that the client's
        /// unfrozen clock then runs to completion, so a client explored a site with no aircraft of the host's
        /// there and diverged permanently (the diff is host-now vs host-before, so a client-local mutation is
        /// never corrected). <see cref="ClientSimGate"/> only ever covered the HOURLY tick.
        /// The wire carries the vehicle root key and NOTHING else: the site explored is the vehicle's own
        /// <c>CurrentSite</c>, which the host reads off its own graph (law 3 — no derived state on the wire).
        /// </summary>
        [HarmonyPatch(typeof(GeoVehicle), nameof(GeoVehicle.StartExploringCurrentSite))]
        internal static class ExploreCapturePatch
        {
            private static bool Prefix(GeoVehicle __instance) => CaptureExplore(__instance);
        }

        private static bool CaptureExplore(GeoVehicle vehicle)
        {
            if (IntentRail.ShouldRunNative()) return true;
            string vehicleRef = IdentityResolver.RootRef(vehicle);
            try
            {
                var geo = GeoLevel();
                if (geo == null || !ReferenceEquals(vehicle.Owner, geo.PhoenixFaction))
                {
                    if (_logged.Add("explore:" + vehicleRef))
                        Debug.Log("[MP][vehicle] CLIENT site exploration by " + vehicleRef +
                                  " BLOCKED — not a player aircraft, nothing to relay");
                    return false;
                }
                IntentRail.Send(SurfaceIds.GeoVehicleIntent, OpExploreSite, "explore " + vehicleRef,
                    w => w.Write(vehicleRef));
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][vehicle] CLIENT explore capture failed for " + vehicleRef +
                               " — reconverging local UI: " + ex);
                OpenUiRepaint.MarkDirty();
            }
            return false;
        }

        private struct Sent { internal byte[] Body, Canon; }

        private static readonly Dictionary<string, Sent> _sent = new Dictionary<string, Sent>(StringComparer.Ordinal);

        /// <summary>THE REPEAT GUARD, the same one EquipSync needed (EquipSync.cs:145-163) and for the same
        /// reason: <c>UIStateVehicleRoster</c> re-flushes the whole slot list on EVERY slot-changed event
        /// (:229, :235-242), and blocking the client's native write leaves the model untouched — so the next
        /// flush recomputes the identical difference and would ship the identical intent again. IntentDedup
        /// cannot see it: those ARE distinct intents by its (peer, surface, nonce) key. Suppress only while
        /// NOTHING moved (same body AND same canon it was computed against); the host echo mutates the model,
        /// which retires the memo by itself — no event wiring and nothing to reset by hand.</summary>
        internal static bool AlreadySent(string vehicleRef, byte[] body, byte[] canon)
        {
            if (_sent.TryGetValue(vehicleRef, out var s) &&
                RailMeta.BytesEqual(s.Body, body) && RailMeta.BytesEqual(s.Canon, canon)) return true;
            _sent[vehicleRef] = new Sent { Body = body, Canon = canon };
            return false;
        }

        /// <summary>Slot list → wire body: two counted runs of def guids, "" for an empty slot. Also the
        /// CANON encoder the repeat guard compares against, so both sides of that comparison are produced
        /// by one function.
        ///
        /// The wire body stays CAPACITY-shaped on purpose — its empty slots are the only evidence of slot
        /// capacity the host ever receives (capacity is a PREFAB fact,
        /// <c>UIVehicleEquipmentInventoryList.Slots</c>, carried by no def and no model member), and
        /// <see cref="TakeSlots"/> turns each "" straight back into a null slot. <paramref name="filledOnly"/>
        /// is the shape-proof projection for COMPARING two loadouts whose shapes may differ.</summary>
        /// <summary>An empty slot's guid. THE definition of "empty", used by every arm below, so the encoder,
        /// the no-op compare and the capacity arithmetic cannot disagree about what an empty slot is.</summary>
        internal static string GuidOfSlot(GeoVehicleEquipment e) => e?.EquipmentDef == null ? "" : e.EquipmentDef.Guid;

        /// <summary>Generic in the slot type for the SAME reason <see cref="TakeSlots"/> is (RailCheck L32):
        /// a live <c>GeoVehicleEquipment</c> needs a DefRepository, so the law drives the real function with
        /// plain guid strings.</summary>
        internal static byte[] EncodeSlots<T>(IEnumerable<T> weapons, IEnumerable<T> modules, Func<T, string> guidOf,
                                              bool filledOnly = false)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                foreach (var list in new[] { weapons, modules })
                {
                    var guids = (list ?? Enumerable.Empty<T>()).Select(guidOf).Select(g => g ?? "");
                    if (filledOnly) guids = guids.Where(g => g.Length > 0);
                    var slots = guids.ToList();
                    w.Write((ushort)slots.Count);
                    foreach (var g in slots) w.Write(g);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Requested slot count vs the vehicle's, in the ONE case where the two are the same unit.
        /// A capacity comparison needs a live list the UI itself shaped — one that still carries its empty
        /// slots — because capacity lives in the roster PREFAB and nowhere in the model or the defs. A list
        /// with no empty slot is either full (nothing to compare) or COMPACT because it came off a save
        /// (GeoVehicle.AddNullWeapon has no caller but ReplaceEquipments, decompile GeoVehicle.cs:884-897),
        /// and there the host has no capacity to compare against at all. It abstains rather than subtracting
        /// two different units: the raw arithmetic refused a perfectly legal edit as "slot count off by 2"
        /// (1 weapon + 2 empty module slots against a host list holding 1 item), and would go on refusing
        /// every edit to every aircraft the host had not itself opened the roster on this session.
        /// Generic in the slot type for the same reason <see cref="TakeSlots"/> is — RailCheck L366 drives
        /// this exact function with plain guid strings.</summary>
        internal static int CapacityDelta<T>(int wantSlots, IEnumerable<T> live, Func<T, string> guidOf)
        {
            var slots = (live ?? Enumerable.Empty<T>()).ToList();
            if (!slots.Any(s => string.IsNullOrEmpty(guidOf(s)))) return 0;
            return wantSlots - slots.Count;
        }

        private static List<string> ReadSlots(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString());
            return list;
        }

        // ─── HOST: the two appliers (decode/dedup/reject discipline = IntentRail) ─────

        private static void HandleTravelTo(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string vehicleRef = null;
            try
            {
                vehicleRef = r.ReadString();
                string siteRef = r.ReadString();
                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, vehicleRef, "no geoscape"); return; }

                var vehicle = IdentityResolver.Resolve(geo, vehicleRef, null) as GeoVehicle;
                var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                string why = Validate(OpTravelTo, new Facts
                {
                    Resolved = vehicle != null,
                    OwnedByPlayer = vehicle != null && ReferenceEquals(vehicle.Owner, geo.PhoenixFaction),
                    CanRedirect = vehicle != null && vehicle.CanRedirect,
                    TargetResolved = site != null,
                    TargetIsIdleCurrentSite = vehicle != null && vehicle.DestinationSites.Count == 0 &&
                                              ReferenceEquals(vehicle.CurrentSite, site),
                });
                if (why != null) { Reject(senderPeerId, vehicleRef, "travel to " + siteRef + ": " + why); return; }

                // Re-derive the route on the HOST's graph — MoveVehicleAbility.ActivateInternal:55-63,
                // verbatim. GeoVehicle.TravelTo(GeoSite) is NOT usable: it AddRanges into the vehicle's own
                // _destinationSites and hands that SAME list to StartTravel, which Clear()s it first
                // (GeoVehicle.cs:526-527) — so the route it navigates is always EMPTY. A game bug we route
                // around, not one we inherit.
                var src = vehicle.CurrentSite != null ? vehicle.CurrentSite.WorldPosition : vehicle.WorldPosition;
                bool foundPath;
                var nodes = vehicle.Navigation.FindPath(src, site.WorldPosition, out foundPath);
                var path = !foundPath ? null : nodes.Where(pn => pn.Site != null && !ReferenceEquals(pn.Site, vehicle.CurrentSite))
                                                    .Select(pn => pn.Site).ToList();
                if (path == null || path.Count == 0)
                {
                    // StartTravel throws on an empty path (GeoVehicle.cs:520-523) — name it instead.
                    Reject(senderPeerId, vehicleRef, "travel to " + siteRef + ": no route from the host's own navigation graph");
                    return;
                }
                vehicle.StartTravel(path);
                AircraftDispatch.Note(vehicle, senderPeerId); // the dispatcher, for the tab-pause rule
                Debug.Log("[MP][vehicle] HOST intent APPLIED op=travelTo " + vehicleRef + " -> " + siteRef +
                          " legs=" + path.Count + " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex) { Reject(senderPeerId, vehicleRef, "travel (throw) " + ex.Message); }
        }

        /// <summary>The host runs the SAME native method the client's click was blocked from
        /// (GeoVehicle.cs:414) and everything it produces — the site's inspected state, its rewards, the
        /// aircraft's <c>StartExplorationTime</c> — rides back on the ordinary value rail. Every gate the
        /// game itself applies is repeated here against the HOST's own graph, because a client mirror can be
        /// arbitrarily stale.</summary>
        private static void HandleExploreSite(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string vehicleRef = null;
            try
            {
                vehicleRef = r.ReadString();
                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, vehicleRef, "no geoscape"); return; }

                var vehicle = IdentityResolver.Resolve(geo, vehicleRef, null) as GeoVehicle;
                var site = vehicle == null ? null : vehicle.CurrentSite;
                string why = Validate(OpExploreSite, new Facts
                {
                    Resolved = vehicle != null,
                    OwnedByPlayer = vehicle != null && ReferenceEquals(vehicle.Owner, geo.PhoenixFaction),
                    // GeoSite.ExplorationTime:87 / GetInspected(GeoFaction):398 — the ability's own target gates.
                    SiteExplorable = site != null && site.ExplorationTime != TimeUnit.Zero && !site.GetInspected(vehicle.Owner),
                    CanExploreSites = vehicle != null && vehicle.CanExploreSites,
                    HasCrew = vehicle != null && vehicle.Units.Any(),
                    AlreadyExploring = vehicle != null && vehicle.IsExploringSite,
                });
                if (why != null) { Reject(senderPeerId, vehicleRef, "explore: " + why); return; }

                vehicle.StartExploringCurrentSite();
                Debug.Log("[MP][vehicle] HOST intent APPLIED op=exploreSite " + vehicleRef + " site=" +
                          IdentityResolver.RootRef(site) + " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex) { Reject(senderPeerId, vehicleRef, "explore (throw) " + ex.Message); }
        }

        private static void HandleSetEquipment(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string vehicleRef = null;
            try
            {
                vehicleRef = r.ReadString();
                var wantWeapons = ReadSlots(r);
                var wantModules = ReadSlots(r);
                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, vehicleRef, "no geoscape"); return; }

                var vehicle = IdentityResolver.Resolve(geo, vehicleRef, null) as GeoVehicle;
                string why = Validate(OpSetEquipment, new Facts
                {
                    Resolved = vehicle != null,
                    OwnedByPlayer = vehicle != null && ReferenceEquals(vehicle.Owner, geo.PhoenixFaction),
                    // The SAME predicate the sending client gated itself with (CaptureEquip) — one copy.
                    Docked = IsDocked(vehicle),
                    SlotCountDelta = vehicle == null ? 0
                        : CapacityDelta(wantWeapons.Count, vehicle.Weapons, GuidOfSlot) +
                          CapacityDelta(wantModules.Count, vehicle.Modules, GuidOfSlot),
                });
                if (why != null) { Reject(senderPeerId, vehicleRef, "loadout: " + why); return; }

                // The host's OWN instances are the only source (law 3): the vehicle's current slots first,
                // then the faction's aircraft storage. Weapons are assigned before modules and both share
                // ONE storage pool, so a def can be consumed only once per intent.
                var storage = geo.PhoenixFaction.AircraftItemStorage;
                var storagePool = storage.Items.ToList();
                var weaponPool = vehicle.Weapons.Where(x => x != null).ToList();
                var modulePool = vehicle.Modules.Where(x => x != null).ToList();
                List<GeoVehicleEquipment> newWeapons = new List<GeoVehicleEquipment>(), newModules = new List<GeoVehicleEquipment>();
                List<bool> wFromStorage = new List<bool>(), mFromStorage = new List<bool>();
                Func<GeoVehicleEquipment, string> guidOf = GuidOfSlot;   // ONE definition of "empty slot"

                why = TakeSlots(wantWeapons, weaponPool, storagePool, guidOf, newWeapons, wFromStorage)
                      ?? TakeSlots(wantModules, modulePool, storagePool, guidOf, newModules, mFromStorage);
                if (why != null) { Reject(senderPeerId, vehicleRef, "loadout: " + why); return; }
                // Kind is a trust boundary, not a formality: AddEquipment routes by the INSTANCE's IsWeapon
                // (GeoVehicle.cs:829-836) regardless of which list it arrived in, so a module guid in a
                // weapon slot would silently land in _modules and skew both slot counts.
                if (newWeapons.Any(e => e != null && !e.IsWeapon) || newModules.Any(e => e != null && !e.IsModule))
                { Reject(senderPeerId, vehicleRef, "loadout: a slot asked for the wrong equipment kind — stale mirror or def mismatch"); return; }

                vehicle.ReplaceEquipments(newWeapons, newModules);
                // The storage half the native screen does by list diffing (UIStateVehicleRoster:278-290):
                // what came OUT of storage leaves it, what was UNEQUIPPED (the pool leftovers) goes back.
                for (int i = 0; i < newWeapons.Count; i++) if (wFromStorage[i]) storage.RemoveItem(newWeapons[i]);
                for (int i = 0; i < newModules.Count; i++) if (mFromStorage[i]) storage.RemoveItem(newModules[i]);
                foreach (var left in weaponPool) storage.AddItem(left);
                foreach (var left in modulePool) storage.AddItem(left);

                Debug.Log("[MP][vehicle] HOST intent APPLIED op=setEquipment " + vehicleRef +
                          " weapons=" + wantWeapons.Count + " modules=" + wantModules.Count +
                          " unequipped=" + (weaponPool.Count + modulePool.Count) +
                          " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex) { Reject(senderPeerId, vehicleRef, "loadout (throw) " + ex.Message); }
        }
    }

    /// <summary>THE DEPARTURE EDGE THE OPEN DEPLOYMENT WINDOWS HANG ON, and its OWN patch class — not a
    /// postfix on the capture above, because a capture class that declares one ships a RESULT (L19, and
    /// RailCheck L32 says so by name). This one mutates nothing and sends nothing: it is presentation.
    ///
    /// An aircraft leaving a site removes it — and its soldiers — from
    /// <c>GeoMission.GetDeploymentSources</c>, which is the SET the squad screen and its brief are backed
    /// by (<c>DeploymentWindowClose</c>). Nothing on the HOST marks the UI dirty for that: on a client the
    /// order arrives as the <c>CurrentSite</c>/<c>Travelling</c>/<c>DestinationSites</c> leaves the rail
    /// batch already repaints on (<c>GenericApplier.IsOrderLeaf</c>), while the host's own gesture rides
    /// no batch at all. Postfix, so the native write has happened before anything reads it;
    /// <c>MarkDirty</c> coalesces per frame, so a multi-leg route still costs one repaint.</summary>
    [HarmonyPatch(typeof(GeoVehicle), nameof(GeoVehicle.StartTravel), new[] { typeof(List<GeoSite>) })]
    internal static class TravelRepaintPatch
    {
        private static void Postfix(GeoVehicle __instance)
        {
            MissionSync.CommitCapturedVehicleDeparture(__instance); // AFTER native StartTravel write
            OpenUiRepaint.MarkDirty();
        }
    }

    /// <summary>Host-issued generation carried as an optional tail of the existing GeoRail delta. It is
    /// advanced by every successful native StartTravel, whether or not a DWI occurrence currently binds
    /// the vehicle; the DWI delta names this exact value and can therefore never align by coincidence.</summary>
    internal static class DepartureGenerationRail
    {
        internal const byte TailMarker = 0xD7;
        internal const int MaxVehicles = 256; // <= ~8 KiB tail; 45 KiB rail chunk remains under u16 envelope
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ulong> Generations =
            new Dictionary<string, ulong>(StringComparer.Ordinal);

        internal static bool TryAdvance(string vehicleIdentity, out ulong generation)
        {
            generation = 0; if (string.IsNullOrEmpty(vehicleIdentity)) return false;
            lock (Gate)
            {
                ulong previous;
                if (!Generations.TryGetValue(vehicleIdentity, out previous) && Generations.Count >= MaxVehicles)
                    return false;
                try { generation = checked(previous + 1); } catch (OverflowException) { return false; }
                Generations[vehicleIdentity] = generation; return true;
            }
        }

        internal static KeyValuePair<string, ulong>[] Snapshot()
        { lock (Gate) return Generations.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(); }
        internal static void Remove(string vehicleIdentity)
        { if (vehicleIdentity == null) return; lock (Gate) Generations.Remove(vehicleIdentity); }
        internal static void Clear() { lock (Gate) Generations.Clear(); }
    }
}
