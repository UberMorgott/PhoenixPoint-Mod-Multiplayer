using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// ONE always-on frame-cost line, every 10 s, on every peer. Deliberately NOT behind
    /// <see cref="Multiplayer.Network.MpDiag"/>: the whole point is that the NEXT live run answers "which
    /// of the rail's steps costs frame time, and is it the rail at all" without anyone having remembered to
    /// set an environment variable — the 2026-07-31 three-instance session was analysed with
    /// MULTIPLAYER_DIAG unset in every instance, so every <c>[MP][rail] cycle</c> and <c>[MP][uirepaint]</c>
    /// line was off and the client-stutter hunt had nothing but inference to work with.
    ///
    /// The control number is <c>frameMax</c>: the worst <c>Time.unscaledDeltaTime</c> in the same window.
    /// If frameMax spikes while tickMax and the worst step stay flat, the hitch is NOT ours and no amount
    /// of budgeting the walk will touch it. That single comparison is what makes this cheap to keep.
    ///
    /// Cost: two <c>Stopwatch.GetTimestamp()</c> per charged step per frame (tens of ns) and one
    /// <c>Debug.Log</c> per 10 s.
    /// </summary>
    internal static class RailCost
    {
        private const float ReportInterval = 10f;

        private static float _nextAt;
        private static int _frames;
        private static double _tickMaxMs;
        private static double _frameMaxMs;
        private static double _worstStepMs;
        private static string _worstStep = "-";

        /// <summary>Per-step count + total for the window. `worst=` alone cannot tell "one 30 ms repaint
        /// per window" from "thirty of them" — and that RATE difference is exactly the client-vs-host
        /// asymmetry the 2026-08-18 stutter logs could not see (both peers named the same worst= at the
        /// same magnitude while only the client stuttered). Bounded by the fixed set of charged step
        /// names (~a dozen), so the dictionary never grows past that.</summary>
        private struct Acc { public int N; public double SumMs; }
        private static readonly System.Collections.Generic.Dictionary<string, Acc> _steps =
            new System.Collections.Generic.Dictionary<string, Acc>(System.StringComparer.Ordinal);

        /// <summary>Start a step. Paired with <see cref="Charge(string,long)"/>, which returns a fresh
        /// timestamp so consecutive steps chain without a second read.</summary>
        internal static long Now() => Stopwatch.GetTimestamp();

        internal static long Charge(string step, long since)
        {
            long now = Stopwatch.GetTimestamp();
            Charge(step, (now - since) * 1000.0 / Stopwatch.Frequency);
            return now;
        }

        internal static void Charge(string step, double ms)
        {
            if (ms > _worstStepMs) { _worstStepMs = ms; _worstStep = step; }
            Acc a; _steps.TryGetValue(step, out a);
            a.N++; a.SumMs += ms;
            _steps[step] = a;
        }

        /// <summary>End of one SyncEngine.Tick. Emits the window's line when it is due — reporting HERE and
        /// not on a timer keeps the whole class driven by the one loop it measures.</summary>
        internal static void EndFrame(long tickSince)
        {
            double tickMs = (Stopwatch.GetTimestamp() - tickSince) * 1000.0 / Stopwatch.Frequency;
            _frames++;
            if (tickMs > _tickMaxMs) _tickMaxMs = tickMs;
            double frameMs = Time.unscaledDeltaTime * 1000.0;
            if (frameMs > _frameMaxMs) _frameMaxMs = frameMs;

            float now = Time.realtimeSinceStartup;
            if (_nextAt == 0f) { _nextAt = now + ReportInterval; return; }
            if (now < _nextAt) return;
            _nextAt = now + ReportInterval;
            MpLog.Log("[Multiplayer][rail] cost/" + ReportInterval + "s: frames=" + _frames +
                      " tickMax=" + _tickMaxMs.ToString("F1") + "ms frameMax=" + _frameMaxMs.ToString("F1") +
                      "ms worst=" + _worstStep + " " + _worstStepMs.ToString("F1") + "ms" + StepsSuffix());
            _frames = 0; _tickMaxMs = 0; _frameMaxMs = 0; _worstStepMs = 0; _worstStep = "-";
            _steps.Clear();
        }

        /// <summary>" steps: apply n=12 sum=340.1ms; repaint:scrolls n=47 sum=1290.5ms" — every charged
        /// step of the window, heaviest total first, so the RATE of a cost is as visible as its peak.</summary>
        private static string StepsSuffix()
        {
            if (_steps.Count == 0) return "";
            var names = new System.Collections.Generic.List<string>(_steps.Keys);
            names.Sort((a, b) => _steps[b].SumMs.CompareTo(_steps[a].SumMs));
            var sb = new System.Text.StringBuilder(" steps:");
            foreach (var name in names)
            {
                var a = _steps[name];
                sb.Append(' ').Append(name).Append(" n=").Append(a.N)
                  .Append(" sum=").Append(a.SumMs.ToString("F1")).Append("ms;");
            }
            return sb.ToString(0, sb.Length - 1);
        }
    }
}
