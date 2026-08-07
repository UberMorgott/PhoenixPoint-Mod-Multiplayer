using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.UI;

namespace RailCheck
{
    /// <summary>
    /// L159 — THE PLAYER PANEL REPORTS, AND WHAT IT CANNOT REPORT IT SAYS OUT LOUD.
    ///
    /// The co-op player panel (name | ping | status) is a presentation seam, and L158's seam set already
    /// asserts the things a seam must NOT do — no block, no leaf write, no throw into game code, no
    /// latency reading reachable from the rail. This law asserts the other half, which no arm of L158
    /// covers: that the panel is actually FED, and that a gap in the feed arrives on screen as a gap.
    ///
    /// WHY IT IS ITS OWN FILE RATHER THAN A SIXTH L158 ARM. L158 quantifies over a SET of seams and asks
    /// one question of each ("does this seam alter anything?"). Everything below is specific to one
    /// feature's data path — a wire block, a staleness bar, a threshold table — and would have to be
    /// special-cased out of every one of L158's set-wide sweeps. Same reason L119 is not an arm of L91.
    ///
    /// EVERY ARM IS EXECUTED, because all three failures are silent ones — this repo's dominant bug class.
    /// A dropped status block does not log; it renders every remote player as permanently not-ready. A
    /// stale SRTT does not log either; it renders as a perfectly plausible number for a player who left
    /// the room ten minutes ago, which is worse than admitting the gap.
    ///
    ///   (a) STATUS SURVIVES THE ROSTER. The two per-peer flags the panel's status column is made of
    ///       (<c>Paused</c> = dropped out, <c>TacReady</c> = says they are done moving) go through
    ///       SerializePeerList → DeserializePeerList and come back unchanged, in both states. They ride the
    ///       roster because the roster is the message that already carries every other per-peer fact; a
    ///       silently truncated trailing block is exactly how that ride goes wrong.
    ///   (b) NO SAMPLE IS NEGATIVE, ALWAYS (law L145). <c>PingTable.GetPingMs</c> answers a never-measured
    ///       peer, and a peer whose last sample has aged past the staleness bar, with a NEGATIVE number —
    ///       the panel's only "render the em-dash" signal. If this ever returned a plausible-looking 0 the
    ///       panel would draw one bar and a millisecond count for a peer nobody has heard from, and the
    ///       whole point of the column is that it never lies about that.
    ///   (c) THE BARS MEAN WHAT THE RECON SAID. <c>PlayerPanel.BarsFor</c> at every boundary: 59 → 4,
    ///       60 → 3, 119 → 3, 120 → 2, 249 → 2, 250 → 1. Off-by-one here is invisible to a reader and
    ///       obvious to a player watching a green meter on a link that stutters.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • delete the status block from SerializePeerList → <c>status-not-carried</c>
    ///   • make GetPingMs return 0 instead of -1 for an unknown peer → <c>no-sample-not-negative</c>
    ///   • widen PingTable.StaleAfterMs to never expire → <c>stale-sample-served</c>
    ///   • shift any BarsFor boundary by one → <c>bars-off-the-recon</c>
    /// </summary>
    internal static class L159_PlayerPanelReportsOnly
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var barsFor = typeof(PlayerPanel).GetMethod("BarsFor", All);
            var paused = typeof(PeerListEntry).GetProperty("Paused", All);
            var tacReady = typeof(PeerListEntry).GetProperty("TacReady", All);
            var getPing = typeof(PingTable).GetMethod("GetPingMs", All, null,
                                                      new[] { typeof(ulong), typeof(long) }, null);

            if (barsFor == null || paused == null || tacReady == null || getPing == null)
            {
                yield return "L159 premise-changed: PlayerPanel.BarsFor / PeerListEntry.Paused / " +
                             "PeerListEntry.TacReady / PingTable.GetPingMs(ulong,long) did not all resolve. " +
                             "The panel's data path has moved and every arm below would pass vacuously — which " +
                             "is how a law survives the feature it was written for and protects nothing.";
                yield break;
            }

            // ── (a) the status flags survive the roster wire ────────────────────────────────────────
            var sent = new List<PeerListEntry>
            {
                new PeerListEntry { SteamId = 1, PlayerGuid = Guid.NewGuid(), Nickname = "host",
                                    IsHost = true, SlotIndex = 0, Paused = false, TacReady = true },
                new PeerListEntry { SteamId = 2, PlayerGuid = Guid.NewGuid(), Nickname = "gone",
                                    SlotIndex = 1, Paused = true, TacReady = false },
            };
            List<PeerListEntry> back = null;
            string threw = null;
            try { back = MessageSerializer.DeserializePeerList(MessageSerializer.SerializePeerList(sent)); }
            catch (Exception e) { threw = e.GetType().Name + ": " + e.Message; }

            if (threw != null)
                yield return "L159 status-not-carried: the roster round trip THREW (" + threw + "). Every peer " +
                             "would lose the whole roster, not just the status column — a joiner's " +
                             "\"Connecting…\" box is ended by this exact message.";
            else if (back == null || back.Count != sent.Count)
                yield return "L159 status-not-carried: the roster came back with " + (back?.Count ?? -1) +
                             " row(s) instead of " + sent.Count + ".";
            else
                for (var i = 0; i < sent.Count; i++)
                {
                    if (back[i].Paused != sent[i].Paused)
                        yield return "L159 status-not-carried: row " + i + " went out Paused=" + sent[i].Paused +
                                     " and came back " + back[i].Paused + ". A peer that dropped out renders " +
                                     "as present, which is the one thing the grey marker exists to prevent — " +
                                     "and it fails without a log line on either side.";
                    if (back[i].TacReady != sent[i].TacReady)
                        yield return "L159 status-not-carried: row " + i + " went out TacReady=" +
                                     sent[i].TacReady + " and came back " + back[i].TacReady + ". Every remote " +
                                     "player sits at a red cross for the whole battle however many times they " +
                                     "press the button.";
                }

            // ── (b) no sample is negative, both ways it can happen ──────────────────────────────────
            var table = new PingTable();
            const long t0 = 1_000_000;
            if ((int)getPing.Invoke(table, new object[] { 7UL, t0 }) >= 0)
                yield return "L159 no-sample-not-negative: GetPingMs answered a peer that was never measured " +
                             "with a non-negative number. The panel's ONLY signal for \"render the em-dash\" is " +
                             "the sign of this number (law L145 — a peer with no sample must never render as a " +
                             "measurement, and nothing may wait for one).";

            var stamp = table.StartProbe(t0);
            table.Sample(7UL, stamp, t0 + 40);
            if ((int)getPing.Invoke(table, new object[] { 7UL, t0 + 40 }) != 40)
                yield return "L159 no-sample-not-negative: a FRESH 40 ms sample did not read back as 40. Arm " +
                             "(b) can no longer tell a served reading from a withheld one, so its other half " +
                             "proves nothing.";
            // POSITIVE CONTROL for the staleness bar: the same reading, one tick past 3 x cadence.
            if ((int)getPing.Invoke(table, new object[] { 7UL, t0 + 40 + 3 * PingTable.CadenceMs + 1 }) >= 0)
                yield return "L159 stale-sample-served: a sample older than three probe cadences still reads " +
                             "back as a number. A stale reading that still looks like a measurement is worse " +
                             "than an admitted gap — it is a live-looking latency for a peer that has said " +
                             "nothing for three seconds.";

            // ── (c) the bar thresholds are the recon's ──────────────────────────────────────────────
            foreach (var (ms, want) in new[] { (0, 4), (59, 4), (60, 3), (119, 3), (120, 2), (249, 2),
                                               (250, 1), (5000, 1) })
            {
                var got = (int)barsFor.Invoke(null, new object[] { ms });
                if (got != want)
                    yield return "L159 bars-off-the-recon: BarsFor(" + ms + ") = " + got + ", expected " + want +
                                 ". The meter's boundaries are <60 / <120 / <250 / else; a shifted one paints a " +
                                 "green four-bar link for latency the session is actually struggling with.";
            }
        }
    }
}
