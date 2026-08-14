using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Multiplayer.Util;

namespace RailCheck
{
    /// <summary>
    /// L450 — A DISCOVERED SESSION IS ONLY EVER AN ENDPOINT, AND ONLY EVER A WELL-FORMED ONE.
    ///
    /// THE PREMISE. <see cref="LanBeacon"/> is the browser's second feed: a host shouts a datagram on the
    /// local wire and any open browser turns it into a clickable row. The sender is UNAUTHENTICATED — any
    /// process on the LAN can write those bytes — so the parser is a trust boundary, and it is the only one
    /// in this feature: everything downstream (SmartJoinParser → JoinPlan → Direct TCP) is the same path a
    /// hand-typed address takes and cannot tell the two apart.
    ///
    /// PURE BCL, so every arm is EXECUTED against the shipped codec rather than read off its IL.
    ///
    ///   (a) THE VACUITY GUARD, <c>premise-changed</c> — <c>EncodePayload</c> → <c>ParsePayload</c> returns
    ///       the endpoint it started from, for the addresses a real adapter carries. A parser that refuses
    ///       EVERYTHING passes arm (b) perfectly, so without this floor the arm that matters cannot fail.
    ///   (b) <c>malformed-accepted</c> — THE ARM THAT MATTERS. Truncation, a foreign/absent tag, a padded
    ///       oversize packet, a length that overruns the buffer, non-ASCII bytes, one flipped symbol
    ///       (the check symbol's job), and the addresses nobody can dial (0.x, multicast, broadcast) must
    ///       every one of them yield null — and none may throw, because the caller is a socket callback on
    ///       a thread-pool thread where an escaping exception ends the process.
    ///   (c) <c>broadcast-wrong</c> — <c>DirectedBroadcast</c> is <c>ip | ~mask</c> at every prefix length,
    ///       and NEVER 255.255.255.255: the limited broadcast is dropped by exactly the routed/bridged and
    ///       virtual links (ZeroTier, Radmin, Hamachi) this feature exists to cross, so a beacon sent there
    ///       is a beacon nobody hears while the code looks like it works.
    ///
    /// Falsify: drop the length or tag check in ParsePayload → (b); return the limited broadcast for a
    /// degenerate mask → (c); break ConnectCode → (a).
    /// </summary>
    internal static class L450_ADiscoveredSessionIsOnlyEverAnEndpoint
    {
        private const string Magic = "PPMP1 ";

        internal static IEnumerable<string> Check()
        {
            // ═══ (a) THE ROUND TRIP — the vacuity guard, so it runs first ═══
            var endpoints = new[]
            {
                new IPEndPoint(IPAddress.Parse("192.168.1.42"), 14242),
                new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1),
                new IPEndPoint(IPAddress.Parse("172.16.255.254"), 65535),
                new IPEndPoint(IPAddress.Parse("25.77.3.9"), 14242),   // a Hamachi-style virtual adapter
            };
            var broke = new List<string>();
            foreach (var ep in endpoints)
            {
                string how = null;
                try
                {
                    var bytes = LanBeacon.EncodePayload(ep);
                    if (bytes == null) how = "encoded null";
                    else
                    {
                        var back = LanBeacon.ParsePayload(bytes, bytes.Length);
                        if (back == null) how = "parsed null";
                        else if (!back.Equals(ep)) how = "became " + back;
                    }
                }
                catch (Exception e) { how = "threw " + e.GetType().Name; }
                if (how != null) broke.Add(ep + " (" + how + ")");
            }
            if (broke.Count > 0)
            {
                yield return "L450 premise-changed: [" + string.Join(", ", broke.ToArray()) + "] did not " +
                             "survive EncodePayload→ParsePayload. Arm (b) below asks whether the parser " +
                             "REFUSES a stranger's malformed packet, and a parser that refuses everything " +
                             "answers that perfectly — so a broken round trip turns the trust-boundary arm " +
                             "into an arm that cannot fail. Fix the codec before believing anything below.";
                yield break;
            }

            // ═══ (b) EVERYTHING ELSE IS REFUSED, AND NOTHING THROWS ═══
            var good = LanBeacon.EncodePayload(endpoints[0]);
            var oversize = new byte[200];
            for (int i = 0; i < oversize.Length; i++) oversize[i] = (byte)'A';
            Array.Copy(good, oversize, good.Length);

            var nonAscii = (byte[])good.Clone();
            nonAscii[nonAscii.Length - 1] = 0xFF;

            var flipped = (byte[])good.Clone();
            flipped[Magic.Length] = flipped[Magic.Length] == (byte)'7' ? (byte)'8' : (byte)'7';

            var truncated = new byte[good.Length - 1];
            Array.Copy(good, truncated, truncated.Length);

            var foreignTag = Encoding.ASCII.GetBytes("SSDP1 " +
                Encoding.ASCII.GetString(good, Magic.Length, good.Length - Magic.Length));

            var cases = new List<KeyValuePair<string, Func<IPEndPoint>>>
            {
                new KeyValuePair<string, Func<IPEndPoint>>("null buffer",     () => LanBeacon.ParsePayload(null, 0)),
                new KeyValuePair<string, Func<IPEndPoint>>("empty",           () => LanBeacon.ParsePayload(new byte[0], 0)),
                new KeyValuePair<string, Func<IPEndPoint>>("truncated",       () => LanBeacon.ParsePayload(truncated, truncated.Length)),
                new KeyValuePair<string, Func<IPEndPoint>>("foreign tag",     () => LanBeacon.ParsePayload(foreignTag, foreignTag.Length)),
                new KeyValuePair<string, Func<IPEndPoint>>("no tag at all",   () => LanBeacon.ParsePayload(Encoding.ASCII.GetBytes("7F3B-21K-9M4X"), 13)),
                new KeyValuePair<string, Func<IPEndPoint>>("oversize",        () => LanBeacon.ParsePayload(oversize, oversize.Length)),
                new KeyValuePair<string, Func<IPEndPoint>>("length overruns", () => LanBeacon.ParsePayload(good, good.Length + 8)),
                new KeyValuePair<string, Func<IPEndPoint>>("negative length", () => LanBeacon.ParsePayload(good, -1)),
                new KeyValuePair<string, Func<IPEndPoint>>("non-ascii",       () => LanBeacon.ParsePayload(nonAscii, nonAscii.Length)),
                new KeyValuePair<string, Func<IPEndPoint>>("flipped symbol",  () => LanBeacon.ParsePayload(flipped, flipped.Length)),
                new KeyValuePair<string, Func<IPEndPoint>>("0.0.0.0",         () => LanBeacon.ParsePayload(LanBeacon.EncodePayload(new IPEndPoint(IPAddress.Parse("0.0.0.0"), 14242)), Magic.Length + 13)),
                new KeyValuePair<string, Func<IPEndPoint>>("multicast",       () => LanBeacon.ParsePayload(LanBeacon.EncodePayload(new IPEndPoint(IPAddress.Parse("239.1.2.3"), 14242)), Magic.Length + 13)),
                new KeyValuePair<string, Func<IPEndPoint>>("port 0",          () => LanBeacon.ParsePayload(LanBeacon.EncodePayload(new IPEndPoint(IPAddress.Parse("192.168.1.7"), 0)), Magic.Length + 13)),
            };
            foreach (var c in cases)
            {
                string verdict;
                try { var got = c.Value(); verdict = got == null ? null : "accepted it as " + got; }
                catch (Exception e) { verdict = "THREW " + e.GetType().Name; }
                if (verdict != null)
                    yield return "L450 malformed-accepted: LanBeacon.ParsePayload(" + c.Key + ") " + verdict +
                                 ". Every byte here comes from an unauthenticated LAN sender, so the parser " +
                                 "must answer null and only null — and it must never throw, because it runs " +
                                 "on the receive callback's thread-pool thread where a throw ends the process.";
            }

            // ═══ (c) THE BROADCAST IS THE SUBNET'S OWN, NEVER THE LIMITED ONE ═══
            var subnets = new[]
            {
                new[] { "192.168.1.5",  "255.255.255.0",   "192.168.1.255" },
                new[] { "10.4.0.9",     "255.255.0.0",     "10.4.255.255"  },
                new[] { "172.16.3.200", "255.255.255.240", "172.16.3.207"  },
                new[] { "192.168.8.3",  null,              "192.168.8.255" },   // no mask → /24 fallback
            };
            foreach (var s in subnets)
            {
                var got = LanBeacon.DirectedBroadcast(IPAddress.Parse(s[0]),
                                                      s[1] == null ? null : IPAddress.Parse(s[1]));
                if (got == null || got.ToString() != s[2])
                    yield return "L450 broadcast-wrong: DirectedBroadcast(" + s[0] + ", " + (s[1] ?? "no mask") +
                                 ") = " + (got == null ? "null" : got.ToString()) + ", expected " + s[2] +
                                 ". A beacon sent anywhere else is a beacon the intended subnet never hears.";
            }
            var degenerate = LanBeacon.DirectedBroadcast(IPAddress.Parse("192.168.1.5"), IPAddress.Parse("0.0.0.0"));
            if (degenerate != null)
                yield return "L450 broadcast-wrong: a 0.0.0.0 mask produced " + degenerate + " rather than " +
                             "skipping the adapter. 255.255.255.255 is the one address this may never send " +
                             "to — routed and bridged links (ZeroTier, Radmin, Hamachi) drop it, which is " +
                             "silent: the code looks like it is announcing and nobody hears a thing.";
        }
    }
}
