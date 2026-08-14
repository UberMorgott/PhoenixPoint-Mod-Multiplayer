using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L460 — A RE-SEEDED LEG STARTS FROM THE HOST'S DEPARTURE EPOCH, NEVER FROM LOCAL NOW.
    ///
    /// Reported 2026-08-14: non-player faction aircraft existed on every peer but their map positions did
    /// not match at all. Aircraft POSE is deliberately not mirrored (fbc9065, rail-baseline.txt
    /// "EXCLUDED derived pose") — every peer re-derives motion from the mirrored ORDER. But BOTH start
    /// inputs of that derivation are LOCAL: <c>GeoNavComponent.NavigateRoutine</c> takes
    /// <c>startTime = Timing.Now</c> at leg start (GeoNavComponent.cs:96) and <c>CalculatePath</c> starts
    /// from that peer's own <c>WorldPosition</c> (:69-72), while a client re-seeds only when the order delta
    /// LANDS. It therefore departs late by latency x <c>Timing.Scale</c> — minutes to hours of geo time.
    /// PLAYER craft hid it: every leg of theirs ends in the host's arrival snap
    /// (<c>ReseedNavigation</c>'s parked arm). Foreign craft fly leg after leg with no snap and never
    /// reconverge, and <c>NavigateRoutine</c>:116/:138 keeps falsifying the COVERED <c>RangeRemaining</c> /
    /// <c>Travelling</c> leaves off that wrong start — the CRC backstop's permanently DIVERGED subtrees.
    ///
    /// So the derivation must be anchored to SHARED time: the host stamps its departure on the LEVEL clock
    /// (the one <c>TimeAnchor</c> holds equal across peers — per-ACTOR clocks are unshared epochs, L77), the
    /// stamp rides the optional GeoRail tail, and the client fast-forwards the leg by
    /// <c>sharedNow - departure</c> BEFORE calling Navigate.
    ///
    /// Falsify: delete the <c>AnchorToHostDeparture</c> call from <c>ReseedNavigation</c> →
    /// <c>reseed-unanchored</c>; make the anchor read the local clock instead of the host stamp →
    /// <c>anchor-ignores-host-epoch</c>; stop stamping the level clock on the host →
    /// <c>departure-unstamped</c>; drop the tail section → <c>anchor-never-shipped</c>.
    /// </summary>
    internal static class L460_AReSeededLegStartsFromTheHostDepartureEpoch
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var applier = typeof(GenericApplier);
            var mod = applier.Assembly;
            var reseed = applier.GetMethod("ReseedNavigation", All);
            var anchor = applier.GetMethod("AnchorToHostDeparture", All);
            if (reseed == null || anchor == null)
            {
                yield return "L460 seam-gone: GenericApplier." + (reseed == null ? "ReseedNavigation" : "AnchorToHostDeparture") +
                             " does not resolve — the client's re-derived leg has no place left to be anchored to the " +
                             "host's departure, so every foreign aircraft is back to flying off this peer's own clock";
                yield break;
            }

            // (0) THE NATIVE PREMISE. The anchor places the actor through the game's OWN placement method
            // rather than a hand-written localRotation, precisely because guessing that write has broken this
            // seam before (the parked arm's comment records it). If the game stops offering it, this law is
            // asserting against a shape that no longer exists and must say so instead of passing.
            var place = typeof(PhoenixPoint.Geoscape.Entities.GeoActor)
                .GetMethod("SetOrientedGlobeWorldPosition", All, null, new[] { typeof(UnityEngine.Vector3) }, null);
            var levelClock = typeof(GeoLevelController).GetProperty("Timing", All);
            if (place == null || levelClock == null)
            {
                yield return "L460 premise-changed: " + (place == null
                        ? "GeoActor.SetOrientedGlobeWorldPosition(Vector3) is gone — the native globe placement the " +
                          "anchor writes through no longer exists"
                        : "GeoLevelController.Timing is gone — there is no level clock left for TimeAnchor to hold " +
                          "equal across peers") +
                    ", so 'a re-seeded leg starts from the host's departure epoch' is unenforceable as written";
                yield break;
            }

            // (a) THE CALL, and its ORDER. CalculatePath (GeoNavComponent.cs:69-72) reads the actor's
            // WorldPosition at Navigate() time, so an anchor placed AFTER the Navigate call is a pose write the
            // routine's own Slerp overwrites on the next frame — the exact failure the parked arm records.
            var navigate = typeof(GeoVehicle).GetProperty("Navigation", All)?.PropertyType
                               .GetMethod("Navigate", All, null, new[] { typeof(List<UnityEngine.Vector3>) }, null);
            // The un-filtered overload: both call sites carry their IL OFFSET, and one of the two is native.
            var sites = Program.CallSites(reseed);
            int anchorAt = sites.Where(x => x.Value.MetadataToken == anchor.MetadataToken && x.Value.Module == anchor.Module)
                                .Select(x => (int?)x.Key).FirstOrDefault() ?? -1;
            int navigateAt = navigate == null ? -1
                : sites.Where(x => x.Value.MetadataToken == navigate.MetadataToken && x.Value.Module == navigate.Module)
                       .Select(x => (int?)x.Key).FirstOrDefault() ?? -1;
            if (anchorAt < 0)
                yield return "L460 reseed-unanchored: ReseedNavigation never calls AnchorToHostDeparture — the client " +
                             "re-derives the leg from its OWN Timing.Now and its OWN WorldPosition, which is exactly the " +
                             "latency x Timing.Scale head start that made foreign aircraft disagree on every peer";
            else if (navigateAt >= 0 && anchorAt > navigateAt)
                yield return "L460 anchor-after-navigate: AnchorToHostDeparture runs AFTER Navigate — CalculatePath has " +
                             "already sampled the un-anchored WorldPosition, and NavigateRoutine's per-frame Slerp " +
                             "overwrites the pose the anchor just wrote";

            // (b) THE ANCHOR READS THE HOST'S STAMP, not this peer's clock alone. Both halves are asserted by
            // name because either one alone is satisfiable by the broken code: reading the level clock without
            // the stamp IS local now.
            var game = typeof(GeoVehicle).Assembly; // Callees filters by declaring assembly — the clock is native
            var anchorReads = Program.Callees(anchor, game).ToList();
            var stampMap = applier.GetField("_departureAnchors", All);
            if (stampMap == null || !Program.ReadsField(anchor, stampMap))
                yield return "L460 anchor-ignores-host-epoch: AnchorToHostDeparture does not read " +
                             "GenericApplier._departureAnchors — whatever it places the aircraft at, it is not where the " +
                             "HOST is, and the two peers' legs stay independent derivations";
            var levelTiming = levelClock.GetGetMethod(true);
            if (levelTiming != null && !anchorReads.Any(c => c.MetadataToken == levelTiming.MetadataToken &&
                                                             c.Module == levelTiming.Module))
                yield return "L460 anchor-clockless: AnchorToHostDeparture never reads GeoLevelController.Timing — the " +
                             "host's stamp can only be turned into elapsed time against the LEVEL clock, the one " +
                             "TimeAnchor holds equal across peers (a per-actor clock is an unshared epoch, L77)";

            // (c) THE PURE DECISION, executed. `elapsed`, never zero-by-default, and never a stamp that cannot
            // describe THIS route.
            var covered = applier.GetMethod("CoveredSeconds", All);
            if (covered == null)
                yield return "L460 decision-gone: GenericApplier.CoveredSeconds does not resolve — the anchor's refusal " +
                             "rules are no longer executable, so nothing can tell a shared-time anchor from a guess";
            else
            {
                Func<bool, double, double, double, double> f = (have, dep, now, route) =>
                    (double)covered.Invoke(null, new object[] { have, dep, now, route });
                if (Math.Abs(f(true, 100.0, 340.0, 600.0) - 240.0) > 1e-9)
                    yield return "L460 anchor-ignores-host-epoch: CoveredSeconds does not return sharedNow - departure " +
                                 "for a live stamp — the client starts the leg at zero elapsed, i.e. at local now, and " +
                                 "the whole fix is a no-op";
                if (f(false, 100.0, 340.0, 600.0) >= 0.0)
                    yield return "L460 anchor-invents-an-epoch: CoveredSeconds fast-forwards a leg it has NO host stamp " +
                                 "for (a save-transfer join, or a host too old to ship the tail section) — it would place " +
                                 "the aircraft by arithmetic on an unset field";
                if (f(true, 900.0, 340.0, 600.0) >= 0.0)
                    yield return "L460 anchor-accepts-a-future-stamp: CoveredSeconds accepts a departure LATER than " +
                                 "shared now — a client whose TimeAnchor has not applied yet would fly the leg backwards";
                if (f(true, 100.0, 900.0, 600.0) >= 0.0)
                    yield return "L460 anchor-reuses-a-finished-journey: CoveredSeconds accepts a stamp older than the " +
                                 "whole route — that stamp belongs to a journey already over, and honouring it parks the " +
                                 "aircraft on its destination the instant a new order lands";
            }

            // (d) THE WIRE. A stamp neither taken on the host nor shipped is an anchor no client can ever read.
            var stamp = typeof(DepartureAnchorRail).GetMethod("Stamp", All);
            var range = typeof(GeoVehicle).GetProperty("RangeRemaining", All)?.GetGetMethod(true);
            if (stamp == null)
                yield return "L460 departure-unstamped: DepartureAnchorRail.Stamp does not resolve — nothing records WHEN " +
                             "the host left, and the tail can only carry what was captured at StartTravel";
            else
            {
                var stampCalls = Program.Callees(stamp, game).ToList();
                if (levelTiming != null && !stampCalls.Any(c => c.MetadataToken == levelTiming.MetadataToken &&
                                                                c.Module == levelTiming.Module))
                    yield return "L460 departure-unstamped: DepartureAnchorRail.Stamp never reads GeoLevelController.Timing " +
                                 "— the departure is being stamped in some other epoch, and only the level clock is shared";
                if (range != null && !stampCalls.Any(c => c.MetadataToken == range.MetadataToken && c.Module == range.Module))
                    yield return "L460 range-unstamped: DepartureAnchorRail.Stamp never reads GeoVehicle.RangeRemaining — " +
                                 "the client's own NavigateRoutine:116 subtracts from whatever it happens to hold, so the " +
                                 "covered range leaf keeps landing on a different number and the CRC backstop keeps " +
                                 "reporting the subtree DIVERGED";
                var travelPatch = mod.GetType("Multiplayer.Network.Sync.TravelRepaintPatch");
                var postfix = travelPatch?.GetMethod("Postfix", All);
                if (postfix == null || !Program.Callees(postfix, mod, directCallsOnly: true)
                        .Any(c => c.MetadataToken == stamp.MetadataToken && c.Module == stamp.Module))
                    yield return "L460 departure-unstamped: VehicleSync.TravelRepaintPatch.Postfix does not call " +
                                 "DepartureAnchorRail.Stamp — the stamp must be taken on the StartTravel edge itself, not " +
                                 "behind MissionSync's site-parked capture, which bails on CurrentSite == null and so " +
                                 "misses exactly the mid-route foreign departures this law exists for";
            }

            var snapshot = typeof(DepartureAnchorRail).GetMethod("Snapshot", All);
            var emit = typeof(DiffEngine).GetMethod("Emit", All);
            if (snapshot == null || emit == null ||
                !Program.Callees(emit, mod, directCallsOnly: true)
                    .Any(c => c.MetadataToken == snapshot.MetadataToken && c.Module == snapshot.Module))
                yield return "L460 anchor-never-shipped: DiffEngine.Emit does not call DepartureAnchorRail.Snapshot — the " +
                             "host stamps its departures and keeps them, so every client is still deriving foreign " +
                             "aircraft motion from its own clock";

            var reader = applier.GetMethod("ApplyDepartureGenerationTail", All);
            if (reader == null || stampMap == null || !Program.ReadsField(reader, stampMap))
                yield return "L460 anchor-never-installed: GenericApplier.ApplyDepartureGenerationTail does not write " +
                             "_departureAnchors — the tail section arrives and is dropped, which is indistinguishable on " +
                             "the wire from a host that never sent it";
        }
    }
}
