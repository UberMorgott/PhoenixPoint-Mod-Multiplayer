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

    /// <summary>
    /// Inc Vision — HOST turn-boundary vision-push trigger. Postfix on
    /// <c>TacticalFactionVision.OnFactionStartTurn()</c> (TacticalFactionVision.cs:154). At every faction
    /// turn start the host ages out located-enemy knowledge (<c>DecrementMyCountersForFaction</c> +
    /// <c>UpdateVisibilityAll(notifyChange:false)</c>, :166-170) with notifyChange:false → NO
    /// <c>FactionKnowledgeChanged</c> fires, so <see cref="VisionBroadcastPatch"/> (and the enemy-action
    /// pushes) never re-broadcast the shrunken set and the client keeps STALE located icons. This postfix
    /// closes that gap: <see cref="TacticalVisionSync.HostOnFactionStartTurn"/> forces one dedup-guard reset
    /// + a <c>tac.vision</c> re-baseline so the client drops the aged-out enemies once per turn. Host + co-op
    /// gated at runtime inside the handler; off-host / single-player → no-op. Coexists with the client-side
    /// <c>VisionOnFactionStartTurnSuppressPatch</c> prefix on the same method (the suppress prefix runs the
    /// native body on the host, then this postfix pushes; on the client the handler is host-gated → no-op).
    /// Auto-registers via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class VisionStartTurnBroadcastPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (t == null) return false;
            // public void OnFactionStartTurn() — no params (TacticalFactionVision.cs:154).
            _target = AccessTools.Method(t, "OnFactionStartTurn");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix()
        {
            try { TacticalVisionSync.HostOnFactionStartTurn(); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] VisionStartTurnBroadcastPatch.Postfix failed: " + ex);
            }
        }
    }
}
