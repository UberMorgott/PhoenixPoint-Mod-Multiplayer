using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Multiplayer.Network.Sync;

namespace RailSim
{
    /// <summary>
    /// RECORDED-TRACE REPLAY (§C.2). The generated scenarios exercise the model against inputs the harness
    /// invented; this exercises it against inputs a REAL 3-instance session produced. It is the paired
    /// half of the honesty statement in Program's header: the harness cannot see a wrong Harmony seam, a
    /// patch that does not bind, or the Unity hierarchy — replaying real traffic is what stops the model
    /// drifting away from what the game actually emits.
    ///
    /// Trace format: any log file containing lines the mod itself wrote, matched on
    /// `[MP][windows] journal pos=&lt;n&gt; family=&lt;name&gt;` (the `[Multiplayer]` prefix spelling is
    /// accepted too, because both spellings ship in the mod's own log lines). Nothing else in the file is
    /// read, so a trimmed log and a full one behave identically.
    /// </summary>
    internal static class TraceReplay
    {
        private static readonly Regex Line =
            new Regex(@"\[(?:MP|Multiplayer)\]\[windows\] journal pos=(\d+) family=(\S+)",
                      RegexOptions.Compiled);

        internal static IEnumerable<string> Replay(string path, int seed)
        {
            if (!File.Exists(path))
            {
                yield return "trace-replay: no trace at '" + path + "'. §C.2 makes replay MANDATORY — " +
                             "capture a real 3-instance session's multiplayer*.log into tools/RailSim/traces " +
                             "and pass --trace. A model validated only against generated inputs is the " +
                             "failure mode this pairing exists to prevent.";
                yield break;
            }

            var recorded = new List<KeyValuePair<uint, string>>();
            foreach (var raw in File.ReadLines(path))
            {
                var m = Line.Match(raw);
                uint pos;
                if (m.Success && uint.TryParse(m.Groups[1].Value, out pos))
                    recorded.Add(new KeyValuePair<uint, string>(pos, m.Groups[2].Value));
            }
            if (recorded.Count == 0)
            {
                yield return "trace-replay: '" + path + "' contains no '[MP][windows] journal pos=' lines. " +
                             "Either the trace predates the journal or the log line was renamed; a replay " +
                             "over zero records is a pass nobody earned.";
                yield break;
            }

            // Feed the recorded stream through the injected transport, so real positions meet simulated
            // reordering — the exact combination that produced P1 in the field.
            var clock = new SimClock();
            var net = new SimNet(seed, clock);
            WindowJournal.Reset();
            foreach (var rec in recorded)
            {
                var name = Encoding.UTF8.GetBytes(rec.Value);
                var frame = new byte[4 + name.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(rec.Key), 0, frame, 0, 4);
                Buffer.BlockCopy(name, 0, frame, 0 + 4, name.Length);
                net.Send(0, frame);
            }
            clock.Advance(net.MaxDelaySeconds + 1.0f);
            foreach (var msg in net.Drain())
                WindowJournal.Append(BitConverter.ToUInt32(msg.Value, 0),
                                     Encoding.UTF8.GetString(msg.Value, 4, msg.Value.Length - 4),
                                     msg.Value);

            var presented = new List<uint>();
            JournalEntry e;
            while (WindowJournal.TryRead(out e)) presented.Add(e.Pos);
            WindowJournal.Reset();

            var expected = recorded.Select(r => r.Key).Distinct().OrderBy(x => x).ToList();
            if (!presented.SequenceEqual(expected))
                yield return "trace-replay: replaying " + recorded.Count + " recorded raises through the " +
                             "reordering transport presented [" + string.Join(",", presented.Take(20)) +
                             "…] but the host minted [" + string.Join(",", expected.Take(20)) + "…]. " +
                             "Real traffic must present in the host's order exactly as generated traffic " +
                             "does.";
        }
    }
}
