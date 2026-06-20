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

        /// <summary>The frozen-mirror replacement for PlayTurnCrt: NO sim setup, NO stall yields, NO teardown —
        /// just the player input/end-turn wait loop, bounded by <c>_endTurnRequested</c> / game-over. For a
        /// non-player faction it returns immediately (pure spectator). Reads turn-state via reflection/Traverse
        /// so it stays bound to the real engine fields without a hard type reference.</summary>
        private static IEnumerator<NextUpdate> MirrorPlayTurnCrt(object faction, Action turnStartAction)
        {
            // Reset the end-turn latch for this turn (native PlayTurnCrt:391 does the same first thing).
            TrySetEndTurnRequested(faction, false);

            // Only player-controlled factions get a live input turn on the client; enemy/AI factions are pure
            // spectators here (their presentation is driven by TacticalTurnSync.ClientOnTurn).
            bool isPlayer = ReadBool(faction, "IsControlledByPlayer");
            if (!isPlayer)
                yield break;

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
            // so this is idempotent.
            SetIsPlayingTurn(faction, false);
        }

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
