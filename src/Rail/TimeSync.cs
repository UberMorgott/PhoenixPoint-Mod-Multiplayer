using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        internal const byte OpPause = 1;  // val = 0 resume / 1 pause      → PauseHold.Apply → SetGamePauseState
        private const byte OpSpeed = 2;   // val = preset index            → SelectTimePreset
        // op 3 = PauseHold.OpHold (val = 1 hold / 0 release) — the window hold, same family, same envelope.

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

        public static void Reset() => ResetForReloadBoundary();

        /// <summary>Stateless per intent (same rca-3 contract as <see cref="PersonnelSync"/>):
        /// nothing geoscape-bound cached; dedup/nonce live in <see cref="IntentRail"/>. The window-hold
        /// set is NOT per-intent state and must go: its windows died with the level, and a surviving hold
        /// would veto every resume in the next one.</summary>
        public static void ResetForReloadBoundary() => PauseHold.Reset();

        /// <summary>Arm the 0xB0 surface on the generic intent engine. No family reconverge and no
        /// reject prefixes: the client BLOCKED its local clock write, so a dropped intent leaves both
        /// clocks still equal, and residual drift from any writer is continuously corrected by
        /// <see cref="TimeAnchor.EnforceDrift"/> (~1 Hz) — the standing corrector IS this family's
        /// convergence mechanism.</summary>
        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpPause] = HandleIntentOp,
                [OpSpeed] = HandleIntentOp,
                // The window hold rides THIS family, not a surface of its own: it is a time-control
                // statement about the same clock, decided by the same host, and a second surface would be
                // a second ordering stream for one value.
                [PauseHold.OpHold] = HandleIntentOp,
            };
            IntentRail.Register(SurfaceIds.GeoTimeIntent, "time", ops);
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
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
            => IntentRail.Send(SurfaceIds.GeoTimeIntent, op, what, w => w.Write(value));

        /// <summary>THE ONE ASYMMETRY in this family's block-first posture: after shipping the intent, a
        /// client lets a PAUSE run natively and still blocks a RESUME.
        ///
        /// It exists because block-first had a silent hole exactly here. A client's window pause reaches a
        /// host that is ALREADY paused, so <c>SetGamePauseState</c>:1265 writes a value the change-gated
        /// <c>Timing.Paused</c> setter (Timing.cs:112) swallows — no event, no diff, no delta — and the
        /// client that blocked its own write free-runs forever with nothing to correct it. That is the
        /// aircraft flying on while every player reads the popup (live 2026-08-04).
        ///
        /// Letting the pause through is not an optimistic model write in the sense law 3 forbids: a clock
        /// can only end up TOO SLOW, the host's authoritative rate rides "T"/"TA" as before, and
        /// <see cref="TimeAnchor.EnforceDrift"/> re-asserts it within ~1 s if the host disagrees. A RESUME
        /// stays blocked because that direction is not self-healing — it would run the client's campaign
        /// ahead of the host's — and because a resume is exactly what <see cref="PauseHold"/> arbitrates.
        /// Both native bodies are provably inert past the write for <c>paused == true</c>: the only side
        /// branch in <c>SetGamePauseState</c> needs <c>!paused</c> (GeoscapeView.cs:1259).</summary>
        private static bool PausesLocally(bool paused) => paused;

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
                if (IntentRail.ShouldRunNative())
                {
                    // The ONE resume veto (host-only inside): a blocking window on ANY peer holds the
                    // shared clock, and the host pressing play is not an exemption.
                    if (!pause && PauseHold.VetoResume(GeoLevel(), out string vetoWhy))
                    { Debug.Log("[MP][pause] host resume gesture VETOED — " + vetoWhy); return false; }
                    return true;
                }
                if (!BindOk()) return false;                 // cannot identify the clock: never write locally
                if (!IsLevelClock(__instance)) return true;  // interception clock / mid-load: local by design
                try
                {
                    var geo = GeoLevel();
                    if (geo != null && geo.Timing.Paused != pause)
                        Send(OpPause, pause ? (byte)1 : (byte)0, pause ? "pause" : "resume");
                }
                catch (Exception ex) { Debug.LogError("[MP][time] pause capture failed: " + ex); }
                return PausesLocally(pause);
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
                if (IntentRail.ShouldRunNative()) return true;
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
        /// client: the TimeLimit branch inside it is host-only logic.
        ///
        /// UNLIKE the two gesture seams above, this method is ENGINE-driven: almost every geoscape UI
        /// transition re-asserts the pause state on an already-paused clock (UIModuleGeoSectionBar.cs:119-194
        /// on every section click, UIStateResearch:22, UIStateManufacturing:51, UIStateDiplomacy:27/39,
        /// UIStateGeoscapeLog:18, UIStateGeoscapeOptions:36, GeoscapeView.RequestPauseCrt:1293). Those
        /// no-change calls are tested out BEFORE the shared gate, because the gate's host arm
        /// (DiffEngine.FlushOnHostGesture) would otherwise force one MONOLITHIC diff walk — and abandon
        /// the in-flight sliced cycle (DiffEngine.HostTick:320) — per UI transition.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.SetGamePauseState))]
        internal static class ProgrammaticPauseCapturePatch
        {
            private static bool Prefix(bool paused)
            {
                try
                {
                    var geo = GeoLevel();
                    // The ONE resume veto, same as the gesture seam (host-only inside). Asked BEFORE the
                    // inert test: a resume against a paused clock is a real change and would go through.
                    if (!paused && PauseHold.VetoResume(geo, out string vetoWhy))
                    { Debug.Log("[MP][pause] screen resume VETOED — " + vetoWhy); return false; }
                    // Native is provably INERT here: its only side branch needs (!paused && timing.Paused)
                    // = a change (GeoscapeView.cs:1259), and the else-write is swallowed by the
                    // change-gated Paused setter (Timing.cs:112). So nothing to capture and nothing to
                    // ship, on either peer. A REAL host pause/resume still flushes same-frame without
                    // this seam: that same setter raises EffectiveScaleChangedEvent (Timing.cs:126) →
                    // DiffEngine.OnEffectiveScaleChanged → FlushNow (DiffEngine.cs:243).
                    if (geo != null && geo.Timing.Paused == paused) return true;

                    if (IntentRail.ShouldRunNative()) return true;
                    if (geo != null)
                        Send(OpPause, paused ? (byte)1 : (byte)0, paused ? "pause (screen)" : "resume (screen)");
                    return PausesLocally(paused);
                }
                catch (Exception ex) { Debug.LogError("[MP][time] screen-pause capture failed: " + ex); }
                return false;
            }
        }

        // ─── HOST: apply through the SAME native funnels (dedup/decode/reject = IntentRail) ───────────

        private static void HandleIntentOp(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            byte val = r.ReadByte();

            var geo = GeoLevel();

            if (op == OpPause || op == PauseHold.OpHold)
            {
                // Deliberately BEFORE the no-geoscape reject: hold membership is a fact about a PEER, not
                // about this host's level, and it must survive a moment with no geoscape (mid-load, a
                // battle) or the peer's window would be forgotten and its hold never released. Apply skips
                // the clock write itself when there is no level to write.
                // ONE arbiter for both (PauseHold.Decide): a hold pauses, a release never resumes, and a
                // resume is refused while ANY peer still has a blocking window up. It writes the clock
                // through the same native funnel as before — TimeLimit guard kept, and the Paused setter
                // raises the events that latch TimeAnchor + FlushNow the delta out.
                PauseHold.Apply(geo, senderPeerId, op, val);
                return;
            }

            // OpSpeed (the op set is table-gated upstream)
            if (geo == null)
            { IntentRail.Reject(SurfaceIds.GeoTimeIntent, senderPeerId, "no geoscape op=" + op); return; }
            var module = geo.View == null || geo.View.GeoscapeModules == null
                ? null : geo.View.GeoscapeModules.TimeControlModule;
            if (module == null)
            { IntentRail.Reject(SurfaceIds.GeoTimeIntent, senderPeerId, "speed: no time module"); return; }
            // Native funnel: clamps the index, writes Timing.Scale through the property
            // setter (events → anchor latch + flush) and keeps the host's label honest.
            module.SelectTimePreset(val);
            Debug.Log("[MP][time] HOST intent APPLIED op=" + op + " val=" + val +
                      " nonce=" + nonce + " peer=" + senderPeerId);
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
