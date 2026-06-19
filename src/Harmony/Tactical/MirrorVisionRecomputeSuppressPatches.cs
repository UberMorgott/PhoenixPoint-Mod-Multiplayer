using System.Reflection;
using HarmonyLib;
using Multipleer.Sync.Tactical;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// Inc Vision — CLIENT mirror single-writer guard. The host→client vision push (<c>tac.vision</c>,
    /// <see cref="TacticalVisionSync"/>) must be the ONLY writer of the player faction's
    /// <c>TacticalFactionVision.KnownActors</c> on a mirroring client. But the client still runs the player
    /// faction's <c>PlayTurnCrt</c> (TacticalFaction.cs:389), which calls <c>Vision.OnFactionStartTurn()</c>
    /// (:396) — a full raycast recompute of every faction's vision — and the client also applies host moves
    /// (Navigate/SetPosition) which raise <c>ActorMovedEvent</c> → <c>Vision.OnActorMoved(actor)</c>
    /// (TacticalFactionVision.cs:274, subscribed at :146-147), a per-move raycast recompute. Both are local
    /// raycasts that (a) can only produce RED (losing the host's GREY/Located distinction), (b) diverge from the
    /// host's authoritative knowledge, and (c) clobber/compete with the just-pushed snapshot.
    ///
    /// FIX: on a mirroring client (<see cref="TacticalDeploySync.IsClientMirroring"/>) SKIP both recompute
    /// entries. The host push + the reconcile apply is then the single writer. Off-mirror (host / single-player)
    /// these are byte-identical no-ops (native runs). Consumers (icons, target gate, UI) only READ KnownActors,
    /// so suppressing the local WRITE recompute does not break them — they read the host-pushed state.
    ///
    /// Both targets return void → a false prefix just skips the body (no <c>__result</c> to set). Auto-registers
    /// via PatchAll. Narrow + defensive (theturned-tftv-compat pattern).
    /// </summary>
    [HarmonyPatch]
    public static class VisionOnFactionStartTurnSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (t == null) return false;
            // public void OnFactionStartTurn()  (TacticalFactionVision.cs:155) — no params.
            _target = AccessTools.Method(t, "OnFactionStartTurn");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix()
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / non-mirror → run native recompute
            return false;                                            // mirror → host push is the only writer
        }
    }

    /// <summary>Client mirror: skip the per-move local vision recompute
    /// (<c>TacticalFactionVision.OnActorMoved(TacticalActorBase)</c>). Patching the 1-arg overload covers BOTH
    /// the <c>ActorMovedEvent</c> (3-arg) and <c>ActorFinishedMovingEvent</c> (1-arg) subscriptions, because the
    /// 3-arg overload delegates to the 1-arg (TacticalFactionVision.cs:271). Exact param-match binding on the
    /// 1-arg overload (AccessTools is exact — disambiguate by the TacticalActorBase param type).</summary>
    [HarmonyPatch]
    public static class VisionOnActorMovedSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (t == null) return false;
            var actorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
            if (actorBaseType == null) return false;
            // private void OnActorMoved(TacticalActorBase movedActor) — the 1-arg overload (the 3-arg delegates
            // to it). EXACT param-match so we bind THIS overload, not the (actor, Vector3, Quaternion) one.
            _target = AccessTools.Method(t, "OnActorMoved", new[] { actorBaseType });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix()
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;
            return false;
        }
    }
}
