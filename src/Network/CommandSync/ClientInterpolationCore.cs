namespace Multipleer.Network.CommandSync
{
    // ── INC-C: PURE (Unity-free) snapshot-buffer bracket selector for ClientVehicleInterpolator ──
    //
    // The client is a pure mirror: it NEVER recomputes vehicle movement. Smoothness comes from rendering the
    // host's authoritative position/rotation samples at a fixed delay (renderTime = now − InterpDelay) and
    // interpolating between the two samples that bracket renderTime. This file holds ONLY the timestamp math —
    // which two samples bracket renderTime and at what fraction — so it can be unit-tested without Unity. The
    // actual Vector3.Lerp / Quaternion.Slerp stays in the interpolator (Unity glue).
    //
    // Sample timestamps are a LOCAL arrival stamp (Time.realtimeSinceStartup at apply time): monotonic and
    // pause-independent, so a paused host (no new samples) simply makes renderTime overrun the newest sample →
    // HOLD (no extrapolation), never a freeze-teleport. The stamp is self-consistent (stamp local, render at the
    // same local now − delay), so no clock needs to cross the wire.
    internal static class ClientInterpolationCore
    {
        // How renderTime relates to the buffered samples.
        internal enum SampleMode
        {
            Empty,   // no samples — nothing to place
            Direct,  // exactly one sample, OR renderTime at/before the oldest sample → place that sample raw
            Hold,    // renderTime newer than the newest sample (underrun) → hold the newest sample (NO extrapolation)
            Interp   // renderTime strictly between two samples → lerp/slerp i0→i1 by Frac
        }

        // Result of bracketing renderTime against an ordered (oldest→newest) timestamp window.
        internal struct Bracket
        {
            public SampleMode Mode;
            public int I0;     // lower (older) sample index; the sample to place for Direct/Hold
            public int I1;     // upper (newer) sample index (== I0 for Direct/Hold)
            public float Frac; // [0,1] interpolation fraction from I0 toward I1 (0 for Direct/Hold)
        }

        // Select the bracket for renderTime over times[0..count) which MUST be ascending (oldest→newest).
        //   count<=0          → Empty
        //   count==1          → Direct(0)
        //   renderTime <= oldest → Direct(0)                (full mirror until the buffer fills / clamp to oldest)
        //   renderTime >= newest → Hold(newest)             (underrun: hold last, never extrapolate)
        //   otherwise         → Interp(i0,i1,frac) where times[i0] <= renderTime < times[i1], adjacent.
        // Equal adjacent timestamps (zero gap) degenerate to frac 0 (place i0) — no divide-by-zero.
        // times[] / renderTime are DOUBLE: the host-time keys are the geoscape clock (~6.4e10 game-seconds),
        // where a float32 ULP (~8192 s) exceeds the ~231 s between samples → float keys collapse to one value →
        // renderTime always <= times[0] → Direct every frame → no interpolation (the in-game lag/jerk). double
        // keeps the keys distinct so the bracket is real. Frac stays float (positions/lerp are float; [0,1] is
        // precise in float).
        internal static Bracket Select(double[] times, int count, double renderTime)
        {
            if (times == null || count <= 0)
                return new Bracket { Mode = SampleMode.Empty, I0 = 0, I1 = 0, Frac = 0f };

            if (count == 1)
                return new Bracket { Mode = SampleMode.Direct, I0 = 0, I1 = 0, Frac = 0f };

            int newest = count - 1;

            // Clamp to oldest: renderTime at/before the first sample → place it raw (don't extrapolate backwards).
            if (renderTime <= times[0])
                return new Bracket { Mode = SampleMode.Direct, I0 = 0, I1 = 0, Frac = 0f };

            // Underrun: renderTime at/after the newest sample → hold last (NO forward extrapolation).
            if (renderTime >= times[newest])
                return new Bracket { Mode = SampleMode.Hold, I0 = newest, I1 = newest, Frac = 0f };

            // Find adjacent pair with times[i] <= renderTime < times[i+1]. Buffer is tiny (~12) → linear scan.
            for (int i = 0; i < newest; i++)
            {
                double t0 = times[i];
                double t1 = times[i + 1];
                if (renderTime >= t0 && renderTime < t1)
                {
                    double span = t1 - t0;
                    float frac = span > 0.0 ? (float)((renderTime - t0) / span) : 0f;
                    if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
                    return new Bracket { Mode = SampleMode.Interp, I0 = i, I1 = i + 1, Frac = frac };
                }
            }

            // Unreachable for ascending input (covered by the clamps above); be safe → hold newest.
            return new Bracket { Mode = SampleMode.Hold, I0 = newest, I1 = newest, Frac = 0f };
        }
    }
}
