using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network.MessageLayer;
using Multipleer.Validation;
using UnityEngine;

namespace Multipleer.Network.TimeSync
{
    /// <summary>
    /// Unity/native GLUE for host-authoritative geoscape time sync. The pure decode/decision logic
    /// lives in <see cref="TimeSyncProtocol"/>; this class binds it to the live engine state:
    ///
    ///  • HOST  — each frame (driven from <see cref="NetworkEngine.Update"/>) reads the current
    ///            {Paused, SpeedIndex, Now} of the geoscape clock. On a real change (incl. native
    ///            auto-pauses, which flip Timing.Paused) it reliably broadcasts an authoritative
    ///            TimeState; it also emits an unreliable state-heartbeat at ~3 Hz (the watchdog).
    ///  • CLIENT— applies host TimeState: sets Timing.Scale + Timing.Paused for smooth local advance,
    ///            mirrors the UI, and hard-resnaps via Timing.ProcessInstanceData only when drift
    ///            exceeds the threshold. Client time-control INPUT is intercepted by the Harmony
    ///            patches and relayed to the host as a TimeRequest; host applies last-writer-wins.
    ///
    /// All native access is reflection-based (mirrors <c>TacticalPatches</c>) so the mod tolerates
    /// game-version / TFTV variance and is inert outside the geoscape.
    /// </summary>
    public class TimeSyncManager
    {
        // ~3 Hz authoritative state-heartbeat over the unreliable channel (idempotent + always-current,
        // so packet loss self-corrects on the next tick). This heartbeat IS the lag watchdog.
        private const float HeartbeatIntervalSeconds = 0.33f;

        private readonly NetworkEngine _engine;

        /// <summary>
        /// Echo-guard: set while the client applies host state (or the host applies a relayed client
        /// request) so the time-control input-intercept patches let our own programmatic writes
        /// through instead of bouncing them back onto the wire. Mirrors the CommandRelay.IsApplying
        /// pattern. Static so the Harmony prefixes (which have no instance handle) can read it.
        /// </summary>
        public static bool IsApplyingRemote { get; private set; }

        // Client: last applied HOST-stamped TimeState version (stale-drop on the single host clock —
        // NOT the per-sender header ts, which would skew across machines).
        private long _clientLastAppliedVersion;
        // Host: monotonic ordering counter stamped onto every outgoing TimeState (single source).
        private long _hostVersion;

        // Host change-detection cache + heartbeat accumulator.
        private bool _haveCache;
        private bool _cachedPaused;
        private int _cachedSpeedIndex;
        private float _hbAccum;

        // ─── Cached reflection handles (resolved lazily once the types exist) ──
        private static Type _geoLevelType;
        private static Type _gameUtlType;
        private static Type _geoscapeViewType;
        private static Type _timeControlType;
        private static Type _timingInstanceDataType;
        private static MethodInfo _currentLevelMethod;
        private static bool _reflectionReady;

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
            _currentLevelMethod = _gameUtlType != null
                ? AccessTools.Method(_gameUtlType, "CurrentLevel")
                : null;
            _reflectionReady = _geoLevelType != null && _currentLevelMethod != null;
        }

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
                return AccessTools.Property(geo.GetType(), "Timing")?.GetValue(geo, null);
            }
            catch { return null; }
        }

        private static UnityEngine.Object FindTimeControl()
            => _timeControlType != null ? UnityEngine.Object.FindObjectOfType(_timeControlType) : null;

        // ─── State readers ────────────────────────────────────────────────

        private static bool GetPaused(object timing)
            => (bool)AccessTools.Property(timing.GetType(), "Paused").GetValue(timing, null);

        private static double GetNowSeconds(object timing)
        {
            var now = AccessTools.Property(timing.GetType(), "Now").GetValue(timing, null); // TimeUnit
            var ts = AccessTools.Property(now.GetType(), "TimeSpan").GetValue(now, null);    // TimeSpan
            return ((TimeSpan)ts).TotalSeconds;
        }

        private static int GetSpeedIndex()
        {
            var tc = FindTimeControl();
            if (tc == null) return 1; // sane default (PresetTimes default index)
            var field = AccessTools.Field(tc.GetType(), "SelectedPresetTime");
            return field != null ? (int)field.GetValue(tc) : 1;
        }

        // ─── Per-frame tick (called from NetworkEngine.Update) ────────────

        public void Tick()
        {
            if (_engine == null || !_engine.IsActive || !_engine.IsHost)
            {
                _haveCache = false;
                return;
            }

            var timing = GetTiming();
            if (timing == null) { _haveCache = false; return; }

            bool paused;
            int idx;
            double now;
            try
            {
                paused = GetPaused(timing);
                idx = GetSpeedIndex();
                now = GetNowSeconds(timing);
            }
            catch { return; }

            // Authoritative broadcast on any real change (covers native auto-pauses).
            bool changed = !_haveCache || paused != _cachedPaused || idx != _cachedSpeedIndex;
            if (changed)
            {
                _cachedPaused = paused;
                _cachedSpeedIndex = idx;
                _haveCache = true;
                _hbAccum = 0f;
                Broadcast(new TimeStatePayload(paused, idx, now), reliable: true);
                return;
            }

            // Periodic unreliable state-heartbeat (watchdog).
            _hbAccum += Time.unscaledDeltaTime;
            if (_hbAccum >= HeartbeatIntervalSeconds)
            {
                _hbAccum = 0f;
                Broadcast(new TimeStatePayload(paused, idx, now), reliable: false);
            }
        }

        private void Broadcast(TimeStatePayload p, bool reliable)
        {
            // Stamp the single-source host-monotonic ordering version onto every outgoing TimeState
            // (reliable change, heartbeat, AND request-echo) so clients order on one clock, never on
            // the cross-machine header ts.
            _hostVersion = TimeSyncProtocol.NextVersion(_hostVersion);
            p.Version = _hostVersion;
            var msg = new NetworkMessage(PacketType.TimeState, TimeSyncProtocol.EncodeTimeState(p));
            if (reliable) _engine.BroadcastToAll(msg);
            else _engine.BroadcastUnreliable(msg);
        }

        // ─── CLIENT: apply authoritative host state ───────────────────────

        public void OnHostStateReceived(byte[] payload, long headerTs)
        {
            if (_engine == null || _engine.IsHost) return; // host never applies its own state
            if (!TimeSyncProtocol.TryDecodeTimeState(payload, out var p)) return;
            // Stale-drop on the HOST-stamped version (single clock), NOT the per-sender header ts.
            if (!TimeSyncProtocol.ShouldApply(p.Version, _clientLastAppliedVersion)) return;
            _clientLastAppliedVersion = p.Version;

            var timing = GetTiming();
            if (timing == null) return; // not in geoscape → no-op

            IsApplyingRemote = true;
            try
            {
                // 1) Lag resnap FIRST (raw field write via ProcessInstanceData; fires no events) so the
                //    subsequent property writes can emit the UI events on top of the corrected clock.
                double clientNow = GetNowSeconds(timing);
                if (TimeSyncProtocol.NeedsResnap(clientNow, p.Now))
                    ResnapClock(timing, p);

                // 2) Smooth local advance. Write Timing.Scale DIRECTLY from the index (independent of
                //    the UI mirror) — SelectTimePreset is a no-op when the preset index already matches,
                //    which would otherwise leave Scale un-rewritten after a resnap. Then mirror the UI
                //    widget + pause state (setting Paused fires OnPausedEvent → UI refresh).
                SetScale(timing, ScaleForIndex(p.SpeedIndex, timing));
                MirrorSpeedUi(p.SpeedIndex);
                SetPaused(timing, p.Paused);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] TimeSync apply failed: " + ex.Message);
            }
            finally
            {
                IsApplyingRemote = false;
            }
        }

        // ─── HOST: apply a relayed client time-control request ────────────

        public void OnClientRequestReceived(byte[] payload, long headerTs)
        {
            if (_engine == null || !_engine.IsHost) return;
            if (!TimeSyncProtocol.TryDecodeTimeState(payload, out var p)) return;

            var timing = GetTiming();
            if (timing == null) return; // host not in geoscape → ignore

            // Last-writer-wins = ARRIVAL ORDER on the host's single thread. NO cross-client ts compare
            // (sender wall-clock skew could otherwise permanently starve a slightly-behind client).
            // Each request is simply applied; the most-recently-processed one wins.
            IsApplyingRemote = true;
            try
            {
                SetScale(timing, ScaleForIndex(p.SpeedIndex, timing));
                MirrorSpeedUi(p.SpeedIndex);
                SetPaused(timing, p.Paused);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] TimeSync host-apply request failed: " + ex.Message);
            }
            finally
            {
                IsApplyingRemote = false;
            }

            // Echo the authoritative result back to ALL immediately (incl. originator); the per-frame
            // change-detect would also catch it, but this makes the round-trip snappy.
            try
            {
                bool paused = GetPaused(timing);
                int idx = GetSpeedIndex();
                double now = GetNowSeconds(timing);
                _cachedPaused = paused; _cachedSpeedIndex = idx; _haveCache = true; _hbAccum = 0f;
                Broadcast(new TimeStatePayload(paused, idx, now), reliable: true);
            }
            catch { /* heartbeat will recover */ }
        }

        // ─── Native writers ───────────────────────────────────────────────

        private static void SetPaused(object timing, bool paused)
            => AccessTools.Property(timing.GetType(), "Paused").SetValue(timing, paused, null);

        private static void SetScale(object timing, float scale)
            => AccessTools.Property(timing.GetType(), "Scale").SetValue(timing, scale, null);

        private static void MirrorSpeedUi(int speedIndex)
        {
            var tc = FindTimeControl();
            if (tc == null) return;
            // SelectTimePreset(int): clamps, sets SelectedPresetTime, writes Timing.Scale, updates the
            // speed widget, and fires OnTimeSpeedChangeRequested (our input-intercept lets it through
            // under IsApplyingRemote). No-op if the index already matches.
            var m = AccessTools.Method(tc.GetType(), "SelectTimePreset", new[] { typeof(int) });
            m?.Invoke(tc, new object[] { speedIndex });
        }

        /// <summary>
        /// Hard-set the client clock to the host's Now via the game's own save/load seam
        /// (Timing.ProcessInstanceData) — R5: NOT GeoscapeView.SetGamePauseState (which carries a
        /// TimeLimit unpause guard). We synthesize a TimingInstanceData with StartTime = host Now and
        /// OwnNow = 0 so the derived Now == host Now, carrying the authoritative Paused/Scale too.
        /// </summary>
        private void ResnapClock(object timing, TimeStatePayload p)
        {
            EnsureReflection();
            if (_timingInstanceDataType == null) return;

            var tid = Activator.CreateInstance(_timingInstanceDataType);
            var tidType = _timingInstanceDataType;

            // TimeUnit.FromTimeSpan(TimeSpan.FromSeconds(now)) → exact Now.
            var timeUnitType = AccessTools.TypeByName("Base.Core.TimeUnit");
            var fromTimeSpan = AccessTools.Method(timeUnitType, "FromTimeSpan", new[] { typeof(TimeSpan) });
            object startTime = fromTimeSpan.Invoke(null, new object[] { TimeSpan.FromSeconds(p.Now) });
            object zero = AccessTools.Field(timeUnitType, "Zero").GetValue(null);

            float scale = ScaleForIndex(p.SpeedIndex, timing);

            AccessTools.Field(tidType, "Paused").SetValue(tid, p.Paused);
            AccessTools.Field(tidType, "Scale").SetValue(tid, scale);
            AccessTools.Field(tidType, "StartTime").SetValue(tid, startTime);
            AccessTools.Field(tidType, "StartFixedTime").SetValue(tid, zero);
            AccessTools.Field(tidType, "OwnNow").SetValue(tid, zero);
            AccessTools.Field(tidType, "OwnFixedNow").SetValue(tid, zero);

            AccessTools.Method(timing.GetType(), "ProcessInstanceData", new[] { _timingInstanceDataType })
                .Invoke(timing, new[] { tid });
        }

        private static float ScaleForIndex(int idx, object timing)
        {
            var tc = FindTimeControl();
            if (tc != null)
            {
                var presets = AccessTools.Field(tc.GetType(), "PresetTimes")?.GetValue(tc) as float[];
                if (presets != null && presets.Length > 0)
                {
                    int clamped = Mathf.Clamp(idx, 0, presets.Length - 1);
                    return presets[clamped];
                }
            }
            // Fallback: keep current scale.
            try { return (float)AccessTools.Property(timing.GetType(), "Scale").GetValue(timing, null); }
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

            // Permission gate (ControlTime bit). If the client isn't granted time control, swallow the
            // input (block local) so it can't desync — host stays authoritative.
            if (!PermissionManager.HasCampaignPermission(ClientIdentity.PlayerGuid, CampaignPermission.ControlTime))
                return true;

            var p = new TimeStatePayload(paused, speedIndex, 0.0);
            var msg = new NetworkMessage(PacketType.TimeRequest, TimeSyncProtocol.EncodeTimeState(p));
            _engine.SendToHost(msg);
            return true;
        }

        /// <summary>Current geoscape SelectedPresetTime index (for building a pause request payload).</summary>
        public int CurrentSpeedIndex() => GetSpeedIndex();

        /// <summary>Current geoscape paused state (for building a speed request payload); false if unknown.</summary>
        public bool CurrentPaused()
        {
            var t = GetTiming();
            if (t == null) return false;
            try { return GetPaused(t); } catch { return false; }
        }
    }
}
