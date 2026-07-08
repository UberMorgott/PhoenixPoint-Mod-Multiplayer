using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.TimeSync;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// Inc4 S1 (§3.3) — CLIENT pause/speed GLYPH decouple for the geoscape sim-freeze. Design spec:
    /// docs/superpowers/specs/2026-07-02-multiplayer-inc4-client-sim-freeze-design.md §3.3.
    ///
    /// Under the freeze the sim <c>Timing._paused</c> is pinned true (so producers stay Max'd), but the native
    /// time-control widget renders its pause/play glyph and speed indicator from <c>_timing.Paused</c>
    /// (<c>UIModuleTimeControl.SetTimerPausedState</c>/<c>SetTimerSpeedState</c>, UIModuleTimeControl.cs:236-260)
    /// — which would now read "always paused" even while the synced clock advances at host speed. The scheduler
    /// AND the widget read the SAME <c>Timing.Paused</c> getter (NextUpdate.cs:199), so the getter can't be made
    /// to differ per-caller; instead we correct the widget OUTPUT.
    ///
    /// Mechanism: Harmony POSTFIX on the two private widget render methods. It runs AFTER the widget set its
    /// (wrong, always-paused) visuals and overwrites them with the HOST cosmetic glyph state
    /// (<see cref="TimeSyncManager.GlyphHostPaused"/>/<see cref="TimeSyncManager.GlyphHostSpeedIndex"/>,
    /// republished each client frame by <c>WriteClock</c>). The widget re-renders whenever its dirty flags are
    /// armed — <c>MirrorPauseUi</c> on a host-pause change, <c>SelectTimePreset</c> on a host-speed change, and
    /// the setter's <c>OnPausedEvent</c> at the load-time freeze re-assert — so the glyph tracks the host on
    /// every change WITHOUT per-frame cost (steady state arms no flag → these postfixes don't run).
    ///
    /// Gated on <see cref="ClientSimFreeze.ShouldFreeze"/> (flag-gated; default-ON since S3): when OFF the postfix returns
    /// before touching anything → the widget's native pause/speed sync stands unchanged (byte-identical
    /// in-game). Reflection targets so an engine rename never PatchAll-bombs; best-effort try/catch — never
    /// throws into game code. HOST / single-player never reach the freeze gate.
    ///
    /// DEVIATION FROM SPEC (noted): §3.3's header said "EDIT MirrorPauseUi/MirrorSpeedUi", but its own hint
    /// ("push the anchor's paused/speed straight into SetTimerPausedState/SetTimerSpeedState inputs") points at
    /// these param-less render methods — a postfix is the robust way to make "the value the widget reads" be
    /// the host anchor's. MirrorPauseUi/MirrorSpeedUi keep only their on-change dirty-flag arming (they already
    /// pass host values); the visual correction lives here so there is no ordering/state-tracking fragility.
    /// </summary>
    internal static class TimeGlyphWidgetReflection
    {
        internal static Type WidgetType;
        internal static FieldInfo PausedTimeText;      // GameObject
        internal static FieldInfo PlayGraphic;         // Image  (TimerButtonPlayGraphic)
        internal static FieldInfo PauseGraphic;        // Image  (TimerButtonPauseGraphic)
        internal static FieldInfo TimerSpeed;          // Text
        internal static FieldInfo SpeedNames;          // List<LocalizedTextBind>
        internal static FieldInfo PresetTimes;         // float[]
        internal static FieldInfo AnimatorField;       // Animator (private _animator)
        internal static MethodInfo LocalizeMethod;     // LocalizedTextBind.Localize(string=null)
        // Inc4 V2 — the date/time render targets (UIModuleTimeControl.Update paints these from Now).
        internal static FieldInfo TimerHrsMins;        // Text  (HH:mm)
        internal static FieldInfo TimerDMY;            // Text  (dd.MM.yyyy)
        internal static FieldInfo MinutesHand;         // Image (clock minute hand)
        private static bool _resolved;

        // Resolve the widget's fields once. Verified vs decompile (2026-07-02, UIModuleTimeControl.cs):
        // PausedTimeText :54, TimerButtonPlayGraphic :39, TimerButtonPauseGraphic :42, TimerSpeed :27,
        // SpeedNames :63, PresetTimes :20, _animator :71. Localize() = Base.UI.LocalizedTextBind :35.
        internal static bool Ensure()
        {
            if (_resolved) return WidgetType != null;
            _resolved = true;
            WidgetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
            if (WidgetType == null) return false;
            PausedTimeText = AccessTools.Field(WidgetType, "PausedTimeText");
            PlayGraphic = AccessTools.Field(WidgetType, "TimerButtonPlayGraphic");
            PauseGraphic = AccessTools.Field(WidgetType, "TimerButtonPauseGraphic");
            TimerSpeed = AccessTools.Field(WidgetType, "TimerSpeed");
            SpeedNames = AccessTools.Field(WidgetType, "SpeedNames");
            PresetTimes = AccessTools.Field(WidgetType, "PresetTimes");
            AnimatorField = AccessTools.Field(WidgetType, "_animator");
            var bindType = AccessTools.TypeByName("Base.UI.LocalizedTextBind");
            if (bindType != null) LocalizeMethod = AccessTools.Method(bindType, "Localize", new[] { typeof(string) });
            // Inc4 V2 date/time render targets. Verified vs decompile (UIModuleTimeControl.cs): TimerHrsMins :30,
            // TimerDMY :33, MinutesHand :36 — the exact fields Update() paints (:143-149).
            TimerHrsMins = AccessTools.Field(WidgetType, "TimerHrsMins");
            TimerDMY = AccessTools.Field(WidgetType, "TimerDMY");
            MinutesHand = AccessTools.Field(WidgetType, "MinutesHand");
            return true;
        }

        /// <summary>True only when the client geoscape sim-freeze is active (flag ON + active client).</summary>
        internal static bool FreezeActive()
        {
            var engine = NetworkEngine.Instance;
            return ClientSimFreeze.ShouldFreeze(
                ClientSimFreeze.Enabled,
                engine != null,
                engine != null && engine.IsActiveSession,
                engine != null && engine.IsHost);
        }

        private static string LocalizeAt(IList list, int index)
        {
            if (LocalizeMethod == null || list == null || index < 0 || index >= list.Count) return null;
            var bind = list[index];
            if (bind == null) return null;
            return LocalizeMethod.Invoke(bind, new object[] { null }) as string;
        }

        /// <summary>Best-effort text correction: mirror SetTimerSpeedState's TimerSpeed.text with host state.</summary>
        internal static void CorrectSpeedText(object widget, bool hostPaused, int hostSpeedIndex)
        {
            try
            {
                var textObj = TimerSpeed?.GetValue(widget) as Text;
                var names = SpeedNames?.GetValue(widget) as IList;
                var presets = PresetTimes?.GetValue(widget) as float[];
                if (textObj == null || names == null || presets == null || presets.Length == 0) return;
                // SpeedNames[PresetTimes.Length] = the "paused" label (see UIModuleTimeControl.cs:253).
                int idx = hostPaused ? presets.Length : Mathf.Clamp(hostSpeedIndex, 0, presets.Length - 1);
                string s = LocalizeAt(names, idx);
                if (s != null) textObj.text = s;
            }
            catch { /* text label is cosmetic-secondary — never let it break the graphic/animator correction */ }
        }

        /// <summary>
        /// Inc4 V2 — paint the widget's date/time DISPLAY from a host-mirrored <paramref name="dt"/> while the
        /// sim <c>Timing.Now</c> is pinned. Byte-for-byte reproduces <c>UIModuleTimeControl.Update</c>'s render
        /// block (UIModuleTimeControl.cs:143-149 + <c>UpdateHourHands</c> :262-266): HH:mm (invariant), dd.MM.yyyy,
        /// and the minute hand at <c>-6°·Minute</c>. Display-only — never touches the frozen sim clock. The
        /// <c>Text.text</c> setter is the codebase's accepted write (mirrors <see cref="CorrectSpeedText"/>).
        /// </summary>
        internal static void PaintDate(object widget, DateTime dt)
        {
            try
            {
                var hm = TimerHrsMins?.GetValue(widget) as Text;
                if (hm != null) hm.text = dt.ToString("HH:mm", CultureInfo.InvariantCulture);
                var dmy = TimerDMY?.GetValue(widget) as Text;
                if (dmy != null) dmy.text = dt.ToString("dd.MM.yyyy");
                var hand = MinutesHand?.GetValue(widget) as Image;
                if (hand != null)
                    hand.rectTransform.rotation = Quaternion.Euler(new Vector3(0f, 0f, -6 * dt.Minute));
            }
            catch { /* display-only — never throw into the widget's Update */ }
        }
    }

    /// <summary>Postfix on <c>UIModuleTimeControl.SetTimerPausedState()</c> — play/pause button + PAUSED overlay.</summary>
    [HarmonyPatch]
    public static class ClientTimePausedGlyphFreezePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            if (!TimeGlyphWidgetReflection.Ensure()) return false;
            _target = AccessTools.Method(TimeGlyphWidgetReflection.WidgetType, "SetTimerPausedState", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Runs after the widget set play/pause graphics from _timing.Paused (== true under the freeze). Override
        // them with the host cosmetic paused state so the glyph mirrors the host, not the pinned sim flag.
        public static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null || !TimeGlyphWidgetReflection.FreezeActive()) return;
                bool hostPaused = TimeSyncManager.GlyphHostPaused;

                var pausedText = TimeGlyphWidgetReflection.PausedTimeText?.GetValue(__instance) as GameObject;
                if (pausedText != null) pausedText.SetActive(hostPaused);

                var play = TimeGlyphWidgetReflection.PlayGraphic?.GetValue(__instance) as Component;
                if (play != null) play.gameObject.SetActive(!hostPaused);

                var pause = TimeGlyphWidgetReflection.PauseGraphic?.GetValue(__instance) as Component;
                if (pause != null) pause.gameObject.SetActive(hostPaused);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientTimePausedGlyphFreezePatch failed: " + ex.Message); }
        }
    }

    /// <summary>Postfix on <c>UIModuleTimeControl.SetTimerSpeedState()</c> — Animator IsPaused/TimeMode + speed text.</summary>
    [HarmonyPatch]
    public static class ClientTimeSpeedGlyphFreezePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            if (!TimeGlyphWidgetReflection.Ensure()) return false;
            _target = AccessTools.Method(TimeGlyphWidgetReflection.WidgetType, "SetTimerSpeedState", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Runs after the widget pushed Animator IsPaused=_timing.Paused (== true under the freeze) + the "paused"
        // speed label. Override with the host cosmetic paused/speed so the speed glyph mirrors the host.
        public static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null || !TimeGlyphWidgetReflection.FreezeActive()) return;
                bool hostPaused = TimeSyncManager.GlyphHostPaused;
                int hostSpeed = TimeSyncManager.GlyphHostSpeedIndex;

                var animator = TimeGlyphWidgetReflection.AnimatorField?.GetValue(__instance) as Animator;
                if (animator != null && animator.isActiveAndEnabled)
                {
                    animator.SetInteger("TimeMode", hostSpeed);
                    animator.SetBool("IsPaused", hostPaused);
                }

                TimeGlyphWidgetReflection.CorrectSpeedText(__instance, hostPaused, hostSpeed);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientTimeSpeedGlyphFreezePatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Inc4 V2 — the CLIENT date/time DISPLAY driver for the geoscape sim-pin. Design: under the V2 pin
    /// (<see cref="ClientSimFreezeV2Gate"/>) TimeSyncManager.WriteClock no longer advances the geoscape
    /// <c>Timing.Now</c> (sim frozen = canon), so <c>UIModuleTimeControl.Update</c> (which paints the HUD clock
    /// from <c>_timing.Now.DateTime</c>, UIModuleTimeControl.cs:143-149) would show a STUCK clock. This POSTFIX
    /// runs after the widget's own Update and repaints HH:mm / dd.MM.yyyy / minute-hand from the host-mirrored
    /// time (<see cref="TimeSyncManager.DisplayHostGameSeconds"/>) — display-only, never touching the frozen sim.
    ///
    /// Gated on <see cref="TimeSyncManager.DisplayActive"/> (true ONLY while the V2 pin is driving: active
    /// client, in geoscape, anchor+offset seeded). V2-OFF / pre-sync / host ⇒ DisplayActive false ⇒ this returns
    /// before touching anything and the widget paints itself from Now exactly as at HEAD (byte-identical
    /// rollback). Repaints ONLY when the host game-MINUTE changes (the rendered fields' granularity), so steady
    /// state is a long compare, not a per-frame string format — cheaper than V1's per-frame widget repaint.
    /// Reflection target so an engine rename never PatchAll-bombs; best-effort try/catch.
    /// </summary>
    [HarmonyPatch]
    public static class ClientTimeDateDisplayFreezePatch
    {
        private static MethodBase _target;
        private static object _lastWidget;  // reset the minute cache when the widget instance changes (scene reload)
        private static long _lastMinute = long.MinValue;

        public static bool Prepare()
        {
            if (!TimeGlyphWidgetReflection.Ensure()) return false;
            _target = AccessTools.Method(TimeGlyphWidgetReflection.WidgetType, "Update", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null || !TimeSyncManager.DisplayActive) return; // V2 pin inactive → widget owns the paint
                if (!TimeGlyphWidgetReflection.FreezeActive()) return;             // defensive belt (host/single-player)

                double seconds = TimeSyncManager.DisplayHostGameSeconds;
                long minute = (long)Math.Floor(seconds / 60.0);
                if (!ReferenceEquals(__instance, _lastWidget)) { _lastWidget = __instance; _lastMinute = long.MinValue; }
                if (minute == _lastMinute) return; // on-change (per game-minute) only — the rendered fields don't change intra-minute
                _lastMinute = minute;

                TimeGlyphWidgetReflection.PaintDate(__instance, ClientSimFreezeV2Gate.DisplayDateTime(seconds));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientTimeDateDisplayFreezePatch failed: " + ex.Message); }
        }
    }
}
