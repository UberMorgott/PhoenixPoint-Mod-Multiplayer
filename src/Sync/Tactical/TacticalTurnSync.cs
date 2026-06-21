using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multipleer.Network;
using UnityEngine;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// LIVE END-TURN + faction-handoff replication (spec §3.5, §5; Inc 4). Host-authoritative turn engine;
    /// the client follows discretely via <c>tac.turn</c> and NEVER self-advances.
    ///
    /// HOST capture: postfix on <c>TacMission.OnNewTurn(prevFaction, nextFaction)</c> (TacMission.cs:359),
    /// which the host's authoritative <c>TacticalLevelController.NextTurnCrt</c> calls exactly once per
    /// faction-turn-start (TLC.cs:716, right before <c>PlayTurnCrt</c>). From <c>nextFaction</c> we read the
    /// faction index, its TurnNumber, and its FactionDef guid, and broadcast <c>tac.turn</c>.
    ///
    /// CLIENT turn-follow (the load-bearing, grounded piece): the Inc-1 blanket suppress of
    /// <c>NextTurnCrt</c> is KEPT (the client must never run the autonomous turn engine), but
    /// <c>PlayTurnCrt</c> is NOT patched. So on <c>tac.turn</c> the client:
    ///   • sets <c>_currentFactionIndex</c> (Traverse) + the faction's <c>TurnNumber</c>,
    ///   • if the new faction <c>IsControlledByPlayer</c> → STARTS the native
    ///     <c>TacticalFaction.PlayTurnCrt(noop)</c> (TacticalFaction.cs:389) so the client ENTERS the player
    ///     turn (sets the viewer faction + <c>IsPlayingTurn</c> + the player input loop), letting the local
    ///     user act. PlayTurnCrt's player branch loops until <c>_endTurnRequested</c>; it does NOT advance
    ///     the faction index (only NextTurnCrt does), so running it alone can never auto-advance the client.
    ///   • else (AI/enemy faction) → does NOT start PlayTurnCrt; the client stays a frozen spectator
    ///     (AIUpdateCrt is suppressed) and enemy actions stream in via tac.move / tac.damage.
    /// This is why the client CAN take its player turn but NEVER runs enemy AI or auto-advances the turn.
    ///
    /// CLIENT end-turn: the existing <c>RequestEndTurnPatch</c> is repointed here — on a mirroring client it
    /// sends <c>tac.intent.endturn</c> to the host AND lets the native <c>RequestEndTurn</c> set the LOCAL
    /// <c>_endTurnRequested</c> flag so the client's own PlayTurnCrt loop exits cleanly (input stops) while
    /// it waits for the host's next <c>tac.turn</c>.
    /// </summary>
    public static class TacticalTurnSync
    {
        private static uint _nonceCounter;
        private static uint NextNonce() => unchecked(++_nonceCounter);

        // CLIENT: a tac.turn that arrived before the live TLC existed (hydrate race). Stashed here and drained
        // at hydrate-completion (ClientEnterInitialTurn). Self-ignores if stale (ShouldApply + monotonic seq).
        private static TacticalLiveCodec.TurnOutcome? _pendingTurn;

        // ─── CLIENT: relay end-turn intent (called from the repointed RequestEndTurnPatch) ─────────
        /// <summary>CLIENT (mirroring): send <c>tac.intent.endturn</c> to the host. Returns true so the
        /// caller lets the native <c>RequestEndTurn</c> run locally (sets _endTurnRequested → the client's
        /// PlayTurnCrt player loop exits). On a non-mirroring instance returns true (untouched, host path).</summary>
        public static bool ClientRelayEndTurn()
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / single-player → run native
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return true;
            try
            {
                byte[] payload = TacticalLiveCodec.EncodeEndTurnIntent(NextNonce());
                TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacIntentEndTurn, payload);
                Debug.Log("[Multipleer][tac] CLIENT sent tac.intent.endturn");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ClientRelayEndTurn failed: " + ex); }
            // Let native RequestEndTurn set the LOCAL flag so the client's PlayTurnCrt loop exits (input off).
            return true;
        }

        // ─── HOST: a client end-turn intent arrived → advance the real turn ───────────────────────
        /// <summary>HOST inbound: end the CURRENT faction's turn on the host sim (any client may end any
        /// turn for now — open permission). The host's <c>NextTurnCrt</c> then advances and the
        /// <c>TacMission.OnNewTurn</c> postfix broadcasts <c>tac.turn</c>.</summary>
        public static void HostOnEndTurnIntent(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeEndTurnIntent(payload, out uint nonce)) { Debug.LogError("[Multipleer][tac] endturn intent decode failed"); return; }
            if (!TacticalDeploySync.IntentDedup.IsNew(TacticalSurfaceIds.TacIntentEndTurn, nonce)) return;

            try
            {
                object tlc = TacticalDeploySync.LiveTlc ?? ResolveTlc();
                object current = tlc != null ? GetProp(tlc, "CurrentFaction") : null;
                if (current == null) { Debug.LogError("[Multipleer][tac] endturn intent: no CurrentFaction"); return; }
                // public void RequestEndTurn() — sets _endTurnRequested; host NextTurnCrt picks it up.
                var req = AccessTools.Method(current.GetType(), "RequestEndTurn");
                if (req == null) { Debug.LogError("[Multipleer][tac] endturn intent: RequestEndTurn not found"); return; }
                req.Invoke(current, null);
                Debug.Log("[Multipleer][tac] HOST applied client end-turn intent");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HostOnEndTurnIntent failed: " + ex); }
        }

        // ─── HOST: a faction turn started → broadcast tac.turn ────────────────────────────────────
        /// <summary>HOST postfix on <c>TacMission.OnNewTurn(prev, next)</c>: broadcast the new current
        /// faction index + turn number + faction-def guid. No-op off-host / off-session / when mirroring.</summary>
        public static void HostBroadcastTurn(object nextFaction)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (nextFaction == null) return;

            try
            {
                object tlc = GetProp(nextFaction, "TacticalLevel") ?? TacticalDeploySync.LiveTlc ?? ResolveTlc();
                int index = ResolveFactionIndex(tlc, nextFaction);
                if (index < 0) { Debug.LogError("[Multipleer][tac] tac.turn: faction not found in Factions"); return; }
                int turnNumber = ToInt(GetProp(nextFaction, "TurnNumber"));
                string guid = ResolveFactionDefGuid(nextFaction);

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacTurn);
                byte[] payload = TacticalLiveCodec.EncodeTurn(seq, index, turnNumber, guid);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacTurn, payload);
                Debug.Log("[Multipleer][tac] HOST broadcast tac.turn seq=" + seq + " idx=" + index +
                          " turn=" + turnNumber + " guid=" + guid);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HostBroadcastTurn failed: " + ex); }
        }

        // ─── CLIENT: apply the faction handoff ────────────────────────────────────────────────────
        /// <summary>CLIENT inbound: set the current faction index + turn number, then ENTER the player turn
        /// (start PlayTurnCrt) if the new faction is player-controlled; stay frozen for AI factions.</summary>
        public static void ClientOnTurn(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeTurn(payload, out var t)) { Debug.LogError("[Multipleer][tac] tac.turn decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacTurn, t.Seq)) return;

            // DIAG: incoming handoff envelope (before any state resolution). Catches faction-index desync (cause C).
            Debug.Log("[Multipleer][tac] ClientOnTurn ENTER seq=" + t.Seq + " incomingIdx=" + t.CurrentFactionIndex +
                      " turn=" + t.TurnNumber + " guid=" + t.FactionDefGuid);

            object tlc = TacticalDeploySync.LiveTlc ?? ResolveTlc();
            if (tlc == null)
            {
                // No live TLC yet (hydrate racing the host's tac.turn): STASH the decoded turn so the
                // hydrate-completion drain (ClientEnterInitialTurn) can re-apply it. ShouldApply + monotonic
                // seq make a stale stash self-ignore. Do NOT Mark here (apply not yet performed).
                _pendingTurn = t;
                Debug.Log("[Multipleer][tac] tac.turn: no live TacticalLevelController — buffered seq=" + t.Seq + " idx=" + t.CurrentFactionIndex);
                return;
            }

            try
            {
                // 1a) Exit the OUTGOING turn on every handoff.
                //   • PLAYER faction: set its _endTurnRequested=true so the native player PlayTurnCrt loop
                //     (TacticalFaction.cs:479) breaks, runs end-turn side-effects, and clears IsPlayingTurn.
                //     Without this the client's own PlayTurnCrt stays running → "already running" dead-end on
                //     return to the player turn.
                //   • NON-PLAYER faction (Feature A): we marked its IsPlayingTurn=true by hand for the
                //     enemy-turn presentation and never started PlayTurnCrt, so nothing native clears it.
                //     Clear it directly (mirror native PlayTurnCrt exit TacticalFaction.cs:486) so the next
                //     handoff starts clean and the view dispatcher can re-evaluate.
                bool prevEnded = false;
                try
                {
                    object outgoing = GetProp(tlc, "CurrentFaction");
                    if (outgoing != null && ToBool(GetProp(outgoing, "IsPlayingTurn")))
                    {
                        bool outgoingIsPlayer = ToBool(GetProp(outgoing, "IsControlledByPlayer"));
                        if (ClientEnemyTurnPresentationGate.ShouldClearOutgoingIsPlayingTurn(outgoingIsPlayer, outgoingIsPlayingTurn: true))
                        {
                            SetIsPlayingTurn(outgoing, false);   // outgoing enemy presentation → clear our manual flag
                            prevEnded = true;
                        }
                        else
                        {
                            Traverse.Create(outgoing).Field("_endTurnRequested").SetValue(true);   // outgoing player → native loop exit
                            prevEnded = true;
                        }
                    }
                }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] tac.turn: outgoing end-turn failed: " + ex); }

                // 1b) Point the TLC at the host's current faction (private _currentFactionIndex).
                var idxTrav = Traverse.Create(tlc).Field("_currentFactionIndex");
                idxTrav.SetValue(t.CurrentFactionIndex);

                object current = GetProp(tlc, "CurrentFaction");
                if (current == null) { Debug.LogError("[Multipleer][tac] tac.turn: CurrentFaction null after index set"); return; }

                // 2) Re-stamp the authoritative TurnNumber (public setter) — corrects any local drift and the
                //    +1 PlayTurnCrt will apply when we start it (we re-stamp AFTER the start below for player).
                bool isPlayer = ToBool(GetProp(current, "IsControlledByPlayer"));
                bool viewDown = false;
                string branch = isPlayer ? "player" : "enemy";

                if (isPlayer)
                {
                    // 3a) PLAYER-TURN CONTROL RESTORE (deterministic; runs EVERY player resume).
                    //
                    // CONTROL == the view in UIStateCharacterSelected (the only state with the action bar +
                    // soldier selection; there is no UIStatePlayerTurn). That state is reached ONLY by the native
                    // dispatcher UIStateInitial.InitialStateUpdateCrt, whose player branch (UIStateInitial.cs:66-87)
                    // SPINS `while (!CurrentFaction.IsPlayingTurn)` (line 68) before switching to
                    // UIStateCharacterSelected. So the load-bearing precondition for regaining control is
                    // IsPlayingTurn == true on THIS player faction PLUS the view being driven into
                    // UIStateInitial(initForNewTurn:true).
                    //
                    // The OLD "if IsPlayingTurn already true → just re-stamp, no re-start" early-out is REMOVED:
                    // it made control restoration depend on PlayTurnCrt having reached TacticalFaction.cs:442
                    // (where the native code sets IsPlayingTurn=true). On a frozen mirror PlayTurnCrt stalls
                    // BEFORE :442 on host-authoritative yields (Map.IsMapUpdateInProgress / EnsureNavObstacle /
                    // ExecuteQueuedAbilitiesSequence / SituationCache.WaitForAutomaticEvaluation, :432-441) that
                    // may never complete → IsPlayingTurn stays false → the dispatcher dead-spins → permanent loss
                    // of control after the first enemy turn (cause B). We therefore FORCE the precondition by hand
                    // (exactly as the enemy-presentation gate already forces IsPlayingTurn for UIStateOtherFactionTurn).
                    bool wasPlaying = ToBool(GetProp(current, "IsPlayingTurn"));

                    // (i) Start the native PlayTurnCrt ONLY if no player loop is currently live (wasPlaying==false).
                    //     This runs the real player-turn setup + input/end-turn loop while avoiding doubling the
                    //     coroutine when one is already running. PlayTurnCrt never advances the faction index, so a
                    //     standalone run can never auto-advance the client. (There is no stored coroutine handle to
                    //     tear down a stale loop; not re-starting when one is live is the safe minimal equivalent —
                    //     control is restored regardless by the forced IsPlayingTurn + HUD drive below.)
                    if (!wasPlaying) StartPlayTurn(current);

                    // (ii) FORCE IsPlayingTurn=true (private setter → Traverse), NOT relying on PlayTurnCrt reaching
                    //      :442. This is the single missing input the native player-branch dispatcher needs so its
                    //      `while (!IsPlayingTurn)` (UIStateInitial.cs:68) breaks and it enters UIStateCharacterSelected.
                    SetIsPlayingTurn(current, true);

                    // (iii) Re-stamp the host's authoritative TurnNumber (PlayTurnCrt's internal +1 is corrected here).
                    SetTurnNumber(current, t.TurnNumber);

                    // (iv) ALWAYS drive the view into UIStateInitial(initForNewTurn:true) + set ViewerFaction
                    //      (race-independent; the NewTurnEvent we raise can fire before TacticalView subscribes).
                    //      With IsPlayingTurn now true the dispatcher will reach UIStateCharacterSelected.
                    bool hudRan = EnsureClientTurnHud(current);

                    // DIAG: player-resume restore receipt — catches cause B (IsPlayingTurn stays false) and the
                    // resolved view state. isPlayingBefore is pre-restore; isPlayingAfter must be true.
                    Debug.Log("[Multipleer][tac] CLIENT PLAYER resume idx=" + t.CurrentFactionIndex +
                              " faction=" + ResolveFactionDefName(current) +
                              " isControlledByPlayer=" + isPlayer +
                              " isPlayingBefore=" + wasPlaying +
                              " isPlayingAfter=" + ToBool(GetProp(current, "IsPlayingTurn")) +
                              " startedPlayTurn=" + (!wasPlaying) +
                              " hudRan=" + hudRan +
                              " viewState=" + ResolveViewStateName(current) +
                              " turn=" + t.TurnNumber);
                }
                else if (ClientEnemyTurnPresentationGate.ShouldEnterEnemyPresentation(incomingIsControlledByPlayer: false))
                {
                    // 3b) AI/enemy faction (Feature A) → enter the NATIVE enemy-turn PRESENTATION instead of the
                    //     old frozen-spectator view-down. We still do NOT start PlayTurnCrt and do NOT run AI
                    //     (host is authoritative; NextTurnCrt + AIUpdateCrt stay suppressed) — enemy actions
                    //     stream in via tac.move / tac.intent.ability and the camera follows them for free
                    //     (TacticalAbility.Activate → CameraDirector.Hint(AbilityActivated)).
                    SetTurnNumber(current, t.TurnNumber);
                    // VIEW GATE: set the enemy faction's IsPlayingTurn=true (private setter → Traverse). The
                    // native UIStateInitial.InitialStateUpdateCrt (UIStateInitial.cs:58-65), once the view is in
                    // UIStateInitial with a non-player CurrentFaction, spins `while (!IsPlayingTurn)` and then
                    // SwitchToState(new UIStateOtherFactionTurn()) — which hides the player action bar, shows the
                    // "<faction> turn" banner, and sets overwatch visuals. Setting IsPlayingTurn is the one missing
                    // input on the client (PlayTurnCrt, which sets it natively at :442, never runs here).
                    SetIsPlayingTurn(current, true);
                    // Drive the view into UIStateInitial (mirror native OnViewerFactionEndedTurn,
                    // TacticalView.cs:1248-1252) so its dispatcher re-evaluates and, with IsPlayingTurn now true +
                    // a non-player CurrentFaction, transitions to UIStateOtherFactionTurn on its own. We KEEP the
                    // viewer faction = the client's own player faction (fog/vision correctness) — do NOT call
                    // SetViewerTacticalFaction. Reuses the readiness-guard + deferred retry (survives a not-yet-
                    // ready view).
                    viewDown = EnsureClientViewDown(current);
                    Debug.Log("[Multipleer][tac] CLIENT entered ENEMY-TURN presentation idx=" + t.CurrentFactionIndex + " turn=" + t.TurnNumber + " (UIStateOtherFactionTurn, host-authoritative)");
                }
                else
                {
                    // Defensive: non-player target that should NOT show enemy presentation (currently unreachable;
                    // ShouldEnterEnemyPresentation is true for every non-player). Stay a frozen spectator.
                    SetTurnNumber(current, t.TurnNumber);
                    viewDown = EnsureClientViewDown(current);
                    Debug.Log("[Multipleer][tac] CLIENT mirrored ENEMY/AI turn idx=" + t.CurrentFactionIndex + " turn=" + t.TurnNumber + " (frozen spectator)");
                }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacTurn, t.Seq);
                Debug.Log("[Multipleer][tac] CLIENT turn handoff: prevEnded=" + prevEnded + " idx=" + t.CurrentFactionIndex +
                          " branch=" + branch + " viewDown=" + viewDown);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ClientOnTurn failed: " + ex); }
        }

        // ─── CLIENT: enter the INITIAL turn right after deploy hydrate ────────────────────────────
        /// <summary>CLIENT: after the snapshot restore set <c>_currentFactionIndex</c> (turn 0's faction),
        /// enter that turn locally. If it's a player faction, start <c>PlayTurnCrt</c> so the client can act
        /// from the first turn (a host <c>tac.turn</c> for turn 0 may have raced ahead of this client's
        /// hydrate and been dropped). If it's an AI faction, stay a frozen spectator. Best-effort.</summary>
        public static void ClientEnterInitialTurn(object tlc)
        {
            if (tlc == null) return;
            object current = GetProp(tlc, "CurrentFaction");
            if (current == null) { Debug.LogError("[Multipleer][tac] ClientEnterInitialTurn: no CurrentFaction"); return; }
            bool isPlayer = ToBool(GetProp(current, "IsControlledByPlayer"));
            if (isPlayer)
            {
                int turnNumber = ToInt(GetProp(current, "TurnNumber"));
                StartPlayTurn(current);
                SetTurnNumber(current, turnNumber);   // undo PlayTurnCrt's internal +1
                // CONTROL-RESTORE PARITY with ClientOnTurn's player branch (:223): FORCE IsPlayingTurn=true by
                // hand BEFORE driving the HUD. StartPlayTurn only schedules the (mirror) PlayTurnCrt coroutine —
                // its IsPlayingTurn=true runs one Timing tick LATER, so without this synchronous force the native
                // dispatcher's `while (!IsPlayingTurn)` (UIStateInitial.cs:68) spins for a frame and the HUD drive
                // below races it. Setting it here closes that gap (idempotent with the coroutine's own set).
                SetIsPlayingTurn(current, true);
                Debug.Log("[Multipleer][tac] CLIENT entered INITIAL player turn (turn " + turnNumber + ")");
                // UI-LOCK FIX: the initial turn entry fires at the END of deploy-hydrate, typically BEFORE
                // TacticalView's UIStateInitView callback has subscribed OnNewTurn → the raised NewTurnEvent
                // is dropped and no action HUD appears. Drive the view into the new-turn state explicitly.
                EnsureClientTurnHud(current);
            }
            else Debug.Log("[Multipleer][tac] CLIENT initial turn is AI/enemy → frozen spectator");

            // DRAIN a tac.turn that raced ahead of this hydrate and was buffered. ShouldApply + monotonic seq
            // make a stale stash self-ignore (ClientOnTurn re-runs ShouldApply). Re-encode from the stash and
            // re-run the full ClientOnTurn body (now that LiveTlc is set).
            DrainPendingTurn();
        }

        /// <summary>CLIENT: re-apply a buffered tac.turn (stashed when no live TLC existed) once hydrate is
        /// done. Re-runs <see cref="ClientOnTurn"/> via a fresh encode of the stash; the seq guard inside
        /// drops it if a newer turn was already applied.</summary>
        private static void DrainPendingTurn()
        {
            if (_pendingTurn == null) return;
            var p = _pendingTurn.Value;
            _pendingTurn = null;
            try
            {
                byte[] payload = TacticalLiveCodec.EncodeTurn(p.Seq, p.CurrentFactionIndex, p.TurnNumber, p.FactionDefGuid);
                Debug.Log("[Multipleer][tac] CLIENT draining buffered tac.turn seq=" + p.Seq + " idx=" + p.CurrentFactionIndex);
                ClientOnTurn(payload);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] DrainPendingTurn failed: " + ex); }
        }

        // ─── CLIENT UI-LOCK FIX: force the action HUD up regardless of the raced NewTurnEvent ─────────

        // Fail-safe budget for the deferred view-readiness wait (~600 frames ≈ 10 s @ 60 fps): the
        // TacticalView's UIStateInitView callback (which builds _statesStack + subscribes OnNewTurn) normally
        // completes within a few frames of the scene going Playing.
        private const int ViewReadyMaxFrames = 600;

        /// <summary>
        /// CLIENT belt-and-suspenders: bring up the player action HUD for <paramref name="faction"/> WITHOUT
        /// relying on the NewTurnEvent we raise in <see cref="MakeTurnStartAction"/> (that event is dropped if
        /// it fires before <c>TacticalView</c> subscribes OnNewTurn inside its <c>UIStateInitView</c> completion
        /// callback — TacticalView.cs:1091-1100, the exact race that left the client with no action buttons).
        ///
        /// The HUD needs (a) the client faction = the view's ViewerFaction (TacticalFaction.IsViewerFaction,
        /// set via the PUBLIC <c>TacticalView.SetViewerTacticalFaction</c>, TacticalView.cs:457) AND (b) the
        /// view in <c>UIStateInitial(initForNewTurn:true)</c> (entered by the PRIVATE
        /// <c>TacticalView.OnNewTurn(prev,next)</c>, TacticalView.cs:1256-1260). We reach the live view via
        /// <c>TacticalLevelController.View</c> (TacticalView.cs:1296). OnNewTurn requires the view's private
        /// <c>_statesStack</c> to exist — it is created ONLY inside that UIStateInitView callback — so if the
        /// view/stack isn't ready yet we DEFER onto the level Timing and retry (same coroutine pattern as the
        /// rest of this file), exactly mirroring the engine's own <c>OnNewTurn(null, CurrentFaction)</c> call.
        /// </summary>
        /// <returns>true if the HUD drive was applied SYNCHRONOUSLY this call; false if it was deferred onto
        /// Timing (view not ready) or could not be driven. Returned for diagnostics in the player-resume log.</returns>
        private static bool EnsureClientTurnHud(object faction)
        {
            try
            {
                if (TryDriveClientTurnHud(faction)) return true;
                // View / _statesStack not ready → defer onto Timing and poll until it is (bounded).
                object tlc = GetProp(faction, "TacticalLevel");
                if (tlc == null) { Debug.LogError("[Multipleer][tac] EnsureClientTurnHud: no TacticalLevel on faction"); return false; }
                object timing = ResolveTiming(faction, tlc);
                if (timing == null || !InvokeStart(timing, timing.GetType(), DriveTurnHudCrt(faction)))
                    Debug.LogError("[Multipleer][tac] EnsureClientTurnHud: could not defer HUD drive onto Timing (view may stay locked)");
                else
                    Debug.Log("[Multipleer][tac] EnsureClientTurnHud: view not ready → deferred HUD drive onto Timing");
                return false;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] EnsureClientTurnHud failed: " + ex); return false; }
        }

        /// <summary>Deferred poll: each frame, retry the HUD drive until it succeeds (view + _statesStack ready)
        /// or the fail-safe budget elapses. <c>IEnumerator&lt;NextUpdate&gt;</c> so it binds the native
        /// <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …)</c> overload.</summary>
        private static IEnumerator<NextUpdate> DriveTurnHudCrt(object faction)
        {
            int frames = 0;
            while (frames < ViewReadyMaxFrames)
            {
                bool done = false;
                try { done = TryDriveClientTurnHud(faction); }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] DriveTurnHudCrt: drive failed: " + ex); }
                if (done) yield break;
                frames++;
                yield return NextUpdate.NextFrame;
            }
            Debug.LogError("[Multipleer][tac] DriveTurnHudCrt: gave up after " + frames + " frames — view never became ready");
        }

        /// <summary>Single attempt to drive the view: returns false (caller should defer/retry) if the live
        /// <c>TacticalView</c> or its <c>_statesStack</c> isn't constructed yet. On success: set the viewer
        /// faction (public) + switch to <c>UIStateInitial(initForNewTurn:true)</c> by invoking the private
        /// <c>OnNewTurn(null, faction)</c> — the same call the engine makes during view init.</summary>
        private static bool TryDriveClientTurnHud(object faction)
        {
            object tlc = GetProp(faction, "TacticalLevel");
            object view = tlc != null ? GetProp(tlc, "View") : null;
            if (view == null) return false;   // view not constructed yet → defer
            // _statesStack (private) is created only inside UIStateInitView's completion callback; until then
            // OnNewTurn would NRE. Treat a null stack as "view not ready".
            object statesStack = Traverse.Create(view).Field("_statesStack").GetValue();
            if (statesStack == null) return false;

            // (a) Make the client faction the viewer faction (PUBLIC setter).
            try
            {
                var setViewer = AccessTools.Method(view.GetType(), "SetViewerTacticalFaction");
                if (setViewer != null) setViewer.Invoke(view, new[] { faction });
                else Debug.LogError("[Multipleer][tac] TryDriveClientTurnHud: SetViewerTacticalFaction not found");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TryDriveClientTurnHud: SetViewerTacticalFaction failed: " + ex); }

            // (b) Enter UIStateInitial(initForNewTurn:true) via the PRIVATE OnNewTurn(prev=null, next=faction) —
            //     the exact path the engine uses (TacticalView.cs:1100). Reflection (private), 2 args.
            bool hudEntered = false;
            try
            {
                var onNewTurn = AccessTools.Method(view.GetType(), "OnNewTurn");
                if (onNewTurn != null) { onNewTurn.Invoke(view, new[] { null, faction }); hudEntered = true; }
                else Debug.LogError("[Multipleer][tac] TryDriveClientTurnHud: OnNewTurn not found");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TryDriveClientTurnHud: OnNewTurn failed: " + ex); }

            object viewerNow = GetProp(view, "ViewerFaction");
            Debug.Log("[Multipleer][tac] CLIENT drove turn HUD: viewerFaction set=" + ReferenceEquals(viewerNow, faction) +
                      " UIStateInitial(initForNewTurn) entered=" + hudEntered);
            return true;
        }

        // ─── CLIENT VIEW-DOWN (enemy/non-player handoff): mirror OnViewerFactionEndedTurn ───────────

        /// <summary>CLIENT belt-and-suspenders for an enemy/non-player faction handoff: dismiss the player
        /// action HUD by switching the view's state stack to a fresh <c>UIStateInitial()</c>, exactly mirroring
        /// native <c>TacticalView.OnViewerFactionEndedTurn</c> (TacticalView.cs:1248-1252). The client's
        /// NextTurnCrt is suppressed so the engine never raises FactionEndedTurnEvent → this mirror is the only
        /// thing that can drive the view down. Reuses the same view-readiness guard + Timing-deferred retry as
        /// <see cref="EnsureClientTurnHud"/> so it survives a not-yet-ready view. Returns true if the switch was
        /// applied synchronously; false → it was deferred onto Timing (or could not be deferred).</summary>
        private static bool EnsureClientViewDown(object faction)
        {
            try
            {
                if (TryDriveClientViewDown(faction)) return true;
                object tlc = GetProp(faction, "TacticalLevel");
                if (tlc == null) { Debug.LogError("[Multipleer][tac] EnsureClientViewDown: no TacticalLevel on faction"); return false; }
                object timing = ResolveTiming(faction, tlc);
                if (timing == null || !InvokeStart(timing, timing.GetType(), DriveViewDownCrt(faction)))
                    Debug.LogError("[Multipleer][tac] EnsureClientViewDown: could not defer view-down onto Timing (HUD may stay up)");
                else
                    Debug.Log("[Multipleer][tac] EnsureClientViewDown: view not ready → deferred view-down onto Timing");
                return false;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] EnsureClientViewDown failed: " + ex); return false; }
        }

        /// <summary>Deferred poll for the enemy-turn view-down (same pattern as <see cref="DriveTurnHudCrt"/>).</summary>
        private static IEnumerator<NextUpdate> DriveViewDownCrt(object faction)
        {
            int frames = 0;
            while (frames < ViewReadyMaxFrames)
            {
                bool done = false;
                try { done = TryDriveClientViewDown(faction); }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] DriveViewDownCrt: drive failed: " + ex); }
                if (done) yield break;
                frames++;
                yield return NextUpdate.NextFrame;
            }
            Debug.LogError("[Multipleer][tac] DriveViewDownCrt: gave up after " + frames + " frames — view never became ready");
        }

        /// <summary>Single attempt to drive the view DOWN: <c>_statesStack.SwitchToState(new UIStateInitial(),
        /// StateStackAction.ClearStackAndPush)</c> on the live <c>TacticalLevelController.View</c>. Returns false
        /// (caller should defer/retry) if the view or its <c>_statesStack</c> isn't constructed yet.</summary>
        private static bool TryDriveClientViewDown(object faction)
        {
            object tlc = GetProp(faction, "TacticalLevel");
            object view = tlc != null ? GetProp(tlc, "View") : null;
            if (view == null) return false;   // view not constructed yet → defer
            object statesStack = Traverse.Create(view).Field("_statesStack").GetValue();
            if (statesStack == null) return false;

            bool switched = false;
            try
            {
                // new UIStateInitial(initForNewTurn:false) — internal tactical view-state, default ctor.
                var initialType = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateInitial");
                var actionType = AccessTools.TypeByName("Base.UI.StateStackAction");
                if (initialType == null || actionType == null)
                { Debug.LogError("[Multipleer][tac] TryDriveClientViewDown: UIStateInitial/StateStackAction type not found"); return true; }
                // UIStateInitial is INTERNAL (class + ctor) → bind non-public. ctor(bool initForNewTurn=false).
                object initialState = Activator.CreateInstance(
                    initialType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { false }, null);
                object clearAndPush = Enum.Parse(actionType, "ClearStackAndPush");
                // StateStack<TContext>.SwitchToState(IState<TContext> state, StateStackAction stackAction).
                var switchTo = AccessTools.Method(statesStack.GetType(), "SwitchToState");
                if (switchTo != null) { switchTo.Invoke(statesStack, new[] { initialState, clearAndPush }); switched = true; }
                else Debug.LogError("[Multipleer][tac] TryDriveClientViewDown: SwitchToState not found");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TryDriveClientViewDown: SwitchToState failed: " + ex); }

            Debug.Log("[Multipleer][tac] CLIENT drove view DOWN: UIStateInitial entered=" + switched);
            return true;
        }

        /// <summary>Resolve a <c>Base.Core.Timing</c> for the deferred HUD drive: the faction's own Timing, else
        /// the level's Timing, else the ambient <c>Timing.Current</c>.</summary>
        private static object ResolveTiming(object faction, object tlc)
        {
            object timing = GetProp(faction, "Timing") ?? (tlc != null ? GetProp(tlc, "Timing") : null);
            if (timing == null)
            {
                var timingType = AccessTools.TypeByName("Base.Core.Timing");
                var currentProp = timingType != null ? AccessTools.Property(timingType, "Current") : null;
                try { timing = currentProp?.GetValue(null, null); } catch { timing = null; }
            }
            return timing;
        }

        // ─── Engine helpers ────────────────────────────────────────────────────────────────────────

        private static void StartPlayTurn(object faction)
        {
            // IEnumerator<NextUpdate> PlayTurnCrt(Action turnStartAction). Drive it on the game's Timing so it
            // runs as a real coroutine (the player branch loops until _endTurnRequested). Use the faction's
            // own Timing (Actor.Timing) via Timing.Start.
            var playTurn = AccessTools.Method(faction.GetType(), "PlayTurnCrt");
            if (playTurn == null) { Debug.LogError("[Multipleer][tac] PlayTurnCrt not found"); return; }
            // turnStartAction MUST replicate native NextTurnCrt's closure (TLC.cs:717-721) so the client's
            // turn-start side-effects fire: set TacticalLevelController.HasAnyTurnStarted=true and raise its
            // NewTurnEvent(prev, next). Without this the client's objective/UI hooks (GameOverCondition,
            // TacticalView, ContextHelp, …) never refresh on turn start. prev is unknown here, but the engine
            // itself calls OnNewTurn(null, CurrentFaction) during view init (TacticalView.cs:1100), so a null
            // prev is a sanctioned pattern its subscribers tolerate.
            Action turnStartAction = MakeTurnStartAction(faction);
            object crt = playTurn.Invoke(faction, new object[] { turnStartAction });
            if (crt == null) { Debug.LogError("[Multipleer][tac] PlayTurnCrt returned null"); return; }
            if (!StartCoroutineOnTiming(faction, crt))
                Debug.LogError("[Multipleer][tac] could not start PlayTurnCrt on Timing");
        }

        /// <summary>Build the per-turn-start action native <c>NextTurnCrt</c> passes into <c>PlayTurnCrt</c>
        /// (TLC.cs:717-721): set <c>TacticalLevelController.HasAnyTurnStarted = true</c> (private setter →
        /// Traverse) and raise <c>NewTurnEvent(prevFaction, CurrentFaction)</c>. The event's backing delegate
        /// field shares the event's name; we read it via Traverse and DynamicInvoke with <c>(null, faction)</c>.
        /// Fully replicated — NOT partial. Best-effort: each step is independently guarded so a missing member
        /// degrades gracefully without aborting the turn entry.</summary>
        private static Action MakeTurnStartAction(object faction)
        {
            return delegate
            {
                try
                {
                    object tlc = GetProp(faction, "TacticalLevel");
                    if (tlc == null) { Debug.LogError("[Multipleer][tac] turnStartAction: no TacticalLevel on faction"); return; }

                    // 1) HasAnyTurnStarted = true (public getter / private setter).
                    try { Traverse.Create(tlc).Property("HasAnyTurnStarted").SetValue(true); }
                    catch (Exception ex) { Debug.LogError("[Multipleer][tac] turnStartAction: set HasAnyTurnStarted failed: " + ex); }

                    // 2) Raise NewTurnEvent(prev=null, next=faction) via the backing delegate field.
                    try
                    {
                        object del = Traverse.Create(tlc).Field("NewTurnEvent").GetValue();
                        if (del is Delegate handler) handler.DynamicInvoke(null, faction);
                    }
                    catch (Exception ex) { Debug.LogError("[Multipleer][tac] turnStartAction: raise NewTurnEvent failed: " + ex); }
                }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] turnStartAction failed: " + ex); }
            };
        }

        /// <summary>Start a game coroutine (IEnumerator&lt;NextUpdate&gt;) on the faction's Timing, mirroring
        /// the native <c>Timing.Start(NextTurnCrt())</c> pattern (TLC.cs:680). Resolves Timing via the
        /// faction's <c>TacticalLevel.Timing</c> or a static <c>Timing.Current</c>.</summary>
        private static bool StartCoroutineOnTiming(object faction, object crt)
        {
            try
            {
                object timing = GetProp(faction, "Timing");
                if (timing == null)
                {
                    object tlc = GetProp(faction, "TacticalLevel");
                    timing = tlc != null ? GetProp(tlc, "Timing") : null;
                }
                if (timing != null && InvokeStart(timing, timing.GetType(), crt)) return true;

                // Fallback: static Timing.Current.Start(...).
                var timingType = AccessTools.TypeByName("Base.Core.Timing");
                if (timingType != null)
                {
                    var currentProp = AccessTools.Property(timingType, "Current");
                    object cur = currentProp?.GetValue(null, null);
                    if (cur != null && InvokeStart(cur, timingType, crt)) return true;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] StartCoroutineOnTiming failed: " + ex); }
            return false;
        }

        /// <summary>Find and invoke the <c>Timing.Start(IEnumerator&lt;NextUpdate&gt; coroutine, …)</c> overload
        /// whose FIRST param is the coroutine and whose remaining params are all OPTIONAL (the native
        /// signature is <c>Start(IEnumerator&lt;NextUpdate&gt;, Func&lt;…&gt; catchException = null)</c>,
        /// Timing.cs:246). Optional trailing params are filled with <see cref="Type.Missing"/> so the invoke
        /// matches. Returns true on a successful start.</summary>
        private static bool InvokeStart(object timingInstance, Type timingType, object crt)
        {
            MethodInfo best = null;
            foreach (var m in timingType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Start") continue;
                var pars = m.GetParameters();
                if (pars.Length < 1) continue;
                if (!typeof(IEnumerator).IsAssignableFrom(pars[0].ParameterType)) continue;
                // First param must accept the coroutine; all the rest must be optional.
                bool restOptional = true;
                for (int i = 1; i < pars.Length; i++) if (!pars[i].IsOptional) { restOptional = false; break; }
                if (!restOptional) continue;
                // Prefer the fewest-param overload (the simplest Start).
                if (best == null || pars.Length < best.GetParameters().Length) best = m;
            }
            if (best == null) return false;
            var bp = best.GetParameters();
            var args = new object[bp.Length];
            args[0] = crt;
            for (int i = 1; i < bp.Length; i++) args[i] = Type.Missing;   // use the optional default
            best.Invoke(timingInstance, args);
            return true;
        }

        private static void SetTurnNumber(object faction, int turnNumber)
        {
            try
            {
                var p = AccessTools.Property(faction.GetType(), "TurnNumber");
                if (p != null && p.CanWrite) { p.SetValue(faction, turnNumber, null); return; }
                Traverse.Create(faction).Property("TurnNumber").SetValue(turnNumber);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] SetTurnNumber failed: " + ex); }
        }

        /// <summary>Set <c>TacticalFaction.IsPlayingTurn</c> (TacticalFaction.cs:79, public getter / PRIVATE
        /// setter). Used by the Feature-A enemy-turn presentation: setting it true is the one input the native
        /// view dispatcher (UIStateInitial.InitialStateUpdateCrt:58-65) is missing on the client so it can enter
        /// UIStateOtherFactionTurn; clearing it false on handoff mirrors PlayTurnCrt's exit (TacticalFaction.cs:486)
        /// since we never run PlayTurnCrt for the enemy. Private setter → Traverse, with AccessTools fallback.</summary>
        private static void SetIsPlayingTurn(object faction, bool value)
        {
            try
            {
                var p = AccessTools.Property(faction.GetType(), "IsPlayingTurn");
                if (p != null && p.CanWrite) { p.SetValue(faction, value, null); return; }
                Traverse.Create(faction).Property("IsPlayingTurn").SetValue(value);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] SetIsPlayingTurn failed: " + ex); }
        }

        private static int ResolveFactionIndex(object tlc, object faction)
        {
            if (tlc == null) return -1;
            var factions = GetProp(tlc, "Factions") as IEnumerable;
            if (factions == null) return -1;
            int i = 0;
            foreach (var f in factions)
            {
                if (ReferenceEquals(f, faction)) return i;
                i++;
            }
            return -1;
        }

        private static string ResolveFactionDefGuid(object faction)
        {
            try
            {
                // faction.TacticalFactionDef.FactionDef.Guid (PPFactionDef → BaseDef.Guid).
                object tfd = GetProp(faction, "TacticalFactionDef");
                object fd = tfd != null ? GetProp(tfd, "FactionDef") : null;
                object guid = fd != null ? GetProp(fd, "Guid") : null;
                return guid?.ToString() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>DIAG ONLY: readable faction name for logs — <c>TacticalFactionDef.GetName()</c>
        /// (native, used at TacticalFaction.cs:386), falling back to the def guid then the runtime type.</summary>
        private static string ResolveFactionDefName(object faction)
        {
            try
            {
                object tfd = GetProp(faction, "TacticalFactionDef");
                if (tfd != null)
                {
                    var getName = AccessTools.Method(tfd.GetType(), "GetName");
                    object name = getName?.Invoke(tfd, null);
                    if (name != null && !string.IsNullOrEmpty(name.ToString())) return name.ToString();
                }
                string guid = ResolveFactionDefGuid(faction);
                return !string.IsNullOrEmpty(guid) ? guid : (faction?.GetType().Name ?? "null");
            }
            catch { return faction?.GetType().Name ?? "null"; }
        }

        /// <summary>DIAG ONLY: name of the live view's current state for logs, read from
        /// <c>TacticalLevelController.View._statesStack.CurrentState</c> (Base.UI.StateStack.CurrentState,
        /// StateStack.cs:23). Returns a marker when the view/stack isn't ready. NOTE: the
        /// UIStateInitial → UIStateCharacterSelected transition runs async inside the dispatcher coroutine, so
        /// immediately after a synchronous HUD drive this typically reads "UIStateInitial" (the gateway state),
        /// not yet "UIStateCharacterSelected" — that is expected and still confirms control restore reached the
        /// dispatcher.</summary>
        private static string ResolveViewStateName(object faction)
        {
            try
            {
                object tlc = GetProp(faction, "TacticalLevel");
                object view = tlc != null ? GetProp(tlc, "View") : null;
                if (view == null) return "<no-view>";
                object statesStack = Traverse.Create(view).Field("_statesStack").GetValue();
                if (statesStack == null) return "<no-statesStack>";
                object cur = GetProp(statesStack, "CurrentState");
                return cur != null ? cur.GetType().Name : "<no-currentState>";
            }
            catch (Exception ex) { return "<err:" + ex.GetType().Name + ">"; }
        }

        private static object ResolveTlc()
        {
            // Resolve the live TacticalLevelController from the current level (GameUtl.CurrentLevel → component).
            try
            {
                var tlcType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
                if (tlcType == null) return null;
                var gameUtl = AccessTools.TypeByName("PhoenixPoint.Common.Core.GameUtl");
                var currentLevel = gameUtl != null ? AccessTools.Method(gameUtl, "CurrentLevel") : null;
                object level = currentLevel?.Invoke(null, null);
                if (level == null) return null;
                var getComp = AccessTools.Method(level.GetType(), "GetComponent", new Type[0], new[] { tlcType });
                return getComp?.Invoke(level, null);
            }
            catch { return null; }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static int ToInt(object o) { try { return o != null ? Convert.ToInt32(o) : 0; } catch { return 0; } }
        private static bool ToBool(object o) => o is bool b && b;
    }
}
