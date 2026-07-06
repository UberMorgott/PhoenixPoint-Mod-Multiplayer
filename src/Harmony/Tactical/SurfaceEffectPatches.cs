using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// GROUND-SURFACE mirror host CAPTURE chokepoint (spec TS3). A single HOST postfix on the leaf voxel mutation
    /// <c>TacticalVoxel.SetVoxelType</c> — the one funnel every fire / goo / acid / MIST spawn AND removal passes
    /// through (SpawnTacticalVoxelEffect / RemoveTacticalVoxelEffect / SpawnMistAbility / Fire+Goo self-extinguish).
    /// It hands each changed voxel to <see cref="TacticalSurfaceSync.HostCaptureVoxelChange"/>, which coalesces it
    /// into the flush heartbeat (broadcast as 0x94). All gating lives in the sync layer (host + active session +
    /// deploy-captured + not applying-remote), so a stray fire off-host / pre-deploy / during a client replay is a
    /// clean no-op. Auto-registers via PatchAll; reflection-target lazily like the sibling tactical patches.
    /// </summary>
    [HarmonyPatch]
    public static class VoxelSetTypeCapturePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.TacticalVoxel");
            if (t == null) return false;
            // public void SetVoxelType(TacticalVoxelType voxelType, float visualsDelay = 0f, float voxelValue = 0f)
            _target = AccessTools.Method(t, "SetVoxelType");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Postfix (never suppresses the native mutation). __instance = the TacticalVoxel that just changed type.
        public static void Postfix(object __instance)
        {
            try { TacticalSurfaceSync.HostCaptureVoxelChange(__instance); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] VoxelSetTypeCapturePatch.Postfix failed: " + ex);
            }
        }
    }

    /// <summary>
    /// CLIENT-ONLY inert guards that keep a MIRRORED ground volume PRESENTATION + LoS only (spec TS3): the frozen
    /// client replays the native voxel type for display, but the DAMAGE / STATUS the volume deals to actors stays
    /// host-authoritative (rides tac.damage 0x88 / 0x8F). The per-turn fire/goo ticks are already frozen (the
    /// matrix StartTurn/EndTurn is driven by the turn coroutines the client suppresses); these guards are the
    /// defense-in-depth for those PLUS the two paths the frozen client can still reach synchronously:
    ///   • <c>TacticalVoxelMatrix.UpdateGooedStatus</c> — applied at goo-voxel spawn (GooVoxelManager.OnSpawn) AND
    ///     on every mirrored actor tile-change (OnActorMovedInNewTile) → would double the goo status the 0x8F delta
    ///     already mirrors as an inert icon.
    ///   • <c>FireVoxelManager.ApplyFireDamageToActor</c> (instance) — applied on a mirrored actor stepping into
    ///     fire (OnActorMovedInNewTile) → would double the fire damage tac.damage already mirrors.
    ///   • <c>FireVoxelManager.StartTurn</c> / <c>GooVoxelManager.EndTurn</c> — the per-turn damage/decay ticks
    ///     (defense-in-depth; host drives fire/goo REMOVAL via a 0x94 remove op, not the client's own decay).
    ///
    /// SCOPE (hard): every guard early-returns (lets the original run) UNLESS this instance is a co-op CLIENT inside
    /// a mirrored tactical mission (<see cref="TacticalDeploySync.IsClientMirroring"/>). On the client, EVERY ground
    /// volume is a host mirror (client abilities are suppressed), so no per-instance tracking is needed — unlike the
    /// status guards. Host and single-player are NEVER affected. Fail-OPEN on any error. Auto-registers via PatchAll.
    /// </summary>
    public static class ClientSurfaceInertGuards
    {
        // Live wiring of the pure decision (ClientSurfaceInertGate.ShouldSuppress). Fail-open — never wedge a
        // native surface method.
        private static bool SuppressNow()
        {
            try { return ClientSurfaceInertGate.ShouldSuppress(TacticalDeploySync.IsClientMirroring); }
            catch { return false; }
        }

        // ─── UpdateGooedStatus — no goo-status double-apply on the client (0x8F owns the icon) ─────────────
        [HarmonyPatch]
        public static class UpdateGooedStatusGuard
        {
            private static MethodBase _target;
            public static bool Prepare()
            {
                var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.TacticalVoxelMatrix");
                if (t == null) return false;
                // public void UpdateGooedStatus(TacticalActorBase actor, Vector3 pos)
                _target = AccessTools.Method(t, "UpdateGooedStatus");
                return _target != null;
            }
            public static MethodBase TargetMethod() => _target;
            public static bool Prefix() => !SuppressNow();
        }

        // ─── FireVoxelManager.ApplyFireDamageToActor(TacticalActorBase) — no fire-on-enter damage on the client ──
        [HarmonyPatch]
        public static class ApplyFireDamageToActorGuard
        {
            private static MethodBase _target;
            public static bool Prepare()
            {
                var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.FireVoxelManager");
                var actorBase = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
                if (t == null || actorBase == null) return false;
                // The INSTANCE 1-arg overload: public bool ApplyFireDamageToActor(TacticalActorBase actor)
                _target = AccessTools.Method(t, "ApplyFireDamageToActor", new[] { actorBase });
                return _target != null;
            }
            public static MethodBase TargetMethod() => _target;
            public static bool Prefix() => !SuppressNow();
        }

        // ─── FireVoxelManager.StartTurn — per-turn fire damage tick (defense-in-depth; frozen anyway) ─────
        [HarmonyPatch]
        public static class FireStartTurnGuard
        {
            private static MethodBase _target;
            public static bool Prepare()
            {
                var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.FireVoxelManager");
                if (t == null) return false;
                _target = AccessTools.Method(t, "StartTurn");
                return _target != null;
            }
            public static MethodBase TargetMethod() => _target;
            public static bool Prefix() => !SuppressNow();
        }

        // ─── GooVoxelManager.EndTurn — per-turn goo decay (defense-in-depth; host drives removal via 0x94) ──
        [HarmonyPatch]
        public static class GooEndTurnGuard
        {
            private static MethodBase _target;
            public static bool Prepare()
            {
                var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.GooVoxelManager");
                if (t == null) return false;
                _target = AccessTools.Method(t, "EndTurn");
                return _target != null;
            }
            public static MethodBase TargetMethod() => _target;
            public static bool Prefix() => !SuppressNow();
        }
    }
}
