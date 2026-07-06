using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the campaign-end sync (feat-campaign-end) around the ONE native chokepoint
    /// <c>GeoLevelController.TriggerGameOver(GeoFaction victoriousFaction)</c>. Decompile-verified 2026-07-07:
    ///   • <c>GeoLevelController._gameOverTriggered</c> (private bool, GeoLevelController.cs:195) — the native
    ///     one-shot latch; read in the patch Prefix so only the FIRST (state-flipping) call broadcasts.
    ///   • <c>GeoLevelController.TriggerGameOver(GeoFaction)</c> (:1068) — invoked on the CLIENT to replay the
    ///     SAME native ending: statistics (<c>PhoenixStatisticsManager.OnGeoscapeGameOver</c>) +
    ///     <c>View.ToGameOverState(victoriousFaction == PhoenixFaction)</c>, whose cinematic def
    ///     (AlienVictoryCinematicDef / BasesLostCinematicDef, GeoscapeView.cs:662-670) is resolved LOCALLY off
    ///     the view — no assets on the wire (behemoth local-template precedent).
    ///   • <c>GeoLevelController.PhoenixFaction</c> (GeoPhoenixFaction, :225) / <c>AlienFaction</c>
    ///     (GeoAlienFaction, :223) — auto-properties; the replay faction is picked by the wire victory flag
    ///     (native TriggerGameOver branches ONLY on <c>== PhoenixFaction</c>, so the boolean is lossless).
    ///   • <c>GeoFaction.Def</c> (property) → GeoFactionDef : BaseDef — victor guid via
    ///     <see cref="DefReflection.GetGuid"/> (the informational "ending id" on the wire).
    /// Every member is best-effort: a miss degrades (logged, false/null) — never throws into game code.
    /// </summary>
    public static class CampaignEndReflection
    {
        private static bool _ensured;
        private static FieldInfo _gameOverTriggeredField;   // GeoLevelController._gameOverTriggered
        private static MethodInfo _triggerGameOver;         // GeoLevelController.TriggerGameOver(GeoFaction)
        private static PropertyInfo _phoenixFactionProp;    // GeoLevelController.PhoenixFaction
        private static PropertyInfo _alienFactionProp;      // GeoLevelController.AlienFaction
        private static PropertyInfo _factionDefProp;        // GeoFaction.Def

        private static void Ensure()
        {
            if (_ensured) return;
            _ensured = true;   // one attempt; every user null-guards
            try
            {
                var geoT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
                var factionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
                if (geoT == null || factionT == null) return;
                _gameOverTriggeredField = AccessTools.Field(geoT, "_gameOverTriggered");
                // EXACT param match (harmony-accesstools-exact-param-match): (GeoFaction).
                _triggerGameOver = AccessTools.Method(geoT, "TriggerGameOver", new[] { factionT });
                _phoenixFactionProp = AccessTools.Property(geoT, "PhoenixFaction");
                _alienFactionProp = AccessTools.Property(geoT, "AlienFaction");
                _factionDefProp = AccessTools.Property(factionT, "Def");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CampaignEndReflection.Ensure failed: " + ex.Message); }
        }

        /// <summary>Read the native one-shot latch. A miss reports triggered=true (fail-closed: the caller
        /// then treats the call as a re-entrant no-op and never double-broadcasts).</summary>
        public static bool ReadGameOverTriggered(object geoLevel)
        {
            try
            {
                Ensure();
                if (geoLevel == null || _gameOverTriggeredField == null) return true;
                return (bool)_gameOverTriggeredField.GetValue(geoLevel);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] CampaignEndReflection.ReadGameOverTriggered failed: " + ex.Message);
                return true;
            }
        }

        /// <summary>Host: classify the ending off the live chokepoint args — victory iff
        /// <paramref name="victoriousFaction"/> IS the level's PhoenixFaction (the exact native branch),
        /// plus the victor's Def.Guid (informational ending id; "" on any miss).</summary>
        public static void ReadHostEnd(object geoLevel, object victoriousFaction,
                                       out bool victory, out string victorFactionGuid)
        {
            victory = false;
            victorFactionGuid = "";
            try
            {
                Ensure();
                if (geoLevel != null && _phoenixFactionProp != null)
                    victory = ReferenceEquals(_phoenixFactionProp.GetValue(geoLevel, null), victoriousFaction)
                              && victoriousFaction != null;
                if (victoriousFaction != null && _factionDefProp != null)
                    victorFactionGuid = DefReflection.GetGuid(_factionDefProp.GetValue(victoriousFaction, null)) ?? "";
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CampaignEndReflection.ReadHostEnd failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: replay the SAME native campaign ending — invoke <c>TriggerGameOver(victory ?
        /// PhoenixFaction : AlienFaction)</c> on the live level (native branches only on the Phoenix
        /// comparison, so the boolean reproduces the host's outro + statistics + GameOver screen exactly;
        /// the cutscene def is the view's own local field). Caller wraps in <c>SyncApplyScope</c> so the
        /// chokepoint patch's client-suppress Prefix passes the replay through. Returns false on ANY
        /// unresolved piece (caller degrades to the notify prompt + menu return).
        /// </summary>
        public static bool ReplayCampaignEnd(GeoRuntime rt, bool victory)
        {
            try
            {
                Ensure();
                var geo = rt?.GeoLevel();
                if (geo == null || _triggerGameOver == null) return false;
                var factionProp = victory ? _phoenixFactionProp : _alienFactionProp;
                var faction = factionProp?.GetValue(geo, null);
                if (faction == null) return false;
                _triggerGameOver.Invoke(geo, new[] { faction });
                Debug.Log("[Multiplayer] CampaignEndReflection.ReplayCampaignEnd victory=" + victory
                          + " → native TriggerGameOver replayed (local cinematic + GameOver screen)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] CampaignEndReflection.ReplayCampaignEnd failed: " + ex.Message);
                return false;
            }
        }
    }
}
