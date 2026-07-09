using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT-ONLY skip of the native post-mission gear pipeline — <c>GeoMission.ManageGear</c>
    /// (decompile GeoMission.cs:856, called from <c>ApplyMissionResults</c> :551). In co-op the client
    /// runs the native mission-end flow LOCALLY, so ManageGear writes the client's MIRRORED model:
    ///   • recovered-gear collection into <c>Reward.Items</c> (:864-892);
    ///   • <c>GeoPhoenixFaction.PostmissionReplenish</c> (:894-897) — consumes <c>Reward.Items</c>
    ///     FIRST, then faction storage (<c>PostmissionReplenishManager.RestoreList</c> →
    ///     <c>ItemStorage.PopItem</c>);
    ///   • <c>TryReloadItem</c> per surviving soldier item (:899-926) — <c>ModifyCharges</c> against
    ///     <c>Reward.Items</c>, then faction storage (:1095-1104);
    ///   • <c>ManageFreeReloads</c> (:927) + <c>ManageAutosellItems</c> (:928).
    /// The ASYMMETRY is the storage desync (e.g. ammo host 2 / client 1): the host's
    /// <c>Reward.Items</c> holds the recovered gear, the client's mirror is empty, so the client's
    /// replenish eats its mirrored FACTION STORAGE instead — and the eaten amount never converged
    /// because those native writes are event-silent (see <c>InventoryChannel.PollHostDrift</c>).
    ///
    /// The WHOLE method is skippable on the client because every write is model state mirrored from
    /// the host: faction storage rides inventory channel #1, soldier items/charges ride the #9
    /// personnel blob, reward resources ride the wallet rail, and the client's mission-outcome /
    /// reward DISPLAY is host-mirrored too (outcome mirror + <see cref="RewardRenderPatch"/> replay
    /// host-formatted lines; they never read the client's local <c>Reward</c>). No UI/view calls in
    /// the method body, and no override of ManageGear exists anywhere in the game assembly (verified
    /// against the decompile 2026-07-09). Gated strictly on an ACTIVE co-op CLIENT — host /
    /// single-player run native code untouched. Best-effort try/catch — never throws into game code
    /// (mirrors <see cref="EventSuppressClientGeoscapePatch"/>).
    /// </summary>
    [HarmonyPatch]
    public static class PostMissionGearClientSkipPatch
    {
        private static MethodBase _target;   // GeoMission.ManageGear(TacMissionResult, GeoSquad)

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
            if (t == null) return false;   // engine not loaded -> Harmony skips this class
            _target = AccessTools.Method(t, "ManageGear");   // single method, no overloads
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Skip (return false) ONLY on an active co-op client; anything else runs native code.
        public static bool Prefix()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
                Debug.Log("[Multiplayer] client GeoMission.ManageGear skipped (host-authoritative post-mission gear; truth arrives via #1 inventory + #9 personnel)");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] PostMissionGearClientSkipPatch failed: " + ex.Message);
                return true;   // never block native code on our own failure
            }
        }
    }
}
