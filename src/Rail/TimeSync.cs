using System;
using System.IO;
using System.Reflection;
using System.Text;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// GEOSCAPE TIME CONTROL intent seam (law 4a). Same shape as <see cref="PersonnelSync"/>: the
    /// client BLOCKS the native clock mutation and sends an intent; the host runs the SAME native
    /// path; the result reaches every peer through the existing host→client machinery — Paused/Scale
    /// as ordinary "T" leaves via the property setters, the clock base via the "TA" anchor, both
    /// same-frame because the host's Paused/Scale setters raise EffectiveScaleChangedEvent which
    /// DiffEngine's change-driven flush already subscribes (DiffEngine.ArmChangeDrivenFlush).
    /// There is NO host→all message here: this file is intent-only.
    ///
    /// SEAM CHOICE — the two native funnels every geoscape time write routes through (call-site
    /// sweep over the decompile):
    ///   • <c>UIModuleTimeControl.OnPauseTime</c> (UIModuleTimeControl.cs:182) — the pause/resume
    ///     GESTURE: play/pause button + keyboard land in OnPauseTimeKeyPressed:173 → here, where the
    ///     module writes <c>_timing.Paused</c> DIRECTLY (:187) before raising
    ///     OnTimePauseChangeRequested. Blocking the method blocks both the write and the event.
    ///   • <c>UIModuleTimeControl.UpdateSelectedTime</c> (:268) — the ONE UI write of
    ///     <c>_timing.Scale</c>: speed +/- buttons → ChangeTime → SelectTimePreset:192 → here.
    ///   • <c>GeoscapeView.SetGamePauseState</c> (GeoscapeView.cs:1256) — the PROGRAMMATIC pause
    ///     funnel: screen-open pauses (UIStateResearch:22, UIStateDiplomacy:27, UIStateManufacturing:51,
    ///     UIStateGeoscapeOptions:36, UIStateGeoscapeLog:18, UIModuleGeoSectionBar:119-194), the
    ///     launch resume (UIStateVehicleSelected:422/897) and the gesture event itself
    ///     (UIStateNothingSelected:99). Captured too: a client screen-open pause must pause EVERYONE
    ///     (the host's own screen-open pause already does, via the rail) — and any local-only write
    ///     is exactly the free-running-clock bug this seam exists to kill.
    /// There is no separate time-skip control in the geoscape: every speed/pause control routes
    /// through the two module methods above. NOT captured (deliberate): the quick-save/load
    /// coroutines' direct <c>Timing.Paused</c> writes (GeoscapeView.cs:1146/1154/1171 — transient,
    /// restored around the save) and level-init writes (GeoLevelController.cs:493/568/611 — reload
    /// boundary, TimeAnchor.Reset owns it); residual local drift from those is corrected by
    /// <see cref="TimeAnchor.EnforceDrift"/> on the tick below.
    ///
    /// The interception mini-game reuses the same module type bound to ITS OWN clock
    /// (UIStateInterception.cs:566 — <c>_interceptionGameController.Timing</c>, not the level clock);
    /// that one stays fully local, hence the <see cref="IsLevelClock"/> guard.
    ///
    /// HOST replay runs the identical native funnels: <c>GeoscapeView.SetGamePauseState</c> for
    /// pause (keeps the TimeLimit guard), <c>UIModuleTimeControl.SelectTimePreset</c> for speed
    /// (clamps the index natively, keeps the host's speed label honest — the module never repaints
    /// its label from a raw Scale write). Validation is trivial by design: co-op parity, any
    /// connected peer may control time. The wire carries the PRESET INDEX, never a raw float scale —
    /// both peers ship identical PresetTimes (mod parity, law 10), so the index is the stable id.
    /// </summary>
    public static class TimeSync
    {
        // Intent ops (GeoTimeIntent inner payload: [nonce:u32][op:u8][val:u8]).
        private const byte OpPause = 1;  // val = 0 resume / 1 pause      → SetGamePauseState
        private const byte OpSpeed = 2;  // val = preset index            → SelectTimePreset

        private static readonly IntentDedup Intents = new IntentDedup();
        private static uint _nextIntentNonce;
        private static float _nextEnforceAt;

        // The module's clock binding (private) — read only to tell the LEVEL clock from the
        // interception one; _updateTimeSpeedState is the view-refresh half of the blocked
        // UpdateSelectedTime, re-stated so the speed label repaints from the staged preset.
        private static readonly FieldInfo FTiming = AccessTools.Field(typeof(UIModuleTimeControl), "_timing");
        private static readonly FieldInfo FUpdateSpeedState = AccessTools.Field(typeof(UIModuleTimeControl), "_updateTimeSpeedState");

        private static bool _bindChecked;

        private static bool BindOk()
        {
            if (!_bindChecked)
            {
                _bindChecked = true;
                if (FTiming == null || FUpdateSpeedState == null)
                    Debug.LogError("[MP][time] FIELD BIND FAILED on UIModuleTimeControl — " +
                                   "client time gestures CANNOT be captured; clocks will desync.");
                else
                    Debug.Log("[MP][time] module fields bound");
            }
            return FTiming != null;
        }

        public static void Reset()
        {
            ResetForReloadBoundary();
            Intents.Reset();
        }

        /// <summary>Stateless per intent (same rca-3 contract as <see cref="PersonnelSync"/>):
        /// nothing geoscape-bound cached, nonce stream persists symmetrically.</summary>
        public static void ResetForReloadBoundary() { }

        public static void ResetIntentDedupForPeer(ulong peerId) => Intents.ResetPeer(peerId);

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>TRUE = let the native write run (host, solo, or inside an apply).</summary>
        private static bool ShouldRunNative()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            return SyncApplyScope.Active; // law 8: applying a delta never echoes an intent
        }

        /// <summary>Only the geoscape LEVEL clock is shared state; the interception module drives a
        /// private mini-game clock and stays native. No geoscape (mid-load) → native too: the reload
        /// boundary + save transfer own that window. Callers check <see cref="BindOk"/> first.</summary>
        private static bool IsLevelClock(UIModuleTimeControl module)
        {
            var geo = GeoLevel();
            return geo != null && ReferenceEquals(FTiming.GetValue(module), geo.Timing);
        }

        private static void Send(byte op, byte value, string what)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            try
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(++_nextIntentNonce);
                    w.Write(op);
                    w.Write(value);
                    var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoTimeIntent, SyncKind.ActionRequest, ms.ToArray());
                    engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
                }
                Debug.Log("[MP][time] CLIENT intent " + what + " nonce=" + _nextIntentNonce);
            }
            catch (Exception ex) { Debug.LogError("[MP][time] intent send failed: " + ex); }
        }

        // ─── Harmony seams (law 4a, intent-capture only) ───────────────────

        /// <summary>The pause/resume gesture. A no-change request (SetTimeState re-stating the
        /// current value from GeoscapeView.RequestPauseCrt:1295) asks for nothing — the same no-op
        /// test native's setter would apply. The pause indicator flips when the host echo lands:
        /// the rail applies Paused through the property setter, whose OnPausedEvent the module
        /// already subscribes (UIModuleTimeControl.cs:105).</summary>
        [HarmonyPatch(typeof(UIModuleTimeControl), "OnPauseTime")]
        internal static class PauseGestureCapturePatch
        {
            private static bool Prefix(UIModuleTimeControl __instance, bool pause)
            {
                if (ShouldRunNative()) return true;
                if (!BindOk()) return false;                 // cannot identify the clock: never write locally
                if (!IsLevelClock(__instance)) return true;  // interception clock / mid-load: local by design
                try
                {
                    var geo = GeoLevel();
                    if (geo != null && geo.Timing.Paused != pause)
                        Send(OpPause, pause ? (byte)1 : (byte)0, pause ? "pause" : "resume");
                }
                catch (Exception ex) { Debug.LogError("[MP][time] pause capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>The speed gesture. SelectTimePreset:192 has already staged SelectedPresetTime
        /// when this runs, so the intent ships that staged index; the view-refresh half of the
        /// blocked native body (_updateTimeSpeedState = true) is re-stated so the label repaints
        /// from the staged preset — the CLOCK itself only changes when the host echo lands.</summary>
        [HarmonyPatch(typeof(UIModuleTimeControl), "UpdateSelectedTime")]
        internal static class SpeedGestureCapturePatch
        {
            private static bool Prefix(UIModuleTimeControl __instance)
            {
                if (ShouldRunNative()) return true;
                if (!BindOk()) return false;                 // cannot identify the clock: never write locally
                if (!IsLevelClock(__instance)) return true;  // interception clock / mid-load: local by design
                try
                {
                    Send(OpSpeed, (byte)__instance.SelectedPresetTime, "speed preset=" + __instance.SelectedPresetTime);
                    FUpdateSpeedState?.SetValue(__instance, true);
                }
                catch (Exception ex) { Debug.LogError("[MP][time] speed capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>The programmatic pause funnel (screen-open pauses, launch resume). Always the
        /// LEVEL clock (SetGamePauseState body reads _context.Level.Timing). Blocked wholesale on a
        /// client: the TimeLimit branch inside it is host-only logic.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.SetGamePauseState))]
        internal static class ProgrammaticPauseCapturePatch
        {
            private static bool Prefix(bool paused)
            {
                if (ShouldRunNative()) return true;
                try
                {
                    var geo = GeoLevel();
                    if (geo != null && geo.Timing.Paused != paused)
                        Send(OpPause, paused ? (byte)1 : (byte)0, paused ? "pause (screen)" : "resume (screen)");
                }
                catch (Exception ex) { Debug.LogError("[MP][time] screen-pause capture failed: " + ex); }
                return false;
            }
        }

        // ─── HOST: dedup → apply through the SAME native funnels ───────────

        /// <summary>Returns true when the surface was consumed (GeoTimeIntent).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoTimeIntent) return false;
            if (engine == null || !engine.IsHost) return true; // intents are host-only inbound
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint nonce = r.ReadUInt32();
                    byte op = r.ReadByte();
                    byte val = r.ReadByte();
                    if (!Intents.IsNew(senderPeerId, SurfaceIds.GeoTimeIntent, nonce)) return true; // double-send

                    var geo = GeoLevel();
                    if (geo == null)
                    { Debug.LogWarning("[MP][time] HOST intent dropped — no geoscape (peer=" + senderPeerId + ")"); return true; }

                    switch (op)
                    {
                        case OpPause:
                            // Native funnel: keeps the TimeLimit guard; the Paused property setter it
                            // reaches raises the events that latch TimeAnchor + FlushNow the delta out.
                            if (geo.View != null) geo.View.SetGamePauseState(val != 0);
                            else geo.Timing.Paused = val != 0; // view mid-init: same write, same events
                            break;
                        case OpSpeed:
                            var module = geo.View == null || geo.View.GeoscapeModules == null
                                ? null : geo.View.GeoscapeModules.TimeControlModule;
                            if (module == null)
                            { Debug.LogWarning("[MP][time] HOST speed intent dropped — no time module"); return true; }
                            // Native funnel: clamps the index, writes Timing.Scale through the property
                            // setter (events → anchor latch + flush) and keeps the host's label honest.
                            module.SelectTimePreset(val);
                            break;
                        default:
                            Debug.LogWarning("[MP][time] HOST intent unknown op " + op);
                            return true;
                    }
                    Debug.Log("[MP][time] HOST intent APPLIED op=" + op + " val=" + val +
                              " nonce=" + nonce + " peer=" + senderPeerId);
                }
            }
            catch (Exception ex) { Debug.LogError("[MP][time] HOST intent failed: " + ex); }
            return true;
        }

        // ─── CLIENT: anchor enforcement cadence ────────────────────────────

        /// <summary>Client-side backstop for the rail's diff blindness: the rail re-sends nothing
        /// while HOST state is unchanged, so a client clock mutated by a writer the seams above do
        /// not capture (quick-save coroutine, another mod) would free-run forever. ~1 Hz, a handful
        /// of float ops — the check itself lives in <see cref="TimeAnchor.EnforceDrift"/> next to
        /// the prediction it re-asserts.</summary>
        public static void ClientTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
            if (Time.realtimeSinceStartup < _nextEnforceAt) return;
            _nextEnforceAt = Time.realtimeSinceStartup + 1f;
            var geo = GeoLevel();
            if (geo != null) TimeAnchor.EnforceDrift(geo);
        }
    }
}
