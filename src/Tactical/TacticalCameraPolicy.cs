using System.Reflection;
using Base.Cameras;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Tactical.Cameras;
using PhoenixPoint.Tactical.Entities;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// CAMERA OWNERSHIP — the cinematic belongs to whoever is WATCHING that soldier, not to whoever moved
    /// him. Six players commanding six soldiers at once (law 5) means six independent cameras; A5 mirrors
    /// every <c>TacticalAbility.Activate</c> onto every peer, and the NATIVE code inside that call
    /// (<c>TacticalAbility.Activate</c>:1102-1108, gated on <c>TrackWithCamera</c>:278-288) pushes
    /// <c>CameraDirectorHint.AbilityActivated</c> — so today every peer's camera is yanked onto a soldier
    /// someone ELSE is playing, mid-order, while its own soldier is walking.
    ///
    /// THERE IS NO CAMERA CODE OF OURS TO FIX. The push is the game's, inside the very call the mirror has
    /// to make, so the seam is a presentation FILTER (law 4c) on the game's own choke point:
    /// <c>CameraDirector.Hint(CameraDirectorHint, CameraDirectorParams)</c> (<c>CameraDirector</c>:123).
    /// NO WIRE BYTES, NO SURFACE, NO STATE — camera is local cosmetics that law 5 forbids relaying.
    ///
    /// THE RULE, and why it needs nobody else's selection:
    ///  • AI/enemy turn (<c>CurrentFaction.IsControlledByPlayer == false</c>) → EVERY hint passes. The shared
    ///    monster cinematic then falls out FREE, with no agreement protocol: A5 already mirrors the alien's
    ///    <c>Activate</c> on every peer, so every peer independently pushes the same hint for the same actor.
    ///  • Player turn → pass only for the actor THIS peer has selected (<c>TacticalView.SelectedActor</c>).
    ///    Each peer tests its OWN selection, which is exactly "both of us watching the same soldier both get
    ///    the cinematic" — two peers on one soldier both pass, and nobody has to publish a selection.
    ///
    /// NARROWED BY THE PARAM TYPE, deliberately. <c>CameraDirector</c> is shared with the geoscape
    /// (<c>GeoscapeView</c>:1109), and only an ABILITY cinematic carries <see cref="TacAbilityDirectorParams"/>
    /// (<c>TacAbilityDirectorParams</c>:24-28, <c>.ActorBase</c> = the acting actor). Actor reveals
    /// (<c>TacticalView</c>:908) and selection chases (<c>TacticalActorViewBase.DoCameraChase</c>:486) ride
    /// plain <c>TacCamDirectorParams</c> / the chase path and are untouched — local selection and the
    /// mission-start intro keep working.
    ///
    /// NOT <c>CameraDirector.Silenced</c>: it is vestigial. Nothing in <c>CameraDirector</c> or
    /// <c>CameraManager</c> reads it — the only readers are <c>TacConsoleGameplay</c>:531/568 (the
    /// <c>silence_cameras</c> console command) and the writer at <c>TacticalView</c>:1210.
    ///
    /// NO HANG RISK: <c>TacticalView.WaitForCameraChase</c>:953-966 loops on <c>CameraDirector.Chasing</c>,
    /// which is <c>PlanarScrollCamera.IsDoingChase</c> (<c>CameraDirector</c>:35-45) — no hint pushed means
    /// no chase started means the wait exits on its first evaluation.
    /// </summary>
    internal static class TacticalCameraPolicy
    {
        /// <summary>THE RULE ITSELF, pure and free of game types so RailCheck L75 can hold it to its truth
        /// table. Enemy turn = shared cinematic for everyone; player turn = only the peer watching that
        /// soldier. Reference identity, never Unity <c>==</c>: both sides are MonoBehaviours and the
        /// overloaded operator answers "is this destroyed", which is a different question.</summary>
        internal static bool Allow(bool playerTurn, object actorBase, object selectedActor)
            => !playerTurn || ReferenceEquals(actorBase, selectedActor);

        /// <summary>THE NARROWING, split out for the same reason: it is the single thing keeping this filter
        /// off the geoscape and off every non-ability tactical hint, and it is one word wide. Only an ABILITY
        /// cinematic carries <see cref="TacAbilityDirectorParams"/>; reveals and selection chases carry the
        /// plain <c>TacCamDirectorParams</c> base. RailCheck L75 holds all three answers.</summary>
        internal static bool IsAbilityCinematic(CameraDirectorParams param) => param is TacAbilityDirectorParams;

        /// <summary>Prefix verdict for the ability-cinematic hint. Anything that is not an ability cinematic,
        /// and every solo session, returns true unchanged.</summary>
        internal static bool AllowAbilityHint(CameraDirectorHint hint, CameraDirectorParams param)
        {
            if (!IsAbilityCinematic(param)) return true;         // geoscape + every non-ability hint
            var abilityParams = (TacAbilityDirectorParams)param;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // solo play stays fully native

            TacticalActorBase actor = abilityParams.ActorBase;
            if (ReferenceEquals(actor, null)) return true;
            var tlc = actor.TacticalLevel;
            if (ReferenceEquals(tlc, null)) return true;

            var faction = tlc.CurrentFaction;
            var view = tlc.View;
            var selected = ReferenceEquals(view, null) ? null : view.SelectedActor;
            if (Allow(faction != null && faction.IsControlledByPlayer, actor, selected)) return true;

            // Never silent: an unexplained camera that DOESN'T move is as confusing as one that does, and a
            // swallow with no log line is this repo's dominant bug class. One line per suppressed cinematic
            // (~tens per battle), so it is not gated behind MpDiag.
            Debug.Log("[Multiplayer][tac] camera hint suppressed — " + hint + " for '" + actor.DisplayName +
                      "', this peer is watching '" + (ReferenceEquals(selected, null) ? "<nothing>" : selected.DisplayName) +
                      "'. Player turn: the cinematic belongs to the peers holding that soldier.");
            return false;
        }

        /// <summary>Companion guard. <c>TacticalAbility.OnPlayingActionEnd</c>:1067-1069 pops
        /// <c>AbilityActivated</c> unconditionally, so a suppressed push still gets its pop. The pop itself
        /// is harmless (<c>CameraDirectorState.Pop</c> no-ops on an empty match) but <c>RemoveHint</c>:129
        /// then runs <c>Evaluate()</c>, which re-matches the director tree and re-instantiates a
        /// non-persistent <c>ActionCamDef</c> (<c>CameraDirector</c>:213-219) — a visible jolt on a peer that
        /// was told nothing happened.
        ///
        /// ponytail: pops are matched by PRESENCE, not by ability identity. If this peer holds its OWN live
        /// cinematic while a suppressed one ends, the foreign pop consumes the local hint (state stays
        /// correct in count; the camera simply stops tracking one cinematic early and self-heals on the next
        /// hint). Upgrade path if that ever reads badly in play: remember suppressed abilities and pair the
        /// pop through a prefix on <c>TacticalAbility.OnPlayingActionEnd</c>.</summary>
        internal static bool AllowRemoveHint(CameraDirector director, CameraDirectorHint hint)
        {
            if (hint != CameraDirectorHint.AbilityActivated) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;
            return director.DirectorState.Contains(hint);
        }
    }

    /// <summary>Presentation seam (law 4c) on the game's hint choke point. The signature is PINNED:
    /// <c>CameraDirector</c> has a second, unrelated <c>Hint(CameraHint, object)</c> overload at :167, and
    /// <c>AccessTools.Method</c> matches parameters EXACTLY — an unpinned lookup here would resolve the wrong
    /// method or none, and a null <c>TargetMethod</c> is how a patch silently never binds.</summary>
    [HarmonyPatch]
    internal static class CameraAbilityHintGate
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(CameraDirector), nameof(CameraDirector.Hint),
                               new[] { typeof(CameraDirectorHint), typeof(CameraDirectorParams) });

        private static bool Prefix(CameraDirectorHint hint, CameraDirectorParams param)
            => TacticalCameraPolicy.AllowAbilityHint(hint, param);
    }

    /// <summary>The pop half — see <see cref="TacticalCameraPolicy.AllowRemoveHint"/>.</summary>
    [HarmonyPatch]
    internal static class CameraAbilityUnhintGate
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(CameraDirector), nameof(CameraDirector.RemoveHint),
                               new[] { typeof(CameraDirectorHint) });

        private static bool Prefix(CameraDirector __instance, CameraDirectorHint hint)
            => TacticalCameraPolicy.AllowRemoveHint(__instance, hint);
    }
}
