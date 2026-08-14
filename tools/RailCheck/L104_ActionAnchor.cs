using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View;

namespace RailCheck
{
    /// <summary>
    /// L104 — A SHARED ACTION STARTS AT ONE MOMENT, AND A TURN DOES NOT END OVER AN ORDER NOBODY HAS PLAYED.
    ///
    /// THE BUG THIS LAW IS THE GRAVE OF (live 3-instance run, 2026-08-05 00:22-00:47). Every action the game
    /// plays is wrapped in <c>TacticalAbility.WaitingForCameraBlendingAction</c>:969-976, which — only when
    /// <c>TrackWithCamera</c> — first spins <c>WaitForCameraChase</c>:953-966 on <c>CameraDirector.Chasing</c>,
    /// i.e. <c>PlanarScrollCamera.IsDoingChase</c>: ONE GLOBAL camera state per peer. So the visible start of a
    /// REPLICATED action was a function of where each peer's camera happened to be pointing. Mirrors were
    /// already exempted; the ACTING peer was not — and the acting peer is precisely the peer that pays, because
    /// it is the one holding that soldier SELECTED and therefore the only one whose hint survives
    /// <c>TacticalCameraPolicy.AllowAbilityHint</c>. Measured in that run: the host, watching a DIFFERENT
    /// soldier (three "camera hint suppressed" lines at 00:33:41.213/41.826/41.878), finished a shot ahead of
    /// both watchers; a peer that IS watching its own soldier pays the whole flight instead.
    /// <c>TacticalCameraPolicy.SnapToAbilitySubject</c> removes the DISTANCE, but only for a
    /// <c>PlanarScrollCamera</c> (:163) — a shot that resolves to the orbit behaviour keeps its full flight,
    /// which is exactly why shooting is where the complaint survived that earlier fix.
    ///
    /// THE SECOND HALF, same report, different mechanism. <c>ShootAbility.Activate</c>:167 branches on
    /// <c>TacticalLevelController.AnyAIEvaluationAbilityExecuting</c> between <c>PlayAction</c>
    /// (<c>cancelCurrent: true</c> — cancels the move it followed and fires at once) and
    /// <c>EnqueueAction(soloAfterCurrent: true)</c> (finishes the walk first). Law L78 — PROSE-ONLY, it never
    /// had an executable arm; see the law-number registry in laws.md — forced that flag TRUE
    /// for every mirror; but the host's own answer is FALSE during a player turn, so the lie made the WATCHERS
    /// the only peers cancelling the walk — "I move behind a wall and immediately shoot and the other windows
    /// do it faster than mine", with the watchers additionally standing where the order never sent them until
    /// the settle dragged them back. Arm (i) pins the narrowing.
    ///
    /// WHY THE ARMS ARE OUTCOMES. Several earlier laws in this repo were green through real breakage because
    /// they asserted that a CALL EXISTS. Every arm below either EXECUTES the decision (d, e) or pins the fact
    /// that makes the mechanism work at all against the REAL game assembly (a, b, c, f) — if the game stops
    /// gating actions on the camera, or stops reading this predicate in its turn loop, the anchor is pointless
    /// and the law says so instead of staying quietly green.
    /// </summary>
    internal static class L104_ActionAnchor
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            foreach (var violation in SubjectGuard(new[] { typeof(TacticalCommandSync) }))
                yield return violation;
            foreach (var violation in PositiveControls(typeof(TacticalCommandSync)))
                yield return violation;

            var cmd = typeof(TacticalCommandSync);
            var ability = typeof(TacticalAbility);
            var trackWithCamera = ability.GetProperty("TrackWithCamera", All)?.GetGetMethod(true);
            var token = cmd.GetField("_mirrorSkipsCameraWait", All);
            var onActivated = cmd.GetMethod("OnAbilityActivated", All);
            var applyActivate = cmd.GetMethod("ApplyActivate", All);
            var tailPatch = typeof(TacticalCommandSync).Assembly
                .GetType("Multiplayer.Tactical.SharedAttackDoesNotWaitForLocalCameraTail");
            var tailScope = typeof(TacticalCommandSync).Assembly
                .GetType("Multiplayer.Tactical.SharedFireCoroutineCameraReadScope");

            // ── (a) THE GAME STILL GATES THE VISIBLE START ON THE CAMERA ───
            // The whole anchor is a claim about the game's own code. Pinned against the real assembly so a
            // patch that quietly removed the wait turns this red rather than leaving a no-op skip in place.
            var blend = ability.GetMethod("WaitingForCameraBlendingAction", All);
            var wait = ability.GetMethod("WaitForCameraChase", All);
            if (blend == null || wait == null || trackWithCamera == null)
                yield return "L104(a): TacticalAbility no longer has WaitingForCameraBlendingAction / " +
                             "WaitForCameraChase / TrackWithCamera — the anchor is a claim about code that is " +
                             "gone, so every peer's action start is once again ungoverned.";
            else
            {
                var body = MoveNextOf(blend);
                if (!Calls(body, wait) || !Calls(body, trackWithCamera))
                    yield return "L104(a): TacticalAbility.WaitingForCameraBlendingAction no longer reaches " +
                                 "WaitForCameraChase under TrackWithCamera — the action no longer waits for the " +
                                 "camera, so skipping that wait anchors NOTHING and this arc needs re-measuring.";
            }

            var tailPostfix = tailPatch?.GetMethod("Postfix", All);
            if (tailPostfix == null || !Calls(tailPostfix,
                    cmd.GetProperty("SharedAttackMustNotWaitForCamera", All)?.GetGetMethod(true)) ||
                !Calls(tailScope?.GetMethod("Prefix", All), cmd.GetMethod("EnterFireCoroutineFrame", All)) ||
                !Calls(tailScope?.GetMethod("Finalizer", All), cmd.GetMethod("ExitFireCoroutineFrame", All)))
                yield return "L104 camera-tail-gates-simulation: FireWeaponAtTargetCrt waits on the local " +
                             "CameraDirector.Busy after projectiles; L75 intentionally makes that camera state " +
                             "different by selection, so a shared attack must force that presentation-only " +
                             "tail out of action completion/settle timing.";

            // ── (b) THE EXEMPTION IS ONE MECHANISM, TAKEN BY BOTH PATHS ────
            // The mirror path (ApplyActivate) and the ACTING path (OnAbilityActivated) must arm the SAME
            // one-shot token. Two mechanisms would drift; one path alone is the bug this law buries.
            if (token == null || onActivated == null || applyActivate == null)
                yield return "L104(b): TacticalCommandSync lost _mirrorSkipsCameraWait / OnAbilityActivated / " +
                             "ApplyActivate — the anchor cannot be armed at all.";
            else
            {
                if (!Touches(applyActivate, token))
                    yield return "L104(b): ApplyActivate no longer arms _mirrorSkipsCameraWait — a mirrored " +
                                 "action is back to waiting for the WATCHING peer's camera, which is the " +
                                 "original desync law 5 forbids (camera is local-only and must not decide WHEN " +
                                 "a shared action begins).";
                if (!Touches(onActivated, token))
                    yield return "L104(b): OnAbilityActivated no longer arms _mirrorSkipsCameraWait — the peer " +
                                 "that CLICKED is once again the only one waiting out a camera flight, so it " +
                                 "starts every aim/shot roughly half a second behind every watcher.";
                // The TrackWithCamera guard is not decoration: it is exactly the arm that reaches
                // WaitForCameraChase (:971). Armed unconditionally, the one-shot token is never consumed and
                // goes STALE — the NEXT action on that same ability object silently skips a wait it should
                // have taken, which is a swallow with no log line, this repo's dominant bug class.
                if (trackWithCamera != null && !Calls(onActivated, trackWithCamera))
                    yield return "L104(b): OnAbilityActivated arms the camera-wait token without reading " +
                                 "TrackWithCamera — a token armed for an ability that never reaches " +
                                 "WaitForCameraChase is never consumed and goes stale onto a later action.";
                if (trackWithCamera != null && !Calls(applyActivate, trackWithCamera))
                    yield return "L104(b): ApplyActivate arms the camera-wait token without reading " +
                                 "TrackWithCamera — same stale-token hazard as the acting path.";
            }

            // ── (c) THE TOKEN ACTUALLY REACHES THE GAME'S WAIT ─────────────
            // A token nothing consumes is a no-op. The patch must resolve against the REAL method and its
            // prefix must both consume the token and SKIP the original — a prefix that returns true would
            // leave the wait running and the law green.
            var patch = typeof(MirroredPlayDoesNotWaitForThisPeersCamera);
            var consume = cmd.GetMethod("ConsumeCameraWaitSkip", All);
            if (patch == null || consume == null)
                yield return "L104(c): the WaitForCameraChase skip patch (or ConsumeCameraWaitSkip) is gone — " +
                             "the anchor arms a token nothing reads, so every peer waits for its own camera again.";
            else
            {
                var attr = Targeted(patch);
                var declared = attr?.declaringType;
                var named = attr?.methodName;
                if (declared != typeof(TacticalAbility) || named != "WaitForCameraChase" ||
                    (declared != null && named != null && declared.GetMethod(named, All) == null))
                    yield return "L104(c): the skip patch no longer names an EXISTING " +
                                 "TacticalAbility.WaitForCameraChase — an unresolvable target is one warning " +
                                 "MultiplayerMain swallows, and the anchor silently does nothing.";
                var prefix = patch.GetMethod("Prefix", All);
                if (prefix == null || prefix.ReturnType != typeof(bool) || !Calls(prefix, consume))
                    yield return "L104(c): the skip patch's Prefix no longer consumes the token and returns " +
                                 "bool — a prefix that cannot skip the original leaves the native wait in place.";
            }

            // ── (d) THE END-TURN GATE, EXECUTED ────────────────────────────
            // The outcome, not a call site: with an order held, the predicate the native turn loop reads must
            // come back TRUE; with nothing held it must not touch the game's own answer in either direction.
            var gate = typeof(EndTurnWaitsForHeldOrders);
            var post = gate.GetMethod("Postfix", All);
            var deferred = cmd.GetField("_deferred", All)?.GetValue(null) as System.Collections.IList;
            if (post == null || deferred == null)
                yield return "L104(d): EndTurnWaitsForHeldOrders.Postfix or TacticalCommandSync._deferred is " +
                             "gone — a turn can end on top of an order the host accepted and has not started, " +
                             "and that order is then refused into a faction that is no longer playing.";
            else
            {
                deferred.Clear();
                if (Verdict(post, false)) yield return "L104(d): with nothing held the gate INVENTS a wait — " +
                                                       "every turn would hang on a board the game calls idle.";
                if (!Verdict(post, true)) yield return "L104(d): the gate CLEARS the game's own wait — it must " +
                                                       "only ever add to it, never overrule it.";
                var elem = deferred.GetType().IsGenericType
                    ? deferred.GetType().GetGenericArguments()[0] : null;
                if (elem == null) yield return "L104(d): _deferred is no longer a generic list, so this arm " +
                                               "cannot construct a held order and the gate is unverified.";
                else
                {
                    deferred.Add(Blank(elem));
                    if (!Verdict(post, false))
                        yield return "L104(d): a HELD order does not make the turn wait — the exact window the " +
                                     "gate exists for (the previous order has ended, HostTick has not yet " +
                                     "released the held one) is open again.";
                    deferred.Clear();
                }
            }

            // ── (e) THE HOLD IS BOUNDED, OR (d) IS A TURN-PARKER ───────────
            // NOT a field-touch test on DeferCeilingSeconds: it is a `const`, so the compiler INLINES it as a
            // literal and no ldsfld ever appears — the arm would have been permanently, invisibly green. The
            // falsifiable property is the one that matters anyway: HostTick can REMOVE a hold, and says so.
            var ceiling = cmd.GetField("DeferCeilingSeconds", All);
            var hostTick = cmd.GetMethod("HostTick", All);
            var deferredField = cmd.GetField("_deferred", All);
            float seconds = ceiling != null && ceiling.IsLiteral && ceiling.FieldType == typeof(float)
                ? (float)ceiling.GetRawConstantValue() : -1f;
            if (seconds <= 0f || seconds > 30f)
                yield return "L104(e): DeferCeilingSeconds is " + seconds + " — a hold with no credible ceiling " +
                             "turns the end-turn gate of arm (d) into a turn that never ends.";
            bool drops = hostTick != null && Touches(hostTick, deferredField);
            if (drops)
            {
                drops = false;
                foreach (var c in Callees(hostTick))
                    if (c.Name == "RemoveAt" || c.Name == "Clear") { drops = true; break; }
            }
            if (!drops)
                yield return "L104(e): HostTick can no longer REMOVE anything from _deferred — a hold that is " +
                             "never dropped keeps HasHeldOrders true forever and parks the turn silently, which " +
                             "is arm (d) turned into the swallow it exists to prevent.";
            if (hostTick != null && !Reaches(hostTick, "Reject"))
                yield return "L104(e): HostTick no longer rejects a hold that gave up — a peer whose order is " +
                             "dropped at the ceiling is told nothing, and its speculative play is never snapped back.";

            // ── (f) THE GATE SITS ON THE PREDICATE THE TURN LOOP READS ─────
            // Gating a method the native loop does not consult is the classic green-through-breakage shape.
            var predicate = typeof(TacticalView).GetMethod("IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate", All);
            var playTurn = typeof(TacticalFaction).GetMethod("PlayTurnCrt", All);
            if (predicate == null || playTurn == null)
                yield return "L104(f): TacticalView.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate or " +
                             "TacticalFaction.PlayTurnCrt is gone — the native end-turn gate this law builds on " +
                             "no longer exists and the whole arm needs re-grounding.";
            else
            {
                if (!Calls(MoveNextOf(playTurn), predicate))
                    yield return "L104(f): TacticalFaction.PlayTurnCrt no longer consults " +
                                 "IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate — the postfix patches a " +
                                 "predicate the turn loop ignores, so the gate is decorative.";
                var gateAttr = Targeted(gate);
                if (gateAttr?.declaringType != typeof(TacticalView) || gateAttr?.methodName != predicate.Name)
                    yield return "L104(f): EndTurnWaitsForHeldOrders no longer targets " +
                                 "TacticalView." + predicate.Name + " — it gates something else, or nothing.";
            }

            // ── (g) NO ARTIFICIAL DELAY WAS SMUGGLED IN ────────────────────
            // The anchor is allowed to REMOVE a wait; it is not allowed to add one. A hand-rolled sleep or
            // timed hold in either family would be a global lag budget nobody measured.
            foreach (var owner in new[] { cmd, typeof(TacticalTurnSync) })
                foreach (var m in owner.GetMethods(All))
                    foreach (var callee in Callees(m))
                    {
                        var dt = callee.DeclaringType;
                        if (dt == null) continue;
                        if (dt.FullName == "UnityEngine.WaitForSeconds" ||
                            (dt.FullName == "System.Threading.Thread" && callee.Name == "Sleep"))
                        {
                            yield return "L104(g): " + owner.Name + "." + m.Name + " reaches " + dt.FullName +
                                         "." + callee.Name + " — the anchor is a REMOVED wait, never an added " +
                                         "one; a hand-rolled delay is an unmeasured lag budget on every action.";
                            break;
                        }
                    }

            // ── (i) THE OTHER HALF OF THE ANCHOR: THE ACTION-CHANNEL BRANCH ─
            // ShootAbility.Activate:167 chooses PlayAction (cancelCurrent:true — starts NOW, cancelling the
            // move it followed) over EnqueueAction(soloAfterCurrent:true) partly on
            // AnyAIEvaluationAbilityExecuting. MirroredPlayMatchesHostPacing forces that flag for a mirror so
            // it paces like the host — but the host's OWN answer is false during a PLAYER turn, so an
            // unnarrowed lie makes a WATCHER the only peer that cancels the walk and fires early. The
            // narrowing must therefore read whose turn it is; it cannot be executed headless (the postfix
            // takes a live TacticalLevelController), so it is pinned by the two getters it must consult.
            var pacing = typeof(MirroredPlayMatchesHostPacing).GetMethod("Postfix", All);
            var currentFaction = typeof(TacticalLevelController).GetProperty("CurrentFaction", All)?.GetGetMethod(true);
            var isAi = typeof(TacticalFaction).GetProperty("IsControlledByAI", All)?.GetGetMethod(true);
            if (pacing == null || currentFaction == null || isAi == null)
                yield return "L104(i): MirroredPlayMatchesHostPacing.Postfix / TacticalLevelController" +
                             ".CurrentFaction / TacticalFaction.IsControlledByAI no longer resolve — the " +
                             "action-channel half of the anchor cannot be checked.";
            else if (!Calls(pacing, currentFaction) || !Calls(pacing, isAi))
                yield return "L104(i): the mirrored-pacing postfix no longer narrows on whose turn it is — " +
                             "during a PLAYER turn it hands a watcher PlayAction(cancelCurrent:true) while the " +
                             "acting peer takes EnqueueAction(soloAfterCurrent:true), so the watcher cancels " +
                             "the move mid-walk and shoots early. That is not an anchor, it is the desync with " +
                             "its sign flipped.";

            // ── (j) EVERY PEER TAKES THE SAME SIDE OF THE AIM BRANCH ───────
            // TacticalLevelController:1645 gates the whole aim entry (:1647-1678, the measured 495-781 ms) on
            // `shootAnimAction.UseAiming && !stepOutNeeded && !tacticalActor.CurrentlyAiming`. CurrentlyAiming
            // is TacticalActor.cs:228 — Animator.GetInteger("TravelType")==7 || GetInteger("ShootSegmentType")
            // ==5 — a LOCAL animator integer that is not in the order and cannot be. Two peers answering it
            // differently take opposite sides of a half-second branch for the SAME shot: observers fire
            // instantly while the acting peer plays the full aim. L104's own anchor (:1274) fixed the camera
            // WAIT; this branch one layer down was never anchored.
            //
            // The branch's other two inputs (UseAiming off the shared anim-action def, stepOutNeeded off the
            // relayed target) are the same on every peer already; CurrentlyAiming is the one that is purely
            // local, so forcing it constant under an arm IS "every peer takes the same side". EXECUTED over
            // the real predicate and the real postfix — a call-site test would stay green through the exact
            // failure this arm exists for.
            var aimPatch = typeof(RelayedAimBranchIsTheSameOnEveryPeer);
            var aimPost = aimPatch.GetMethod("Postfix", All);
            var aimToken = cmd.GetField("_relayedAim", All);
            var underRelayed = cmd.GetMethod("IsRelayedAimKey", All);
            var armAim = cmd.GetMethod("ArmRelayedAim", All);
            var currentlyAiming = typeof(PhoenixPoint.Tactical.Entities.TacticalActor)
                                  .GetProperty("CurrentlyAiming", All)?.GetGetMethod(true);
            var fireCrt = typeof(TacticalLevelController).GetMethod("FireWeaponAtTargetCrt", All);

            if (aimPost == null || aimToken == null || underRelayed == null || armAim == null ||
                currentlyAiming == null)
                yield return "L104(j): the aim anchor no longer resolves (RelayedAimBranchIsTheSameOnEveryPeer" +
                             ".Postfix, TacticalCommandSync._relayedAim/IsRelayedAimKey/ArmRelayedAim, " +
                             "TacticalActor.CurrentlyAiming). The aim entry is back to being decided by each " +
                             "peer's own animator, so one window fires half a second before another on the " +
                             "same order.";
            else
            {
                // The patch must sit on the getter the game's own branch reads — not near it.
                var aimAttr = Targeted(aimPatch);
                if (aimAttr?.declaringType != typeof(PhoenixPoint.Tactical.Entities.TacticalActor) ||
                    aimAttr?.methodName != "CurrentlyAiming")
                    yield return "L104(j): the aim anchor no longer targets TacticalActor.CurrentlyAiming — it " +
                                 "forces some other answer, or none, and :1645 goes back to reading a local " +
                                 "animator integer that differs per peer.";
                if (fireCrt != null && !Calls(MoveNextOf(fireCrt), currentlyAiming))
                    yield return "L104(j): TacticalLevelController.FireWeaponAtTargetCrt no longer reads " +
                                 "CurrentlyAiming — the anchor forces an answer nothing consults, which is the " +
                                 "decorative-patch shape arm (f) already exists to catch once.";
                // ARMED BY BOTH PATHS, exactly like the camera token: one path alone reproduces the desync
                // with its sign flipped (the peer that armed plays one length, everyone else the other).
                if (!Calls(onActivated, armAim))
                    yield return "L104(j): OnAbilityActivated does not arm the aim anchor — the ACTING peer is " +
                                 "the one peer left on its own animator's answer, and it is the peer that " +
                                 "reported the desync.";
                if (!Calls(applyActivate, armAim))
                    yield return "L104(j): ApplyActivate does not arm the aim anchor — every MIRROR is left on " +
                                 "its own animator, so a mirror still walking keeps AimLoop and fires with no " +
                                 "wind-up at all while the acting peer plays the full one.";

                var normalize = cmd.GetMethod("NormalizeRelayedShootStart", All);
                var normalizeRule = cmd.GetMethod("ShouldNormalizeRelayedShoot", All);
                if (normalize == null || normalizeRule == null ||
                    !Calls(normalize, typeof(PhoenixPoint.Tactical.Levels.PathProcessors.PathProcessorUtils)
                                      .GetMethod("SetNullNavParams", All)))
                    yield return "L104(j): relayed shoot baseline is unbound or no longer uses the game's " +
                                 "nav-free SetNullNavParams; FreeCam AimLoop can survive into replay.";
                else
                {
                    var sequence = Callees(applyActivate);
                    int normalizeAt = sequence.FindIndex(m => m.MetadataToken == normalize.MetadataToken &&
                                                              m.Module == normalize.Module);
                    int activateAt = normalizeAt < 0 ? -1 :
                        sequence.FindIndex(normalizeAt + 1, m => m.Name == "Activate");
                    if (normalizeAt < 0 || activateAt < 0 || normalizeAt >= activateAt)
                        yield return "L104(j): ApplyActivate does not normalize the relayed ShootAbility " +
                                     "animator BEFORE native Activate; the clicker's pre-confirm AimLoop and " +
                                     "a watcher's Null state still enter FireWeaponAtTargetCrt differently.";
                    Func<bool, bool, bool> rule = (shoot, navigating) =>
                        (bool)normalizeRule.Invoke(null, new object[] { shoot, navigating });
                    if (!rule(true, false) || rule(false, false) || rule(true, true))
                        yield return "L104(j): shoot-baseline rule must normalize only ShootAbility after nav " +
                                     "completion. This covers FreeCam and move-followup at its destination " +
                                     "without stomping movement or unrelated abilities.";
                }

                // EXECUTED, part 1 — THE ARM PREDICATE ITSELF, which is what makes the branch constant.
                var set = aimToken.GetValue(null) as HashSet<int>;
                if (set == null)
                    yield return "L104(j): _relayedAim is not a key set any more, so this arm cannot construct " +
                                 "an armed order and the anchor is unverified.";
                else
                {
                    set.Clear();
                    bool unarmed = (bool)underRelayed.Invoke(null, new object[] { -7 });
                    armAim.Invoke(null, new object[] { -7 });
                    bool armed = (bool)underRelayed.Invoke(null, new object[] { -7 });
                    bool other = (bool)underRelayed.Invoke(null, new object[] { -8 });
                    bool keyless = (bool)underRelayed.Invoke(null, new object[] { 0 });
                    armAim.Invoke(null, new object[] { 0 });
                    bool keylessAfter = (bool)underRelayed.Invoke(null, new object[] { 0 });
                    set.Clear();
                    if (unarmed || !armed || other)
                        yield return "L104(j): the aim arm does not answer per ORDER (unarmed=" + unarmed +
                                     " armed=" + armed + " other-actor=" + other + "). It must be true for " +
                                     "exactly the actors under a relayed activation: always-false leaves every " +
                                     "peer on its own animator, always-true rewrites solo play as well.";
                    if (keyless || keylessAfter)
                        yield return "L104(j): key 0 answers armed. An actor no peer can NAME is one no peer " +
                                     "can agree with either — forcing its branch changes one screen's animation " +
                                     "and nobody else's, which is the desync with extra steps.";

                    // EXECUTED, part 2 — THE POSTFIX IS ONE-DIRECTIONAL AND A NO-OP OUTSIDE THE ARM.
                    if (!Verdict2(aimPost, true))
                        yield return "L104(j): with NOTHING armed the anchor still forces the answer — solo " +
                                     "play and every unrelayed ability lose vanilla's aim behaviour for no " +
                                     "reason. The anchor is a co-op correction, not a rewrite.";
                    if (Verdict2(aimPost, false))
                        yield return "L104(j): the anchor turns a FALSE answer into TRUE — it may only ever " +
                                     "force one direction (everybody plays the wind-up), never invent an aim " +
                                     "nobody holds, which would make every peer skip a block the host played.";
                }
            }

            // ── (h) THE CATCH-UP MEASUREMENT SAYS IT ONCE PER BURST ────────
            // Executed, because "how often" is the measurement and one line per MESSAGE is the log volume that
            // buried a live run in 23642 lines of a single family.
            var burst = cmd.GetMethod("CatchUpBurst", All);
            if (burst == null)
                yield return "L104(h): TacticalCommandSync.CatchUpBurst is gone — a peer replaying the history " +
                             "it missed is once again indistinguishable from a peer that is merely slow.";
            else
            {
                int frame = 0, count = 0;
                var seq = new List<bool>();
                foreach (var f in new[] { 7, 7, 7, 7, 8, 8, 9 })
                {
                    object[] args = { f, frame, count };
                    seq.Add((bool)burst.Invoke(null, args));
                    frame = (int)args[1]; count = (int)args[2];
                }
                // frame 7: order 1 silent, order 2 SPEAKS, orders 3-4 silent again; frame 8 is a fresh burst
                // (1 silent, 2 speaks); frame 9's single order is the ordinary case and says nothing.
                var want = new[] { false, true, false, false, false, true, false };
                for (int i = 0; i < want.Length; i++)
                    if (seq[i] != want[i])
                    {
                        yield return "L104(h): CatchUpBurst answered [" + string.Join(",", seq.ConvertAll(b => b ? "1" : "0").ToArray()) +
                                     "] over frames 7,7,7,7,8,8,9 but must answer [0,1,0,0,0,1,0] — exactly one " +
                                     "line per burst, none for the ordinary single order.";
                        break;
                    }
            }
        }

        private static IEnumerable<string> SubjectGuard(Type[] subjects)
        {
            if (subjects == null || subjects.Length == 0)
                yield return "L104 premise-changed: an empty subject set was accepted, so the action-anchor law can pass without inspecting its command rail.";
            else if (Array.Exists(subjects, t => t == null))
                yield return "L104 premise-changed: an unresolved subject was accepted, so a missing command rail can make the law vacuous.";
        }

        private static IEnumerable<string> PositiveControls(Type subject)
        {
            if (!HasViolation(SubjectGuard(new Type[0])))
                yield return "L104 control-empty-subject: the executable subject guard did not reject an empty set.";
            if (!HasViolation(SubjectGuard(new Type[] { null })))
                yield return "L104 control-unresolved-subject: the executable subject guard did not reject an unresolved type.";
            if (HasViolation(SubjectGuard(new[] { subject })))
                yield return "L104 control-valid-subject: TacticalCommandSync was rejected by the subject guard, so the executable checks never reached production code.";
        }

        private static bool HasViolation(IEnumerable<string> violations) => violations.GetEnumerator().MoveNext();

        /// <summary>Same, for a postfix that also takes the patched instance. Null is the honest argument
        /// here: an actor cannot be built headless, and with nothing armed the anchor must not consult it.</summary>
        private static bool Verdict2(MethodInfo postfix, bool native)
        {
            object[] args = { null, native };
            postfix.Invoke(null, args);
            return (bool)args[1];
        }

        /// <summary>Drive the ref-bool postfix and read back what it left in the game's answer.</summary>
        private static bool Verdict(MethodInfo postfix, bool native)
        {
            object[] args = { native };
            postfix.Invoke(null, args);
            return (bool)args[0];
        }

        /// <summary>What a patch class declares it patches. <c>HarmonyPatch</c> is AllowMultiple, so
        /// <c>GetCustomAttribute</c> (singular) THROWS on a class carrying two — read the list and take the
        /// one that names a method.</summary>
        private static HarmonyMethod Targeted(Type patchClass)
        {
            if (patchClass == null) return null;
            foreach (HarmonyPatch a in patchClass.GetCustomAttributes(typeof(HarmonyPatch), false))
                if (a.info != null && a.info.methodName != null) return a.info;
            return null;
        }

        /// <summary>A default instance of a PRIVATE nested struct. <c>Activator</c> is the ordinary route;
        /// the fallback exists because accessibility rules differ across runtimes and a law that THROWS is
        /// worse than one that is merely strict.</summary>
        private static object Blank(Type t)
        {
            try { return Activator.CreateInstance(t); }
            catch { return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t); }
        }

        /// <summary>Does this method call anything by that simple name? Used where the callee is an overload
        /// set (<c>IntentRail.Reject</c>) and pinning one signature would be a rename trap of its own.</summary>
        private static bool Reaches(MethodBase m, string calleeName)
        {
            foreach (var c in Callees(m)) if (c.Name == calleeName) return true;
            return false;
        }

        // ─── IL helpers (same technique as L97/L103: real operand-size table, never a byte scan) ───

        private static bool Calls(MethodBase m, MethodBase callee)
        {
            if (m == null || callee == null) return false;
            foreach (var c in Callees(m))
                if (c.MetadataToken == callee.MetadataToken && c.Module == callee.Module) return true;
            return false;
        }

        private static bool Touches(MethodBase m, FieldInfo field)
        {
            if (m == null || field == null) return false;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineField) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos)); } catch { }
                if (f != null && f.MetadataToken == field.MetadataToken && f.Module == field.Module) return true;
            }
            return false;
        }

        /// <summary>Compiler-generated coroutine bodies live on the state machine's MoveNext, not the method
        /// that returns the enumerator — an IL scan of the latter finds one <c>newobj</c> and nothing else.</summary>
        private static MethodBase MoveNextOf(MethodInfo m)
        {
            if (m == null) return null;
            foreach (var t in m.DeclaringType.GetNestedTypes(All))
                if (t.Name.IndexOf(m.Name, StringComparison.Ordinal) >= 0)
                {
                    var mn = t.GetMethod("MoveNext", All);
                    if (mn != null) return mn;
                }
            return m;
        }

        private static List<MethodBase> Callees(MethodBase m)
        {
            var seq = new List<MethodBase>();
            if (m == null) return seq;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                // NEWOBJ counts: arm (g) hunts `new WaitForSeconds(...)`, which is a constructor and would be
                // invisible to a Call/Callvirt-only walk.
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt &&
                     step.Value.Op != OpCodes.Newobj)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                      typeArgs, methodArgs); } catch { }
                if (callee != null) seq.Add(callee);
            }
            return seq;
        }

        private struct Step { public OpCode Op; public int Pos; }

        private static IEnumerable<KeyValuePair<byte[], Step>> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                OpCode op;
                if (!OpCodeByValue.TryGetValue(code, out op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                yield return new KeyValuePair<byte[], Step>(il, new Step { Op = op, Pos = i });
                i += size;
            }
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }

        private static int OperandSize(OperandType t, byte[] il, int pos)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return pos + 4 > il.Length ? -1 : 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
            }
        }
    }
}
