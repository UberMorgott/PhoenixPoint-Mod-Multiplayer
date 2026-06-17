using System.Reflection;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// LIVE faction-handoff capture (spec §3.5, Inc 4). HOST postfix on
    /// <c>TacMission.OnNewTurn(TacticalFaction prevFaction, TacticalFaction nextFaction)</c>
    /// (TacMission.cs:359) — the host's authoritative <c>NextTurnCrt</c> calls this exactly once per
    /// faction-turn-start (TLC.cs:716), for BOTH player and AI factions. Broadcasts <c>tac.turn</c> so every
    /// client mirrors the handoff. No-op off-host. Auto-registers via PatchAll.
    ///
    /// The CLIENT side is driven entirely by the <c>tac.turn</c> inbound handler
    /// (<see cref="TacticalTurnSync.ClientOnTurn"/>), not a patch — the client's own NextTurnCrt stays
    /// suppressed (Inc-1 MirrorSuppressPatches), so its OnNewTurn never fires; the client follows discretely.
    /// </summary>
    [HarmonyPatch]
    public static class TacMissionOnNewTurnPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Missions.TacMission");
            if (t == null) return false;
            // public void OnNewTurn(TacticalFaction prevFaction, TacticalFaction nextFaction)
            _target = AccessTools.Method(t, "OnNewTurn");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Signature: void OnNewTurn(TacticalFaction prevFaction, TacticalFaction nextFaction)
        public static void Postfix(object nextFaction)
        {
            try { TacticalTurnSync.HostBroadcastTurn(nextFaction); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multipleer][tac] TacMissionOnNewTurnPatch.Postfix failed: " + ex);
            }
        }
    }
}
