using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// CLIENT-only DIAGNOSTIC (no behaviour change) — the input-gate disambiguator for the "client is a
    /// pure spectator, cannot select a soldier / issue commands" regression. RCA proved the client turn/
    /// ownership are correct and the intent pipeline is wired, but input dies BEFORE Ability.Activate at a
    /// NATIVE gate in <c>UIStateCharacterSelected</c>: <c>OnSelect()</c> bails on <c>base.CursorOverGui</c>
    /// (decompile :792) and <c>UpdateState()</c> both drops hover selection on <c>base.CursorOverGui</c>
    /// (:1130) and early-returns on <c>Context.Map.IsMapUpdateInProgress</c> (:1144). <c>CursorOverGui</c> is
    /// <c>Context.View.IsCursorOverGUI()</c> (TacticalViewState.cs:48) which resolves to
    /// <c>EventSystem.current.IsPointerOverGameObject()</c>. The leading suspect is a leftover full-screen
    /// NATIVE geoscape UI GraphicRaycaster (report/mission-brief modal) that survived the client's unusual
    /// geoscape→tactical save-transfer entry and now eats the pointer everywhere → IsPointerOverGameObject()
    /// true → selection blocked.
    ///
    /// This postfix samples the ground truth once per ~2 s on a live co-op CLIENT while a character is
    /// selected: which gate is stuck-true, and — if the raycaster is the culprit — WHICH GameObject and root
    /// canvas is capturing the pointer. It logs one <c>PROBE-INPUTGATE</c> line through the mod's log sink
    /// (the "[Multiplayer]" marker is what <see cref="Multiplayer.Util.MultiplayerLog"/> mirrors into
    /// multiplayer.log; grep that file for PROBE-INPUTGATE). Fully fail-open; no game-state writes.
    ///
    /// Mirrors <see cref="AbilityBarStateDiagPatch"/>: reflection-only binding (no hard type ref), Prepare()
    /// self-reports the bind, Harmony auto-registers via MultiplayerMain's PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class InputGateProbePatch
    {
        private static MethodBase _target;
        private static PropertyInfo _contextProp;    // TacticalViewState.Context (protected)
        private static PropertyInfo _mapProp;        // TacticalViewContext.Map (resolved lazily on first hit)
        private static PropertyInfo _mapUpdateProp;  // <map>.IsMapUpdateInProgress (resolved lazily)
        private static float _lastLogTime = -999f;
        private const float ThrottleSeconds = 2f;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateCharacterSelected");
            if (t == null)
            {
                Debug.LogWarning("[Multiplayer][probe] InputGateProbePatch NOT bound: UIStateCharacterSelected not found");
                return false;
            }
            // protected override void UpdateState() — single override, no overloads.
            _target = AccessTools.Method(t, "UpdateState");
            if (_target == null)
            {
                Debug.LogWarning("[Multiplayer][probe] InputGateProbePatch NOT bound: UpdateState not found");
                return false;
            }
            var baseType = AccessTools.TypeByName("PhoenixPoint.Tactical.View.TacticalViewState");
            _contextProp = baseType != null ? AccessTools.Property(baseType, "Context") : null; // protected auto-prop
            Debug.Log("[Multiplayer][probe] InputGateProbePatch BOUND to UIStateCharacterSelected.UpdateState (contextProp=" +
                      (_contextProp != null) + ")");
            return true;
        }

        public static MethodBase TargetMethod()
        {
            if (_target == null)
                Debug.LogWarning("[Multiplayer][probe] InputGateProbePatch.TargetMethod returned null — patch will not bind");
            return _target;
        }

        // Signature: protected override void UpdateState() — __instance is the UIStateCharacterSelected.
        public static void Postfix(object __instance)
        {
            try
            {
                var e = NetworkEngine.Instance;
                if (e == null || !e.IsActive || e.IsHost) return;   // co-op CLIENT ONLY (SP / host never spammed)

                float now = Time.realtimeSinceStartup;
                if (now - _lastLogTime < ThrottleSeconds) return;   // throttle ~1 line / 2 s
                _lastLogTime = now;

                // Gate #1: the exact predicate CursorOverGui reads (EventSystem.current.IsPointerOverGameObject()).
                var es = EventSystem.current;
                bool overGui = es != null && es.IsPointerOverGameObject();

                // If the pointer is "over GUI", RaycastAll names the topmost UI GameObject + its root canvas —
                // that is the stuck raycaster layer (expected: a leftover geoscape modal canvas, not a tactical one).
                string topName = "<none>";
                string rootCanvas = "<none>";
                int hitCount = 0;
                if (es != null)
                {
                    var ped = new PointerEventData(es) { position = Input.mousePosition };
                    var results = new List<RaycastResult>();
                    es.RaycastAll(ped, results);
                    hitCount = results.Count;
                    if (results.Count > 0 && results[0].gameObject != null)
                    {
                        var go = results[0].gameObject;
                        topName = go.name;
                        var canvas = go.GetComponentInParent<Canvas>();
                        if (canvas != null)
                        {
                            var rc = canvas.rootCanvas;
                            rootCanvas = rc != null ? rc.name : canvas.name;
                        }
                    }
                }

                // Gate #2: Context.Map.IsMapUpdateInProgress (UIStateCharacterSelected.cs:1144 early-return).
                string mapUpd = "n/a";
                try
                {
                    var ctx = _contextProp != null ? _contextProp.GetValue(__instance, null) : null;
                    if (ctx != null)
                    {
                        if (_mapProp == null) _mapProp = AccessTools.Property(ctx.GetType(), "Map");
                        var map = _mapProp != null ? _mapProp.GetValue(ctx, null) : null;
                        if (map != null)
                        {
                            if (_mapUpdateProp == null) _mapUpdateProp = AccessTools.Property(map.GetType(), "IsMapUpdateInProgress");
                            if (_mapUpdateProp != null) mapUpd = _mapUpdateProp.GetValue(map, null)?.ToString() ?? "null";
                        }
                    }
                }
                catch { mapUpd = "err"; }

                Debug.Log("[Multiplayer] PROBE-INPUTGATE overGui=" + overGui +
                          " topHit=" + topName + " rootCanvas=" + rootCanvas + " hits=" + hitCount +
                          " mapUpdateInProgress=" + mapUpd);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][probe] InputGateProbePatch.Postfix failed: " + ex.Message);
            }
        }
    }
}
