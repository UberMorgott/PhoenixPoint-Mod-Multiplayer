using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L565 — THE DEPARTURE CATCH-UP IS A DURATION THE HOST MEASURED, NEVER TWO CLOCKS SUBTRACTED ACROSS PEERS.
    ///
    /// L460 established that a re-seeded leg must start from the host's departure, not from local now. It got
    /// the mechanism half right: the host stamped its LEVEL clock, shipped the absolute reading, and the
    /// client computed <c>ownNow - hostStamp</c>. Both peers do hold that clock "equal" — but equality is a
    /// re-latch on pause/scale/drift, not an identity, and what is left over is a REAL-TIME phase error
    /// multiplied by <c>Timing.Scale</c>.
    ///
    /// MEASURED, client-2 Player.log 2026-08-18, one line apart:
    ///   <c>[MP][clockphase] host=64566046717.200 client=64566046274.894 dGame=-442.306 dReal=-0.123 scale=3600.00</c>
    ///   <c>[MP][rail] nav anchor SKIPPED V#4@d31c78b9… — elapsed=-140s route=6757s</c>
    /// 0.123 real seconds of lag is 442 GAME seconds at geoscape scale, and the quantity the catch-up wants
    /// to recover — one rail round trip x Scale — is that same fraction of a real second. The signal was the
    /// size of the instrument's noise, so its SIGN was random: 21 re-seeds, 16 refused, 9 negative and 7
    /// exactly 0.000 (those given from a PAUSED geoscape, <c>dReal=paused scale=0.00</c>, where the two
    /// readings are bit-identical), 5 accepted. Widening the guard would have been the wrong repair — it
    /// admits a number that never described the route and misplaces the aircraft.
    ///
    /// So the subtraction moved to the host, where both terms are the SAME clock, and only the difference
    /// crosses the wire. This law is what keeps it there.
    ///
    /// Falsify: read <c>GeoLevelController.Timing</c> back inside <c>AnchorToHostDeparture</c> →
    /// <c>client-subtracts-again</c>; drop the host's clock read at emit → <c>host-ships-a-raw-epoch</c>;
    /// let <c>CoveredSeconds</c> admit a negative, a NaN, or a value past the route →
    /// <c>guard-admits-nonsense</c>; make it reject an exact zero → <c>guard-refuses-a-standing-start</c>.
    /// </summary>
    internal static class L565_TheDepartureCatchUpIsAHostMeasuredDuration
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var applier = typeof(GenericApplier);
            var mod = applier.Assembly;
            var game = typeof(GeoLevelController).Assembly;
            var anchorMethod = applier.GetMethod("AnchorToHostDeparture", All);
            var decide = applier.GetMethod("CoveredSeconds", All);
            var snapshot = typeof(DepartureAnchorRail).GetMethod("Snapshot", All);
            var emit = typeof(DiffEngine).GetMethod("Emit", All);
            var levelTiming = typeof(GeoLevelController).GetProperty("Timing", All)?.GetGetMethod(true);

            // THE NATIVE PREMISE. Every arm below is stated in terms of who reads the game's level clock —
            // the one clock TimeAnchor holds equal across peers. If the game stops offering it, this law is
            // asserting about a shape that no longer exists and must say so rather than pass.
            if (levelTiming == null)
            {
                yield return "L565 premise-changed: GeoLevelController.Timing is gone — there is no level clock " +
                             "left for either peer to read, so 'the catch-up is a duration the host measured' " +
                             "is unenforceable as written";
                yield break;
            }
            if (anchorMethod == null || decide == null || snapshot == null || emit == null)
            {
                yield return "L565 seam-gone: " +
                             (anchorMethod == null ? "GenericApplier.AnchorToHostDeparture"
                              : decide == null ? "GenericApplier.CoveredSeconds"
                              : snapshot == null ? "DepartureAnchorRail.Snapshot"
                              : "DiffEngine.Emit") +
                             " does not resolve — there is nothing left to hold the catch-up inside one frame " +
                             "of reference, and a cross-peer clock subtraction can return silently";
                yield break;
            }

            // (a) THE CLIENT NEVER SUBTRACTS. The level clock is exactly what must NOT appear here: reading it
            // is the only way to turn the host's value back into "my now minus something", which is the defect.
            if (Program.Callees(anchorMethod, game)
                       .Any(c => c.MetadataToken == levelTiming.MetadataToken && c.Module == levelTiming.Module))
                yield return "L565 client-subtracts-again: AnchorToHostDeparture reads GeoLevelController.Timing — the " +
                             "catch-up is measuring this peer's clock against a foreign one again, and the host↔client " +
                             "phase error (0.123 real s = 442 game s at scale 3600) is larger than the leg time it is " +
                             "trying to recover, so the sign of the answer is noise";

            // (b) THE HOST DOES. Snapshot takes the host's reading as an argument, and Emit is where that
            // reading is taken — both halves named, because either alone is satisfied by the broken shape
            // (a parameterless Snapshot shipping the raw epoch, or an Emit that reads a clock it never uses).
            var snapParams = snapshot.GetParameters();
            if (snapParams.Length != 1 || snapParams[0].ParameterType != typeof(double))
                yield return "L565 host-ships-a-raw-epoch: DepartureAnchorRail.Snapshot takes no host clock reading — " +
                             "it can only be shipping the stored departure epoch, which is a number no other peer can " +
                             "interpret without subtracting its own clock from it";
            if (!Program.Callees(emit, game)
                        .Any(c => c.MetadataToken == levelTiming.MetadataToken && c.Module == levelTiming.Module))
                yield return "L565 host-ships-a-raw-epoch: DiffEngine.Emit never reads GeoLevelController.Timing — the " +
                             "difference is not being taken on the host, so whatever rides the tail is not a duration";

            // (c) THE WIRE TYPE. Two structs, deliberately: Anchor is the host's stored epoch and Covered is the
            // duration. A client map typed Anchor is a client reading an epoch, whatever the field is called.
            var clientMap = applier.GetField("_departureAnchors", All);
            var coveredType = typeof(DepartureAnchorRail).GetNestedType("Covered", All);
            if (coveredType == null)
                yield return "L565 wire-type-gone: DepartureAnchorRail.Covered does not exist — the epoch and the " +
                             "duration are back to sharing one type, and nothing distinguishes them on the wire";
            else if (clientMap == null || !clientMap.FieldType.IsGenericType ||
                     !clientMap.FieldType.GetGenericArguments().Contains(coveredType))
                yield return "L565 client-holds-an-epoch: GenericApplier._departureAnchors is not a map of " +
                             "DepartureAnchorRail.Covered — the client is storing the host's absolute departure " +
                             "reading, which it has no shared epoch to interpret";

            // (d) THE GUARD, EXECUTED. Both directions: nonsense must be refused, and a legitimate standing
            // start must NOT be. Zero is the exact answer for an order the host issued in the batch that
            // carries it — every order a player gives from a paused geoscape — and refusing it was 7 of the
            // 16 refusals measured.
            Func<bool, double, double, double> f = (have, hostCovered, route) =>
                (double)decide.Invoke(null, new object[] { have, hostCovered, route });
            var refuse = new[]
            {
                new { Have = true,  Covered = -0.001, Route = 600.0, What = "a negative covered time",
                      Why = "no host clock can measure one, so the tail is unreadable and honouring it flies the leg backwards" },
                new { Have = true,  Covered = -442.0, Route = 600.0, What = "the old cross-peer clock error",
                      Why = "this is the exact shape the 2026-08-18 session produced 9 times; admitting it places the aircraft behind its own departure point" },
                new { Have = true,  Covered = 600.001, Route = 600.0, What = "a covered time past the whole route",
                      Why = "that value belongs to a journey already finished, and honouring it parks the aircraft on its destination the instant a new order lands" },
                new { Have = true,  Covered = double.NaN, Route = 600.0, What = "NaN",
                      Why = "this is what the host writes when it has no started geoscape to measure against, and a reject-test written as a pair of comparisons lets it through every one" },
                new { Have = false, Covered = 240.0, Route = 600.0, What = "a leg with no host stamp at all",
                      Why = "a save-transfer join, or a host too old to ship the tail — it would place the aircraft by arithmetic on an unset field" },
                new { Have = true,  Covered = 240.0, Route = 0.0, What = "a route of zero length",
                      Why = "there is no leg to fast-forward along, so the walk below the guard divides the covered time by nothing" },
            };
            foreach (var k in refuse)
            {
                double got = -1.0; string threw = null;
                try { got = f(k.Have, k.Covered, k.Route); }
                catch (Exception ex) { threw = ex.Message; }
                if (threw != null) { yield return "L565 guard-threw: " + k.What + " → " + threw; continue; }
                if (got >= 0.0)
                    yield return "L565 guard-admits-nonsense: CoveredSeconds accepts " + k.What + " (returned " +
                                 got.ToString("R") + ") — " + k.Why;
            }

            var accept = new[]
            {
                new { Covered = 0.0,   Route = 600.0, What = "a standing start",
                      Why = "the host issued the order in the batch that carries it, which on a paused geoscape is every order a player gives; refusing it was 7 of the 16 refusals measured, and honouring it costs one placement at t=0 (the aircraft's own position) while seeding the host's RangeRemaining" },
                new { Covered = 240.0, Route = 600.0, What = "an ordinary mid-leg catch-up",
                      Why = "this is the whole purpose of the anchor, and refusing it leaves the peer a round trip behind for the rest of the flight" },
                new { Covered = 600.0, Route = 600.0, What = "a leg the host has exactly finished",
                      Why = "the boundary is reachable and places the aircraft on the destination, which is where the host is; excluding it would refuse the one case the arrival snap has nothing left to correct" },
            };
            foreach (var k in accept)
            {
                double got = double.NaN; string threw = null;
                try { got = f(true, k.Covered, k.Route); }
                catch (Exception ex) { threw = ex.Message; }
                if (threw != null) { yield return "L565 guard-threw: " + k.What + " → " + threw; continue; }
                if (Math.Abs(got - k.Covered) > 1e-9)
                    yield return "L565 guard-refuses-a-standing-start: CoveredSeconds does not pass " + k.What +
                                 " through (returned " + got.ToString("R") + ", expected " + k.Covered.ToString("R") +
                                 ") — " + k.Why;
            }
        }
    }
}
