using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// Host deploy-snapshot NULL-FACTION guard (the real fix for the "HostOnLevelReady failed … null faction"
    /// abort). Prefix on <c>TacticalFactionVision.RecordInstanceData(TacFactionData)</c>
    /// (TacticalFactionVision.cs:968). The native body copies <c>KnownActors</c> into the snapshot (:970) then
    /// iterates it and THROWS <c>ArgumentException</c> for any known actor whose <c>TacticalFaction == null</c>
    /// (:973-976). A deploy intruder (<c>Deploy_Intruder_1x1_Grunt_Elite_and_Tiny</c>) sits in
    /// <c>KnownActors</c> with a null faction, so the host snapshot capture throws and aborts
    /// <c>TacticalDeploySync.HostOnLevelReady</c> at its FIRST step — before the <c>tac.deploy</c> broadcast.
    /// Downstream: the client never receives a deploy → never enters mirror mode → its moves run locally and
    /// no <c>tac.intent.move</c> is sent (the move/end-turn "Bug #1" was a symptom of THIS, confirmed by the
    /// 2026-06-18 host/client logs: host 0 tac.deploy broadcasts, client CAPTURE mirrorArmed=False action=PASS).
    ///
    /// FIX: in an active synced session, REMOVE every null-faction entry from this vision's <c>KnownActors</c>
    /// BEFORE the native runs, so the snapshot is throw-free AND carries no phantom null-faction known-actor
    /// (which would also break the client's <c>ProcessInstanceData</c>). A faction-less known actor is a phantom
    /// (a deploy marker that never got a real faction) with no meaningful vision relation, so dropping it from
    /// vision is correct. Outside a session → no-op (native byte-identical). Narrow + defensive
    /// (<c>theturned-tftv-compat-required</c> pattern). Decision in the pure, unit-tested
    /// <see cref="NullFactionKnownActorsScrubGate"/>. Auto-registers via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class NullFactionKnownActorsScrubPatch
    {
        private static MethodBase _target;
        private static FieldInfo _knownActorsField;
        private static PropertyInfo _keyFactionProp;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (t == null) return false;
            // public void RecordInstanceData(TacFactionData factionData)
            _target = AccessTools.Method(t, "RecordInstanceData");
            _knownActorsField = AccessTools.Field(t, "KnownActors");
            return _target != null && _knownActorsField != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Signature: void RecordInstanceData(TacFactionData factionData). __instance = the TacticalFactionVision.
        public static bool Prefix(object __instance)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                bool inSession = engine != null && engine.IsActive;
                if (!NullFactionKnownActorsScrubGate.ShouldScrub(inSession)) return true;   // native unchanged

                ScrubNullFactionKnownActors(__instance);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] NullFactionKnownActorsScrubPatch.Prefix failed: " + ex);
            }
            return true;   // always let the native run (now throw-free); never wedge the snapshot
        }

        /// <summary>Remove every <c>KnownActors</c> entry whose key actor has a null <c>TacticalFaction</c>.
        /// KnownActors is a <c>public readonly Dictionary&lt;TacticalActorBase, KnownCounters&gt;</c>
        /// (TacticalFactionVision.cs:115) — readonly is the FIELD ref; the dictionary itself is mutable.</summary>
        private static void ScrubNullFactionKnownActors(object vision)
        {
            if (vision == null) return;
            var dict = _knownActorsField.GetValue(vision) as IDictionary;
            if (dict == null || dict.Count == 0) return;

            var toRemove = new List<object>();
            foreach (DictionaryEntry e in dict)
            {
                object actor = e.Key;
                if (actor == null) { toRemove.Add(e.Key); continue; }
                if (_keyFactionProp == null || _keyFactionProp.DeclaringType == null ||
                    !_keyFactionProp.DeclaringType.IsInstanceOfType(actor))
                {
                    _keyFactionProp = AccessTools.Property(actor.GetType(), "TacticalFaction");
                }
                // TacticalFaction is a plain class (implements the Defineable INTERFACE, not a UnityEngine.Object),
                // and the native throw fires on a genuine null reference, so a plain reference null-check is exact.
                object faction = _keyFactionProp != null ? _keyFactionProp.GetValue(actor, null) : actor;
                if (faction == null) toRemove.Add(e.Key);
            }

            if (toRemove.Count == 0) return;
            foreach (var k in toRemove) dict.Remove(k);
            Debug.Log("[Multiplayer][tac] scrubbed " + toRemove.Count +
                      " null-faction KnownActor(s) before snapshot RecordInstanceData");
        }
    }
}
