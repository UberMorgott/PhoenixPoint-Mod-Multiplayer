using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Multiplayer.Validation;
using UnityEngine;

namespace Multiplayer.Network.TimeSync
{
    /// <summary>
    /// Unity/native GLUE for the host-authoritative geoscape ANCHOR clock. Pure math lives in
    /// <see cref="TimeSyncProtocol"/> (wire), <see cref="AnchorClock"/> (derive + visual lerp) and
    /// <see cref="ClockOffsetEstimator"/> (NTP offset); this class binds them to the live engine:
    ///
    ///  • HOST   — each frame reads {Paused, SpeedIndex} of the geoscape clock. On a real change (incl.
    ///             native auto-pauses) it captures a fresh anchor {version, tAnchor=hostRT, gAnchor=Now,
    ///             paused, speedIndex} and RELIABLY broadcasts it (0x37). A ~3 Hz heartbeat re-delivers
    ///             the SAME anchor (safety re-sync for late joiners / packet loss — NOT a correction).
    ///             Answers client clock-pings (0x39→0x3A) with its own receive stamp.
    ///  • CLIENT — NEVER accumulates its own game-time. Each frame it DERIVES auth = gAnchor +
    ///             rate*(serverNow-tAnchor), smooths the DISPLAY toward it (~150 ms, forward-monotone,
    ///             hard-set on a big gap), and overwrites Timing via ProcessInstanceData (no events,
    ///             bypasses the TimeLimit unpause guard). serverNow = localRT + clockOffset, measured by
    ///             a periodic ping/pong burst. Client time-control INPUT is intercepted by the Harmony
    ///             patches and relayed to the host as a TimeRequest; host applies last-writer-wins.
    ///
    /// All native access is reflection-based (mirrors <c>TacticalPatches</c>) so the mod tolerates
    /// game-version / TFTV variance and is inert outside the geoscape.
    ///
    /// NOTE (Unity 2019.4): the spec's <c>realtimeSinceStartupAsDouble</c> does NOT exist in this engine
    /// build (added in Unity 2020.2). We use <c>Time.realtimeSinceStartup</c> (float) widened to double as
    /// the monotonic per-process <c>localRT</c>; the clock offset reconciles the per-process zero and the
    /// ~150 ms visual smoothing absorbs float resolution — adequate for the clock-display domain.
    /// </summary>
    public class TimeSyncManager
    {
        // ~3 Hz authoritative anchor re-delivery over the unreliable channel (idempotent: SAME anchor
        // each time, so packet loss self-corrects). This re-delivery IS the lag/late-join watchdog —
        // it is the clock-anchor re-send cadence, NOT a connection heartbeat (cf. SessionManager).
        private const float AnchorRedeliverIntervalSeconds = 0.33f;

        // Visual smoothing time-constant (~150 ms) + hard-set threshold (skip lerp across a big gap).
        private const double VisualTauSeconds = 0.15;
        private const double SnapThresholdSeconds = 2.0;

        // Clock-offset ping cadence + a large-OS-clock-jump step threshold.
        private const float OffsetBurstIntervalSeconds = 0.2f;   // join/reconnect burst
        private const int OffsetBurstCount = 5;
        private const float OffsetSteadyIntervalSeconds = 3.0f;  // steady-state re-estimation
        private const double OffsetStepHardSetSeconds = 2.0;

        // Host game-time scrub/skip detection threshold = max(floor, rate * realSlop). The host's
        // anchor prediction uses rate*(LocalRt()-tAnchor); LocalRt() (float Time.realtimeSinceStartup)
        // and the native game-clock integrator can drift a little in REAL-time terms, and that drift is
        // amplified by the (possibly large) speed rate — so the tolerance scales with rate. A real
        // native time-skip jumps game-time by minutes→hours, far above either term, while ordinary
        // per-frame advance + basis slop stays below it → re-anchor only on a genuine jump.
        private const double ScrubDivergenceFloorSeconds = 5.0; // absolute floor (paused / low rate)
        private const double ScrubDivergenceRealSlopSeconds = 0.5; // tolerated real-time basis slop, ×rate

        private readonly NetworkEngine _engine;

        /// <summary>
        /// Echo-guard: set while the client applies host state (or the host applies a relayed client
        /// request) so the time-control input-intercept patches let our own programmatic writes
        /// through. Mirrors CommandRelay.IsApplying. Static so the Harmony prefixes can read it.
        /// </summary>
        public static bool IsApplyingRemote { get; private set; }

        // Client: last applied HOST-stamped anchor version (stale-drop on the single host clock).
        private long _clientLastAppliedVersion;
        // Host: monotonic ordering counter stamped onto every outgoing anchor (single source).
        private long _hostVersion;

        // Host change-detection cache + heartbeat accumulator + last captured anchor.
        private bool _haveCache;
        private bool _cachedPaused;
        private int _cachedSpeedIndex;
        private bool _cachedLocked;   // interception time-lock state at the last captured anchor (change-detect)
        private float _hbAccum;
        private bool _haveAnchor;
        private AnchorPayload _lastAnchor;

        // ─── Client derive/offset/display state ──────────────────────────
        private bool _clientHaveAnchor;
        private AnchorPayload _clientAnchor;
        private readonly ClockOffsetEstimator _offset = new ClockOffsetEstimator();
        private bool _needHardSet;          // hard-set display on the next derive (first anchor / reconnect)
        private bool _haveDisplay;
        private double _displayGameSeconds;
        // Last paused state we pushed into the native time widget's visual (white+blink vs yellow).
        // Idempotency: only re-arm the widget's dirty flags when it actually changes (no per-frame spam).
        private bool _uiPausedKnown;
        private bool _uiPausedShown;
        // Last interception time-lock state we pushed into the native time widget (grey vs interactable).
        // Idempotency: only toggle the widget's CanvasGroup when it actually changes (no per-frame spam).
        private bool _uiLockKnown;
        private bool _uiLockShown;
        // Ping scheduler.
        private int _nextPingId;
        private int _burstRemaining;
        private float _pingAccum;
        private bool _loggedFallback;     // one-shot guard for the high-RTT offset-fallback warning

        // ─── Cached reflection handles (resolved lazily once the types exist) ──
        private static Type _geoLevelType;
        private static Type _gameUtlType;
        private static Type _geoscapeViewType;
        private static Type _timeControlType;
        private static Type _timingInstanceDataType;
        private static Type _timeUnitType;
        private static MethodInfo _currentLevelMethod;
        private static MethodInfo _fromTimeSpanMethod;
        private static object _timeUnitZero;
        private static bool _reflectionReady;

        // PERF: the client per-frame path (ClientTick→WriteClock→ScaleForIndex/MirrorSpeedUi) formerly
        // re-resolved every member AND called UnityEngine.Object.FindObjectOfType TWICE per frame (a
        // full scene scan). The host never runs this path per frame, so it was a client-only ~2× FPS
        // tax. We now resolve every handle ONCE and cache the live widget reference + a reusable
        // TimingInstanceData so the steady-state client frame does zero scene scans and zero allocs.
        private static PropertyInfo _geoTimingProp;        // GeoLevelController.Timing
        private static PropertyInfo _timingPausedProp;     // Timing.Paused
        private static PropertyInfo _timingNowProp;        // Timing.Now
        private static PropertyInfo _timingScaleProp;      // Timing.Scale
        private static PropertyInfo _timeUnitTimeSpanProp; // TimeUnit.TimeSpan
        private static MethodInfo _processInstanceDataMethod; // Timing.ProcessInstanceData(TimingInstanceData)
        private static FieldInfo _tcSelectedPresetField;   // UIModuleTimeControl.SelectedPresetTime
        private static FieldInfo _tcPresetTimesField;      // UIModuleTimeControl.PresetTimes
        private static FieldInfo _tcUpdatePausedField;     // UIModuleTimeControl._updatePausedState
        private static MethodInfo _tcSelectTimePresetMethod; // UIModuleTimeControl.SelectTimePreset(int)
        private static FieldInfo _tidPausedField, _tidScaleField, _tidStartTimeField,
            _tidStartFixedTimeField, _tidOwnNowField, _tidOwnFixedNowField;
        // Review fix BUG 2 — private Base.Core.Timing.RescheduleUpdateables(Timing) →
        // GetSchedulerInHierarchy().RescheduleForTiming(timing) (Timing.cs:356-358). NOTE: the geoscape
        // Timing's own public Scheduler FIELD is NULL (GeoLevelController.cs:346-350 sets only ParentTime);
        // the scheduler lives on the root game Timing, reached only by this method's hierarchy walk — so we
        // reflect-call the private method rather than the Scheduler field.
        private static MethodInfo _timingRescheduleMethod;   // Timing.RescheduleUpdateables(Timing)

        // Cached live geoscape time-control widget (avoids per-frame FindObjectOfType). A destroyed
        // UnityEngine.Object compares == null, so the helper transparently re-resolves on scene change.
        private static UnityEngine.Object _timeControlCached;
        private static float[] _presetTimesCached; // cached off the live widget (re-read on widget change)

        // Reusable per-frame scratch so WriteClock allocates nothing in steady state.
        private object _tidScratch;                       // single reusable TimingInstanceData
        private readonly object[] _arg1 = new object[1];  // reusable 1-arg invoke buffer
        // Client: last speed index we pushed into the widget (gate MirrorSpeedUi on-change, like pause).
        private int _uiSpeedShown = int.MinValue;

        // Inc4 S1 (§3.3) — host COSMETIC glyph state, republished each client frame in WriteClock (and at the
        // load-time freeze re-assert). Under the sim-freeze the sim _paused is pinned true, so the pause/speed
        // widget can no longer read _timing.Paused for its glyph; ClientTimeGlyphFreezePatch reads THESE (the
        // host anchor's paused/speed) instead. Ignored entirely when ClientSimFreeze.Enabled is OFF (the patch
        // is inert), so they are inert scaffolding under flag-OFF.
        internal static bool GlyphHostPaused;
        internal static int GlyphHostSpeedIndex = 1;

        // Inc4 V2 (ClientSimFreezeV2Gate) — DISPLAY-clock split. Under the V2 sim-pin WriteClock no longer
        // advances the geoscape Timing (Now stays constant = sim frozen), so the native HUD clock widget can no
        // longer read a live Timing.Now. These publish the host-mirrored game-time each client frame;
        // ClientTimeDateDisplayFreezePatch paints the widget's HH:mm / dd.MM.yyyy / minute-hand from them.
        // DisplayActive gates that postfix — true ONLY while the V2 pin is actually driving (active client,
        // in geoscape, anchor+offset seeded). V2-OFF / pre-sync / host ⇒ false ⇒ the postfix is inert and the
        // widget paints itself from Now exactly as at HEAD.
        internal static double DisplayHostGameSeconds;
        internal static bool DisplayActive;

        public TimeSyncManager(NetworkEngine engine)
        {
            _engine = engine;
        }

        private static void EnsureReflection()
        {
            if (_reflectionReady) return;
            _geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            _gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl");
            _geoscapeViewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            _timeControlType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
            _timingInstanceDataType = AccessTools.TypeByName("Base.Core.TimingInstanceData");
            _timeUnitType = AccessTools.TypeByName("Base.Core.TimeUnit");
            _currentLevelMethod = _gameUtlType != null
                ? AccessTools.Method(_gameUtlType, "CurrentLevel")
                : null;
            if (_timeUnitType != null)
            {
                _fromTimeSpanMethod = AccessTools.Method(_timeUnitType, "FromTimeSpan", new[] { typeof(TimeSpan) });
                _timeUnitZero = AccessTools.Field(_timeUnitType, "Zero")?.GetValue(null);
                _timeUnitTimeSpanProp = AccessTools.Property(_timeUnitType, "TimeSpan");
            }
            if (_geoLevelType != null)
                _geoTimingProp = AccessTools.Property(_geoLevelType, "Timing");
            // Base.Core.Timing instance members (resolved off the runtime type via AccessTools).
            var timingType = AccessTools.TypeByName("Base.Core.Timing");
            if (timingType != null)
            {
                _timingPausedProp = AccessTools.Property(timingType, "Paused");
                _timingNowProp = AccessTools.Property(timingType, "Now");
                _timingScaleProp = AccessTools.Property(timingType, "Scale");
                _processInstanceDataMethod = _timingInstanceDataType != null
                    ? AccessTools.Method(timingType, "ProcessInstanceData", new[] { _timingInstanceDataType })
                    : null;
                _timingRescheduleMethod = AccessTools.Method(timingType, "RescheduleUpdateables", new[] { timingType });
            }
            if (_timeControlType != null)
            {
                _tcSelectedPresetField = AccessTools.Field(_timeControlType, "SelectedPresetTime");
                _tcPresetTimesField = AccessTools.Field(_timeControlType, "PresetTimes");
                _tcUpdatePausedField = AccessTools.Field(_timeControlType, "_updatePausedState");
                _tcSelectTimePresetMethod = AccessTools.Method(_timeControlType, "SelectTimePreset", new[] { typeof(int) });
            }
            if (_timingInstanceDataType != null)
            {
                _tidPausedField = AccessTools.Field(_timingInstanceDataType, "Paused");
                _tidScaleField = AccessTools.Field(_timingInstanceDataType, "Scale");
                _tidStartTimeField = AccessTools.Field(_timingInstanceDataType, "StartTime");
                _tidStartFixedTimeField = AccessTools.Field(_timingInstanceDataType, "StartFixedTime");
                _tidOwnNowField = AccessTools.Field(_timingInstanceDataType, "OwnNow");
                _tidOwnFixedNowField = AccessTools.Field(_timingInstanceDataType, "OwnFixedNow");
            }
            _reflectionReady = _geoLevelType != null && _currentLevelMethod != null;
        }

        /// <summary>Monotonic per-process real-time seconds (Unity 2019.4: float widened to double).</summary>
        private static double LocalRt() => UnityEngine.Time.realtimeSinceStartup;

        /// <summary>The live geoscape <c>GeoLevelController</c> instance, or null when not in geoscape.</summary>
        private object GetGeoLevel()
        {
            EnsureReflection();
            if (!_reflectionReady) return null;
            try
            {
                var level = _currentLevelMethod.Invoke(null, null); // GameUtl.CurrentLevel()
                if (level == null) return null;
                if (level is Component comp)
                    return comp.GetComponent(_geoLevelType); // null if current level isn't geoscape
                return null;
            }
            catch { return null; }
        }

        /// <summary>The geoscape <c>Base.Core.Timing</c> instance, or null when not in geoscape.</summary>
        public object GetTiming()
        {
            var geo = GetGeoLevel();
            if (geo == null) return null;
            try
            {
                return _geoTimingProp?.GetValue(geo, null);
            }
            catch { return null; }
        }

        /// <summary>
        /// Inc4 S1 (§3.1) — CLIENT geoscape sim-freeze RE-ASSERT. Sets the live geoscape
        /// <c>Timing.Paused = true</c> via the SETTER (Timing.cs:110), whose <c>RescheduleForTiming</c> Max's
        /// every already-Started geoscape producer (a paused source ⇒ <c>NextUpdate.ConvertToTiming</c> returns
        /// Max, NextUpdate.cs:199 — engine-native TOTAL sim freeze). Called from
        /// <see cref="Multiplayer.Harmony.ClientGeoSimFreezePatch"/>'s postfix on
        /// <c>GeoscapeEventSystem.OnLevelStart()</c>, which runs on EVERY (re)load AFTER
        /// <c>GeoLevelController.LevelCrt</c>'s <c>Timing.ProcessInstanceData(host, Paused=false)</c> (:515)
        /// reset and BEFORE the hourly producer is Started (:761) — so the already-scheduled producers (via the
        /// reschedule) AND the yet-to-Start <c>LevelHourlyUpdateCrt</c> (auto-Max under the now-true
        /// <c>_paused</c>) are all frozen. <see cref="WriteClock"/> then pins <c>_paused=true</c> every frame
        /// (via ProcessInstanceData, no reschedule) between re-asserts, so any new producer auto-Max's and any
        /// later reschedule (host speed-change → <c>set_Scale</c>) RE-Max's rather than un-freezing.
        ///
        /// The setter fires <c>OnPausedEvent</c>/<c>EffectiveScaleChangedEvent</c> once per load (spec §9 Q2
        /// accepted default); the widget's <c>TimingOnPausedEvent</c> re-render is corrected to the host
        /// cosmetic glyph by <c>ClientTimeGlyphFreezePatch</c>. Reflection-only; best-effort try/catch — never
        /// throws into game code. Self-gated to a client-in-session (defensive belt on top of the patch's gate);
        /// no-op on the host / outside geoscape.
        /// </summary>
        public void FreezeClientGeoSim()
        {
            if (_engine == null || _engine.IsHost || !_engine.IsActiveSession) return; // client-in-session only
            var timing = GetTiming();
            if (timing == null) return; // not in geoscape yet — next (re)load's postfix re-asserts
            EnsureReflection();
            if (_timingPausedProp == null) return;
            try
            {
                // Refresh the cosmetic glyph anchor so the widget re-render triggered by the setter's
                // OnPausedEvent this same load shows the HOST state immediately (WriteClock also republishes
                // it every frame). No-op until the first host anchor has arrived.
                if (_clientHaveAnchor)
                {
                    GlyphHostPaused = _clientAnchor.Paused;
                    GlyphHostSpeedIndex = _clientAnchor.SpeedIndex;
                }
                // Review fix BUG 2: the Paused setter SHORT-CIRCUITS when value==_paused (Timing.cs:112) —
                // no RescheduleForTiming. WriteClock field-pins _paused=true every frame via
                // ProcessInstanceData (no reschedule); if that pin landed before this re-assert, the setter
                // no-ops and producers Started while unpaused keep live times (each fires ONE stale tick —
                // TimingScheduler.CallUpdateable defers only frame-based updateables when paused). So after
                // committing Paused=true, ALWAYS fire the explicit reschedule (hierarchy walk → root
                // scheduler → RescheduleForTiming → every producer re-Max's under the paused timing).
                var t = timing;
                ClientSimFreeze.ReassertFreeze(
                    v => _timingPausedProp.SetValue(t, v, null),
                    () => _timingRescheduleMethod?.Invoke(t, new object[] { t }));
                Debug.Log("[Multiplayer] ClientGeoSimFreeze re-asserted: geoscape Timing.Paused = true + rescheduled (sim frozen)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] FreezeClientGeoSim failed: " + ex.Message); }
        }

        /// <summary>
        /// The live geoscape time-control widget, CACHED. PERF: replaces a per-frame
        /// <c>UnityEngine.Object.FindObjectOfType</c> (full scene scan) — the dominant client-only
        /// per-frame cost. A destroyed Unity object compares <c>== null</c>, so on a scene change the
        /// cache is transparently invalidated and re-resolved (one scan), then reused every frame.
        /// </summary>
        private static UnityEngine.Object FindTimeControl()
        {
            if (_timeControlType == null) return null;
            if (_timeControlCached != null) return _timeControlCached; // Unity-null check: destroyed → re-find
            _timeControlCached = UnityEngine.Object.FindObjectOfType(_timeControlType);
            _presetTimesCached = null; // widget changed → drop the cached preset array
            return _timeControlCached;
        }

        /// <summary>
        /// True iff <paramref name="widget"/> IS the geoscape time-control widget — NOT the air-combat
        /// InterceptionTimeControlModule, a SECOND UIModuleTimeControl instance the interception spawns
        /// (GeoscapeModulesData.InterceptionTimeControlModule). Scopes the interception time-lock deny to the
        /// geoscape clock only, so the host keeps pause/speed control INSIDE the air combat. The cached
        /// FindTimeControl reference is the geoscape widget: it is resolved at geoscape entry (the anchor loop
        /// runs continuously) and the interception overlay never destroys it, so the cache stays the geoscape
        /// instance for the whole window while the interception widget is a distinct reference.
        /// </summary>
        public static bool IsGeoscapeTimeControl(object widget)
            => widget != null && ReferenceEquals(widget, FindTimeControl());

        // ─── State readers ────────────────────────────────────────────────

        private static bool GetPaused(object timing)
            => (bool)_timingPausedProp.GetValue(timing, null);

        private static long GetNowTicks(object timing)
        {
            var now = _timingNowProp.GetValue(timing, null);                 // TimeUnit
            var ts = _timeUnitTimeSpanProp.GetValue(now, null);             // TimeSpan
            return ((TimeSpan)ts).Ticks;
        }

        private static int GetSpeedIndex()
            => TryGetSpeedIndex(out int idx) ? idx : 1; // sane default for callers that need a value

        /// <summary>
        /// Read the current SelectedPresetTime. Returns false when the time-control widget is momentarily
        /// absent (scene transition) so the host change-detect can SKIP that frame instead of defaulting
        /// to index 1 — which would otherwise fire a spurious anchor when the real speed isn't 1.
        /// </summary>
        private static bool TryGetSpeedIndex(out int idx)
        {
            idx = 1;
            var tc = FindTimeControl();
            if (tc == null || _tcSelectedPresetField == null) return false;
            idx = (int)_tcSelectedPresetField.GetValue(tc);
            return true;
        }

        // ─── Per-frame tick (called from NetworkEngine.Update) ────────────

        public void Tick()
        {
            if (_engine == null || !_engine.IsActive)
            {
                _haveCache = false;
                DisplayActive = false; // no session → the V2 display postfix must not paint
                return;
            }

            if (_engine.IsHost) HostTick();
            else ClientTick();
        }

        // ─── HOST: capture anchor on change + heartbeat re-delivery ───────

        private void HostTick()
        {
            DisplayActive = false; // host drives the widget natively — never the V2 display postfix
            var timing = GetTiming();
            if (timing == null) { _haveCache = false; return; }

            bool paused;
            int idx;
            long gTicks;
            try
            {
                // Time-control widget momentarily absent (scene transition) → no reading this frame.
                // Skip change-detect rather than defaulting idx to 1 (would emit a spurious anchor).
                if (!TryGetSpeedIndex(out idx)) return;
                paused = GetPaused(timing);
                gTicks = GetNowTicks(timing);
            }
            catch { return; }

            // Capture + reliably broadcast a fresh anchor on any real change (covers native auto-pauses AND an
            // interception time-lock open/close, so the lock bit reaches clients within one frame even when
            // {paused,speedIndex} did not move — the geoscape is natively paused under the brief).
            bool locked = Multiplayer.Network.Sync.InterceptionTimeLock.Active;
            bool changed = !_haveCache || paused != _cachedPaused || idx != _cachedSpeedIndex || locked != _cachedLocked;
            if (changed)
            {
                _cachedPaused = paused;
                _cachedSpeedIndex = idx;
                _cachedLocked = locked;
                _haveCache = true;
                _hbAccum = 0f;
                CaptureAndBroadcastAnchor(paused, idx, gTicks, reliable: true);
                return;
            }

            // Game-time SCRUB / native time-skip detection: with {paused,speedIndex} unchanged, a native
            // jump in Timing.Now (e.g. a scripted time-skip) would never be propagated — the heartbeat
            // just re-sends the SAME stale anchor, so clients keep extrapolating the OLD game-time. Re-derive
            // the game-time the stored anchor predicts for the host's current real-time (host offset = 0 ⇒
            // serverNow == LocalRt) and, if the ACTUAL game-time diverges beyond a small epsilon, re-capture
            // a fresh anchor so the new gAnchor/tAnchor reflect the skip.
            if (_haveAnchor)
            {
                double rate = _lastAnchor.Paused ? 0.0 : ScaleForIndex(_lastAnchor.SpeedIndex, timing);
                double predictedG = AnchorClock.Derive(
                    AnchorPayload.TicksToSeconds(_lastAnchor.GAnchorTicks), rate,
                    AnchorPayload.TicksToSeconds(_lastAnchor.TAnchorTicks), LocalRt());
                double actualG = AnchorPayload.TicksToSeconds(gTicks);
                double threshold = Math.Max(ScrubDivergenceFloorSeconds, rate * ScrubDivergenceRealSlopSeconds);
                if (Math.Abs(actualG - predictedG) > threshold)
                {
                    _hbAccum = 0f;
                    CaptureAndBroadcastAnchor(paused, idx, gTicks, reliable: true);
                    return;
                }
            }

            // Periodic unreliable heartbeat: re-deliver the SAME stored anchor (safety re-sync).
            _hbAccum += Time.unscaledDeltaTime;
            if (_hbAccum >= AnchorRedeliverIntervalSeconds && _haveAnchor)
            {
                _hbAccum = 0f;
                BroadcastAnchor(_lastAnchor, reliable: false);
            }
        }

        /// <summary>
        /// Host: pin a fresh anchor at THIS instant — gAnchor = current game-time, tAnchor = host real-time
        /// now — bump the version, store it (for heartbeat re-delivery) and broadcast.
        /// </summary>
        private void CaptureAndBroadcastAnchor(bool paused, int speedIndex, long gAnchorTicks, bool reliable)
        {
            _hostVersion = TimeSyncProtocol.NextVersion(_hostVersion);
            long tAnchorTicks = AnchorPayload.SecondsToTicks(LocalRt());
            // Stamp the current interception time-lock so EVERY anchor (change-driven, scrub, heartbeat re-seed,
            // per-peer join, post-reload re-anchor) carries the authoritative lock state.
            var anchor = new AnchorPayload(_hostVersion, tAnchorTicks, gAnchorTicks, paused, speedIndex,
                                           Multiplayer.Network.Sync.InterceptionTimeLock.Active);
            _lastAnchor = anchor;
            _haveAnchor = true;
            BroadcastAnchor(anchor, reliable);
        }

        private void BroadcastAnchor(AnchorPayload anchor, bool reliable)
        {
            var msg = new NetworkMessage(PacketType.TimeAnchor, TimeSyncProtocol.EncodeAnchor(anchor));
            if (reliable) _engine.BroadcastToAll(msg);
            else _engine.BroadcastUnreliable(msg);
        }

        /// <summary>
        /// Host: pin + broadcast a FRESH reliable anchor at THIS instant (rca-4 post-reload re-seed).
        /// After an F2 mid-session load the geoscape game-time jumps to the loaded save's clock; the
        /// per-frame scrub detector would re-anchor eventually, but the re-seed moment wants a
        /// deterministic reliable anchor NOW so clients derive from the new clock immediately. No-op
        /// off-host / outside geoscape (a tactical save has no geo Timing; the normal change-detect
        /// re-anchors on the next geoscape entry). Never throws into the caller.
        /// </summary>
        public void HostReAnchorNow()
        {
            if (_engine == null || !_engine.IsHost) return;
            var timing = GetTiming();
            if (timing == null) return; // not in geoscape (e.g. tactical save) — nothing to anchor
            try
            {
                CaptureAndBroadcastAnchor(GetPaused(timing), GetSpeedIndex(), GetNowTicks(timing), reliable: true);
                Debug.Log("[Multiplayer] TimeSync: post-reload re-anchor broadcast (reliable)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] TimeSync HostReAnchorNow failed: " + ex.Message); }
        }

        /// <summary>Host: send the current anchor (reliable, targeted) to a freshly-connected peer.</summary>
        public void OnPeerConnectedHost(ulong peerId)
        {
            if (_engine == null || !_engine.IsHost) return;
            // Capture a current anchor if we have a clock but no anchor yet.
            if (!_haveAnchor)
            {
                var timing = GetTiming();
                if (timing != null)
                {
                    try
                    {
                        CaptureAndBroadcastAnchor(GetPaused(timing), GetSpeedIndex(), GetNowTicks(timing), reliable: true);
                    }
                    catch { /* per-frame change-detect will emit one shortly */ }
                }
                return; // the reliable broadcast above already reaches the new peer
            }
            _engine.SendToClient(peerId, new NetworkMessage(PacketType.TimeAnchor, TimeSyncProtocol.EncodeAnchor(_lastAnchor)));
        }

        // ─── HOST: answer a client clock-ping (0x39) → pong (0x3A) ────────

        public void OnClockPingReceived(ulong senderId, byte[] payload)
        {
            if (_engine == null || !_engine.IsHost) return;
            if (!TimeSyncProtocol.TryDecodePing(payload, out var ping)) return;
            var pong = new ClockPongPayload(ping.PingId, ping.T0, LocalRt());
            _engine.SendToClient(senderId, new NetworkMessage(PacketType.TimeClockPong, TimeSyncProtocol.EncodePong(pong)));
        }

        // ─── CLIENT: receive anchor (0x37) ────────────────────────────────

        public void OnAnchorReceived(byte[] payload)
        {
            if (_engine == null || _engine.IsHost) return; // host never applies its own anchor
            if (!TimeSyncProtocol.TryDecodeAnchor(payload, out var p)) return;
            if (!TimeSyncProtocol.ShouldApply(p.Version, _clientLastAppliedVersion)) return;
            _clientLastAppliedVersion = p.Version;

            bool first = !_clientHaveAnchor;
            _clientAnchor = p;
            _clientHaveAnchor = true;
            if (first) _needHardSet = true; // first anchor after (re)join → hard-set display, no lerp from garbage
        }

        /// <summary>
        /// Clear ALL client derive state on an in-place reconnect (transport drop+reconnect that does NOT
        /// tear the manager down via Shutdown→Initialize). Without this the persisted estimator/version/
        /// burst-gate are reused: no re-burst (RISK-1) and a stale <c>_clientLastAppliedVersion</c> drops
        /// every post-reconnect anchor → frozen client clock (RISK-2). After the reset the next ping burst
        /// re-seeds the offset and the first fresh anchor hard-sets the display (spec §8 reconnect).
        /// No-op on the host (it owns the clock and must keep its monotonic version).
        /// </summary>
        public void ResetClientState()
        {
            if (_engine != null && _engine.IsHost) return;
            _offset.Reset();                 // force HasOffset → false (re-arms the ping burst + gates derive)
            _clientHaveAnchor = false;
            _clientAnchor = default;
            _clientLastAppliedVersion = 0;   // accept the next anchor even if the host restarted version→1
            _needHardSet = true;             // first derive after reconnect hard-sets, no lerp from garbage
            _haveDisplay = false;
            _displayGameSeconds = 0.0;
            _burstRemaining = 0;             // SchedulePings re-arms the burst (HasOffset is now false)
            _pingAccum = 0f;
            _loggedFallback = false;
            _uiPausedKnown = false;          // force the pause/running widget visual to re-sync after reconnect
            _uiSpeedShown = int.MinValue;    // force the speed-widget index to re-push after reconnect
        }

        // ─── CLIENT: receive pong (0x3A) → feed the offset estimator ──────

        public void OnClockPongReceived(byte[] payload)
        {
            if (_engine == null || _engine.IsHost) return;
            if (!TimeSyncProtocol.TryDecodePong(payload, out var pong)) return;
            double t3 = LocalRt();
            bool accepted = _offset.AddSample(pong.T0, pong.T1, t3);
            if (!accepted) return;

            // High-RTT fallback engaged (internet link, no sub-cap sample passed) → warn once.
            if (_offset.UsedFallback && !_loggedFallback)
            {
                _loggedFallback = true;
                Debug.LogWarning("[Multiplayer] TimeSync: link RTT above cap — using best-RTT offset fallback");
            }

            // Large offset step (real OS clock jump) → hard-set the display rather than lerp across it.
            // Deduped onto the estimator's own step-detect (tracks its last reported offset).
            if (_offset.IsLargeStep(OffsetStepHardSetSeconds))
            {
                _needHardSet = true;
                Debug.Log("[Multiplayer] TimeSync: large clock-offset step → display hard-set");
            }
        }

        // ─── CLIENT: per-frame derive + display + clock override ──────────

        private void ClientTick()
        {
            // Reset host-side cache when not host.
            _haveCache = false;
            // Default the V2 display gate OFF; WriteClock re-arms it (= pin) only when it actually runs this
            // frame. So the pre-sync / not-in-geoscape early-returns below leave the display postfix inert.
            DisplayActive = false;

            SchedulePings();

            if (!_clientHaveAnchor || !_offset.HasOffset) return; // gate: need an anchor AND a seeded offset
            var timing = GetTiming();
            if (timing == null) return; // not in geoscape → no-op

            double serverNow = LocalRt() + _offset.Offset;
            double rate = _clientAnchor.Paused ? 0.0 : ScaleForIndex(_clientAnchor.SpeedIndex, timing);
            double gAnchorSeconds = AnchorPayload.TicksToSeconds(_clientAnchor.GAnchorTicks);
            double tAnchorSeconds = AnchorPayload.TicksToSeconds(_clientAnchor.TAnchorTicks);
            double auth = AnchorClock.Derive(gAnchorSeconds, rate, tAnchorSeconds, serverNow);

            if (_needHardSet || !_haveDisplay)
            {
                _displayGameSeconds = auth; // hard-set: no lerp from garbage
                _haveDisplay = true;
                _needHardSet = false;
            }
            else
            {
                double k = AnchorClock.LerpFactor(Time.unscaledDeltaTime, VisualTauSeconds);
                _displayGameSeconds = AnchorClock.VisualStep(_displayGameSeconds, auth, k, SnapThresholdSeconds, rate);
            }

            WriteClock(timing, _displayGameSeconds, _clientAnchor.Paused, (float)rate);
        }

        /// <summary>
        /// Drive the client geoscape clock. TWO modes, gated by <see cref="ClientSimFreezeV2Gate"/>:
        ///
        ///  • V2 PIN (default; active client + V1 freeze on) — the client is a PURE MIRROR, so the sim clock
        ///    must NOT advance: we do NOT rewrite StartTime. With <c>Timing.Paused</c> pinned true (asserted at
        ///    each geoscape load by <see cref="FreezeClientGeoSim"/>, plus the cheap per-frame drift guard
        ///    below) <c>OwnNow</c> is constant, so <c>Now = StartTime + OwnNow</c> stays CONSTANT — the geoscape
        ///    sim clock is genuinely frozen and every producer stays Max'd (matches the "Now frozen" assumption
        ///    the other mirrors already rely on, e.g. GeoVehicleExploreMirror). The HUD date/time widget can no
        ///    longer read a live <c>Now</c>, so it is repainted DISPLAY-ONLY from the host mirror by
        ///    <c>ClientTimeDateDisplayFreezePatch</c> (fed via <see cref="DisplayHostGameSeconds"/>). This
        ///    replaces V1's per-frame <c>ProcessInstanceData</c> (the client-only per-frame burner) with one
        ///    bool read in steady state.
        ///
        ///  • V1 / flag-OFF (byte-identical rollback) — overwrite the clock to the displayed game-time via the
        ///    game's own save/load seam <c>Timing.ProcessInstanceData</c> (R5: NOT GeoscapeView.SetGamePauseState
        ///    — the TimeLimit guard). StartTime = display, OwnNow = 0 ⇒ Now == StartTime == display, ADVANCING
        ///    each frame; the native widget reads that <c>Now</c> itself. ProcessInstanceData sets fields +
        ///    re-anchors but does NOT call RescheduleUpdateables; Scale/Paused are cosmetic (overwritten next
        ///    frame). Note: because that advancing <c>Now</c> is what V2 removes, do NOT re-describe the sim as
        ///    "frozen" under this branch — under V1 the sim clock moves.
        ///
        /// Both modes publish the host cosmetic glyph (paused/speed) for the glyph-decouple patch and mirror the
        /// speed/pause widget on-change under the echo-guard.
        /// </summary>
        private void WriteClock(object timing, double displayGameSeconds, bool paused, float scale)
        {
            EnsureReflection();
            if (_timingInstanceDataType == null || _fromTimeSpanMethod == null || _timeUnitZero == null
                || _processInstanceDataMethod == null || _tidStartTimeField == null) return;

            // Inc4 S1 (§3.2/§3.3): DISPLAY-clock vs SIM-clock split. Under the freeze the sim _paused is pinned
            // true (so producers stay Max'd — see ClientSimFreeze.SimPaused / FreezeClientGeoSim); the host's
            // real paused/speed drive ONLY the cosmetic widget glyph, published here for the glyph-decouple
            // patch. Flag-OFF: SimPaused(false,paused)==paused and the glyph statics are unread → byte-unchanged.
            bool freeze = ClientSimFreeze.ShouldFreeze(
                ClientSimFreeze.Enabled,
                _engine != null,
                _engine != null && _engine.IsActiveSession,
                _engine != null && _engine.IsHost);
            bool simPaused = ClientSimFreeze.SimPaused(freeze, paused);
            // Inc4 V2: when the gate says pin, keep the sim clock constant and route the clock display through
            // the host mirror instead of advancing Now.
            bool pin = ClientSimFreezeV2Gate.ShouldPinSim(ClientSimFreezeV2Gate.Enabled, freeze);
            GlyphHostPaused = paused;
            GlyphHostSpeedIndex = _clientAnchor.SpeedIndex;
            DisplayHostGameSeconds = displayGameSeconds; // fed to ClientTimeDateDisplayFreezePatch
            DisplayActive = pin;                         // gate the display postfix (V2 pin only)

            IsApplyingRemote = true;
            try
            {
                if (pin)
                {
                    // V2 TRUE SIM PIN: do NOT advance StartTime — Timing.Now stays constant (sim frozen). Cheap
                    // per-frame DRIFT GUARD (one bool read, no clock write in steady state): if a native path
                    // unpaused the geoscape Timing (V1 leaned on the per-frame ProcessInstanceData to re-pin
                    // _paused each frame — we removed it), re-assert the freeze via the setter + reschedule so
                    // every producer re-Max's. FreezeClientGeoSim is self-gated + best-effort; it fires only on
                    // an actual drift, which is rare (client time-control input is relayed, not applied locally).
                    if (!GetPaused(timing)) FreezeClientGeoSim();
                }
                else
                {
                    // V1 / flag-OFF: advance the sim clock every frame via ProcessInstanceData (StartTime=display).
                    // PERF: reuse ONE TimingInstanceData (the fixed-clock fields below are constant Zero, so a
                    // single persistent instance is sufficient — only StartTime/Paused/Scale vary per frame).
                    if (_tidScratch == null) _tidScratch = Activator.CreateInstance(_timingInstanceDataType);
                    var tid = _tidScratch;
                    object zero = _timeUnitZero;

                    _arg1[0] = TimeSpan.FromSeconds(displayGameSeconds);
                    object startTime = _fromTimeSpanMethod.Invoke(null, _arg1);

                    _tidPausedField.SetValue(tid, simPaused);
                    _tidScaleField.SetValue(tid, scale);
                    _tidStartTimeField.SetValue(tid, startTime);
                    // INTENTIONAL / verified-inert: the fixed clock (StartFixedTime/OwnFixedNow → FixedNow) is
                    // pinned to Zero. The only FixedNow consumers — TimingScheduler.Update (Fixed phase) and
                    // PhoenixGame fast-physics — read the ROOT game TimeSource.Timing, NOT this geoscape child
                    // Timing we overwrite. So pinning the child's FixedNow=0 each frame is observably inert for
                    // the client (verified vs decompile Timing.cs/TimingScheduler.cs:679). The geoscape clock
                    // display reads Now (= StartTime, OwnNow pinned 0), which we set above.
                    _tidStartFixedTimeField.SetValue(tid, zero);
                    _tidOwnNowField.SetValue(tid, zero);
                    _tidOwnFixedNowField.SetValue(tid, zero);

                    _arg1[0] = tid;
                    _processInstanceDataMethod.Invoke(timing, _arg1);
                }

                // Keep the speed widget index in sync (cosmetic), under the echo-guard. ON-CHANGE only:
                // SelectTimePreset already early-outs internally, but gating here removes the per-frame
                // reflection/invoke (and widget access) entirely in steady state.
                if (_uiSpeedShown != _clientAnchor.SpeedIndex)
                {
                    MirrorSpeedUi(_clientAnchor.SpeedIndex);
                    _uiSpeedShown = _clientAnchor.SpeedIndex;
                }
                // Keep the pause/running VISUAL (white+blink vs yellow) in sync with the host anchor.
                MirrorPauseUi(paused);
                // Grey the time-control buttons while the host's interception time-lock is active (anchor bit).
                MirrorTimeLockUi(_clientAnchor.Locked);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] TimeSync client clock write failed: " + ex.Message);
            }
            finally
            {
                IsApplyingRemote = false;
            }
        }

        // ─── CLIENT: ping scheduler ───────────────────────────────────────

        private void SchedulePings()
        {
            // Seed a burst the first time we tick as a client (or after a reconnect resets state):
            // no offset yet and no burst in flight → arm one.
            if (_burstRemaining == 0 && !_offset.HasOffset)
                _burstRemaining = OffsetBurstCount;

            _pingAccum += Time.unscaledDeltaTime;
            float interval = _burstRemaining > 0 ? OffsetBurstIntervalSeconds : OffsetSteadyIntervalSeconds;
            if (_pingAccum < interval) return;
            _pingAccum = 0f;

            SendClockPing();
            if (_burstRemaining > 0) _burstRemaining--;
        }

        private void SendClockPing()
        {
            var ping = new ClockPingPayload(_nextPingId++, LocalRt());
            _engine.SendToHost(new NetworkMessage(PacketType.TimeClockPing, TimeSyncProtocol.EncodePing(ping)));
        }

        // ─── HOST: apply a relayed client time-control request (0x38) ─────

        public void OnClientRequestReceived(ulong senderId, byte[] payload)
        {
            if (_engine == null || !_engine.IsHost) return;
            if (!TimeSyncProtocol.TryDecodeRequest(payload, out var p)) return;

            // INTERCEPTION TIME-LOCK (host-authoritative hard lock): while an air-combat interception is in
            // progress the shared clock is locked for EVERYONE — reject every relayed client time request
            // regardless of permission (the client's widget is greyed via the anchor Locked bit; this is the
            // authoritative belt against an in-flight / forged request). The window-close anchor re-enables it.
            if (Multiplayer.Network.Sync.InterceptionTimeLock.Active) return;

            // HOST BLOCKING-PROMPT GATE (belt, mirrors the intent-gate): while a blocking native prompt
            // (ambush / site-mission brief) is pending the host is fully modal-locked — no time flow until
            // it resolves. Reject relayed time requests the same way the interception lock above does.
            if (Multiplayer.Network.Sync.HostBlockingPromptGate.IsArmed) return;

            // PERMISSION GATE (host-authoritative): the ControlTime gate existed only client-side at
            // RelayTimeRequest; a client with the bit revoked (or a malformed/forged packet) could still
            // drive the host clock. Resolve the sender's player Guid (mirrors SyncEngine.ResolveActor) and
            // reject unless it holds ControlTime. No mapping / no permission → drop silently (host stays
            // authoritative; the heartbeat keeps that client's clock correct regardless).
            Guid sender = ResolveSender(senderId);
            if (!PermissionGate.CheckFor(sender, ActionCategory.TimeControl)) return;

            var timing = GetTiming();
            if (timing == null) return; // host not in geoscape → ignore

            // Last-writer-wins = ARRIVAL ORDER on the host's single thread. NO cross-client ts compare.
            IsApplyingRemote = true;
            try
            {
                SetScale(timing, ScaleForIndex(p.SpeedIndex, timing));
                MirrorSpeedUi(p.SpeedIndex);
                SetPaused(timing, p.Paused);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] TimeSync host-apply request failed: " + ex.Message);
            }
            finally
            {
                IsApplyingRemote = false;
            }

            // Capture + broadcast a fresh anchor at this instant (snappy round-trip; the per-frame
            // change-detect would also catch it). Use TryGetSpeedIndex (NOT GetSpeedIndex, which defaults
            // to 1 on a widget-read miss) so a momentary widget-absence doesn't anchor a wrong speed — the
            // heartbeat / next-frame change-detect recovers.
            try
            {
                if (!TryGetSpeedIndex(out int idx)) return;
                bool paused = GetPaused(timing);
                long gTicks = GetNowTicks(timing);
                _cachedPaused = paused; _cachedSpeedIndex = idx; _haveCache = true; _hbAccum = 0f;
                CaptureAndBroadcastAnchor(paused, idx, gTicks, reliable: true);
            }
            catch { /* heartbeat will recover */ }
        }

        /// <summary>Resolve a transport peerId to its persistent player Guid (mirrors SyncEngine.ResolveActor).</summary>
        private Guid ResolveSender(ulong peerId)
            => _engine.Session != null && _engine.Session.Clients.TryGetValue(peerId, out var ci)
                ? ci.PlayerGuid
                : Guid.Empty;

        // ─── Native writers ───────────────────────────────────────────────

        private static void SetPaused(object timing, bool paused)
            => _timingPausedProp.SetValue(timing, paused, null);

        private static void SetScale(object timing, float scale)
            => _timingScaleProp.SetValue(timing, scale, null);

        private static void MirrorSpeedUi(int speedIndex)
        {
            var tc = FindTimeControl();
            if (tc == null || _tcSelectTimePresetMethod == null) return;
            // SelectTimePreset(int): clamps, sets SelectedPresetTime, writes Timing.Scale, updates the
            // widget, fires OnTimeSpeedChangeRequested (our input-intercept lets it through under
            // IsApplyingRemote). No-op if the index already matches.
            _tcSelectTimePresetMethod.Invoke(tc, new object[] { speedIndex });
        }

        /// <summary>
        /// Mirror the host pause state into the native time widget's VISUAL indicator (white + blinking
        /// pause glyph when paused vs yellow when running). RCA: the widget drives that look from its
        /// Animator params "IsPaused"/"TimeMode", pushed only by UIModuleTimeControl.SetTimerSpeedState(),
        /// which runs only when the dirty flags _updatePausedState/_updateTimeSpeedState are set — and
        /// those are armed ONLY by Timing.Paused's setter event (TimingOnPausedEvent). The client never
        /// touches that setter: it writes the clock via Timing.ProcessInstanceData, which sets the _paused
        /// FIELD directly and fires NO OnPausedEvent — so the widget never re-evaluates and stays stuck in
        /// its initial (paused/white) look even while the synced clock advances.
        ///
        /// Fix (native-UI-first, NO sim side-effect): ProcessInstanceData has ALREADY written the widget's
        /// _timing._paused field to the host value this same frame, so we just re-arm the widget's own
        /// dirty flag (_updatePausedState=true). Its next Update() then calls SetTimerPausedState +
        /// SetTimerSpeedState, which read _timing.Paused (= the value we wrote) and push the correct
        /// IsPaused/TimeMode to the Animator. We DO NOT set Timing.Paused via the setter (that would fire
        /// events / reschedule and is exactly what the anchor design avoids) and we DO NOT advance the
        /// local sim — the anchor stays the only time driver. Idempotent: re-armed only when the shown
        /// state actually changes (mirrors the MirrorSpeedUi early-out), so no per-frame flag spam.
        /// </summary>
        private void MirrorPauseUi(bool paused)
        {
            if (_uiPausedKnown && _uiPausedShown == paused) return; // unchanged → no-op (idempotent)
            var tc = FindTimeControl();
            if (tc == null || _tcUpdatePausedField == null) return; // widget momentarily absent — retry next frame
            // Re-arm the widget's own paused-state dirty flag; its Update() cascades to _updateTimeSpeedState
            // and re-pushes the Animator params from the already-written _timing._paused. No setter, no event.
            _tcUpdatePausedField.SetValue(tc, true);
            _uiPausedKnown = true;
            _uiPausedShown = paused;
        }

        /// <summary>
        /// Client: grey / restore the native geoscape time-control widget while the host's interception
        /// time-lock is active (the anchor <c>Locked</c> bit). Reuses the codebase's native "grey but keep
        /// readable" pattern (BlockingModalClientLock.SetModalInteractable): a get-or-added CanvasGroup with
        /// <c>interactable=false</c> renders the child pause/speed buttons' NATIVE disabled grey; alpha and
        /// blocksRaycasts stay untouched so the clock display + date stay fully readable. Idempotent (toggles
        /// only on change — no per-frame spam) and best-effort. Never greys unless it has to: if we never
        /// locked, the widget is left byte-for-byte native (the group is only added on the first lock).
        /// </summary>
        private void MirrorTimeLockUi(bool locked)
        {
            if (_uiLockKnown && _uiLockShown == locked) return; // unchanged → no-op (idempotent)
            var tc = FindTimeControl() as Component;
            if (tc == null) return;                             // widget momentarily absent — retry next frame
            var cg = tc.gameObject.GetComponent<CanvasGroup>();
            if (cg == null)
            {
                if (!locked) { _uiLockKnown = true; _uiLockShown = false; return; } // never greyed → nothing to add
                cg = tc.gameObject.AddComponent<CanvasGroup>();
            }
            cg.interactable = !locked;
            _uiLockKnown = true;
            _uiLockShown = locked;
        }

        private static float ScaleForIndex(int idx, object timing)
        {
            var tc = FindTimeControl();
            if (tc != null && _tcPresetTimesField != null)
            {
                // PERF: cache the PresetTimes array off the live widget (FindTimeControl drops the cache
                // when the widget is replaced). Avoids a per-frame field GetValue + cast.
                var presets = _presetTimesCached ?? (_presetTimesCached = _tcPresetTimesField.GetValue(tc) as float[]);
                if (presets != null && presets.Length > 0)
                {
                    int clamped = Mathf.Clamp(idx, 0, presets.Length - 1);
                    return presets[clamped];
                }
            }
            // Fallback: keep current scale.
            try { return (float)_timingScaleProp.GetValue(timing, null); }
            catch { return 1f; }
        }

        // ─── CLIENT INPUT relay helper (called by the Harmony intercept patches) ──

        /// <summary>
        /// Client-side: relay a user's pause/speed request to the host (last-writer-wins) and block the
        /// local commit. Returns true if the request was relayed (caller must block local write);
        /// false if it should fall through locally (no session / not geoscape / no permission).
        /// </summary>
        public bool RelayTimeRequest(bool paused, int speedIndex)
        {
            if (_engine == null || !_engine.IsActive || _engine.IsHost) return false;

            // Permission gate (ControlTime bit). If not granted, swallow the input (block local) so it
            // can't desync — host stays authoritative.
            if (!PermissionManager.HasCampaignPermission(ClientIdentity.PlayerGuid, CampaignPermission.ControlTime))
                return true;

            var p = new TimeRequestPayload(paused, speedIndex);
            var msg = new NetworkMessage(PacketType.TimeRequest, TimeSyncProtocol.EncodeRequest(p));
            _engine.SendToHost(msg);
            return true;
        }

        /// <summary>Current geoscape SelectedPresetTime index (for building a pause request payload).</summary>
        public int CurrentSpeedIndex() => GetSpeedIndex();

        /// <summary>
        /// Current geoscape paused state (for building a speed request payload); false if unknown.
        /// Review fix BUG 1a: under the client sim-freeze the local <c>Timing.Paused</c> is PINNED true,
        /// so a speed click would relay {Paused=true} and PAUSE the host — when the freeze is active and
        /// a host anchor exists, read the ANCHOR's paused instead. Host / flag-OFF / pre-anchor: local
        /// read, byte-identical to the pre-fix behavior.
        /// </summary>
        public bool CurrentPaused()
        {
            bool localPaused = false;
            var t = GetTiming();
            if (t != null)
            {
                try { localPaused = GetPaused(t); } catch { localPaused = false; }
            }
            bool freeze = ClientSimFreeze.ShouldFreeze(
                ClientSimFreeze.Enabled,
                _engine != null,
                _engine != null && _engine.IsActiveSession,
                _engine != null && _engine.IsHost);
            return ClientSimFreeze.RelayCurrentPaused(freeze, _clientHaveAnchor, _clientAnchor.Paused, localPaused);
        }
    }
}
