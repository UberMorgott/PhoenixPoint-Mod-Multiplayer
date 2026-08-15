using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RailSim
{
    /// <summary>
    /// ENGINE-FREE DETERMINISTIC SIMULATION over the mod's rail + window journal.
    ///
    /// WHAT THIS CANNOT PROVE — stated first because it is the thing a reader must not forget:
    /// IT TESTS THE MOD'S MODEL, NOT THE GAME. It will not catch a wrong Harmony seam, a patch that does
    /// not bind, a native method that behaves differently from the fake, or anything at all about the
    /// Unity hierarchy — the same limitation L512 and L514 already admit in their own headers. It is
    /// therefore PAIRED with recorded-trace replay from a real session (TraceReplay.cs), so the model is
    /// exercised against real inputs and not only against generated ones.
    ///
    /// The feasibility condition for deterministic simulation testing is INJECTABLE CLOCK AND TRANSPORT,
    /// not "can you boot the engine" (TigerBeetle VOPR, WarpStream, Antithesis). The rail and the journal
    /// are pure C# and meet it: RailMeta's digest clock is already a BCL Stopwatch chosen precisely so a
    /// headless harness can execute the codec in-process (src/Rail/RailMeta.cs:1449-1453).
    ///
    /// Every scenario asserts an OBSERVABLE HISTORY — the ordered list of what each peer presented — never
    /// the shape of a seam. Runs are seeded and reproducible from the seed alone.
    /// </summary>
    internal static class Program
    {
        private static readonly string[] InstallProbes =
        {
            Environment.GetEnvironmentVariable("PhoenixPointDir"),
            @"D:\Steam\steamapps\common\Phoenix Point",
            @"C:\Steam\steamapps\common\Phoenix Point",
            @"E:\Steam\steamapps\common\Phoenix Point",
        };

        private static string _managed = InstallProbes
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.Combine(p, @"PhoenixPointWin64_Data\Managed"))
            .FirstOrDefault(Directory.Exists) ?? "";

        private static int Main(string[] args)
        {
            var m = Array.IndexOf(args, "--managed");
            if (m >= 0 && m + 1 < args.Length) _managed = args[m + 1];
            if (!Directory.Exists(_managed))
            {
                Console.Error.WriteLine("RailSim: Phoenix Point not found. Pass --managed \"X:\\...\\Managed\" " +
                                        "or set the PhoenixPointDir environment variable.");
                return 2;
            }
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var p = Path.Combine(_managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            int seed = 1;
            var si = Array.IndexOf(args, "--seed");
            if (si >= 0 && si + 1 < args.Length) int.TryParse(args[si + 1], out seed);
            try { return Run(seed); }
            catch (Exception ex) { Console.Error.WriteLine("RailSim CRASHED: " + ex); return 2; }
        }

        // NoInlining for the same reason RailCheck.Program.Run has it: the JIT resolves a method's type
        // references on entry, so every game type must stay out of Main until the resolver is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(int seed)
        {
            var failures = new List<string>();
            int ran = 0;
            foreach (var scenario in Scenarios.All())
            {
                ran++;
                try { failures.AddRange(scenario.Value(seed)); }
                catch (Exception ex)
                {
                    failures.Add(scenario.Key + " CRASHED: " + ex.GetType().Name + " (" +
                                 ex.Message.Replace("\r", "").Replace("\n", " ") + "). A scenario that " +
                                 "threw proved NOTHING and is not a pass.");
                }
            }
            if (failures.Count > 0)
            {
                foreach (var f in failures) Console.Error.WriteLine("  " + f);
                Console.Error.WriteLine("RAILSIM RED — scenarios=" + ran + " failures=" + failures.Count +
                                        " seed=" + seed);
                return 1;
            }
            Console.WriteLine("RAILSIM GREEN — scenarios=" + ran + "/" + ran + " failures=0 seed=" + seed);
            return 0;
        }
    }

    /// <summary>THE INJECTED CLOCK. Nothing in a scenario reads a wall clock: time only moves when a
    /// scenario says so, which is what makes an armed-timer property (a gap self-releasing) assertable
    /// without sleeping.</summary>
    internal sealed class SimClock
    {
        internal float Now { get; private set; }
        internal void Advance(float seconds) { Now += seconds; }
    }

    /// <summary>THE INJECTED TRANSPORT. An in-memory host→peers link with seeded per-message delay, so a
    /// run reorders identically for identical seeds and differently for different ones. Delivery is by
    /// (dueAt, sequence) — never by insertion order — so out-of-order arrival is the DEFAULT shape a
    /// scenario sees, which is exactly the 363 ms inter-channel skew that produced P1 in the field.</summary>
    internal sealed class SimNet
    {
        private sealed class Pending
        {
            internal int To; internal float DueAt; internal long Order; internal byte[] Payload;
        }

        private readonly List<Pending> _inFlight = new List<Pending>();
        private readonly Random _rng;
        private readonly SimClock _clock;
        private long _order;

        internal SimNet(int seed, SimClock clock) { _rng = new Random(seed); _clock = clock; }

        /// <summary>Maximum delay a message may take, in seconds. 0.4 covers the measured 363 ms skew.</summary>
        internal float MaxDelaySeconds = 0.4f;

        internal void Send(int toPeer, byte[] payload)
        {
            _inFlight.Add(new Pending
            {
                To = toPeer,
                DueAt = _clock.Now + (float)(_rng.NextDouble() * MaxDelaySeconds),
                Order = _order++,
                Payload = payload,
            });
        }

        /// <summary>Everything due at or before the clock's current value, in delivery order, removed from
        /// flight. Ties break on send order so a run is a pure function of the seed.</summary>
        internal List<KeyValuePair<int, byte[]>> Drain()
        {
            var due = _inFlight.Where(p => p.DueAt <= _clock.Now)
                               .OrderBy(p => p.DueAt).ThenBy(p => p.Order).ToList();
            foreach (var p in due) _inFlight.Remove(p);
            return due.Select(p => new KeyValuePair<int, byte[]>(p.To, p.Payload)).ToList();
        }

        internal int InFlightCount { get { return _inFlight.Count; } }
    }
}
