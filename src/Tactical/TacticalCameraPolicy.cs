using System.Reflection;
using Base.Cameras;
using Base.Cameras.CameraNodes;
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
    /// NARROWED BY THE HINT, not by the param type. The first cut of this filter tested
    /// <c>param is TacAbilityDirectorParams</c> and that is why the complaint SURVIVED it: an ability's own
    /// <c>AbilityActivated</c> push is only the FIRST of four action-camera hints, and the three that yank
    /// hardest — a shot — carry <see cref="TacOrbitCamDirectorParams"/> instead, a SIBLING subclass the type
    /// test could never match. <c>TacticalLevelController.FireWeaponAtTargetCrt</c> pushes
    /// <c>Shoot</c>:1600/1617, <c>ShootingStarted</c>:1806 and <c>Weapon.SpawnProjectile</c>:460 pushes
    /// <c>ProjectileFired</c> — all with <c>Actor = </c> the shooter, so all four name their actor through the
    /// common base <see cref="TacCamDirectorParams"/>:9 (<c>.ActorBase</c>).
    ///
    /// The four ARE the family and nothing else is. Everything the earlier narrowing protected stays
    /// protected, now by name rather than by type: <c>ActorReveal</c> (<c>TacticalView</c>:908),
    /// <c>Die</c> (<c>RagdollDieAbility</c>:73), <c>EnterPlay</c> (<c>EnterPlayAbility</c>:72),
    /// <c>Aim</c>/<c>ManualAim</c> (<c>UIStateShoot</c>:514-524) and every <c>Geoscape*</c> hint on the shared
    /// <c>CameraDirector</c> (<c>GeoscapeView</c>:1109) are NOT in the set and pass untouched — so local
    /// selection, the death cam and the mission-start intro keep working. Selection chases never reach this
    /// method at all (<c>TacticalActorViewBase.DoCameraChase</c>:486 rides <c>CameraHint.ChaseTarget</c>, the
    /// unrelated :167 overload).
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

        /// <summary>THE ACTION-CAMERA FAMILY — the four hints an ACTION pushes, and the whole width of this
        /// filter. Named explicitly rather than derived from the param type, because the shot hints are a
        /// sibling subclass (<see cref="TacOrbitCamDirectorParams"/>) of the ability one and a type test
        /// silently missed them. Everything outside this set is somebody's LOCAL camera and passes.</summary>
        internal static bool IsActionHint(CameraDirectorHint hint)
            => hint == CameraDirectorHint.AbilityActivated || hint == CameraDirectorHint.Shoot ||
               hint == CameraDirectorHint.ShootingStarted   || hint == CameraDirectorHint.ProjectileFired;

        /// <summary>THE NARROWING, split out for the same reason: it is the single thing keeping this filter
        /// off the geoscape and off every non-action tactical hint. An action hint whose params cannot name an
        /// actor is not filterable and passes. RailCheck L75 holds every answer.</summary>
        internal static bool IsAbilityCinematic(CameraDirectorHint hint, CameraDirectorParams param)
            => IsActionHint(hint) && param is TacCamDirectorParams;

        /// <summary>Prefix verdict for an action-camera hint. Anything outside the family, and every solo
        /// session, returns true unchanged.</summary>
        internal static bool AllowAbilityHint(CameraDirectorHint hint, CameraDirectorParams param)
        {
            if (!IsAbilityCinematic(hint, param)) return true;   // geoscape + every non-action hint
            var abilityParams = (TacCamDirectorParams)param;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // solo play stays fully native

            TacticalActorBase actor = abilityParams.ActorBase;
            if (ReferenceEquals(actor, null)) return true;
            var tlc = actor.TacticalLevel;
            if (ReferenceEquals(tlc, null)) return true;

            var faction = tlc.CurrentFaction;
            var view = tlc.View;
            var selected = ReferenceEquals(view, null) ? null : view.SelectedActor;
            bool allowed = Allow(faction != null && faction.IsControlledByPlayer, actor, selected);

            // The L104(j) wind-up instrumentation that stood here (two Debug.Log per shot per peer) is GONE:
            // the question it measured — when each peer's aim entry begins — was answered and closed by
            // L230/L231, which now start every peer's animation from the host's own record.
            if (allowed) return true;

            // Never silent: an unexplained camera that DOESN'T move is as confusing as one that does, and a
            // swallow with no log line is this repo's dominant bug class. One line per suppressed cinematic
            // (~tens per battle), so it is not gated behind MpDiag.
            Debug.Log("[Multiplayer][tac] camera hint suppressed — " + hint + " for '" + actor.DisplayName +
                      "', this peer is watching '" + (ReferenceEquals(selected, null) ? "<nothing>" : selected.DisplayName) +
                      "'. Player turn: the cinematic belongs to the peers holding that soldier.");
            return false;
        }

        /// <summary>THE CAMERA MUST ARRIVE AT THE SAME MOMENT ON EVERY PEER, because the game makes the
        /// ACTION wait for it. <c>WaitingForCameraBlendingAction</c>:969-974 holds the ability's whole action
        /// body until <c>WaitForCameraChase</c>:962 stops spinning on <c>CameraDirector.Chasing</c>, and that
        /// is <c>PlanarScrollCamera.IsDoingChase</c>:256 — "is my camera still more than 0.2 away from where
        /// this chase is heading". It is the ONLY distance-dependent camera wait in the assembly (nothing else
        /// reads <c>Chasing</c>), and the flight it waits out lasts <c>distance/CameraChaseSpeed</c>
        /// (<c>PlanarScrollCamera</c>:717-721) — so the moment a shot begins was being set by where THAT peer's
        /// camera happened to be pointing. Two screens, two distances, two start times.
        ///
        /// The fix is to remove the DISTANCE, not the wait. Before the director issues its real chase we run
        /// the game's OWN instant-reposition recipe at the same destination —
        /// <c>CameraChaseParams.Instant</c> + <c>EndChaseIfInstant</c>, exactly the pair
        /// <c>PlanarCamDef.GetChaseParams</c>:69-70 sets for itself when the previous node was not a planar cam
        /// (<c>ChaseImpl</c>:816-822 → <c>UpdateCameraChase</c>:687-698 → <c>_target.Set</c>, a cut, that frame).
        /// The real chase then starts from zero distance on every peer: <c>IsDoingChase</c> is false on its
        /// first evaluation, the action begins NOW everywhere, and the chase itself stays live and native, so a
        /// walking soldier is still followed with the game's own damping. A far camera cuts; a camera already
        /// on the subject sets its target to where it already is and nothing moves. The `LockCamera` that
        /// follows (<c>CameraDirector</c>:178) is a DURATION, identical on all peers, so it never diverged.
        ///
        /// SCOPED BY <see cref="IAbilityParams"/> — THE GAME'S OWN NAME FOR THE ACTION-CAMERA FAMILY, and the
        /// same correction <see cref="IsActionHint"/> already had to make. The first cut tested
        /// <see cref="TacAbilityDirectorParams"/>, which is only what <c>AbilityActivated</c> carries
        /// (<c>TacticalAbility.Activate</c>:1104, <c>SpawnActorAbility</c>:142); the three SHOT hints carry
        /// <see cref="TacOrbitCamDirectorParams"/>, a SIBLING subclass that test could never match, so a shot
        /// that evaluated to a planar node kept its full flight and kept setting the moment the action began.
        /// <c>IAbilityParams</c> is implemented by exactly those two types and by nothing else in the assembly,
        /// so it names the four action hints precisely — a wider <c>TacCamDirectorParams</c> would also have
        /// swallowed the <c>ActorReveal</c> pan, which is each peer's own spotting cinematic and gates nothing
        /// shared. Both types name their actor through <see cref="TacCamDirectorParams"/>.<c>ActorBase</c>.
        ///
        /// Still cannot reach a selection chase (<c>DoCameraChaseParam</c>:491 builds its own params and never
        /// calls <c>GetChaseParams</c>) nor the geoscape (<c>GeoscapeCamDef</c> is not a <c>PlanarCamDef</c>).
        /// When a shot hint evaluates to the ORBIT behaviour instead this is simply not called — which is why
        /// widening it costs nothing and closes the case where it is.
        ///
        /// Copies <c>ChaseOnlyOutsideFrame</c> deliberately: with it the game only moves at all when the
        /// subject is off screen (<c>UpdateCameraChase</c>:689), and a peer that already has it framed must
        /// keep its view — that peer's wait is zero either way, which is all this is for.</summary>
        internal static void SnapToAbilitySubject(CameraDirectorParams directorParams, CameraChaseParams chase)
        {
            if (chase == null || chase.Instant) return;   // the game is already cutting — PlanarCamDef:69-70
            // IAbilityParams is implemented by exactly TacAbilityDirectorParams and TacOrbitCamDirectorParams,
            // and both derive from TacCamDirectorParams — so one cast names the family AND the actor.
            if (!(directorParams is IAbilityParams) || !(directorParams is TacCamDirectorParams abilityParams))
                return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;   // solo play stays fully native

            TacticalActorBase actor = abilityParams.ActorBase;
            if (ReferenceEquals(actor, null)) return;
            var director = actor.CameraDirector;
            if (ReferenceEquals(director, null)) return;
            // SwitchToBehavior ran synchronously two lines up (CameraManager.SetCamera:167-175), so this is
            // the planar camera in every real case; if it somehow is not, there is no chase to shorten.
            if (!(director.Manager.CurrentBehavior is PlanarScrollCamera)) return;

            director.Hint(CameraHint.ChaseTarget, new CameraChaseParams
            {
                ChaseTransform = chase.ChaseTransform,
                ChaseVector = chase.ChaseVector,
                SnapToFloorHeight = chase.SnapToFloorHeight,
                ChaseOnlyOutsideFrame = chase.ChaseOnlyOutsideFrame,
                Instant = true,
                EndChaseIfInstant = true,
            });
        }

        // THE POP IS NEVER GATED — and the deleted gate that used to do it is why a peer's camera
        // LOCKED onto a soldier and stopped obeying him (live, 3-peer session 2026-08-07, "when two players
        // act simultaneously one player's camera locks onto a soldier and he can no longer move it").
        //
        // A CHASE ISSUED BY THE DIRECTOR CANNOT END BY ITSELF, and the game says so in its own tooltip:
        // PlanarCamDef:22 — "Chasing a transform has to be manually deactivated or the camera will be
        // locked." GetChaseParams:44 sets LockCameraMovement = DisableInputDuringChase, which
        // DEFAULTS TO TRUE, and points ChaseTransform at the acting actor. From there:
        // PlanarScrollCamera:468 drops every pan/drag/edge-scroll input while that flag is set,
        // :838 StopCameraChase explicitly REFUSES to end a locked chase, and :747 can only self-end
        // a chase whose ChaseTransform is null. The one and only release is
        // CameraDirector.Evaluate() re-matching the tree and issuing a different chase (or a null
        // one) — and Evaluate is reached from exactly three places, all of which are the pop side:
        // RemoveHint:129, RemoveHints:135 and ForcedReset:290.
        //
        // THE OLD GATE SUPPRESSED RemoveHint WHOLESALE when the hint was not in
        // DirectorState, to avoid a cosmetic jolt from a re-instantiated non-persistent
        // ActionCamDef. But CameraDirectorState.Pop ALREADY no-ops on an empty match, so the
        // gate protected nothing the game did not protect itself — while taking the Evaluate() with
        // it. And the state can lose entries without a pop: Evaluate:163 ends with
        // ClearUnmanagedParams, which drops every item whose params are Unmanaged and whose
        // hint is not in the node's StateFrom. Two peers acting at once is precisely what makes those extra
        // evaluations happen: each mirrored order this peer suppresses still delivers its pop, and each pop
        // re-evaluates. Its real pop then found nothing in the state, the gate skipped the original, no
        // Evaluate ran, and the locked chase on that soldier stayed installed for good.
        //
        // CORRECTION (2026-08-08), because the paragraph above used to justify itself with a fact that is
        // FALSE: it claimed "the whole SHOT family is unmanaged (TacOrbitCamDirectorParams, Unmanaged: True in
        // every director log line)". TacOrbitCamDirectorParams never sets Unmanaged and CameraDirectorParams
        // defaults it to false; across the whole assembly the only two pushes that set it are Weapon:459-465
        // (ProjectileFired) and TacticalView:911 (ActorReveal). So the SHOT family is MANAGED and the sweep
        // cannot touch it. The gate deletion still stands — Pop already no-ops and the lost Evaluate was the
        // real damage — but the sweep is not what consumes a live shot entry, and the same fact is why a
        // stranded Aim hint is PERMANENT rather than self-healing (see
        // TacticalCommandSync.TargetingCameraHints, which is now what clears it).
        //
        // So the release path is left exactly as the game wrote it. The cost is the jolt the gate was
        // bought for, plus the pre-existing behaviour it never changed anyway: a suppressed cinematic's pop
        // can consume this peer's own live hint, so a camera occasionally stops tracking one cinematic
        // early and self-heals on the next hint. A camera that stops following is a frame of scenery; a
        // camera that stops obeying is a lost turn.

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

    /// <summary>Presentation seam (law 4c) on the chase the director is about to issue for an ability's
    /// camera — see <see cref="TacticalCameraPolicy.SnapToAbilitySubject"/>. The override
    /// <c>MovementChasingPlanarCamDef.GetChaseParams</c>:12 is NOT patched and does not need to be: it calls
    /// through to this base body, so patching the base covers both. No wire bytes, no surface — this only
    /// changes how fast THIS peer's own camera gets where it was already going (law 5).</summary>
    [HarmonyPatch(typeof(PlanarCamDef), nameof(PlanarCamDef.GetChaseParams))]
    internal static class AbilityCameraArrivesAtOnce
    {
        private static void Postfix(CameraDirectorParams directorParams, CameraChaseParams __result)
            => TacticalCameraPolicy.SnapToAbilitySubject(directorParams, __result);
    }

    // NO POP HALF, DELIBERATELY — the note at the end of TacticalCameraPolicy says why. RemoveHint is the
    // game's only release for a locked chase, and a prefix that can skip it is a camera a player never gets
    // back. RailCheck L162 asserts this stays deleted.
}
