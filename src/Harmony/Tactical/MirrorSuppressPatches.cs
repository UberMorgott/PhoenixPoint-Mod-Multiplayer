using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// Increment-1 client mirror-mode suppression (spec §6, plan T6). When this instance is a synced-session
    /// CLIENT inside a hydrated tactical mission (<see cref="TacticalDeploySync.IsClientMirroring"/>), it is
    /// a PURE MIRROR: it must not run the enemy AI loop and must not auto-advance the turn (both are driven
    /// by the host and arrive over the live outcome rails in later increments). These prefixes skip those
    /// two coroutines on the client only; the host is never suppressed.
    ///
    /// Both targets return <c>IEnumerator&lt;NextUpdate&gt;</c>. A prefix that returns false MUST set
    /// <c>__result</c> to a non-null EMPTY enumerator of the right element type, or the caller (which does
    /// <c>yield return Timing.Current.Call(theCrt())</c>) would dereference null. We build an empty
    /// <c>IEnumerator&lt;NextUpdate&gt;</c> via reflection on the game's NextUpdate type.
    ///
    /// Auto-registers via PatchAll — no bootstrap edit.
    /// </summary>
    internal static class EmptyCrt
    {
        private static System.Type _nextUpdateType;
        private static object _cachedEmpty;

        /// <summary>An empty <c>IEnumerator&lt;NextUpdate&gt;</c> (cached). Returns null if NextUpdate can't
        /// be resolved (then the prefix falls back to letting the original run — safest).</summary>
        public static object Empty()
        {
            if (_cachedEmpty != null) return _cachedEmpty;
            // NextUpdate lives in Base.Core (grounded: Base.Core/NextUpdate.cs). The crts return
            // IEnumerator<NextUpdate>, so an empty enumerator of that element type is the safe skip result.
            _nextUpdateType = _nextUpdateType ?? AccessTools.TypeByName("Base.Core.NextUpdate");
            if (_nextUpdateType == null) return null;
            // Build List<NextUpdate>().GetEnumerator() → a valid empty IEnumerator<NextUpdate>.
            var listType = typeof(List<>).MakeGenericType(_nextUpdateType);
            var list = System.Activator.CreateInstance(listType);
            var getEnum = listType.GetMethod("GetEnumerator");
            _cachedEmpty = getEnum.Invoke(list, null);
            return _cachedEmpty;
        }
    }

    /// <summary>Client: skip the enemy-AI loop (<c>TacticalFaction.AIUpdateCrt</c>). The host runs enemy AI
    /// and streams outcomes; the client never rolls.</summary>
    [HarmonyPatch]
    public static class AIUpdateCrtSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFaction");
            if (t == null) return false;
            _target = AccessTools.Method(t, "AIUpdateCrt");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / non-mirror → run normally
            var empty = EmptyCrt.Empty();
            if (empty == null) return true;   // couldn't build empty crt → safest to let it run
            __result = empty;
            return false;   // skip original
        }
    }

    /// <summary>Client: skip native faction auto-advance (<c>TacticalLevelController.NextTurnCrt</c>). Turn
    /// progression is driven by the host's <c>tac.turn</c> (Increment 4); the client never self-advances.</summary>
    [HarmonyPatch]
    public static class NextTurnCrtSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (t == null) return false;
            _target = AccessTools.Method(t, "NextTurnCrt");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;
            var empty = EmptyCrt.Empty();
            if (empty == null) return true;
            __result = empty;
            return false;
        }
    }

    /// <summary>
    /// Inc1 full-state — FREEZE <c>TacticalFaction.PlayTurnCrt</c>'s SIM BODY on the client mirror.
    ///
    /// The native <c>PlayTurnCrt</c> (TacticalFaction.cs:389) does three things: (1) turn-START sim setup
    /// (StartTurn events + VoxelMatrix.StartTurn + Vision recompute + every actor/effect StartTurn, lines
    /// 391-431), (2) host-authoritative STALL yields (Map.IsMapUpdateInProgress wait, per-actor
    /// EnsureNavObstacleInProperState, ExecuteQueuedAbilitiesSequence, SituationCache.WaitForAutomaticEvaluation,
    /// lines 432-441) which on a frozen mirror can NEVER complete → IsPlayingTurn (set at :442) is never
    /// reached → the UI dispatcher dead-spins → permanent loss of control after the first enemy turn
    /// (cause-B), and (3) the player INPUT / end-turn wait loop (lines 470-484) + the turn-END teardown
    /// (486-509). The host is authoritative for (1) and (3-teardown) — running them on the mirror would
    /// double-tick actor StartTurn/EndTurn side effects.
    ///
    /// So on the mirror we REPLACE PlayTurnCrt with a MINIMAL coroutine that runs ONLY the player input/
    /// end-turn wait loop, skipping the sim setup, the STALL yields, and the teardown. This:
    ///   • KILLS cause-B (the stall yields never run → no dead-spin / no host-hang on handoff), and
    ///   • KEEPS the client able to take its own player turn: it sets <c>IsPlayingTurn=true</c> (the exact
    ///     precondition the native dispatcher's `while(!IsPlayingTurn)` needs — UIStateInitial.cs:68), invokes
    ///     the turn-start action, loops until <c>_endTurnRequested</c> (set by the client's own RequestEndTurn,
    ///     or by the handoff in <c>TacticalTurnSync.ClientOnTurn</c>), then clears IsPlayingTurn and exits.
    ///
    /// COORDINATION with <c>TacticalTurnSync.ClientOnTurn</c>: that path ALREADY force-sets IsPlayingTurn +
    /// drives the HUD by hand (independent of PlayTurnCrt) and only calls StartPlayTurn when not already
    /// playing — so this mirror loop is fully compatible (the force-set is idempotent with our set; turn
    /// ownership still comes from the host's <c>tac.turn</c> handoff). For an AI/enemy faction this loop
    /// simply returns immediately (no input loop, no AI — enemy AI stays suppressed by
    /// <see cref="AIUpdateCrtSuppressPatch"/> and the enemy-turn presentation is driven by ClientOnTurn).
    /// Host is NEVER patched.
    /// </summary>
    [HarmonyPatch]
    public static class PlayTurnCrtMirrorFreezePatch
    {
        private static MethodBase _target;
        private static System.Type _factionType;

        public static bool Prepare()
        {
            _factionType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFaction");
            if (_factionType == null) return false;
            // IEnumerator<NextUpdate> PlayTurnCrt(Action turnStartAction)
            _target = AccessTools.Method(_factionType, "PlayTurnCrt");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance, object[] __args, ref object __result)
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / non-mirror → native sim runs
            // The single param is the per-turn-start Action (may be null). Pass it through so the player branch
            // still fires whatever the caller wired (HUD/new-turn hooks) right after IsPlayingTurn=true.
            Action turnStartAction = (__args != null && __args.Length > 0) ? __args[0] as Action : null;
            __result = MirrorPlayTurnCrt(__instance, turnStartAction);
            return false;   // skip the native sim body (setup + stall yields + teardown)
        }

        /// <summary>The frozen-mirror replacement for PlayTurnCrt: REPLAYS the client-SAFE turn-start SETUP
        /// (presentation / vision / per-actor selection) for the client's OWN player faction, then runs the
        /// player input/end-turn wait loop — with NO host-authoritative STALL yields (native :432-441) and NO
        /// turn-END teardown (native :486-509). For a non-player faction it returns immediately (pure spectator;
        /// the enemy-turn presentation is driven by TacticalTurnSync.ClientOnTurn). Reads turn-state via
        /// reflection/Traverse so it stays bound to the real engine fields without a hard type reference.
        ///
        /// WHY the setup replay (the Inc1 over-suppression fix): the OLD mirror loop set IsPlayingTurn and ran
        /// the input loop but SKIPPED native :394-431, so the client's own soldiers were never StartTurn'd
        /// (RestartAbilities / RemoveUnusableAbilities / AP reset — TacticalActor.cs:1188), vision was never
        /// recomputed, and the view's viewer faction + camera were never bound. With no selectable actor,
        /// UIStateInitial.InitialStateUpdateCrt (UIStateInitial.cs:66-87) never advances to
        /// UIStateCharacterSelected (:80) → no action HUD and the camera stays mid-map (no :81 SnapWorldCursor).
        /// We replay ONLY the load-bearing, client-safe subset (no AI, no stall, no double-tick teardown).</summary>
        private static IEnumerator<NextUpdate> MirrorPlayTurnCrt(object faction, Action turnStartAction)
        {
            // Reset the end-turn latch for this turn (native PlayTurnCrt:391 does the same first thing).
            TrySetEndTurnRequested(faction, false);

            // Only player-controlled factions get a live input turn on the client; enemy/AI factions are pure
            // spectators here (their presentation is driven by TacticalTurnSync.ClientOnTurn).
            bool isPlayer = ReadBool(faction, "IsControlledByPlayer");
            if (!ClientTurnStartSetupGate.ShouldReplayTurnStartSetup(isControlledByPlayer: isPlayer))
                yield break;

            // ── Client-SAFE turn-start SETUP replay (native PlayTurnCrt :392-427, presentation/vision/selection
            //    subset only; skips the host-authoritative stall yields :432-441 and the teardown :486-509). Each
            //    step is independently guarded so a missing engine member degrades gracefully (file style). ──

            // (1) TurnNumber += 1 (native :392-393). The callers (ClientOnTurn / ClientEnterInitialTurn) re-stamp
            //     the host-authoritative TurnNumber right after starting us, so this +1 is corrected there.
            try
            {
                int turnNumber = ToInt(GetMember(faction, "TurnNumber")) + 1;
                Traverse.Create(faction).Property("TurnNumber").SetValue(turnNumber);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror setup TurnNumber++ failed: " + ex); }

            // (2) Faction-level start-turn event (native :394) — presentation hook.
            try { InvokeEvent(faction, "StartTurnEvent"); }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror setup StartTurnEvent failed: " + ex); }

            // (3) Recompute this faction's vision/fog (native :396). Presentation-correct on a mirror: it renders
            //     the client's own fog from the host-mirrored actor positions. NOT host-authoritative.
            try
            {
                object vision = GetMember(faction, "Vision");
                var m = vision != null ? AccessTools.Method(vision.GetType(), "OnFactionStartTurn") : null;
                m?.Invoke(vision, null);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror setup Vision.OnFactionStartTurn failed: " + ex); }

            // (4) Bind the view's VIEWER faction + camera to the client's own faction (native :397-400, only when
            //     player & State==Playing). This is what re-anchors the camera onto the squad. Presentation only.
            try
            {
                if (IsFactionStatePlaying(faction))
                {
                    object tlc = GetMember(faction, "TacticalLevel");
                    object view = tlc != null ? GetMember(tlc, "View") : null;
                    var setViewer = view != null ? AccessTools.Method(view.GetType(), "SetViewerTacticalFaction") : null;
                    setViewer?.Invoke(view, new[] { faction });
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror setup SetViewerTacticalFaction failed: " + ex); }

            // (5) Per-actor StartTurn for the client's OWN actors (native :424-427). This is the load-bearing one:
            //     it RemoveUnusableAbilities / SetAbilityTraits("start") / standby so each soldier becomes SELECTABLE,
            //     the precondition UIStateInitial needs (:66 HasAliveActors, :77 GetSelectableActors) to reach
            //     UIStateCharacterSelected (action HUD + camera snap). FIX E: it would ALSO SetToMax AP/WP
            //     (RestartAbilities → TacticalActor.cs:1243) which a PURE MIRROR must NOT do — AP is host-streamed —
            //     so StartTurnForOwnActors CAPTURES each own-actor's host AP/WP and RESTORES it across the replay
            //     (ClientApPreserveGate). Net: selectability/HUD intact, but the client never re-maxes spent AP.
            StartTurnForOwnActors(faction);

            // Provide the dispatcher precondition the native code sets at :442 (UIStateInitial spins on this),
            // then fire the per-turn-start action (mirror native :443). Belt to ClientOnTurn's hand-force.
            SetIsPlayingTurn(faction, true);
            try { turnStartAction?.Invoke(); }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror PlayTurnCrt turnStartAction failed: " + ex); }

            // Player input / end-turn wait loop (mirror native :472-484, minus the host-sim View.IsWaiting /
            // CameraDirector.Busy gate which is a host concern and could itself stall a pure mirror). Exits on
            // _endTurnRequested (client's own end-turn or the handoff) or game over.
            while (!ReadBool(faction, "_endTurnRequested") && !IsGameOver(faction))
                yield return NextUpdate.NextFrame;

            // Clear the turn-phase flag on exit (mirror native :486). The handoff in ClientOnTurn also clears it,
            // so this is idempotent. We do NOT run the native teardown (:487-509) — host-authoritative.
            SetIsPlayingTurn(faction, false);
        }

        /// <summary>True when the faction's <c>State == TacFactionState.Playing</c> (native PlayTurnCrt :397 gates
        /// SetViewerTacticalFaction on this). Read by name to avoid a hard enum reference; treat unreadable as
        /// playing (best-effort, so the camera bind still attempts).</summary>
        private static bool IsFactionStatePlaying(object faction)
        {
            try
            {
                object state = GetMember(faction, "State");
                return state == null || state.ToString() == "Playing";
            }
            catch { return true; }
        }

        /// <summary>Replay native PlayTurnCrt's per-actor StartTurn loop (:424-427) over this faction's own
        /// <c>TacticalActors</c>, but PRESERVING each actor's host-streamed AP/WP across the replay (FIX E).
        ///
        /// Each <c>TacticalActor.StartTurn</c> (TacticalActor.cs:1188) does the selection/HUD-load-bearing work the
        /// client mirror needs — <c>RemoveUnusableAbilities</c>, the <c>SetAbilityTraits("start")</c> trait reset
        /// (TacticalActor.cs:1242, the precondition for any "start"-trait-gated ability via
        /// <c>TraitsTagsRequirementSatisfied</c>, TacticalAbility.cs:471), <c>TrySetStandBy</c> (TacticalActor.cs:1211)
        /// — so an actor becomes SELECTABLE (<c>IsActive &amp;&amp; !IsOnStandBy &amp;&amp; CanAct()</c>,
        /// TacticalView.cs:886) and the action HUD appears. BUT it ALSO calls <c>RestartAbilities</c> →
        /// <c>ActionPoints.SetToMax()</c> (TacticalActor.cs:1243), which on a PURE MIRROR is WRONG: the client must
        /// take AP ONLY from the host stream (tac.damage ShooterApAfter + the T1 actor-state delta), never re-max it
        /// locally. The old replay let SetToMax stick → after the client spent AP, the next player turn re-maxed it →
        /// ability icons stayed lit (selectability + ability-lit state are LIVE functions of current AP:
        /// <c>ActionPointRequirementSatisfied</c>, TacticalAbility.cs:146).
        ///
        /// FIX (minimal, non-fragile, pure-mirror): CAPTURE each own-actor's AP+WP immediately BEFORE its StartTurn
        /// replay and RESTORE them immediately AFTER. The full StartTurn machinery still runs (selectability / trait
        /// reset / standby intact — no behavioural change to the HUD path), but the AP/WP re-max never sticks, so the
        /// client's AP stays exactly the host-streamed value until the next host update. Restore goes through the same
        /// grounded <c>BaseStat.Set(float,true)</c> the actor-state mirror uses (TacticalActorStateSync.SetStat), which
        /// re-fires <c>StatChangeEvent</c> → the action-points UI + ability-enabled recompute refresh (icons grey out
        /// at 0 AP). Per-actor guarded so one bad actor can't abort the whole turn.</summary>
        private static void StartTurnForOwnActors(object faction)
        {
            try
            {
                var actors = GetMember(faction, "TacticalActors") as System.Collections.IEnumerable;
                if (actors == null) return;
                // Snapshot to a list first (native does .ToList()) so a StartTurn side-effect can't mutate-while-iterate.
                var snapshot = new List<object>();
                foreach (var a in actors) if (a != null) snapshot.Add(a);
                // We only ever run inside the client mirror (MirrorPlayTurnCrt is gated by IsClientMirroring), so the
                // pure gate reads true here; consulting it keeps the "preserve host AP/WP" contract explicit + tested.
                bool preserveApWp = ClientTurnStartSetupGateApWp();
                foreach (var actor in snapshot)
                {
                    try
                    {
                        // Capture host-streamed AP/WP BEFORE StartTurn (which would SetToMax both).
                        object stats = preserveApWp ? GetMember(actor, "CharacterStats") : null;
                        object apStat = stats != null ? GetMember(stats, "ActionPoints") : null;
                        object wpStat = stats != null ? GetMember(stats, "WillPoints") : null;
                        bool haveAp = apStat != null; float apBefore = haveAp ? StatValue(apStat) : 0f;
                        bool haveWp = wpStat != null; float wpBefore = haveWp ? StatValue(wpStat) : 0f;

                        var m = AccessTools.Method(actor.GetType(), "StartTurn", new System.Type[0]);
                        m?.Invoke(actor, null);

                        // DIAG capture: the value StartTurn (RestartAbilities.SetToMax) left BEFORE we restore.
                        float apMaxed = haveAp ? StatValue(apStat) : 0f;
                        float wpMaxed = haveWp ? StatValue(wpStat) : 0f;

                        // Restore the host-streamed AP/WP AFTER StartTurn — undo its local SetToMax re-max (pure mirror).
                        if (haveAp) SetStat(apStat, apBefore);
                        if (haveWp) SetStat(wpStat, wpBefore);

                        // DIAG (co-op client-mirror turn-start, fires turn 2+): show host->StartTurn-maxed->restored so
                        // a re-test can confirm FIX E captures+restores the host AP and isn't itself leaking the re-max
                        // (AP-not-greying RCA). Cheap: one line per own-actor at turn start.
                        if (preserveApWp && (haveAp || haveWp))
                            Debug.Log("[Multipleer][tac] FIX-E StartTurn netId=" +
                                      TacticalDeploySync.NetIdForLiveActor(actor) +
                                      (haveAp ? (" AP host=" + apBefore + " maxed=" + apMaxed + " restored=" + StatValue(apStat)) : "") +
                                      (haveWp ? (" WP host=" + wpBefore + " maxed=" + wpMaxed + " restored=" + StatValue(wpStat)) : ""));
                    }
                    catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror setup actor.StartTurn failed: " + ex); }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror setup StartTurnForOwnActors failed: " + ex); }
        }

        /// <summary>FIX E decision: should the per-actor StartTurn replay preserve host-streamed AP/WP? Delegates to
        /// the pure <see cref="Multipleer.Sync.Tactical.ClientApPreserveGate"/> with the live mirror flag. Always true
        /// on this path (we only run under IsClientMirroring), but routed through the gate so the contract is tested.</summary>
        private static bool ClientTurnStartSetupGateApWp()
            => Multipleer.Sync.Tactical.ClientApPreserveGate.ShouldPreserveActorApWp(TacticalDeploySync.IsClientMirroring);

        /// <summary>Read a <c>BaseStat</c>'s current float value (its <c>op_Implicit</c> → float; IntValue fallback).
        /// Mirrors <see cref="Multipleer.Sync.Tactical.TacticalActorStateSync"/>.StatValue so AP/WP capture uses the
        /// SAME grounded accessor as the actor-state mirror that authoritatively writes those stats.</summary>
        private static float StatValue(object stat)
        {
            if (stat == null) return 0f;
            try
            {
                var op = AccessTools.Method(stat.GetType(), "op_Implicit", new[] { stat.GetType() });
                if (op != null) return Convert.ToSingle(op.Invoke(null, new[] { stat }));
            }
            catch { }
            try { return Convert.ToSingle(GetMember(stat, "IntValue") ?? 0); } catch { return 0f; }
        }

        /// <summary>Write a <c>BaseStat</c> via <c>Set(float, true)</c> (clamps + fires <c>StatChangeEvent</c> →
        /// UI/ability-enabled refresh). Same grounded path as <c>TacticalActorStateSync.SetStat</c>.</summary>
        private static void SetStat(object stat, float value)
        {
            if (stat == null) return;
            var set = AccessTools.Method(stat.GetType(), "Set", new[] { typeof(float), typeof(bool) });
            if (set != null) { set.Invoke(stat, new object[] { value, true }); return; }
            var set1 = AccessTools.Method(stat.GetType(), "Set", new[] { typeof(float) });
            set1?.Invoke(stat, new object[] { value });
        }

        /// <summary>Raise a faction-level event (e.g. <c>StartTurnEvent</c>) via its backing delegate field
        /// (the field shares the event's name), with no args. No-op if absent / not a delegate.</summary>
        private static void InvokeEvent(object obj, string eventName)
        {
            object del = Traverse.Create(obj).Field(eventName).GetValue();
            if (del is Delegate handler) handler.DynamicInvoke();
        }

        private static int ToInt(object o) { try { return o != null ? Convert.ToInt32(o) : 0; } catch { return 0; } }

        // ─── reflection/Traverse helpers (kept local to the patch) ───────────────────────────────────────

        private static void TrySetEndTurnRequested(object faction, bool value)
        {
            try { Traverse.Create(faction).Field("_endTurnRequested").SetValue(value); }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror PlayTurnCrt set _endTurnRequested failed: " + ex); }
        }

        private static void SetIsPlayingTurn(object faction, bool value)
        {
            // IsPlayingTurn has a private setter — go through the property's set accessor, with a backing-field
            // fallback (mirrors TacticalTurnSync.SetIsPlayingTurn).
            try
            {
                var p = AccessTools.Property(faction.GetType(), "IsPlayingTurn");
                if (p != null && p.GetSetMethod(true) != null) { p.GetSetMethod(true).Invoke(faction, new object[] { value }); return; }
                Traverse.Create(faction).Property("IsPlayingTurn").SetValue(value);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] mirror PlayTurnCrt SetIsPlayingTurn failed: " + ex); }
        }

        private static bool IsGameOver(object faction)
        {
            // faction.TacticalLevel.IsGameOver — best-effort; treat unreadable as "not over" (keep looping).
            try
            {
                object tlc = GetMember(faction, "TacticalLevel");
                object go = tlc != null ? GetMember(tlc, "IsGameOver") : null;
                return go is bool b && b;
            }
            catch { return false; }
        }

        private static bool ReadBool(object obj, string name)
        {
            object v = GetMember(obj, name);
            return v is bool b && b;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }
    }
}
