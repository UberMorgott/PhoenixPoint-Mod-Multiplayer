using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT-ONLY mirror of the host's NATIVE "new research available" nav line on the mirrored
    /// GeoResearchComplete popup (the observed HOST/CLIENT button inversion, soak 2026-07-05).
    ///
    /// The native bind — <c>GeoReseatchCompleteDataBind.ModalShowHandler(UIModal)</c>
    /// (GeoReseatchCompleteDataBind.cs:90-140) — toggles <c>NewResearchesGroup</c> by RECOMPUTING
    /// <c>ResearchElement.UnlocksResearches</c> at show time. That recompute is only correct against the
    /// AUTHORITATIVE sim: the client's mirrored research state deliberately skips the native completion
    /// cascade (no CheckInvalidates, no requirement-progress sync — see <see cref="ResearchNavMirror"/> doc),
    /// so the client's popup can show a line the host's native popup doesn't have (or vice versa).
    ///
    /// Fix: a Postfix on the public bind entry that, ONLY on a client in an active session AND only when the
    /// host's broadcast armed a definite answer for THIS researchId (<see cref="ResearchNavMirror.TryConsume"/>,
    /// one-shot, keyed by id so queued popups can't cross-wire), forces <c>NewResearchesGroup.SetActive</c> to
    /// the host's native value. HOST: <see cref="ResearchNavMirror.ShouldOverride"/> is false by construction —
    /// the host's window stays byte-for-byte native (S1 host-transparency invariant; unit-pinned). No pending
    /// flag (legacy payload / read miss / non-mirrored popup) → bind stays fully native (fail-open).
    /// <c>ClientResearchNavigatePatch</c> (the line's click → ToResearchState nav) is untouched.
    ///
    /// Verified against the decompile (2026-07-05):
    ///   • <c>GeoReseatchCompleteDataBind.ModalShowHandler(UIModal)</c> public, 1 param — the parameterless
    ///     private overload (OnModalShow subscriber) delegates here, so ONE patch covers both entries.
    ///   • public field <c>GameObject NewResearchesGroup</c> (GeoReseatchCompleteDataBind.cs:48).
    ///   • <c>UIModal.Data</c> public property (UIModal.cs:17) → GeoResearchCompleteData → ResearchID via
    ///     <see cref="ReportModalReflection.ReadResearchCompleteId"/>.
    /// </summary>
    [HarmonyPatch]
    public static class ResearchNavGroupMirrorPatch
    {
        private static MethodBase _target;
        private static FieldInfo _newResearchesGroupField;   // GeoReseatchCompleteDataBind.NewResearchesGroup
        private static PropertyInfo _modalDataProp;          // UIModal.Data

        public static bool Prepare()
        {
            var bindT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewControllers.Modal.GeoReseatchCompleteDataBind");
            var modalT = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewControllers.UIModal");
            if (bindT == null || modalT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): ModalShowHandler(UIModal).
            _target = AccessTools.Method(bindT, "ModalShowHandler", new[] { modalT });
            _newResearchesGroupField = AccessTools.Field(bindT, "NewResearchesGroup");
            _modalDataProp = AccessTools.Property(modalT, "Data");
            return _target != null && _newResearchesGroupField != null && _modalDataProp != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the GeoReseatchCompleteDataBind; __0 = the UIModal whose Data was just bound.
        public static void Postfix(object __instance, object __0)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                bool isHost = engine != null && engine.IsHost;
                bool isActive = engine != null && engine.IsActiveSession;
                if (isHost || !isActive) return;   // host/native popup: never touched (cheap pre-gate)

                var researchId = ReportModalReflection.ReadResearchCompleteId(_modalDataProp.GetValue(__0, null));
                if (!ResearchNavMirror.TryConsume(researchId, out var navFlag)) return;   // no host answer → native
                if (!ResearchNavMirror.ShouldOverride(isHost, isActive, navFlag)) return;

                if (!(_newResearchesGroupField.GetValue(__instance) is GameObject group)) return;
                bool visible = ResearchNavMirror.NavVisible(navFlag);
                group.SetActive(visible);
                Debug.Log("[Multiplayer] ResearchNavGroupMirrorPatch: researchId=" + researchId +
                          " → NewResearchesGroup forced to host-native " + (visible ? "SHOWN" : "HIDDEN"));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ResearchNavGroupMirrorPatch failed: " + ex.Message); }
        }
    }
}
