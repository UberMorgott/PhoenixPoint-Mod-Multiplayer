using System;
using System.Collections.Generic;
using Base.Core;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The geoscape clock as an ANCHOR, not a clock value.
    ///
    /// The rail may only carry piecewise-constant state (law 6): <c>Timing.Now</c> changes on every walk, so
    /// putting it on the rail would churn the diff forever. An anchor changes only when the clock's RATE
    /// changes (pause / speed) — between those moments the host re-publishes byte-identical bytes, the diff
    /// emits nothing, and re-delivery is idempotent because there is nothing time-varying to oscillate.
    ///
    /// The host latches {Now, Paused, Scale} into the game's OWN save DTO (<see cref="TimingInstanceData"/>)
    /// and publishes it under rail root "TA". The client applies it through
    /// <c>Timing.ProcessInstanceData</c> — the game's own save-load seam (decompile Base.Core/Timing.cs:222):
    /// it sets _paused/_scale/StartTime, OVERWRITES _ownSetTime, and re-anchors _parentSetTime to the local
    /// clock, while firing no events and rescheduling nothing.
    ///
    /// That overwrite is the whole reason a plain StartTime mirror cannot work. <c>Now = StartTime + OwnNow</c>
    /// (Timing.cs:55) and OwnNow accrues from LOCAL realtime on every peer — M2's client sim is deliberately
    /// NOT frozen (see <see cref="ClientSimGate"/>) — so writing the host's base while the client's own
    /// accrual keeps running double-counts: the clocks keep identical pace and still never agree. Because
    /// ProcessInstanceData replaces the accrual rather than adding to it, the client lands at
    /// <c>Now = anchor</c> and then derives <c>anchor + rate * (realtime since receipt)</c> — the derivation
    /// run by the game itself, not by us. (Timing.StartTime/StartFixedTime are opted out of the ordinary
    /// rail in RailMeta for exactly this reason.)
    ///
    /// The latch is CANONICAL: the whole anchor is collapsed into StartTime with the accrual zeroed. So every
    /// latch necessarily changes StartTime, and a client can never be left applying one stale half of an
    /// anchor — the members that never change (OwnNow = Zero) are already equal on both peers and never need
    /// to ride at all.
    ///
    /// ponytail: anchored on RECEIPT, so the client trails the host by one-way latency (~20-80 ms of game
    /// time at speed 1, proportionally more at max speed). No NTP ping/pong offset estimator — the client's
    /// hourly sim is gated so its clock is presentation, and the error does NOT accumulate (every latch
    /// re-states an ABSOLUTE host time). Add the estimator only if drift is visibly wrong at max speed.
    /// ponytail: ProcessInstanceData does not call the private RescheduleUpdateables, so updateables already
    /// scheduled keep wake times computed against the pre-jump clock — harmless while jumps stay
    /// latency-sized; a large first-apply jump settles on the next scheduled wake. Pause/speed themselves
    /// still ride the ordinary "T" root through Timing's property setters, which DO reschedule and fire the
    /// native events, and they land in the same batch — this class deliberately does not take that over.
    /// The unbounded case is a BACKWARD jump (client ahead of the host, e.g. after a client-local pause the
    /// host never made): a scheduled wake computed against the newer clock is now in the future by the size
    /// of the jump, so research/manufacture ETAs stall until it arrives. Bounded by latch frequency in
    /// practice; if it is ever observed, reschedule explicitly rather than widening this class. Risk R12.
    ///
    /// THIS CLASS DOES NOT DELIVER SYMPTOM (c). Host pause/speed reaching the client in one frame instead of
    /// 0.5 s is DiffEngine.FlushNow + Timing.EffectiveScaleChangedEvent (N3, batch 1). This is clock VALUE
    /// sync — the two were conflated once and the conflation is what made 37af665 look like it had failed.
    /// </summary>
    internal static class TimeAnchor
    {
        private static TimingInstanceData _hostDto;
        private static float _latchedAt;        // local realtime of the latch — the host-side prediction base
        private static TimingInstanceData _clientDto;
        private static float _nextChurnLogAt;
        private static int _latchesSinceLog;

        /// <summary>The clock collapsed into its own save DTO: the entire anchor in StartTime, accrual zeroed.
        /// The host latches this and the client seeds with it, so the members that never ride (OwnNow) start
        /// out equal on both peers instead of at a default the client would silently keep.</summary>
        private static TimingInstanceData Canonical(Timing t) => new TimingInstanceData
        {
            Paused = t.Paused,
            Scale = t.Scale,
            StartTime = t.Now,
            StartFixedTime = t.Now,
            OwnNow = TimeUnit.Zero,
            OwnFixedNow = TimeUnit.Zero,
        };

        /// <summary>Has the host's real clock left the anchor's own prediction? Catches every jump that is
        /// not a pause/speed change — save-load, time skip, any native re-anchor — and, over long unchanged
        /// stretches, ordinary rate error. This is the calibration knob: the model is a model, and the host's
        /// Timing is the truth. Threshold as in the quarry: 5 s of game time, or one diff tick at the current
        /// rate when that is larger.</summary>
        private static bool Drifted(Timing t)
        {
            double rate = t.EffectiveScale; // Paused ? 0 : CumulativeScale — the true d(Now)/d(realtime)
            double predicted = _hostDto.StartTime.TimeSpan.TotalSeconds + rate * (Time.realtimeSinceStartup - _latchedAt);
            return Math.Abs(t.Now.TimeSpan.TotalSeconds - predicted) > Math.Max(5.0, rate * 0.5);
        }

        /// <summary>Host: the "TA" root object. Same values between latches ⇒ the walk encodes identical bytes
        /// ⇒ the diff emits nothing (law 6, zero churn on the 0.5 s tick).</summary>
        internal static TimingInstanceData HostDto(Timing t)
        {
            if (t == null) return null;
            if (_hostDto == null || _hostDto.Paused != t.Paused || _hostDto.Scale != t.Scale || Drifted(t))
            {
                _hostDto = Canonical(t);
                _latchedAt = Time.realtimeSinceStartup;
                ChurnCheck();
            }
            return _hostDto;
        }

        /// <summary>THE runnable check. A latch is an EVENT (pause, speed, load), never a heartbeat: if this
        /// fires, the drift prediction disagrees with the host clock and every client is being jerked back to
        /// a fresh anchor twice a second. That is the one failure this class can have that still looks
        /// perfectly healthy in a log, so it gets stated out loud.</summary>
        private static void ChurnCheck()
        {
            _latchesSinceLog++;
            if (Time.realtimeSinceStartup < _nextChurnLogAt) return;
            if (_nextChurnLogAt > 0f && _latchesSinceLog > 20)
                Debug.LogError("[Multiplayer][rail] TimeAnchor: " + _latchesSinceLog + " re-latches in 10 s — the " +
                               "drift prediction does not match the host clock, so clients see the time jump every tick");
            _nextChurnLogAt = Time.realtimeSinceStartup + 10f;
            _latchesSinceLog = 0;
        }

        /// <summary>Client: the "TA" root object — the scratch DTO the generic applier writes leaves into.
        /// Seeded from the client's OWN clock in the same canonical shape so a partial anchor (only the
        /// members that actually changed ride) always layers onto a valid base rather than onto zeros —
        /// a Scale of 0 would silently stop the clock.</summary>
        internal static TimingInstanceData ClientDto(Timing t)
        {
            if (t == null) return null;
            return _clientDto ?? (_clientDto = Canonical(t));
        }

        /// <summary>Client, after the delta batch: load the anchor through the game's own seam, but ONLY when
        /// this batch actually carried a piece of it. Guarding on touched is what keeps re-application honest:
        /// applying an anchor re-states "Now = anchor AS OF NOW", so replaying an old one would rewind the
        /// clock by however long it had been current.</summary>
        internal static void ApplyIfTouched(GeoLevelController geo, HashSet<object> touched)
        {
            if (_clientDto == null || geo?.Timing == null || touched == null || !touched.Contains(_clientDto)) return;
            // The anchor moves the clock BASE only — never the rate state. Paused/Scale already ride as
            // ordinary leaves on root "T" (Base.Core.Timing), where they land through the PROPERTY setters,
            // which call RescheduleUpdateables + fire EffectiveScaleChangedEvent/OnPausedEvent
            // (decompile Base.Core/Timing.cs:88-131). ProcessInstanceData assigns the BACKING fields
            // _paused/_scale directly (Timing.cs:222-232) — silent, no reschedule, no event. Letting the
            // anchor carry them made "TA" a second, silent writer of state "T" owns: a client that paused
            // locally got _paused=false written back on the next drift re-latch, so the clock resumed while
            // the pause indicator stayed stuck (UIModuleTimeControl polls Now, but the paused state is
            // flag-driven via _updatePausedState). Re-state the LIVE values so only the base moves.
            //
            // Read them through the game's own RecordInstanceData (Timing.cs:209-214), which snapshots the
            // BACKING fields _paused/_scale — the exact counterpart of what ProcessInstanceData writes. The
            // Paused PROPERTY is parent-aware (returns true whenever ParentTime.Paused, Timing.cs:100-106),
            // so reading it here would latch a parent-induced pause into this clock's own flag and leave it
            // paused after the parent resumed.
            var live = geo.Timing.RecordInstanceData();
            _clientDto.Paused = live.Paused;
            _clientDto.Scale = live.Scale;
            geo.Timing.ProcessInstanceData(_clientDto);
        }

        /// <summary>Drop both halves. The host MUST re-latch across a reload or a full resend, because a
        /// resend re-emits the stored anchor and a stale one would rewind every client to the latch instant;
        /// the client must re-seed because its clock was just replaced by the transferred save.</summary>
        internal static void Reset()
        {
            _hostDto = null;
            _clientDto = null;
        }
    }
}
