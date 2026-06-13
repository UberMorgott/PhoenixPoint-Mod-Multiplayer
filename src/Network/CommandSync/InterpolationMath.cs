using System;

namespace Multipleer.Network.CommandSync
{
    // PURE (Unity-free) frame-rate-independent render-smoothing math. Extracted from
    // ClientVehicleInterpolator so the convergence behaviour can be unit-tested without a Unity
    // dependency (the test project links only Unity-free cores). The interpolator applies the SAME
    // SmoothFactor to a Vector3.Slerp of the vehicle's globe position, so every property proven here
    // (frame-rate independence, no overshoot, exact settle-at-target) transfers to the rendered motion.
    //
    // Model: exponential smoothing. Each frame, move the current value a fraction `factor` of the
    // remaining gap toward the target, where factor = 1 - exp(-k*dt). This is exact frame-rate
    // independence: composing two steps of dt/2 yields the same factor as one step of dt
    // (1-e^(-k*dt) == 1-(1-f1)(1-f2) when f_i=1-e^(-k*dt_i)), so the eased path is identical at any
    // frame rate. It is a strict ease-IN toward the target: factor in [0,1) for k>0,dt>0, so the value
    // approaches but never overshoots, and settles EXACTLY at the target once reached (no extrapolation
    // past the latest host position — the pure-mirror north star).
    internal static class InterpolationMath
    {
        // Fraction of the remaining gap to close THIS frame for smoothing rate k (1/seconds) over dt
        // seconds. Returns a value in [0,1): 0 when dt<=0 or k<=0 (no easing / paused), approaching 1
        // for large k*dt but never reaching it (asymptotic), so it can never overshoot the target.
        public static float SmoothFactor(float k, float dt)
        {
            if (dt <= 0f || k <= 0f) return 0f;
            return 1f - (float)Math.Exp(-k * dt);
        }

        // Scalar ease of `current` toward `target` by SmoothFactor(k,dt). Monotonic, never overshoots,
        // settles exactly at target. The interpolator uses the same factor on a Vector3.Slerp.
        public static float SmoothTowards(float current, float target, float k, float dt)
        {
            return current + (target - current) * SmoothFactor(k, dt);
        }

        // ─── Native travel-motion equation (PURE RENDER reproduction of GeoNavComponent.NavigateRoutine) ───
        //
        // The native flight animates a vehicle along ONE great-circle PathSegment at a UNIFORM time rate
        // (GeoNavComponent.cs:104-119):
        //   distance = GeoMap.Distance(segStart, segEnd)                                   (EarthUnits, :104)
        //   totalTime(sec) = distance.InMeters / (Speed.InMeters / 3600f)                  (TimeUnit,   :105)
        //   num = totalTime.Ratio01(startTime, Timing.Now)                                 (clamped 0..1,:112)
        //   pos = Vector3.Slerp(segStart, segEnd, num)                                     (:117)
        // The client evaluates this SAME equation against its host-slaved clock (Timing.Now mirrored by the
        // 0x34 TimeSync) to reproduce the EXACT arc at the EXACT rate — pure render, no game-logic sim. These
        // helpers are the Unity-free scalar parts (the Vector3.Slerp itself stays in the interpolator); every
        // quantity below is grounded in decompiled source and is SCALE-INVARIANT (globe radius cancels), so it
        // needs only the two world endpoints, the globe center, and the vehicle's EarthUnits speed value.

        public const float EarthRadiusKm = 6371f;   // EarthUnits.EarthRadius (EarthUnits.cs:14); also the
                                                     // GlobeUnits→EarthUnits scale numerator (GlobeUnits.cs:47).

        // Great-circle angle (radians) subtended at the globe centre C by two world points A,B — the exact
        // geometry GeoMap.Distance uses (GeoMap.cs:823-827): acos(clamp(dot(normalize(A-C),normalize(B-C)),
        // -1,1)). Returns 0 for a degenerate/zero-length radius (coincident with the centre) or coincident
        // points. Raw float components so the core stays Unity-free (the interpolator passes Vector3 .x/.y/.z).
        public static float GreatCircleAngleRad(
            float ax, float ay, float az,
            float bx, float by, float bz,
            float cx, float cy, float cz)
        {
            double dax = ax - cx, day = ay - cy, daz = az - cz;
            double dbx = bx - cx, dby = by - cy, dbz = bz - cz;
            double la = Math.Sqrt(dax * dax + day * day + daz * daz);
            double lb = Math.Sqrt(dbx * dbx + dby * dby + dbz * dbz);
            if (la <= 1e-9 || lb <= 1e-9) return 0f;
            double dot = (dax * dbx + day * dby + daz * dbz) / (la * lb);
            if (dot > 1.0) dot = 1.0; else if (dot < -1.0) dot = -1.0;
            return (float)Math.Acos(dot);
        }

        // Native totalTime in SECONDS for a segment of great-circle angle `angleRad` flown at `speedEarthVal`
        // (the EarthUnits.Value of GeoVehicle.Speed, e.g. 400). Derivation (radius cancels exactly):
        //   distance.Value = GlobeUnits(angle*GlobeRadius).ToEarth = angle*GlobeRadius*(6371/GlobeRadius)
        //                  = angle*6371                                  (GlobeUnits.cs:47, GeoMap.cs:827)
        //   totalTime = distance.InMeters/(Speed.InMeters/3600)
        //             = (angle*6371*1000)/(speedVal*1000/3600) = angle*6371*3600/speedVal   (EarthUnits.cs:29)
        // Returns -1 (INVALID → caller falls back to ease) for a non-positive speed; 0 for a zero-angle
        // segment (the native totalTime==Zero case where num is forced to 1, GeoNavComponent.cs:113-116).
        public static float SegmentTotalSeconds(float angleRad, float speedEarthVal)
        {
            if (speedEarthVal <= 0f || float.IsNaN(speedEarthVal)) return -1f;
            if (angleRad <= 0f) return 0f;
            return angleRad * EarthRadiusKm * 3600f / speedEarthVal;
        }

        // Native num = totalTime.Ratio01(startTime, now): Clamp01((now-start)/total) (TimeUnit.cs:110-112).
        // A non-positive totalTime returns 1 — exactly the engine's `if (totalTime == TimeUnit.Zero) num = 1`
        // fast-arrival (GeoNavComponent.cs:113-116). Times are in seconds (TimeUnit→float == TotalSeconds,
        // TimeUnit.cs:100-103); double in, float out for tick precision over long campaign clocks.
        public static float SegmentNum(double startSec, double nowSec, float totalSec)
        {
            if (totalSec <= 0f) return 1f;
            double n = (nowSec - startSec) / totalSec;
            if (n < 0.0) return 0f;
            if (n > 1.0) return 1f;
            return (float)n;
        }

        // Where an authoritative SurfacePos sample falls along the active arc, as a 0..1 ratio: the angle from
        // the segment start to the sample over the full segment angle (both measured at the globe centre).
        // Used by the gentle 0x35 correction to recover the host's TRUE phase without snapping. Clamp01;
        // a zero-length segment maps everything to 1 (already arrived).
        public static float ArcRatioFromAngles(float angStartToSample, float angStartToEnd)
        {
            if (angStartToEnd <= 0f) return 1f;
            float r = angStartToSample / angStartToEnd;
            if (r < 0f) return 0f;
            if (r > 1f) return 1f;
            return r;
        }

        // Gentle, BOUNDED phase correction for segment render (0x35 SurfacePos sample absorption). The
        // authoritative sample implies the host is at `sampleNum` along the arc right now, i.e. its segment
        // truly started at impliedStart = nowSec - sampleNum*total. We exp-blend the rendered segment's
        // startSec a small `factor` toward impliedStart so the equation-driven motion re-locks to host truth
        // smoothly (no visible jump), and CLAMP the per-sample shift to ±maxShiftSec (the clock-sync
        // tolerance) so a transient outlier can never rubber-band the icon. factor in (0,1]; settles (the
        // shift→0 as render phase matches host) and is frame-gap independent (it is keyed off the host
        // sample, not dt). Returns the new startSec.
        public static double CorrectedStartSec(
            double curStartSec, double nowSec, float totalSec,
            float sampleNum, float factor, float maxShiftSec)
        {
            if (totalSec <= 0f) return curStartSec;          // nothing to phase-correct on a zero segment
            if (factor <= 0f) return curStartSec;            // correction disabled
            if (factor > 1f) factor = 1f;
            double impliedStart = nowSec - (double)sampleNum * totalSec;
            double delta = (impliedStart - curStartSec) * factor;
            if (delta > maxShiftSec) delta = maxShiftSec;
            else if (delta < -maxShiftSec) delta = -maxShiftSec;
            return curStartSec + delta;
        }
    }
}
