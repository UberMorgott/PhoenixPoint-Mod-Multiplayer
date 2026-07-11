using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// CLIENT co-op: skip <c>PhoenixSaveManager.AutosaveGame</c> entirely. The client is a pure mirror —
    /// its local autosave of mirrored state is worthless (real saves arrive from the host via
    /// SaveTransferCoordinator) — and the native autosave runs INSIDE
    /// <c>GeoLevelController.LaunchTacticalGameCrt</c>, where an IO failure ("Autosave failed! Failed to
    /// write save data!") kills the whole geo→tac transition coroutine so the tactical level never reaches
    /// Playing (RCA 2026-07-11: same-machine instances sharing one LocalLow save folder hit an
    /// IOException Sharing violation on autosave.zsav). HOST and single-player are untouched.
    ///
    /// Verified vs decompile (PhoenixSaveManager.cs:414): <c>public IEnumerator&lt;NextUpdate&gt;
    /// AutosaveGame()</c> — parameterless iterator; callers pump it via <c>Timing.Current.Call(...)</c>,
    /// so the skip must hand back a VALID empty enumerator via <c>__result</c> (a bare skip would feed
    /// null into Timing.Call → NRE). Reflection target so an engine rename never PatchAll-bombs
    /// (Prepare false → class skipped); best-effort try/catch falls through to the native autosave.
    /// </summary>
    [HarmonyPatch]
    public static class ClientAutosaveSkipPatch
    {
        private static MethodBase _target;   // PhoenixSaveManager.AutosaveGame()
        // ponytail: 5-min realtime throttle stands in for "once per mission" — self-contained, no
        // mission-lifecycle coupling; wire to OnMissionExit if per-mission precision ever matters.
        private const float LogThrottleSeconds = 300f;
        private static float _nextLogAt;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Saves.PhoenixSaveManager");
            if (t == null) return false;
            _target = AccessTools.Method(t, "AutosaveGame", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref IEnumerator<NextUpdate> __result)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;   // host/SP: native autosave
                __result = Empty();
                if (Time.realtimeSinceStartup >= _nextLogAt)
                {
                    _nextLogAt = Time.realtimeSinceStartup + LogThrottleSeconds;
                    Debug.Log("[Multiplayer] client autosave skipped (mirror)");
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ClientAutosaveSkipPatch failed (autosave runs natively): " + ex);
                return true;
            }
        }

        private static IEnumerator<NextUpdate> Empty() { yield break; }
    }
}
