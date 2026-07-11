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
        private static PropertyInfo _elementStateProp;   // ResearchElement.State  (ResearchElement.cs:195)

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
        /// Diplomacy-grant hook (patch 5): the method mutates ONLY this Phoenix faction's own Research, so no
        /// faction gate — mark unconditionally under the host guard. This is the ONLY hook that catches the
        /// diplomacy grant's progress bump on an ALREADY-Unlocked element: it guards the State write with
        /// <c>if (State != Unlocked)</c> (GeoPhoenixFaction.cs:1004) so the State setter is never called, yet
        /// bumps <c>ResearchProgress</c> unconditionally (:1009, a plain field — not patchable).
        /// </summary>
        public static void MarkPhoenix()
        {
            try
            {
                if (!HostActive()) return;
                MarkDirty();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ResearchDirtyGate.MarkPhoenix failed: " + ex.Message); }
        }

        /// <summary>
        /// State setter (patch 4): <paramref name="element"/> is the <c>ResearchElement</c> being written. Gate
        /// on its owning faction == Phoenix and only mark when the incoming <paramref name="newState"/> actually
        /// differs from the current one — the setter runs <c>OnStateChanged</c> unconditionally, so an equal-value
        /// write would dirty-spam. Called from the setter Prefix, so the getter still reads the OLD state. (The
        /// diplomacy grant's progress bump on an already-Unlocked element is handled by <see cref="MarkPhoenix"/>,
        /// NOT here — that path never calls the setter at all.)
        /// </summary>
        public static void MarkForElementState(object element, object newState)
        {
            try
            {
                if (!HostActive()) return;
                var fac = ElementFaction(element);
                if (fac == null || !ReferenceEquals(fac, GeoRuntime.Instance.PhoenixFaction())) return;
                var current = ElementState(element);                       // OLD value (Prefix, pre-write)
                if (current != null && current.Equals(newState)) return;   // no-op write → skip
                MarkDirty();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ResearchDirtyGate.MarkForElementState failed: " + ex.Message); }
        }

        private static object ElementFaction(object element)
        {
            if (element == null) return null;
            if (_elementFactionProp == null || _elementFactionProp.DeclaringType == null
                || !_elementFactionProp.DeclaringType.IsInstanceOfType(element))
                _elementFactionProp = AccessTools.Property(element.GetType(), "Faction");
            return _elementFactionProp?.GetValue(element, null);
        }

        private static object ElementState(object element)
        {
            if (element == null) return null;
            if (_elementStateProp == null || _elementStateProp.DeclaringType == null
                || !_elementStateProp.DeclaringType.IsInstanceOfType(element))
                _elementStateProp = AccessTools.Property(element.GetType(), "State");
            return _elementStateProp?.GetValue(element, null);
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
    /// <c>ResearchElement.State</c> setter (ResearchElement.cs:195) — catches DIRECT state writes that bypass
    /// every GiveResearch/complete method (incl. the diplomacy grant's State write when an element is not yet
    /// Unlocked). Change-gated + faction-gated in the shared helper (the setter fires <c>OnStateChanged</c> even
    /// for equal-value writes). Prefix so the getter still reads the OLD state; void prefix — never suppresses
    /// the real setter. The diplomacy grant's progress bump on an ALREADY-Unlocked element skips the setter
    /// entirely (GeoPhoenixFaction.cs:1004 guard) and is covered by <see cref="DiplomacyResearchGrantDirtyPatch"/>.
    /// Reflective target (Prepare false → PatchAll skips).
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

        // __instance = the ResearchElement; value = the incoming ResearchState (boxed enum).
        public static void Prefix(object __instance, object value)
            => ResearchDirtyGate.MarkForElementState(__instance, value);
    }

    /// <summary>
    /// <c>GeoPhoenixFaction.CheckForSharedResearchesOnDiplomacyChange(GeoFaction, PartyDiplomacyState, int, int)</c>
    /// (GeoPhoenixFaction.cs:952) — the diplomacy-threshold research grant. It writes <c>State</c> only when NOT
    /// already Unlocked (:1004-1006) but bumps <c>ResearchProgress</c> UNCONDITIONALLY (:1009, a plain field —
    /// not Harmony-patchable), so an already-Unlocked element's progress bump reaches NO setter and the
    /// State-setter hook misses it. This method-level postfix is the root-cause hook: one ch#2 dirty mark per
    /// grant regardless of the per-element branch. The method mutates only this Phoenix faction's own Research,
    /// so no faction gate. Bound by NAME only (single method, no overload — private is fine). Reflective target
    /// (Prepare false → PatchAll skips).
    /// </summary>
    [HarmonyPatch]
    public static class DiplomacyResearchGrantDirtyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            if (t == null) return false;
            _target = AccessTools.Method(t, "CheckForSharedResearchesOnDiplomacyChange");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix() => ResearchDirtyGate.MarkPhoenix();
    }
}
