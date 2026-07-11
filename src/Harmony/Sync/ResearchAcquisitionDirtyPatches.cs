using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Shared HOST-only gate for the OUT-OF-BAND research-acquisition dirty hooks below. Research state
    /// already syncs via ch#2 (<see cref="ResearchChannel"/>) on the faction start/complete events + an
    /// hourly heartbeat, so an out-of-band grant converges within ~1 game-hour. These paths bypass the
    /// queue start/complete events entirely — steal-research raids, Marketplace purchases, geoscape
    /// <c>GiveResearches</c> event grants, allied-faction sharing, TFTV background bonuses, diplomacy-
    /// threshold reputation grants — so without a dirty mark the frozen client lags a whole hour. Each
    /// postfix/prefix here merely re-marks ch#2 dirty on the HOST for the SYNCED (Phoenix) faction; the
    /// existing snapshot engine coalesces the flag into ONE immediate send. NO new message, NO new wire
    /// format. Client is a pure mirror — its local mutation is reconciled by the next host snapshot, so it
    /// never dirty-marks (IsHost gate). Engine-driven replay (<see cref="SyncApplyScope"/>) is already
    /// covered by the action system + faction events, so it is skipped.
    /// </summary>
    internal static class ResearchDirtyGate
    {
        private static PropertyInfo _elementFactionProp; // ResearchElement.Faction (GeoFaction, ResearchElement.cs:209)

        internal static Type ResearchType() => AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
        internal static Type ElementType() => AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");
        internal static Type DefType() => AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchDef");

        // True on the HOST of an active MP session, outside engine-driven replay.
        private static bool HostActive()
        {
            if (SyncApplyScope.IsApplying) return false;
            var engine = NetworkEngine.Instance;
            return engine != null && engine.IsActiveSession && engine.IsHost;
        }

        private static void MarkDirty() => NetworkEngine.Instance?.Sync?.MarkChannelDirty(2);

        /// <summary>
        /// Methods 1-3: <paramref name="research"/> IS the RECEIVING faction's <c>Research</c> (the patched
        /// instance). Mark ch#2 only when it is the synced Phoenix faction's Research — an NPC/alien faction
        /// receiving research or accruing its hourly progress must not spam the channel.
        /// </summary>
        public static void MarkForResearch(object research)
        {
            try
            {
                if (!HostActive()) return;
                if (!ReferenceEquals(research, ResearchStateReflection.GetResearch(GeoRuntime.Instance))) return;
                MarkDirty();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ResearchDirtyGate.MarkForResearch failed: " + ex.Message); }
        }

        /// <summary>
        /// State setter: <paramref name="element"/> is the <c>ResearchElement</c> being written. Gate on its
        /// owning faction == Phoenix and mark on EVERY such write, incl. same-value ones — the diplomacy grant
        /// pairs the State write with a DIRECT <c>ResearchProgress</c> write (a plain field, ResearchElement.cs:139,
        /// not Harmony-patchable), and if State is already Unlocked a change-filter would miss that progress
        /// bump. Dirty-flag coalescing makes the repeated mark free; the faction gate blocks NPC/alien spam.
        /// </summary>
        public static void MarkForElement(object element)
        {
            try
            {
                if (!HostActive()) return;
                var fac = ElementFaction(element);
                if (fac == null || !ReferenceEquals(fac, GeoRuntime.Instance.PhoenixFaction())) return;
                MarkDirty();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ResearchDirtyGate.MarkForElement failed: " + ex.Message); }
        }

        private static object ElementFaction(object element)
        {
            if (element == null) return null;
            if (_elementFactionProp == null || _elementFactionProp.DeclaringType == null
                || !_elementFactionProp.DeclaringType.IsInstanceOfType(element))
                _elementFactionProp = AccessTools.Property(element.GetType(), "Faction");
            return _elementFactionProp?.GetValue(element, null);
        }
    }

    /// <summary>
    /// <c>Research.GiveResearch(ResearchElement, bool copyProgress)</c> (Research.cs:489) — steal-research
    /// raids, Marketplace purchases, allied-faction copy (<c>CopyResearchesFromFaction</c>). Two GiveResearch
    /// overloads exist, so the target is bound with EXACT param types (base-type mismatch → silent null →
    /// patch never binds). Reflective target (Prepare false → PatchAll skips) so an engine rename never bombs.
    /// </summary>
    [HarmonyPatch]
    public static class GiveResearchElementDirtyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = ResearchDirtyGate.ResearchType();
            var elem = ResearchDirtyGate.ElementType();
            if (t == null || elem == null) return false;
            _target = AccessTools.Method(t, "GiveResearch", new[] { elem, typeof(bool) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the receiving faction's Research.
        public static void Postfix(object __instance) => ResearchDirtyGate.MarkForResearch(__instance);
    }

    /// <summary>
    /// <c>Research.GiveResearch(ResearchDef, float progress, bool completeIfReady)</c> (Research.cs:503) —
    /// geoscape <c>GiveResearches</c> event grants. Bound by EXACT param types to disambiguate the overload.
    /// Reflective target (Prepare false → PatchAll skips).
    /// </summary>
    [HarmonyPatch]
    public static class GiveResearchDefDirtyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = ResearchDirtyGate.ResearchType();
            var def = ResearchDirtyGate.DefType();
            if (t == null || def == null) return false;
            _target = AccessTools.Method(t, "GiveResearch", new[] { def, typeof(float), typeof(bool) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance) => ResearchDirtyGate.MarkForResearch(__instance);
    }

    /// <summary>
    /// <c>Research.AddProgressToResearch(ResearchElement, float, bool, bool)</c> (Research.cs:820) — the final
    /// progress-accrual sink (TFTV background research bonuses, ally contributions, <c>GiveResearch(def,…)</c>).
    /// Faction-gated in the shared helper so the NPC/alien factions' hourly accrual never spams ch#2.
    /// Reflective target (Prepare false → PatchAll skips).
    /// </summary>
    [HarmonyPatch]
    public static class AddProgressToResearchDirtyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = ResearchDirtyGate.ResearchType();
            var elem = ResearchDirtyGate.ElementType();
            if (t == null || elem == null) return false;
            _target = AccessTools.Method(t, "AddProgressToResearch", new[] { elem, typeof(float), typeof(bool), typeof(bool) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance) => ResearchDirtyGate.MarkForResearch(__instance);
    }

    /// <summary>
    /// <c>ResearchElement.State</c> setter (ResearchElement.cs:195) — the diplomacy-threshold reputation grant
    /// writes <c>State</c> DIRECTLY (GeoPhoenixFaction.cs:952-1014), bypassing every GiveResearch/complete
    /// method, so this is the only hook that catches it. It also writes <c>ResearchProgress</c> right after
    /// (a plain field, not patchable), so we mark on EVERY PX-faction write incl. same-value ones — the
    /// deferred snapshot then carries the paired progress bump. Faction-gated in the shared helper. Reflective
    /// target (Prepare false → PatchAll skips).
    /// </summary>
    [HarmonyPatch]
    public static class ResearchElementStateDirtyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var elem = ResearchDirtyGate.ElementType();
            if (elem == null) return false;
            _target = AccessTools.PropertySetter(elem, "State");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the ResearchElement whose State was just written.
        public static void Postfix(object __instance) => ResearchDirtyGate.MarkForElement(__instance);
    }
}
