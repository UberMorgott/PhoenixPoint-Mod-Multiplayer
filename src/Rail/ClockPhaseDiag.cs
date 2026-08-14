using System;
using System.Globalization;
using System.IO;
using System.Text;
using Base.Core;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE MEASUREMENT, and nothing else (surface 0xC0). It fixes no lag; it makes the lag a number.
    ///
    /// WHY IT EXISTS. <see cref="TimeAnchor"/>:76-99 records the failure this file answers: the client's
    /// geoscape clock sits a publish lag behind the host's and NOTHING can see it, because
    /// <see cref="TimeAnchor.EnforceDrift"/> compares the client against its OWN anchor derivation and never
    /// against the host. Both sides therefore agree with the anchor while disagreeing with each other, and
    /// all three peer logs of the 2026-08-04 23:22 three-instance session contain not one TimeAnchor line
    /// while the bias was present the whole time. Ten fix attempts have been aimed at a quantity nobody had
    /// ever read. This reads it.
    ///
    /// THE SHAPE. The host ships its own clock reading on a DEDICATED surface once a second; the client
    /// samples its own clock at receipt and prints both, their difference in game seconds, that difference
    /// divided by the live rate (i.e. in REAL seconds), the rate itself, and how long ago each side last
    /// latched. One line, one prefix — grep <c>[MP][clockphase]</c>.
    ///
    /// The three candidate explanations are meant to be told apart BY EYE from that one line:
    ///   (a) a constant REAL-TIME offset  → <c>dReal</c> is flat and <c>dGame</c> tracks <c>scale</c>.
    ///   (b) an offset that scales with the geoscape speed setting → <c>dReal</c> itself grows with
    ///       <c>scale</c> (the compensation is mispriced, not merely late).
    ///   (c) an offset that accumulates between latches → <c>dGame</c> and <c>dReal</c> both grow with
    ///       <c>sinceLatch</c> and SNAP back the sample after <c>sinceLatch</c> resets.
    ///
    /// WHY A SURFACE OF ITS OWN AND NOT A FIELD ON THE ANCHOR. TimeAnchor deliberately rides the game's own
    /// <c>TimingInstanceData</c>, which has no field for a timestamp (that is the very sentence at
    /// TimeAnchor.cs:97-100). Widening it would change what the anchor puts on the wire in a NORMAL session,
    /// which is exactly what a measurement may not do. This surface is separate, is written by nothing but
    /// <see cref="Debug.Log"/>, and is not sent at all unless <see cref="MpDiag.On"/>.
    ///
    /// WHAT THIS CANNOT SEE — read this before believing a number:
    ///   • It cannot separate the ANCHOR's bias from the PROBE's own one-way flight. The reading is
    ///     (client clock at receipt) − (host clock at send), so it over-reads by however long this packet
    ///     was in the air. That leg was 16-24 ms on the 2026-08-04 loopback against a 112 ms apply-to-apply
    ///     figure, so it is small HERE and is not small on a real link — a reading of a few tens of ms on
    ///     the internet may be entirely this and no bias at all. There is no ping/pong here to subtract it
    ///     (TimeAnchor.cs:37-40 refused that estimator; this file does not smuggle it back in).
    ///   • It is a CLIENT-side reading of a HOST-side quantity, so it is silent about anything the host
    ///     itself gets wrong, and silent on the host's own log.
    ///   • It says nothing about WHY. It cannot see whether the residual is the walk, the anchor's
    ///     compensation term, the network, or a fourth thing — only how big it is and how it moves.
    ///   • It samples at 1 Hz. A bias that only exists for a few frames after a speed change is invisible
    ///     to it, and a latch that happens between two samples is seen only as <c>sinceLatch</c> resetting.
    ///   • It measures the LEVEL clock. Every actor clock hangs off that one's accrual (TimeAnchor.Rebase),
    ///     so a divergence introduced below the level clock is not in this number.
    ///
    /// No SurfaceSeq: the transport is per-peer ordered, and a stale probe would in any case be one bad
    /// sample in a log, not a state write. Nothing here is applied, nothing is remembered, nothing resets.
    /// </summary>
    internal static class ClockPhaseDiag
    {
        /// <summary>1 Hz — the same cadence <see cref="TimeSync.ClientTick"/> already runs the drift check
        /// at, and fast enough that the (c) shape's saw-tooth is legible between latches. Faster would put
        /// the probe's own traffic into the thing it is measuring.</summary>
        private const float ProbeIntervalSeconds = 1f;

        private static float _nextProbeAt;

        /// <summary>HOST: ship the clock reading. Costs one static bool read when the flag is off, which is
        /// the whole point of gating here rather than at the log line.</summary>
        internal static void HostTick(NetworkEngine engine)
        {
            if (!MpDiag.On) return;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (Time.realtimeSinceStartup < _nextProbeAt) return;
            _nextProbeAt = Time.realtimeSinceStartup + ProbeIntervalSeconds;
            try
            {
                var geo = GenericApplier.GeoLevel();
                if (geo == null || geo.Timing == null) return;
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(geo.Timing.Now.TimeSpan.TotalSeconds);          // host clock AT SEND
                    w.Write((double)geo.Timing.EffectiveScale);             // the live rate, host side
                    w.Write((double)TimeAnchor.HostLatchAgeSeconds);        // -1 = never latched
                    inner = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.DiagClockPhaseProbe, SyncKind.StateDelta, inner)));
            }
            catch (Exception ex)
            {
                // Never gated: a diagnostic that fails silently is the bug class this file exists for.
                MpLog.LogError("[MP][clockphase] HOST probe FAILED — the phase error stays unmeasured: " + ex);
            }
        }

        /// <summary>CLIENT: sample the local clock at RECEIPT and print the comparison. Returns true when the
        /// surface was consumed. Reads only — no clock write, no anchor write, no rate write.</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.DiagClockPhaseProbe) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                double hostNow, hostScale, hostLatchAge;
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    hostNow = r.ReadDouble();
                    hostScale = r.ReadDouble();
                    hostLatchAge = r.ReadDouble();
                }
                var geo = GenericApplier.GeoLevel();
                if (geo == null || geo.Timing == null) return true;   // in a battle / mid-load: nothing to compare
                MpLog.Log(PhaseLine(hostNow, geo.Timing.Now.TimeSpan.TotalSeconds, hostScale,
                                    TimeAnchor.ClientAnchorAgeSeconds, hostLatchAge));
            }
            catch (Exception ex) { MpLog.LogError("[MP][clockphase] inbound probe FAILED: " + ex); }
            return true;
        }

        /// <summary>THE LINE, as a pure function of five numbers so RailCheck L153 can execute it over the
        /// three candidate shapes instead of trusting that they would look different.
        ///
        /// <paramref name="scale"/> is the HOST's <c>EffectiveScale</c>, because it is the rate the host's
        /// clock actually advanced at over the leg being measured; on a paused clock there is no rate to
        /// divide by and <c>dReal</c> says <c>paused</c> rather than an infinity. Invariant culture on every
        /// number: a decimal comma in a log the developer greps is a second bug.
        ///
        /// Sign convention: dGame &lt; 0 means the CLIENT IS BEHIND, which is the reported symptom.</summary>
        internal static string PhaseLine(double hostNow, double clientNow, double scale,
                                         double clientAnchorAge, double hostLatchAge)
        {
            var inv = CultureInfo.InvariantCulture;
            double dGame = clientNow - hostNow;
            string dReal = scale > 0.0 ? (dGame / scale).ToString("F3", inv) : "paused";
            return "[MP][clockphase] host=" + hostNow.ToString("F3", inv) +
                   " client=" + clientNow.ToString("F3", inv) +
                   " dGame=" + dGame.ToString("F3", inv) +
                   " dReal=" + dReal +
                   " scale=" + scale.ToString("F2", inv) +
                   " sinceLatch=" + clientAnchorAge.ToString("F1", inv) +
                   " hostLatchAge=" + hostLatchAge.ToString("F1", inv);
        }
    }
}
