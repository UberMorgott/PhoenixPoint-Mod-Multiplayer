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
    /// CLIENT-ONLY fix for the mirrored research-complete popup's "new research available" clickable line.
    ///
    /// The native line lives inside the GeoResearchComplete modal (<c>GeoReseatchCompleteDataBind</c>, note the
    /// game's misspelled type name). Clicking it runs <c>NewResearchAvailableClicked()</c> which sets
    /// <c>GeoResearchCompleteData.SwitchToResearchState = true</c> then calls <c>_modal.Confirm()</c>. On the HOST,
    /// the modal's real <c>DialogCallback</c> → <c>GeoscapeView.ResearchCompleteModalHandler</c> reads that flag and
    /// calls <c>ToResearchState()</c> (navigates to the research screen). On the CLIENT the modal is a MIRROR built
    /// by <see cref="GeoModalDisplay.Show"/> with a NULL DialogCallback (deliberate — the native callback can also
    /// fire <c>ToCutsceneState</c>, unsafe on a frozen client), so Confirm just closes the window and the click does
    /// nothing (the reported S2 bug: "clicking the line just closes the window instead of opening research").
    ///
    /// Fix: a client-gated Postfix on <c>NewResearchAvailableClicked</c> that, AFTER native (which already closed the
    /// modal), performs ONLY the research-screen navigation — <see cref="GeoModalDisplay.NavigateToResearch"/> →
    /// <c>GeoscapeView.ToResearchState()</c>, the exact entry the Research tab button uses. Pure local UI navigation,
    /// no sim mutation, host-only cutscene path intentionally NOT reproduced. HOST is left fully native (its own
    /// DialogCallback handles the nav) — gated on active co-op session + <c>!IsHost</c>. Best-effort try/catch.
    ///
    /// Verified against the decompile (2026-07-05):
    ///   • <c>PhoenixPoint.Geoscape.View.ViewControllers.Modal.GeoReseatchCompleteDataBind.NewResearchAvailableClicked()</c>
    ///     private, parameterless (GeoReseatchCompleteDataBind.cs:210-214) — sets SwitchToResearchState + Confirm.
    ///   • <c>GeoscapeView.ToResearchState()</c> public, parameterless (GeoscapeView.cs:696-699).
    /// On the client every research-complete modal is a mirror (local raises are suppressed via SuppressEvents), so
    /// this can only ever fire for a mirrored window.
    /// </summary>
    [HarmonyPatch]
    public static class ClientResearchNavigatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var bindT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewControllers.Modal.GeoReseatchCompleteDataBind");
            if (bindT == null) return false;
            _target = AccessTools.Method(bindT, "NewResearchAvailableClicked", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Client: after native closes the modal, navigate to the research screen (the line's native effect).
        public static void Postfix()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return; // host/non-session: native handles nav
                GeoModalDisplay.NavigateToResearch(GeoRuntime.Instance);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientResearchNavigatePatch failed: " + ex.Message); }
        }
    }
}
