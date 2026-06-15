using System;

namespace Multipleer.Network.TimeSync
{
    /// <summary>
    /// Pure, Unity-FREE math for the anchor+rate authoritative geoscape clock (unit-testable).
    ///
    /// The client NEVER accumulates its own game-time. It DERIVES it as a pure function of the last
    /// host anchor + the shared timebase:
    ///   <c>gameTime(serverNow) = gAnchor + rate * (serverNow - tAnchor)</c>,
    /// where <c>serverNow = localRT + clockOffset</c> and <c>rate = paused ? 0 : PresetTimes[speedIndex]</c>.
    /// Because each <c>gAnchor</c> is pinned at the EXACT change instant, the piecewise-linear curve is
    /// continuous across pause/play/speed toggles — no feedback loop, nothing to oscillate.
    ///
    /// A separate VISUAL layer smooths only offset re-estimation / join steps: the derived
    /// <c>auth</c>oritative time is exact; the DISPLAYED clock lerps toward it (~100–200 ms), forward-
    /// monotone, and hard-sets across a large gap. Truth snaps, picture smooths.
    /// </summary>
    public static class AnchorClock
    {
        /// <summary>
        /// Authoritative derived game-time (seconds): <c>gAnchor + rate*(serverNow - tAnchor)</c>.
        /// rate==0 (paused) ⇒ constant <c>gAnchor</c> regardless of serverNow.
        /// </summary>
        public static double Derive(double gAnchorSeconds, double rate, double tAnchorSeconds, double serverNow)
            => gAnchorSeconds + rate * (serverNow - tAnchorSeconds);

        /// <summary>
        /// Smoothing factor for an exponential approach with time-constant <paramref name="tau"/> at a
        /// frame delta <paramref name="dt"/>: <c>k = 1 - exp(-dt/tau)</c>, clamped to [0,1].
        /// A non-positive tau means "snap" (k=1). dt&lt;=0 means "no progress" (k=0).
        /// </summary>
        public static double LerpFactor(double dt, double tau)
        {
            if (tau <= 0.0) return 1.0;
            if (dt <= 0.0) return 0.0;
            double k = 1.0 - Math.Exp(-dt / tau);
            if (k < 0.0) return 0.0;
            if (k > 1.0) return 1.0;
            return k;
        }

        /// <summary>
        /// Advance the DISPLAYED game-time one frame toward the authoritative <paramref name="auth"/>:
        ///   • |auth - display| &gt; <paramref name="snapThreshold"/> ⇒ HARD-SET (display = auth):
        ///     first frame after join/reconnect, or a large offset/clock-jump step — no lerp across a big gap.
        ///   • else lerp: <c>display + (auth - display)*k</c>.
        ///   • FORWARD-MONOTONE clamp (only while <paramref name="rate"/> &gt; 0): a small backward
        ///     correction must not visibly rewind a RUNNING clock — never return a value below the current
        ///     <paramref name="display"/> (hold instead). When paused (rate == 0) the clamp is OFF so the
        ///     display can settle DOWN to the frozen truth (spec §7) with no sub-frame forward bias.
        /// </summary>
        public static double VisualStep(double display, double auth, double k, double snapThreshold, double rate)
        {
            double gap = auth - display;
            if (Math.Abs(gap) > snapThreshold)
                return auth; // hard-set across a large gap (forward OR backward)

            double next = display + gap * k;
            // Forward-monotone (running only): never visibly rewind on a small backward correction.
            if (rate > 0.0 && next < display)
                return display;
            return next;
        }
    }
}
