using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// Client-side enemy-turn cinematic camera. On the co-op mirror the enemy replay coroutines
    /// (move/fire/melee) bypass TacticalAbility.Activate, so the native
    /// CameraDirector.Hint(AbilityActivated) never fires and the camera never follows enemy
    /// actions. This pushes a low-level CameraHint.ChaseTarget at each replay site (gated by
    /// ClientEnemyTurnCameraGate on TacticalTurnSync.IsClientEnemyTurn), driving the same
    /// PlanarScrollCamera.Chase path the native camera uses
    /// (CameraDirector.Hint(CameraHint, object) -> CameraManager -> HandleHint -> Chase).
    /// Best-effort: any reflection failure is swallowed and never breaks the mirror.
    /// </summary>
    public static class TacticalEnemyTurnCamera
    {
        private static bool _resolved;
        private static bool _resolveFailed;
        private static Type _chaseParamsType;
        private static object _chaseTargetHint;   // boxed CameraHint.ChaseTarget
        private static MethodInfo _directorHint;  // CameraDirector.Hint(CameraHint, object)
        private static FieldInfo _fChaseTransform;
        private static FieldInfo _fChaseVector;
        private static FieldInfo _fSnapToFloor;
        private static FieldInfo _fOnlyOutsideFrame;

        private static void EnsureResolved()
        {
            if (_resolved || _resolveFailed) return;
            try
            {
                _chaseParamsType = AccessTools.TypeByName("Base.Cameras.CameraChaseParams");
                Type hintType = AccessTools.TypeByName("Base.Cameras.CameraHint");
                Type directorType = AccessTools.TypeByName("Base.Cameras.CameraDirector");
                if (_chaseParamsType == null || hintType == null || directorType == null)
                    throw new Exception("camera types not found");

                _chaseTargetHint = Enum.Parse(hintType, "ChaseTarget");
                _directorHint = AccessTools.Method(directorType, "Hint", new[] { hintType, typeof(object) });
                _fChaseTransform = AccessTools.Field(_chaseParamsType, "ChaseTransform");
                _fChaseVector = AccessTools.Field(_chaseParamsType, "ChaseVector");
                _fSnapToFloor = AccessTools.Field(_chaseParamsType, "SnapToFloorHeight");
                _fOnlyOutsideFrame = AccessTools.Field(_chaseParamsType, "ChaseOnlyOutsideFrame");
                if (_directorHint == null || _fChaseVector == null)
                    throw new Exception("camera members not found");

                _resolved = true;
            }
            catch (Exception e)
            {
                _resolveFailed = true;
                Debug.LogWarning("[Multipleer][tac] enemy-turn camera resolve failed: " + e.Message);
            }
        }

        /// <summary>Chase the actor. follow=true tracks the live transform (moves); follow=false
        /// snaps once to the actor's current position (shot/melee).</summary>
        public static void ChaseActor(object actor, bool follow)
        {
            if (actor == null) return;
            EnsureResolved();
            if (_resolveFailed) return;
            try
            {
                object director = GetProp(GetProp(TacticalDeploySync.LiveTlc, "View"), "CameraDirector");
                if (director == null) return;

                object p = Activator.CreateInstance(_chaseParamsType);
                _fSnapToFloor?.SetValue(p, true);
                _fOnlyOutsideFrame?.SetValue(p, true);

                if (follow)
                {
                    Transform tr = (actor as Component)?.transform;
                    if (tr == null) return;
                    _fChaseTransform?.SetValue(p, tr);
                }
                else
                {
                    _fChaseVector.SetValue(p, GetPos(actor));
                }

                _directorHint.Invoke(director, new object[] { _chaseTargetHint, p });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multipleer][tac] enemy-turn camera chase failed: " + e.Message);
            }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo pi = AccessTools.Property(obj.GetType(), name);
            if (pi != null) return pi.GetValue(obj, null);
            FieldInfo fi = AccessTools.Field(obj.GetType(), name);
            return fi?.GetValue(obj);
        }

        private static Vector3 GetPos(object actor)
        {
            object p = GetProp(actor, "Pos");
            return p is Vector3 v ? v : Vector3.zero;
        }
    }
}
