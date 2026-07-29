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
    /// anywhere". Two ops, because the game has exactly TWO model funnels a player gesture on an aircraft
    /// can reach, and both are addressed by the vehicle's ROOT KEY ("V#&lt;id&gt;@&lt;ownerFactionGuid&gt;",
    /// IdentityResolver.cs:126) — never by a local reference and never by a payload-carried id:
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

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpTravelTo] = HandleTravelTo,
                [OpSetEquipment] = HandleSetEquipment,
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
        }

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
                    if (!f.Docked) return "aircraft is not docked at a functioning Phoenix base — its loadout cannot be changed";
                    if (f.SlotCountDelta != 0)
                        return "slot count off by " + f.SlotCountDelta + " — stale mirror or def/mod mismatch";
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
            private static bool Prefix(GeoVehicle __instance, List<GeoSite> path) => CaptureTravel(__instance, path);
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
                byte[] body = EncodeSlots(weapons, modules);
                byte[] canon = EncodeSlots(vehicle.Weapons, vehicle.Modules);
                if (RailMeta.BytesEqual(body, canon)) return false; // this flush changes nothing — zero traffic
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
        /// by one function.</summary>
        private static byte[] EncodeSlots(IEnumerable<GeoVehicleEquipment> weapons, IEnumerable<GeoVehicleEquipment> modules)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                foreach (var list in new[] { weapons, modules })
                {
                    var slots = list == null ? new List<GeoVehicleEquipment>() : list.ToList();
                    w.Write((ushort)slots.Count);
                    foreach (var s in slots) w.Write(s?.EquipmentDef == null ? "" : s.EquipmentDef.Guid);
                }
                return ms.ToArray();
            }
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
                Debug.Log("[MP][vehicle] HOST intent APPLIED op=travelTo " + vehicleRef + " -> " + siteRef +
                          " legs=" + path.Count + " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex) { Reject(senderPeerId, vehicleRef, "travel (throw) " + ex.Message); }
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
                var site = vehicle?.CurrentSite;
                string why = Validate(OpSetEquipment, new Facts
                {
                    Resolved = vehicle != null,
                    OwnedByPlayer = vehicle != null && ReferenceEquals(vehicle.Owner, geo.PhoenixFaction),
                    // UIModuleVehicleCycle.InPhoenixBase:135-149 — the gate the screen itself uses.
                    Docked = site != null && site.Type == GeoSiteType.PhoenixBase && site.State == GeoSiteState.Functioning,
                    SlotCountDelta = vehicle == null ? 0
                        : (wantWeapons.Count + wantModules.Count) - (vehicle.Weapons.Count() + vehicle.Modules.Count()),
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
                Func<GeoVehicleEquipment, string> guidOf = e => e.EquipmentDef == null ? "" : e.EquipmentDef.Guid;

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
}
