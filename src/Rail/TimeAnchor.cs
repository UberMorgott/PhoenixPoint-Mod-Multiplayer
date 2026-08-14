using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Every clock write goes through <see cref="Rebase"/>, which moves StartTime ONLY and reschedules —
    /// see there for why zeroing the accrual teleported every aircraft, and for R12. Pause/speed themselves
    /// still ride the ordinary "T" root through Timing's property setters, which DO reschedule and fire the
    /// native events, and they land in the same batch — this class deliberately does not take that over.
    ///
    /// THIS CLASS DOES NOT DELIVER SYMPTOM (c). Host pause/speed reaching the client in one frame instead of
    /// 0.5 s is DiffEngine.FlushNow + Timing.EffectiveScaleChangedEvent (N3, batch 1). This is clock VALUE
    /// sync — the two were conflated once and the conflation is what made 37af665 look like it had failed.
    /// </summary>
    internal static class TimeAnchor
    {
        private static TimingInstanceData _hostDto;
        private static TimeUnit _hostTruthAtLatch; // uncompensated host clock; DTO StartTime is receiver-priced
        private static float _latchedAt;        // local realtime of the latch — the host-side prediction base
        private static TimingInstanceData _clientDto;
        private static float _appliedAt;        // local realtime of the last apply — the client-side prediction base
        private static float _nextChurnLogAt;
        private static int _latchesSinceLog;

        /// <summary>The churn alarm's window and threshold, as READABLE constants because the alarm's whole
        /// value depends on the threshold sitting BELOW the maximum rate a latch can even occur at, and that
        /// is the one thing prose cannot enforce. <see cref="HostDto"/> is asked once per host walk cycle
        /// (IdentityResolver.Roots, snapshotted at DiffEngine.BeginCycle), so the ceiling is
        /// <c>Window / DiffEngine.TickInterval</c> — 20 per 10 s when this comment was written against a
        /// 0.5 s tick, which is EXACTLY the threshold this used to carry, i.e. the anchor could re-latch
        /// every single cycle forever and never trip it. e43bee8 took the tick to 0.1 s, so the ceiling is
        /// now 100 and the margin is wider still. RailCheck L73 asserts the inequality mechanically against
        /// the LIVE tick so it cannot drift back.</summary>
        internal const float ChurnWindowSeconds = 10f;
        internal const int ChurnThreshold = 4;

        /// <summary>Ceiling on the publish-lag compensation below, and it is not a taste value: the drift
        /// checks on BOTH sides allow <c>max(5 s, rate × 0.5)</c> of disagreement, so a compensation held
        /// strictly under <c>rate × 0.5</c> can never by itself make <see cref="Drifted"/> or
        /// <see cref="EnforceDrift"/> fire. Above it the anchor would start re-latching against its own
        /// correction — the exact churn the alarm exists to confess. 0.4 leaves the margin.</summary>
        private const double MaxPublishLagSeconds = 0.4;

        /// <summary>How long a PAUSE has to hold before the anchor gives back the publish-lag compensation it
        /// priced for the flight — see <see cref="CompensationExpired"/>. Comfortably past
        /// <see cref="MaxPublishLagSeconds"/> (and past the measured 26-176 ms latch→apply of the 2026-08-12
        /// session), so the settle can never land on a client whose clock is still in flight.</summary>
        private const float PauseSettleSeconds = 1f;

        /// <summary>The clock collapsed into its own save DTO: the entire anchor in StartTime, accrual zeroed.
        /// The host latches this and the client seeds with it, so the members that never ride (OwnNow) start
        /// out equal on both peers instead of at a default the client would silently keep.
        ///
        /// StartTime is the host's clock AS IT WILL READ WHEN THE ANCHOR IS PUBLISHED, not as it reads now,
        /// and that difference is the whole of symptom (a) — "the exploration spinner has not finished
        /// filling when the host already completes the exploration" (3-instance session 2026-08-04 23:22).
        /// The latch happens at <c>DiffEngine.BeginCycle</c> (IdentityResolver.Roots asks HostDto), the
        /// delta ships a WHOLE WALK later, and the client then anchors on RECEIPT — so the client's level
        /// clock landed on a host reading that was already one walk old and STAYED there, because latches
        /// are events (pause / speed / drift), not a heartbeat. A permanent, one-directional
        /// client-is-behind bias, and an invisible one: EnforceDrift compares the client against its own
        /// anchor derivation, never against the host, which is why all three peer logs from that session
        /// contain not one TimeAnchor line while the bias was there the whole time. Measured that session:
        /// a host apply reached the clients' appliers 112 ms later (23:28:48.714 host → 23:28:48.826
        /// client-2), against 16-24 ms for a direct order-channel message on the same loopback — so the
        /// walk, not the network, is what the clock was losing.
        /// The correction is therefore the host's OWN publish lag, which the host can measure exactly
        /// (<see cref="DiffEngine.LastCycleSeconds"/>) and which needs nothing new on the wire.
        ///
        /// PRICED AT THE RATE FROM BEFORE THE LATCH, and pricing it at the LIVE one was a second bug of its
        /// own (3-instance session 2026-08-12): every host PAUSE rewound each client's geoscape clock by up
        /// to ~636 game-seconds, a sawtooth the resume then yanked back, and <see cref="Rebase"/> dragged
        /// every timed updateable backward with it. The old reasoning — "a paused clock does not advance in
        /// flight, so a pause latch needs no compensation" — is true only in the HOST's frame. It is the
        /// CLIENT's clock that runs during the flight, at the rate it still believes in because the pause
        /// has not reached it yet (M2's client sim is deliberately not frozen, <see cref="ClientSimGate"/>),
        /// so it arrives at <c>Now + rate x lag</c> and the uncompensated anchor drags it back by exactly
        /// that. Measured latch→apply that session: 176.7/174.7/123.4/115.4/100.6/88.6/79.3/75.3/67.6/59.6/
        /// 34.2/26.2 ms, the two clients within 2-12 ms of each other — so it is the walk, not the link, and
        /// ~3600 game-s per real-s is what turns 176 ms into 636 s.
        /// <c>_hostDto.Scale</c> IS that pre-latch rate, and it is the own scale rather than
        /// <c>EffectiveScale</c> on purpose: Timing's Paused setter never touches <c>_scale</c> (decompile
        /// Base.Core/Timing.cs:99-129), so the last published DTO still carries the RUNNING scale through a
        /// pause — which also leaves the RESUME latch pricing the same <c>rate x lag</c> it already priced,
        /// i.e. the path that works is untouched. The invariant is then "every anchor reads
        /// <c>host Now + rate x lag</c>", held across the whole pause, so a re-latch while paused (drift, a
        /// speed click) re-states the same value instead of fighting the offset. The parent factor is local
        /// clock machinery and is read live, as in <see cref="ClientRate"/>. Only the very first latch has
        /// no prior rate; there the live one is the best available and the clock is not mid-transition
        /// anyway. RailCheck L429 asserts the pre-latch read; L100 arm D still owns the live fallback.
        /// ponytail: the one-way NETWORK leg (16-24 ms loopback, more on a real link) stays uncompensated —
        /// estimating it needs a wire timestamp, and TimeAnchor deliberately rides the game's own DTO,
        /// which has no field to put one in. Add the ping/pong estimator only when in-game says the
        /// residual is still visible.</summary>
        private static TimingInstanceData Canonical(Timing t, bool expired)
        {
            double lag = Math.Min(DiffEngine.LastCycleSeconds, MaxPublishLagSeconds);
            double rate = PriorEffectiveRate(_hostDto != null, _hostDto != null && _hostDto.Paused,
                                             _hostDto == null ? 0.0 : _hostDto.Scale,
                                             ParentScale(t), t.EffectiveScale);
            double candidate = PredictPublishedSeconds(t.Now.TimeSpan.TotalSeconds, rate, lag);
            double publishedSeconds = MonotonePublishedSeconds(candidate,
                PublishFloorSeconds(_hostDto != null, expired,
                                    _hostDto == null ? candidate : _hostDto.StartTime.TimeSpan.TotalSeconds,
                                    candidate));
            var published = TimeUnit.FromTimeSpan(TimeSpan.FromSeconds(publishedSeconds));
            return new TimingInstanceData
            {
                Paused = t.Paused,
                Scale = t.Scale,
                StartTime = published,
                StartFixedTime = published,
                OwnNow = TimeUnit.Zero,
                OwnFixedNow = TimeUnit.Zero,
            };
        }

        /// <summary>Rate the receivers uniformly held before this latch. A paused prior anchor advances 0.</summary>
        internal static double PriorEffectiveRate(bool hasPrior, bool priorPaused, double priorScale,
                                                  double parentScale, double liveFallback)
            => hasPrior ? (priorPaused ? 0.0 : priorScale * parentScale) : liveFallback;

        /// <summary>Pure numeric anchor model used by the pause/resume regression.</summary>
        internal static double PredictPublishedSeconds(double hostNow, double priorRate, double lagSeconds)
            => hostNow + Math.Max(0.0, priorRate * Math.Min(lagSeconds, MaxPublishLagSeconds));

        internal static double HostPredictionError(double hostNow, double truthAtLatch, double rate,
                                                   double elapsed)
            => Math.Abs(hostNow - (truthAtLatch + rate * elapsed));

        internal static double MonotonePublishedSeconds(double candidate, double previousPublished)
            => Math.Max(candidate, previousPublished);

        /// <summary>
        /// THE COMPENSATION HAS AN EXPIRY, and until 2026-08-14 it did not — which is the whole of this bug.
        ///
        /// THE REPORT (3-instance session 2026-08-13, both clients): <c>[MP][clockphase] dGame=1136.426
        /// dReal=paused scale=0.00 sinceLatch=59.8 hostLatchAge=59.9</c> — each client's geoscape clock sat
        /// 1136 GAME seconds AHEAD of the host's, FROZEN at that value for a minute while both clocks were
        /// paused, and a second stretch held +792 for 33 s. Constant, not accumulating, and the client was not
        /// advancing: shape (a), a constant REAL-time lead of <c>lag</c> seconds amplified by the geoscape
        /// rate (1136 / 0.4 ≈ 2841 game-s per real-s — i.e. the compensation, at exactly the
        /// <see cref="MaxPublishLagSeconds"/> clamp).
        ///
        /// WHY IT STUCK. The pause latch prices <c>rate x lag</c> at the PRE-pause rate on purpose (L429): the
        /// client's clock is NOT frozen in flight, so it really does arrive at <c>Now + rate x lag</c> and an
        /// uncompensated anchor would rewind it. That is right FOR THE FLIGHT and wrong for the minute after
        /// it: the host is stopped at <c>Now</c> and is never going to reach the published value, so every
        /// client is left permanently ahead of a clock that will not move. Nothing could take it back —
        /// <see cref="Drifted"/> answers 0 error on a stopped clock, <see cref="EnforceDrift"/> compares the
        /// client against that same compensated anchor, and a re-latch while paused prices rate 0 but was then
        /// ratcheted straight back up to the old value by <see cref="MonotonePublishedSeconds"/>.
        /// Downstream, a client whose clock reads up to ~19 game-minutes early fails every "is this interval
        /// still open" test the shared clock feeds — the exploration leg that "ends at &lt;t&gt; which the local
        /// clock is already past" is this number and nothing else.
        ///
        /// THE FIX is an expiry on the ratchet, not on the compensation: once the pause has held longer than
        /// any publish lag can be, the flight the compensation paid for is provably over, so the next latch
        /// publishes the host's HONEST <c>Now</c> and the clients settle onto it. One backward step per pause,
        /// while both clocks are stopped — never the in-flight rewind L429 forbids, and never a sawtooth,
        /// because after it the anchor equals the host clock and this predicate answers false.
        /// </summary>
        internal static bool CompensationExpired(bool hasPrior, bool hostPaused, bool anchorPaused,
                                                 double anchorPublished, double hostNow,
                                                 double sinceLatchSeconds)
            => hasPrior && hostPaused && anchorPaused && anchorPublished > hostNow &&
               sinceLatchSeconds >= PauseSettleSeconds;

        /// <summary>The floor <see cref="MonotonePublishedSeconds"/> is asked to hold. The previous
        /// publication normally — that is what stops a re-latch rewinding a client mid-flight — except once
        /// the compensation has expired, where the floor is the candidate itself and the anchor is free to
        /// come back DOWN onto the host's real clock. A first latch has no floor to hold either.</summary>
        internal static double PublishFloorSeconds(bool hasPrior, bool expired, double previousPublished,
                                                   double candidate)
            => hasPrior && !expired ? previousPublished : candidate;

        /// <summary>WHEN A NEW ANCHOR GOES ON THE WIRE, as a pure predicate rather than an inline disjunction,
        /// because a term dropped from an inline `if` is invisible to a law that can only see which methods
        /// the body calls (L490 caught exactly that on itself). The first four terms are the original set — no
        /// prior anchor, a pause change, a speed change, drift — and <paramref name="expired"/> is the settle
        /// latch: without it <see cref="CompensationExpired"/> can be computed and then thrown away, which
        /// looks identical from the outside and leaves every client permanently ahead of a stopped host.</summary>
        internal static bool ShouldLatch(bool hasPrior, bool pausedChanged, bool scaleChanged, bool drifted,
                                         bool expired)
            => !hasPrior || pausedChanged || scaleChanged || drifted || expired;

        /// <summary>Has the host's real clock left the anchor's own prediction? Catches every jump that is
        /// not a pause/speed change — save-load, time skip, any native re-anchor — and, over long unchanged
        /// stretches, ordinary rate error. This is the calibration knob: the model is a model, and the host's
        /// Timing is the truth. Threshold as in the quarry: 5 s of game time, or one diff tick at the current
        /// rate when that is larger.
        ///
        /// A RATE CHANGE IS NOT DRIFT, and the guard below is where that has to be said, because the
        /// prediction extrapolates the WHOLE interval since the latch at the rate read NOW. On the one cycle
        /// a pause/resume/speed lands, that rate is one the clock never ran at, so the answer is not merely
        /// noisy — it is guaranteed over threshold in both directions. Resume after a P-second pause: the
        /// clock stood still while the prediction ran, error = rate x P against a rate x 0.5 tolerance, i.e.
        /// true for any pause longer than half a second. Pause: rate reads 0, so the tolerance collapses to
        /// its 5 GAME-second floor while the error is the whole running stretch since the latch. That floor
        /// is nothing at geoscape speed: the same host log clocks 8 game-hours between mist hour 254 and 262
        /// over realtime 411.057-421.059, i.e. ~2880 game-s per real-s, so 5 game seconds is 1.7 ms of wall
        /// clock. Every rate change therefore answered DRIFT.
        ///
        /// Host log 2026-08-07, the only two ERRORs this class produced all session (23:10:09.971 and
        /// 23:10:50.724, "5 DRIFT re-latches in 10 s"): the second window (realtime 401.5-411.5) contains
        /// exactly five [MP][pause] transitions — 402.264, 402.740, 408.488, 409.409, 409.817 — a
        /// vehicle-order storm, and no client logged one clock complaint. Both alarms sit inside the
        /// session's two densest pause bursts and the other ~15 minutes of geoscape are silent, which is the
        /// signature of counting gestures, not of a model that disagrees with its clock.
        ///
        /// REGRESSION: a4a368a added the driftLatch flag to make exactly this exclusion ("a pause/speed latch
        /// is a user gesture", <see cref="ChurnCheck"/>) and did not get it, because the flag was computed by
        /// a predicate that cannot see a rate change. The guard belongs here rather than in the caller so the
        /// predicate cannot be asked a question it has no basis to answer — RailCheck L190 asserts it.
        /// The LATCH is untouched: <see cref="HostDto"/>'s own Paused/Scale terms fire it and must, so
        /// returning false here changes nothing on the wire and only stops the alarm confessing speed
        /// clicks.</summary>
        private static bool Drifted(Timing t)
        {
            if (_hostDto.Paused != t.Paused || _hostDto.Scale != t.Scale) return false;
            double rate = t.EffectiveScale; // Paused ? 0 : CumulativeScale — the true d(Now)/d(realtime)
            double error = HostPredictionError(t.Now.TimeSpan.TotalSeconds,
                                               _hostTruthAtLatch.TimeSpan.TotalSeconds, rate,
                                               Time.realtimeSinceStartup - _latchedAt);
            return error > Math.Max(5.0, rate * 0.5);
        }

        /// <summary>Host: the "TA" root object. Same values between latches ⇒ the walk encodes identical bytes
        /// ⇒ the diff emits nothing (law 6, zero churn on the 0.5 s tick).</summary>
        internal static TimingInstanceData HostDto(Timing t)
        {
            if (t == null) return null;
            bool drifted = _hostDto != null && Drifted(t);
            // The settle latch: a pause that has outlived the flight its compensation paid for gives that
            // compensation back, ONCE (after it the anchor equals the host clock, so the predicate is false
            // again and there is no churn to count — it is not a drift latch either).
            bool expired = CompensationExpired(_hostDto != null, t.Paused, _hostDto != null && _hostDto.Paused,
                                               _hostDto == null ? 0.0 : _hostDto.StartTime.TimeSpan.TotalSeconds,
                                               t.Now.TimeSpan.TotalSeconds,
                                               Time.realtimeSinceStartup - _latchedAt);
            if (ShouldLatch(_hostDto != null, _hostDto != null && _hostDto.Paused != t.Paused,
                            _hostDto != null && _hostDto.Scale != t.Scale, drifted, expired))
            {
                var canonical = Canonical(t, expired);
                _hostTruthAtLatch = t.Now;
                _hostDto = canonical;
                _latchedAt = Time.realtimeSinceStartup;
                ChurnCheck(drifted);
            }
            return _hostDto;
        }

        /// <summary>Fresh host answer even when Timing's change-gated setter swallowed an equal value.</summary>
        internal static bool RefreshForAuthoritativeReply(Timing t)
        {
            if (t == null) return false;
            var canonical = Canonical(t, expired: false);
            _hostTruthAtLatch = t.Now;
            _hostDto = canonical;
            _latchedAt = Time.realtimeSinceStartup;
            return true;
        }

        /// <summary>THE runnable check. A latch is an EVENT (pause, speed, load), never a heartbeat: if this
        /// fires, the drift prediction disagrees with the host clock and every client is being jerked back to
        /// a fresh anchor twice a second. That is the one failure this class can have that still looks
        /// perfectly healthy in a log, so it gets stated out loud.
        ///
        /// Only DRIFT latches are counted. A pause/speed latch is a user gesture — five speed clicks in ten
        /// seconds are not churn, and counting them is how a threshold ends up raised above the rate the
        /// pathological case actually runs at (see <see cref="ChurnThreshold"/>). <see cref="Drifted"/> owns
        /// the other half of that exclusion and is the half a4a368a missed.
        ///
        /// The window is reported MEASURED, never as <see cref="ChurnWindowSeconds"/>, because this runs only
        /// ON a latch: a latch-free stretch keeps the window open past its nominal end, so the count can span
        /// arbitrarily long. The 2026-08-07 host log printed two alarms 40.75 s apart and both claimed
        /// "in 10 s" — with the constant in the text there was no way to tell 5-in-10-s from 5-in-40-s, which
        /// is the difference between churn and background.</summary>
        private static void ChurnCheck(bool driftLatch)
        {
            if (driftLatch) _latchesSinceLog++;
            if (Time.realtimeSinceStartup < _nextChurnLogAt) return;
            if (_nextChurnLogAt > 0f && _latchesSinceLog > ChurnThreshold)
                MpLog.LogError("[Multiplayer][rail] TimeAnchor: " + _latchesSinceLog + " DRIFT re-latches in " +
                               (Time.realtimeSinceStartup - (_nextChurnLogAt - ChurnWindowSeconds))
                                   .ToString("F1", CultureInfo.InvariantCulture) +
                               " s (threshold " + ChurnThreshold + " per " + ChurnWindowSeconds + " s) — the drift " +
                               "prediction does not match the host clock, so every client's level clock is being " +
                               "jerked to a fresh anchor on almost every walk cycle");
            _nextChurnLogAt = Time.realtimeSinceStartup + ChurnWindowSeconds;
            _latchesSinceLog = 0;
        }

        /// <summary>Move the level clock's BASE onto <paramref name="target"/> WITHOUT disturbing its own
        /// accrual — the one shape of clock write that does not teleport every actor on the map.
        ///
        /// RCA 2026-08-01 (client aircraft frozen / rubber-banding while the host stayed smooth): every
        /// geoscape actor owns its OWN <c>Timing</c>, parented to this level clock
        /// (<c>ActorComponent.Initialize</c>:85-90 → <c>GameUtl.LevelOf(...).GetComponent&lt;TimeSource&gt;()</c>;
        /// <c>GeoLevelController</c> IS that TimeSource, :51 and :344-350). A child derives from its parent's
        /// <c>OwnNow</c>, never its <c>Now</c> (<c>ParentOwnNow</c>, Timing.cs:176), measured against a
        /// <c>_parentSetTime</c> latched when the child was made. <c>ProcessInstanceData</c> assigns
        /// <c>_ownSetTime = data.OwnNow</c> and re-anchors <c>_parentSetTime</c> (Timing.cs:222-232) — so
        /// shipping <c>OwnNow = Zero</c>, as this class used to, dropped the LEVEL clock's accrual to 0 while
        /// every ACTOR clock kept measuring against the old base: EVERY apply teleported EVERY actor clock
        /// BACKWARD by the whole accrual since the previous apply. A navigating aircraft's pose is closed-form
        /// on exactly that clock — <c>GeoNavComponent.NavigateRoutine</c>:104-116 recomputes
        /// <c>totalTime.Ratio01(startTime, NavActor.Actor.Timing.Now)</c> → <c>Slerp</c> EVERY FRAME, since it
        /// yields <c>NextUpdate.NextFrame</c>, whose <c>NextTime</c> is Invalid (NextUpdate.cs:9/:38) and which
        /// therefore is NOT a timed wake at all — so the icon snapped back by that much on every apply and made
        /// net progress of about zero. The ORDER stayed authoritative, which is why it still arrived in the end.
        ///
        /// The fix is where the correction is put, not whether it is applied: <c>Now = StartTime + OwnNow</c>
        /// (Timing.cs:55), so <c>StartTime = target - OwnNow</c> lands the level clock exactly on the target
        /// while the accrual every child reads stays CONTINUOUS. Children are then untouched by an anchor
        /// apply — correct, since they are presentation clocks the rail never addresses.
        /// (<c>StartFixedTime</c> is bookkeeping only: <c>FixedNow</c> reads <c>StartTime</c> too, Timing.cs:57.)
        ///
        /// The level clock's own base still MOVES — by the host↔client error, which is the entire point of an
        /// anchor — so timed updateables scheduled ON it keep wake times computed against the pre-jump clock
        /// (research/manufacture ETAs stalling after a backward jump). That is Risk R12, and this is the
        /// explicit reschedule it prescribed: <c>TimingScheduler.RescheduleForTiming</c> is PUBLIC
        /// (TimingScheduler.cs:667-674) and re-derives every timed updateable's scheduler time from its own
        /// <c>OwnNextUpdate</c>, then re-heapifies. <c>Timing.RescheduleUpdateables</c> is private, so the
        /// scheduler is reached the way the game reaches it — the nearest one up the parent chain
        /// (<c>GetSchedulerInHierarchy</c>, Timing.cs:335-354).</summary>
        private static void Rebase(Timing t, TimeUnit target)
        {
            // RecordInstanceData (Timing.cs:209-220) snapshots the BACKING _paused/_scale — the exact
            // counterpart of what ProcessInstanceData writes. The Paused PROPERTY is parent-aware (returns
            // true whenever ParentTime.Paused, Timing.cs:100-106), so reading it here would latch a
            // parent-induced pause into this clock's own flag and leave it paused after the parent resumed.
            // Re-stating the LIVE rate is also what keeps "TA" from becoming a second, silent writer of
            // state the "T" root owns: a client that paused locally used to get _paused=false written back
            // on the next drift re-latch, so the clock resumed while the pause indicator stayed stuck.
            var live = t.RecordInstanceData();
            t.ProcessInstanceData(new TimingInstanceData
            {
                Paused = live.Paused,
                Scale = live.Scale,
                StartTime = target - live.OwnNow,
                StartFixedTime = target - live.OwnFixedNow,
                OwnNow = live.OwnNow,
                OwnFixedNow = live.OwnFixedNow,
            });
            for (var p = t; p != null; p = p.ParentTime)
                if (p.Scheduler != null) { p.Scheduler.RescheduleForTiming(t); break; }
        }

        /// <summary>Client: the "TA" root object — the scratch DTO the generic applier writes leaves into.
        /// Seeded from the client's OWN clock in the same canonical shape so a partial anchor (only the
        /// members that actually changed ride) always layers onto a valid base rather than onto zeros —
        /// a Scale of 0 would silently stop the clock.</summary>
        internal static TimingInstanceData ClientDto(Timing t)
        {
            if (t == null) return null;
            return _clientDto ?? (_clientDto = Canonical(t, expired: false));
        }

        /// <summary>Client, after the delta batch: load the anchor through the game's own seam, but ONLY when
        /// this batch actually carried a piece of it. Guarding on touched is what keeps re-application honest:
        /// applying an anchor re-states "Now = anchor AS OF NOW", so replaying an old one would rewind the
        /// clock by however long it had been current.</summary>
        internal static void ApplyIfTouched(GeoLevelController geo, HashSet<object> touched)
        {
            if (_clientDto == null || geo?.Timing == null || touched == null || !touched.Contains(_clientDto)) return;
            // The anchor moves the clock BASE only — never the rate state, and never the accrual every
            // ACTOR clock hangs off (see Rebase). Paused/Scale ride as ordinary leaves on root "T"
            // (Base.Core.Timing), where they land through the PROPERTY setters, which reschedule and fire
            // EffectiveScaleChangedEvent/OnPausedEvent (Timing.cs:88-131); _clientDto itself keeps the
            // HOST's Paused/Scale as received on the wire — that retained rate is the prediction
            // EnforceDrift checks the local clock against, and it is deliberately NOT written here.
            //
            // SAY THE SIZE OF THE JUMP. This is the peer the anchor actually moves, and until now it logged
            // nothing at all: the 2026-08-07 session has two host-side alarms naming "every client's level
            // clock is being jerked" and not one TimeAnchor line in the client log to confirm or deny it, so
            // a player reporting geoscape stutter produced zero evidence on the peer that stuttered.
            // Unconditional and at Log level on purpose. An apply happens only when the anchor CHANGED
            // (piecewise-constant, law 6), i.e. at rate transitions — ~5 in the densest 10 s of that session
            // and none across quiet play, which is nothing next to the rail's own per-cycle tick line. A
            // severity threshold would need a calibrated "how big is a jerk", and that number is exactly what
            // no measurement has produced yet (ClockPhaseDiag exists because of it) — inventing one here
            // would re-hide the quantity behind a guess. Sign: POSITIVE = the clock is pushed FORWARD, i.e.
            // this peer was behind the host.
            var t = geo.Timing;
            double rate = ClientRate(t);
            double jump = _clientDto.StartTime.TimeSpan.TotalSeconds - t.Now.TimeSpan.TotalSeconds;
            var inv = CultureInfo.InvariantCulture;
            MpLog.Log("[Multiplayer][rail] TimeAnchor: anchor apply moved this peer's level clock by " +
                      jump.ToString("F1", inv) + " s of game time (" +
                      (rate > 0.0 ? (jump / rate).ToString("F3", inv) + " s real" : "paused") +
                      ", rate=" + rate.ToString("F2", inv) + ", sinceLastApply=" +
                      ClientAnchorAgeSeconds.ToString("F1", inv) + " s)");
            Rebase(t, _clientDto.StartTime);
            _appliedAt = Time.realtimeSinceStartup;
        }

        /// <summary>The client's live <c>d(Now)/d(realtime)</c> under the anchor's RETAINED host rate, shared
        /// by <see cref="ApplyIfTouched"/>'s jump report and <see cref="EnforceDrift"/>'s prediction so the
        /// two can never price the same clock differently. The anchor carries only the host's OWN
        /// Paused/Scale; the parent factor is local clock machinery and is read live (decompile
        /// Base.Core/Timing.cs:65-75 — Paused is parent-aware :100-109, CumulativeScale =
        /// ParentCumulativeScale x Scale, no-parent fallback Time.timeScale :184). A bare
        /// <c>_clientDto.Scale</c> mispriced the derivation — spurious re-asserts, missed drift — whenever
        /// the parent ran at anything but 1x or was paused.</summary>
        private static double ClientRate(Timing t)
        {
            bool paused = _clientDto.Paused || (t.ParentTime != null && t.ParentTime.Paused);
            return paused ? 0.0 : _clientDto.Scale * ParentScale(t);
        }

        /// <summary>The LOCAL factor of a clock's rate, which no DTO carries and both sides therefore read
        /// live: <c>CumulativeScale = ParentCumulativeScale x Scale</c> with a no-parent fallback of
        /// <c>Time.timeScale</c> (decompile Base.Core/Timing.cs:184). Pausing is NOT folded in — callers that
        /// care about a paused clock decide that themselves, and <see cref="Canonical"/> deliberately does
        /// not, because its invariant has to survive the pause it is latching.</summary>
        private static double ParentScale(Timing t)
            => t.ParentTime != null ? t.ParentTime.CumulativeScale : Time.timeScale;

        /// <summary>Client-side counterpart of <see cref="Drifted"/> — the free-run backstop. The rail
        /// diffs HOST state, so a client clock mutated locally (a writer the TimeSync seams do not
        /// capture: quick-save coroutine, another mod, a missed native path) is never corrected by the
        /// wire while the host anchor stays unchanged — it free-runs forever. This re-asserts the last
        /// applied anchor once the local clock leaves the anchor's own derivation (same threshold
        /// formula as the host side). Rate is re-stated through the PROPERTY setters — both early-out
        /// on an equal value (Timing.cs:87/112), and on a real flip they fire the native events, so the
        /// pause indicator and scheduler follow instead of silently disagreeing (the exact failure the
        /// ApplyIfTouched comment records for backing-field writes). Idempotent: ProcessInstanceData is
        /// an overwrite, and the prediction base is NOT advanced here — corrections converge on the
        /// anchor, never chase their own writes. Driven at ~1 Hz by TimeSync.ClientTick.</summary>
        internal static void EnforceDrift(GeoLevelController geo)
        {
            if (_clientDto == null || _appliedAt <= 0f || geo == null || geo.Timing == null) return;
            var t = geo.Timing;
            // Same EffectiveScale form as the host derivation (Drifted), via ClientRate — see there.
            double rate = ClientRate(t);
            double derived = _clientDto.StartTime.TimeSpan.TotalSeconds + rate * (Time.realtimeSinceStartup - _appliedAt);
            if (Math.Abs(t.Now.TimeSpan.TotalSeconds - derived) <= Math.Max(5.0, rate * 0.5)) return;
            MpLog.LogWarning("[Multiplayer][rail] TimeAnchor: client clock drifted from anchor derivation (now=" +
                             t.Now.TimeSpan.TotalSeconds.ToString("F0") + "s derived=" + derived.ToString("F0") +
                             "s) — re-asserting");
            t.Paused = _clientDto.Paused;
            t.Scale = _clientDto.Scale;
            Rebase(t, TimeUnit.FromTimeSpan(TimeSpan.FromSeconds(derived)));
        }

        /// <summary>Seconds since the host last latched, or -1 when it never has. READ-ONLY, and it exists
        /// for exactly one caller: <see cref="ClockPhaseDiag"/>, which needs the latch age to tell an error
        /// that ACCUMULATES between latches apart from one that is merely constant. Nothing here decides
        /// anything — see that file for what the resulting number cannot see.</summary>
        internal static float HostLatchAgeSeconds
            => _hostDto == null ? -1f : Time.realtimeSinceStartup - _latchedAt;

        /// <summary>Client counterpart of <see cref="HostLatchAgeSeconds"/>: seconds since the last anchor
        /// APPLY (the client's own prediction base), -1 when no anchor has ever been applied.</summary>
        internal static float ClientAnchorAgeSeconds
            => _appliedAt <= 0f ? -1f : Time.realtimeSinceStartup - _appliedAt;

        /// <summary>Drop both halves. The host MUST re-latch across a reload or a full resend, because a
        /// resend re-emits the stored anchor and a stale one would rewind every client to the latch instant;
        /// the client must re-seed because its clock was just replaced by the transferred save.</summary>
        internal static void Reset()
        {
            _hostDto = null;
            _hostTruthAtLatch = TimeUnit.Zero;
            _clientDto = null;
            _appliedAt = 0f;
        }
    }
}
