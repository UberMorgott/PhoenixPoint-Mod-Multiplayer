using System;
using System.Collections;
using System.Reflection;
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

            object tlc = TacticalDeploySync.LiveTlc ?? ResolveTlc();
            if (tlc == null) { Debug.LogError("[Multipleer][tac] tac.turn: no live TacticalLevelController"); return; }

            try
            {
                // 1) Point the TLC at the host's current faction (private _currentFactionIndex).
                var idxTrav = Traverse.Create(tlc).Field("_currentFactionIndex");
                idxTrav.SetValue(t.CurrentFactionIndex);

                object current = GetProp(tlc, "CurrentFaction");
                if (current == null) { Debug.LogError("[Multipleer][tac] tac.turn: CurrentFaction null after index set"); return; }

                // 2) Re-stamp the authoritative TurnNumber (public setter) — corrects any local drift and the
                //    +1 PlayTurnCrt will apply when we start it (we re-stamp AFTER the start below for player).
                bool isPlayer = ToBool(GetProp(current, "IsControlledByPlayer"));

                if (isPlayer)
                {
                    // 3a) ENTER the player turn natively so the client view/input come alive. PlayTurnCrt is
                    //     NOT suppressed on the client (only NextTurnCrt + AIUpdateCrt are), so this runs the
                    //     real player-turn setup + input loop. It exits when the LOCAL user ends the turn
                    //     (RequestEndTurn → _endTurnRequested) — at which point the client waits for the next
                    //     host tac.turn. It never advances the faction index itself.
                    // Guard re-entry: if this faction's PlayTurnCrt is already running (IsPlayingTurn true),
                    // do NOT launch a 2nd coroutine (would double the player turn loop / input). Just re-stamp.
                    if (ToBool(GetProp(current, "IsPlayingTurn")))
                    {
                        SetTurnNumber(current, t.TurnNumber);
                        Debug.Log("[Multipleer][tac] CLIENT player turn already running idx=" + t.CurrentFactionIndex + " — re-stamped turn=" + t.TurnNumber + " (no re-start)");
                    }
                    else
                    {
                        StartPlayTurn(current);
                        // PlayTurnCrt increments TurnNumber by 1 internally; re-stamp the host's authoritative value.
                        SetTurnNumber(current, t.TurnNumber);
                        Debug.Log("[Multipleer][tac] CLIENT entered PLAYER turn idx=" + t.CurrentFactionIndex + " turn=" + t.TurnNumber);
                    }
                }
                else
                {
                    // 3b) AI/enemy faction → stay frozen spectator. Just stamp the turn number; enemy actions
                    //     arrive via tac.move / tac.damage. Do NOT start PlayTurnCrt (would touch view/AI state).
                    SetTurnNumber(current, t.TurnNumber);
                    Debug.Log("[Multipleer][tac] CLIENT mirrored ENEMY/AI turn idx=" + t.CurrentFactionIndex + " turn=" + t.TurnNumber + " (frozen spectator)");
                }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacTurn, t.Seq);
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
                Debug.Log("[Multipleer][tac] CLIENT entered INITIAL player turn (turn " + turnNumber + ")");
            }
            else Debug.Log("[Multipleer][tac] CLIENT initial turn is AI/enemy → frozen spectator");
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
