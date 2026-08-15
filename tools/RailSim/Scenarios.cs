using System;
using System.Collections.Generic;
using System.Linq;

namespace RailSim
{
    internal static class Scenarios
    {
        /// <summary>Every scenario, by name. A scenario returns zero strings when it holds and one
        /// human-readable failure per broken assertion otherwise.</summary>
        internal static IEnumerable<KeyValuePair<string, Func<int, IEnumerable<string>>>> All()
        {
            yield return Pair("seeded-transport-is-reproducible", SeededTransportIsReproducible);
        }

        private static KeyValuePair<string, Func<int, IEnumerable<string>>> Pair(
            string name, Func<int, IEnumerable<string>> body) =>
            new KeyValuePair<string, Func<int, IEnumerable<string>>>(name, body);

        /// <summary>C-requirement: "seeded runs, reproducible from the seed alone". Two SimNets built from
        /// the same seed must deliver the same messages in the same order, and a different seed must be
        /// able to produce a different one — otherwise the harness is not simulating reordering at all and
        /// every later ordering property would be vacuously true.</summary>
        private static IEnumerable<string> SeededTransportIsReproducible(int seed)
        {
            var a = DeliveryOrder(seed);
            var b = DeliveryOrder(seed);
            if (!a.SequenceEqual(b))
                yield return "seeded-transport-is-reproducible: two runs of seed " + seed +
                             " delivered different orders (" + string.Join(",", a) + " vs " +
                             string.Join(",", b) + "). A run that is not a pure function of its seed " +
                             "cannot reproduce a failure from the seed alone.";

            bool anyDifferent = false;
            for (int other = seed + 1; other <= seed + 32 && !anyDifferent; other++)
                anyDifferent = !DeliveryOrder(other).SequenceEqual(a);
            if (!anyDifferent)
                yield return "seeded-transport-is-reproducible: 32 different seeds all produced the " +
                             "delivery order " + string.Join(",", a) + ". The transport is not reordering, " +
                             "so every ordering scenario in this harness would pass without proving " +
                             "anything.";
        }

        /// <summary>Send 8 numbered messages to peer 1 at t=0, let the clock run past the delay ceiling,
        /// and report the order they came out in.</summary>
        private static List<int> DeliveryOrder(int seed)
        {
            var clock = new SimClock();
            var net = new SimNet(seed, clock);
            for (int i = 0; i < 8; i++) net.Send(1, new[] { (byte)i });
            clock.Advance(1.0f);
            return net.Drain().Select(kv => (int)kv.Value[0]).ToList();
        }
    }
}
