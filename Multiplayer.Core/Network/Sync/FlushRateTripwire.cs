using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE per-channel flush-rate tripwire (BCL-only → unit-testable; RCA 2026-07-08 round 2 safeguard).
    /// A state channel flushing every Tick means some dirty seam is being marked per frame — the exact bug
    /// class (TFTV-defeated flush gate → per-frame SetItems marks) that froze co-op clients SILENTLY: the
    /// storm had no log signature because per-flush logging was (correctly) removed from the hot paths. This
    /// watches the rate instead: sustained &gt; <c>maxPerSec</c> flushes/second for <c>sustainSec</c>
    /// consecutive seconds ⇒ <see cref="OnFlush"/> returns true EXACTLY ONCE (transition-only — the caller
    /// logs one warning line naming the channel), re-armed only after the rate drops back under the
    /// threshold. Time is injected as monotonic ms (no clock reads here — the EditSession convention).
    /// Cost: a dictionary lookup + integer arithmetic per FLUSH (flushes are rare when healthy; under a
    /// storm ~60/s it is still trivial next to the snapshot serialize it is diagnosing).
    /// </summary>
    public sealed class FlushRateTripwire
    {
        private sealed class ChannelWindow
        {
            public long WindowStartMs;
            public int Count;        // flushes inside the current 1s window
            public int HighRunSec;   // consecutive CLOSED windows over the threshold
            public bool Warned;      // already fired for this sustained run
        }

        private readonly int _maxPerSec;
        private readonly int _sustainSec;
        private readonly Dictionary<byte, ChannelWindow> _channels = new Dictionary<byte, ChannelWindow>();

        /// <param name="maxPerSec">Healthy ceiling: a CLOSED 1s window with MORE than this many flushes
        /// counts toward the sustained run.</param>
        /// <param name="sustainSec">Consecutive over-threshold seconds required before firing.</param>
        public FlushRateTripwire(int maxPerSec = 5, int sustainSec = 3)
        {
            _maxPerSec = maxPerSec > 0 ? maxPerSec : 1;
            _sustainSec = sustainSec > 0 ? sustainSec : 1;
        }

        /// <summary>Record one flush of <paramref name="channelId"/> at <paramref name="nowMs"/>. True =
        /// the sustained-storm warning should be logged NOW (fires once per storm, transition-only).</summary>
        public bool OnFlush(byte channelId, long nowMs)
        {
            if (!_channels.TryGetValue(channelId, out var w))
            {
                w = new ChannelWindow { WindowStartMs = nowMs };
                _channels[channelId] = w;
            }

            // Close every elapsed 1s window since the last flush. The first close scores the counted window;
            // any further elapsed windows were EMPTY (0 flushes ≤ threshold) → the run breaks and re-arms.
            if (nowMs - w.WindowStartMs >= 1000)
            {
                CloseWindow(w, w.Count);
                long elapsed = nowMs - w.WindowStartMs;
                if (elapsed >= 2000) CloseWindow(w, 0);   // ≥1 full idle window in the gap → quiet run
                w.WindowStartMs = nowMs - (elapsed % 1000);
                w.Count = 0;
            }

            w.Count++;
            if (!w.Warned && w.HighRunSec >= _sustainSec)
            {
                w.Warned = true;
                return true;
            }
            return false;
        }

        private void CloseWindow(ChannelWindow w, int count)
        {
            if (count > _maxPerSec) w.HighRunSec++;
            else { w.HighRunSec = 0; w.Warned = false; }   // rate dropped → reset + re-arm
        }

        /// <summary>Drop all per-channel state (new session boundary).</summary>
        public void Reset() => _channels.Clear();
    }
}
