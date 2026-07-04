using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// Inc Vision — HOST vision-push trigger. Postfix on
    /// <c>TacticalLevelController.FactionKnowledgeChanged(TacticalFaction faction)</c> (TLC.cs:821 — the method
    /// that raises <c>FactionKnowledgeChangedEvent</c>). This replaces an earlier reflective event subscription:
    /// vision is the keystone of the co-op tactical mirror, so the trigger must not depend on a runtime
    /// <c>GetEvent</c>/<c>Delegate.CreateDelegate</c> bind that could silently return null and leave the host
    /// never pushing (feature dead in-game). A Harmony patch is the codebase's proven, always-registered pattern;
    /// all gating is done at runtime inside <see cref="TacticalVisionSync.HostOnFactionKnowledgeChanged"/> (host +
    /// co-op active + the changed faction is the shared player faction → snapshot + broadcast <c>tac.vision</c>,
    /// keeping the identical-snapshot skip guard). Off-host / single-player / non-player-faction → no-op (native
    /// byte-identical). Auto-registers via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class VisionBroadcastPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (t == null) return false;
            var factionType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFaction");
            if (factionType == null) return false;
            // public void FactionKnowledgeChanged(TacticalFaction faction) — EXACT param-match so AccessTools binds
            // the single overload (TLC.cs:821).
            _target = AccessTools.Method(t, "FactionKnowledgeChanged", new[] { factionType });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Signature: void FactionKnowledgeChanged(TacticalFaction faction). The arg is the faction whose knowledge
        // changed; the gate (host / player faction) lives in HostOnFactionKnowledgeChanged.
        public static void Postfix(object faction)
        {
            try { TacticalVisionSync.HostOnFactionKnowledgeChanged(faction); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] VisionBroadcastPatch.Postfix failed: " + ex);
            }
        }
    }
}
