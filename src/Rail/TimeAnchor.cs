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
        private static float _latchedAt;        // local realtime of the latch — the host-side prediction base
        private static TimingInstanceData _clientDto;
        private static float _appliedAt;        // local realtime of the last apply — the client-side prediction base
        private static float _nextChurnLogAt;
        private static int _latchesSinceLog;

        /// <summary>The churn alarm's window and threshold, as READABLE constants because the alarm's whole
        /// value depends on the threshold sitting BELOW the maximum rate a latch can even occur at, and that
        /// is the one thing prose cannot enforce. <see cref="HostDto"/> is asked once per host walk cycle
        /// (IdentityResolver.Roots, snapshotted at DiffEngine.BeginCycle), so the ceiling is
        /// <c>Window / DiffEngine.TickInterval</c> = 20 per 10 s — which is EXACTLY the threshold this used
        /// to carry, i.e. the anchor could re-latch every single cycle forever and never trip it. RailCheck
        /// L73 asserts the inequality mechanically so it cannot drift back.</summary>
        internal const float ChurnWindowSeconds = 10f;
        internal const int ChurnThreshold = 4;

        /// <summary>Ceiling on the publish-lag compensation below, and it is not a taste value: the drift
        /// checks on BOTH sides allow <c>max(5 s, rate × 0.5)</c> of disagreement, so a compensation held
        /// strictly under <c>rate × 0.5</c> can never by itself make <see cref="Drifted"/> or
        /// <see cref="EnforceDrift"/> fire. Above it the anchor would start re-latching against its own
        /// correction — the exact churn the alarm exists to confess. 0.4 leaves the margin.</summary>
        private const double MaxPublishLagSeconds = 0.4;

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
        /// (<see cref="DiffEngine.LastCycleSeconds"/>) and which needs nothing new on the wire, priced at
        /// the live rate because a paused clock does not advance in flight (rate 0 ⇒ no compensation, which
        /// is also why a pause latch is untouched and only a RESUME needed fixing).
        /// ponytail: the one-way NETWORK leg (16-24 ms loopback, more on a real link) stays uncompensated —
        /// estimating it needs a wire timestamp, and TimeAnchor deliberately rides the game's own DTO,
        /// which has no field to put one in. Add the ping/pong estimator only when in-game says the
        /// residual is still visible.</summary>
        private static TimingInstanceData Canonical(Timing t)
        {
            double lag = Math.Min(DiffEngine.LastCycleSeconds, MaxPublishLagSeconds);
            var publishLag = TimeUnit.FromTimeSpan(TimeSpan.FromSeconds(Math.Max(0.0, t.EffectiveScale * lag)));
            return new TimingInstanceData
            {
                Paused = t.Paused,
                Scale = t.Scale,
                StartTime = t.Now + publishLag,
                StartFixedTime = t.Now + publishLag,
                OwnNow = TimeUnit.Zero,
                OwnFixedNow = TimeUnit.Zero,
            };
        }

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
            bool drifted = _hostDto != null && Drifted(t);
            if (_hostDto == null || _hostDto.Paused != t.Paused || _hostDto.Scale != t.Scale || drifted)
            {
                _hostDto = Canonical(t);
                _latchedAt = Time.realtimeSinceStartup;
                ChurnCheck(drifted);
            }
            return _hostDto;
        }

        /// <summary>THE runnable check. A latch is an EVENT (pause, speed, load), never a heartbeat: if this
        /// fires, the drift prediction disagrees with the host clock and every client is being jerked back to
        /// a fresh anchor twice a second. That is the one failure this class can have that still looks
        /// perfectly healthy in a log, so it gets stated out loud.
        ///
        /// Only DRIFT latches are counted. A pause/speed latch is a user gesture — five speed clicks in ten
        /// seconds are not churn, and counting them is how a threshold ends up raised above the rate the
        /// pathological case actually runs at (see <see cref="ChurnThreshold"/>).</summary>
        private static void ChurnCheck(bool driftLatch)
        {
            if (driftLatch) _latchesSinceLog++;
            if (Time.realtimeSinceStartup < _nextChurnLogAt) return;
            if (_nextChurnLogAt > 0f && _latchesSinceLog > ChurnThreshold)
                Debug.LogError("[Multiplayer][rail] TimeAnchor: " + _latchesSinceLog + " DRIFT re-latches in " +
                               ChurnWindowSeconds + " s (threshold " + ChurnThreshold + ") — the drift prediction " +
                               "does not match the host clock, so every client's level clock is being jerked to a " +
                               "fresh anchor on almost every walk cycle");
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
            return _clientDto ?? (_clientDto = Canonical(t));
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
            Rebase(geo.Timing, _clientDto.StartTime);
            _appliedAt = Time.realtimeSinceStartup;
        }

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
            // Same EffectiveScale form as the host derivation (Drifted): Paused ? 0 : Scale × parent
            // cumulative (decompile Base.Core/Timing.cs:65-75 — Paused is parent-aware :100-109,
            // CumulativeScale = ParentCumulativeScale × Scale, no-parent fallback Time.timeScale :184).
            // The anchor carries only the host's OWN Paused/Scale; the parent factor is local clock
            // machinery, read live — a bare _clientDto.Scale mispriced the derivation (spurious
            // re-asserts / missed drift) whenever the parent ran ≠1× or was paused.
            bool paused = _clientDto.Paused || (t.ParentTime != null && t.ParentTime.Paused);
            double parentScale = t.ParentTime != null ? t.ParentTime.CumulativeScale : Time.timeScale;
            double rate = paused ? 0.0 : _clientDto.Scale * parentScale;
            double derived = _clientDto.StartTime.TimeSpan.TotalSeconds + rate * (Time.realtimeSinceStartup - _appliedAt);
            if (Math.Abs(t.Now.TimeSpan.TotalSeconds - derived) <= Math.Max(5.0, rate * 0.5)) return;
            Debug.LogWarning("[Multiplayer][rail] TimeAnchor: client clock drifted from anchor derivation (now=" +
                             t.Now.TimeSpan.TotalSeconds.ToString("F0") + "s derived=" + derived.ToString("F0") +
                             "s) — re-asserting");
            t.Paused = _clientDto.Paused;
            t.Scale = _clientDto.Scale;
            Rebase(t, TimeUnit.FromTimeSpan(TimeSpan.FromSeconds(derived)));
        }

        /// <summary>Drop both halves. The host MUST re-latch across a reload or a full resend, because a
        /// resend re-emits the stored anchor and a stale one would rewind every client to the latch instant;
        /// the client must re-seed because its clock was just replaced by the transferred save.</summary>
        internal static void Reset()
        {
            _hostDto = null;
            _clientDto = null;
            _appliedAt = 0f;
        }
    }
}
