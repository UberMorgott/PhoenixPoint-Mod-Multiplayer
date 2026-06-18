using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// Deploy NULL-FACTION guard. Prefix on <c>TacticalFactionVision.OnActorEnteredPlay(TacticalActorBase)</c>
    /// (TacticalFactionVision.cs:337). The native handler THROWS <c>ArgumentException</c> when an entering actor
    /// has a null <c>TacticalFaction</c> (:345-348). A deploy intruder actor (observed:
    /// <c>Deploy_Intruder_1x1_Grunt_Elite_and_Tiny</c>) can enter play before its faction is wired — its
    /// FactionDef resolves to a faction not initialized in this mission, so <c>OnEnterPlay</c>
    /// (TacticalActorBase.cs:533-539) ends up calling <c>SetFaction(null)</c>. In a synced session the throw
    /// propagates into the host's deploy capture and aborts part of <c>TacticalDeploySync.HostOnLevelReady</c>
    /// ("HostOnLevelReady failed: …OnActorEnteredPlay()… null faction"). The mod neither spawns nor re-enters
    /// these actors (native/TFTV deploy-ordering quirk), so we guard it defensively on our side, narrowly
    /// (mirrors the <c>theturned-tftv-compat-required</c> pattern).
    ///
    /// Decision lives in the pure, unit-tested <see cref="NullFactionEnterPlayGate"/>:
    ///   • Outside an active session → run native unchanged (single-player byte-identical).
    ///   • Active session + the entering actor has a REAL faction (every soldier) → run native (no throw) →
    ///     the verified soldier-load is untouched.
    ///   • Active session + null faction (the deploy intruder) → SKIP native (return false), so the
    ///     ArgumentException can never fire. Skipping is correct: the native body would only add the actor to
    ///     this faction's vision and then throw — a faction-less actor has no meaningful vision relation.
    ///
    /// Auto-registers via <c>MultipleerMain.PatchAll(GetExecutingAssembly())</c>. Reflection-target lazily.
    /// </summary>
    [HarmonyPatch]
    public static class NullFactionEnterPlayPatch
    {
        private static MethodBase _target;
        private static PropertyInfo _factionProp;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (t == null) return false;
            // private void OnActorEnteredPlay(TacticalActorBase tacticalActorBase)
            _target = AccessTools.Method(t, "OnActorEnteredPlay");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Signature: void OnActorEnteredPlay(TacticalActorBase tacticalActorBase)
        public static bool Prefix(object tacticalActorBase)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                bool inSession = engine != null && engine.IsActive;
                if (!inSession) return true;   // single-player / no session → native runs unchanged

                bool factionIsNull = ReadFactionIsNull(tacticalActorBase);
                return NullFactionEnterPlayGate.ShouldRunNativeEnterPlay(inSession, factionIsNull);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multipleer][tac] NullFactionEnterPlayPatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native vision handler on an unexpected error
            }
        }

        /// <summary>Read <c>actor.TacticalFaction</c> and report whether it is null. A null actor is treated as
        /// "null faction" (the native body would NRE / throw anyway), suppressing it in-session.</summary>
        private static bool ReadFactionIsNull(object actor)
        {
            if (actor == null) return true;
            if (_factionProp == null || _factionProp.DeclaringType == null ||
                !_factionProp.DeclaringType.IsInstanceOfType(actor))
            {
                _factionProp = AccessTools.Property(actor.GetType(), "TacticalFaction");
            }
            if (_factionProp == null) return false;   // can't read → assume real faction → run native (safe)
            // TacticalFaction is a plain class (implements the Defineable INTERFACE, not a UnityEngine.Object),
            // and the native throw fires on a genuine null reference (SetFaction(null) nulls the C# field), so a
            // plain reference null-check is exact here.
            return _factionProp.GetValue(actor, null) == null;
        }
    }
}
