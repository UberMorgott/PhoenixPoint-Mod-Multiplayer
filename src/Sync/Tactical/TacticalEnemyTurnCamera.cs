using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
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
    ///
    /// TS7 adds a WIRE-driven follow (<c>tac.camerahint</c> 0x97): the per-action replay chases only cover
    /// move/fire/melee, so an enemy activating any OTHER camera-tracked ability (psychic, summon, deploy, …) was
    /// never followed. The host tags the acting ENEMY (native TrackWithCamera decision) — but ONLY when it is
    /// VISIBLE to the player faction ("no fog reveals") — and the client chases it (follow=true), gated to its
    /// enemy turn. This funnels through the SAME <see cref="ChaseActor"/> path as the per-action chases.
    /// </summary>
    public static class TacticalEnemyTurnCamera
    {
        // ─── HOST: an enemy actor camera-tracked an ability → broadcast tac.camerahint (0x97) ────────
        /// <summary>HOST: from the postfix on <c>TacticalAbility.Activate</c> (the native site that fires
        /// <c>CameraDirectorHint.AbilityActivated</c> when <c>TrackWithCamera</c>). Broadcast <c>tac.camerahint</c>
        /// with the acting actor's netId ONLY when the native camera would move (<c>TrackWithCamera</c>), the actor
        /// is an ENEMY (not a player-controlled faction — friendly reactions already ride fire/melee-start), and the
        /// actor is VISIBLE to the player faction (revealed/located → "no fog reveals"). PRESENTATION ONLY. Fail-open:
        /// any failure is logged + swallowed so the native activation always proceeds.</summary>
        public static void HostBroadcastCameraHint(object ability)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) return;
                if (TacticalDeploySync.IsClientMirroring) return;
                if (ability == null) return;

                // Cheap gates first (per-activation hot path): only the visible-check walks the faction/vision state.
                bool trackWithCamera = GetProp(ability, "TrackWithCamera") is bool tb && tb;
                if (!trackWithCamera) return;
                object actor = GetProp(ability, "TacticalActorBase");
                if (actor == null) return;
                bool isPlayerFaction = FactionIsControlledByPlayer(GetProp(actor, "TacticalFaction"));
                if (isPlayerFaction) return;   // friendly / player action — client owns its own-turn camera

                bool isVisible = IsActorVisibleToPlayerFaction(actor);
                if (!ClientEnemyTurnCameraGate.ShouldBroadcastEnemyCameraHint(trackWithCamera, isPlayerFaction, isVisible))
                    return;

                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (netId < 0) return;

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacCameraHint);
                byte[] payload = TacticalLiveCodec.EncodeCameraHint(seq, netId);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacCameraHint, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.camerahint seq=" + seq + " enemy=" + netId +
                          " ability=" + ability.GetType().Name);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastCameraHint failed: " + ex); }
        }

        // ─── CLIENT: follow the acting enemy (follow=true), gated to the client's enemy turn ──────────
        /// <summary>CLIENT inbound (<c>tac.camerahint</c>): resolve the enemy actor and chase it (follow=true) so
        /// the camera tracks it through its action. No-op off-client / off-session / stale seq / not the client's
        /// enemy turn (ClientEnemyTurnCameraGate — never fights the client's own-turn input). Idempotent.</summary>
        public static void ClientOnCameraHint(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeCameraHint(payload, out var hint)) { Debug.LogError("[Multiplayer][tac] tac.camerahint decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacCameraHint, hint.Seq)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(hint.ActorNetId);
            // TELEMETRY (divergence diag): the 0x97 chase path was previously unlogged, so a per-client miss
            // (hint dropped / actor unresolved / not the client's enemy turn / gate false) was invisible in
            // Player.log. One line captures every input to the chase decision.
            bool willChase = ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, actor != null);
            Debug.Log("[Multiplayer][tac] CLIENT tac.camerahint seq=" + hint.Seq + " enemy=" + hint.ActorNetId +
                      " resolved=" + (actor != null) + " isClientEnemyTurn=" + TacticalTurnSync.IsClientEnemyTurn +
                      " willChase=" + willChase);
            // Re-target is automatic even when already latched on another actor: ChaseActor pushes
            // CameraDirector.Hint(ChaseTarget, p) → PlanarScrollCamera.Chase(p) which OVERWRITES _chaseParams
            // wholesale (PlanarScrollCamera.cs:825), so a new hint always re-points the follow transform. No
            // "already latched" guard is needed here.
            if (willChase)
                ChaseActor(actor, follow: true);
            TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacCameraHint, hint.Seq);
        }

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
                Debug.LogWarning("[Multiplayer][tac] enemy-turn camera resolve failed: " + e.Message);
            }
        }

        /// <summary>Chase the actor. follow=true tracks the live transform (moves); follow=false
        /// snaps once to the actor's current position (shot/melee).</summary>
        public static void ChaseActor(object actor, bool follow)
        {
            if (actor == null) { Debug.Log("[Multiplayer][tac] cam CHASE skip: null actor follow=" + follow); return; }
            EnsureResolved();
            if (_resolveFailed) { Debug.Log("[Multiplayer][tac] cam CHASE skip: resolve failed actor=" + ActorTag(actor) + " follow=" + follow); return; }
            try
            {
                object director = GetProp(GetProp(TacticalDeploySync.LiveTlc, "View"), "CameraDirector");
                if (director == null) { Debug.Log("[Multiplayer][tac] cam CHASE skip: no director actor=" + ActorTag(actor) + " follow=" + follow); return; }

                object p = Activator.CreateInstance(_chaseParamsType);
                _fSnapToFloor?.SetValue(p, true);
                _fOnlyOutsideFrame?.SetValue(p, true);

                if (follow)
                {
                    Transform tr = (actor as Component)?.transform;
                    if (tr == null) { Debug.Log("[Multiplayer][tac] cam CHASE skip: no transform actor=" + ActorTag(actor)); return; }
                    _fChaseTransform?.SetValue(p, tr);
                }
                else
                {
                    _fChaseVector.SetValue(p, GetPos(actor));
                }

                _directorHint.Invoke(director, new object[] { _chaseTargetHint, p });
                Debug.Log("[Multiplayer][tac] cam CHASE applied actor=" + ActorTag(actor) + " follow=" + follow);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multiplayer][tac] enemy-turn camera chase failed: " + e.Message);
            }
        }

        /// <summary>CLIENT: release the enemy-turn chase latch by re-hinting <c>ChaseTarget</c> with a NULL param
        /// down the SAME low-level path <see cref="ChaseActor"/> uses:
        /// <c>CameraDirector.Hint(CameraHint.ChaseTarget, null)</c> → <c>CameraManager.Hint</c> →
        /// <c>PlanarScrollCamera.HandleHint</c> → <c>Chase(null)</c> → <c>_chaseParams = null</c>
        /// (PlanarScrollCamera.cs:361-365, 825-833). A <c>follow=true</c> chase glues the camera to the actor's
        /// live transform and NEVER auto-ends — <c>UpdateCameraChase</c> only ends a chase whose
        /// <c>ChaseTransform == null</c> (PlanarScrollCamera.cs:747) — so nothing clears it until the player
        /// scrolls (which internally calls the same EndChase). This is that programmatic release, called on the
        /// client's enemy→player phase transition so the resumed player turn starts with a free camera. Idempotent
        /// (a no-op re-hint when nothing is latched) + best-effort (swallows resolve / null-director).</summary>
        public static void ReleaseChase()
        {
            EnsureResolved();
            if (_resolveFailed) return;
            try
            {
                object director = GetProp(GetProp(TacticalDeploySync.LiveTlc, "View"), "CameraDirector");
                if (director == null) { Debug.Log("[Multiplayer][tac] cam RELEASE skip: no director"); return; }
                _directorHint.Invoke(director, new object[] { _chaseTargetHint, null });
                Debug.Log("[Multiplayer][tac] cam RELEASE: chase latch cleared (ChaseTarget<-null)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multiplayer][tac] enemy-turn camera release failed: " + e.Message);
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

        /// <summary>DIAG: cheap actor label for chase telemetry — the Unity object name, else the runtime type.</summary>
        private static string ActorTag(object actor)
            => actor is UnityEngine.Object o && o != null ? o.name : (actor?.GetType().Name ?? "null");

        private static bool FactionIsControlledByPlayer(object faction)
            => faction != null && GetProp(faction, "IsControlledByPlayer") is bool b && b;

        /// <summary>HOST or CLIENT-mirror: is <paramref name="actor"/> currently VISIBLE (revealed or located) to the
        /// shared player faction? Resolves the player faction off the live TLC, reads its
        /// <c>Vision.IsRevealed/IsLocated(actor)</c> — on the CLIENT that reads the MIRRORED KnownActors (the host
        /// reveals pushed via <c>tac.vision</c>). The host 0x97 gate feeds it into
        /// <see cref="ClientEnemyTurnCameraGate.ShouldBroadcastEnemyCameraHint"/>; the client's move/fire/melee
        /// per-action chases AND it after <see cref="ClientEnemyTurnCameraGate.ShouldChaseEnemyAction"/> so the mirror
        /// never follows a fog-hidden enemy the host only replays for world-state sync. Fails CLOSED (returns false →
        /// no chase/broadcast) on any resolution failure so a hidden enemy is never revealed by the camera. Mirrors the
        /// vision read in <see cref="TacticalVisionSync"/> (kept local — camera-scoped).</summary>
        public static bool IsActorVisibleToPlayerFaction(object actor)
        {
            try
            {
                object tlc = TacticalDeploySync.LiveTlc;
                if (tlc == null) return false;
                var factions = GetProp(tlc, "Factions") as IEnumerable;
                if (factions == null) return false;
                object playerFaction = null;
                foreach (var f in factions)
                {
                    if (f != null && FactionIsControlledByPlayer(f)) { playerFaction = f; break; }
                }
                if (playerFaction == null) return false;
                object vision = GetProp(playerFaction, "Vision");
                if (vision == null) return false;
                return InvokeBool(vision, "IsRevealed", actor) || InvokeBool(vision, "IsLocated", actor);
            }
            catch { return false; }
        }

        private static bool InvokeBool(object obj, string method, object arg)
        {
            if (obj == null) return false;
            var m = AccessTools.Method(obj.GetType(), method);
            if (m == null) return false;
            object r = m.Invoke(obj, new[] { arg });
            return r is bool b && b;
        }
    }
}
