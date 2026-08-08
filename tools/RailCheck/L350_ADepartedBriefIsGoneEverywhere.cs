using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L350 — A MISSION BRIEF WHOSE AIRCRAFT LEFT THE SITE IS GONE ON EVERY PEER, CURRENT OR QUEUED.
    ///
    /// A brief is the squad screen's own front door: <c>ModalResultCallback</c>:825 → <c>LaunchMission</c>
    /// :1043 → <c>ToDeploymentState</c>. So a brief for a mission this peer has no container left to deploy
    /// from is exactly as dead as the screen behind it (L354) — its Confirm can only open an empty screen with
    /// a START MISSION button that <c>CheckForDeployment</c>:371 leaves permanently dead.
    ///
    /// BOTH POSITIONS, because a window in this repo lives in two places. A CURRENT one is what the queue is
    /// showing (<c>_currentStateSwitchRequest</c>); a QUEUED one is in <c>_viewStateSwitchRequests</c> and no
    /// repaint path ever looks at it — measured, host 22:04:10: <c>SKIP re-enter of queued window
    /// UIStateRosterDeployment</c>. Closing only the current one merely DEFERS the empty window to the moment
    /// the player leaves whatever screen was holding it.
    ///
    /// AND NEVER BY CANCELLING. <c>UIStateRosterDeployment.ToPreviousScreen</c>:256-268 — the obvious "close
    /// it" — begins with <c>_mission.Cancel()</c>, which is precisely the whole-team deletion L351 is about.
    /// The close here is <c>FinishQueriedState</c>:2164 (+ <c>ResetViewState</c>:414 for the screen), the
    /// game's own pair minus that first line. Going through <c>FinishQueriedState</c> rather than poking the
    /// stack is also what keeps <c>L93</c> arm C green.
    ///
    /// ARMS
    ///   (a) <c>brief-outlives-its-aircraft</c> — <c>CloseDepartedBrief</c> must ask <c>MissionBehind</c> +
    ///       <c>Servable</c> and close.
    ///   (b) <c>queued-brief-survives</c> — <c>DropUnservableQueued</c> must ask the same pair AND edit the
    ///       game's own pending list, and <c>OpenUiRepaint</c> must actually reach it.
    ///   (c) <c>close-cancels-the-mission</c> — the close path must reach <c>FinishQueriedState</c> and must
    ///       NOT reach <c>ToPreviousScreen</c> or <c>Cancel</c>. This is the arm that keeps the fix from
    ///       becoming the bug it sits next to.
    ///   (d) <c>every-modal-closable</c>, THE NARROWNESS — <c>MissionBehind</c> answers null for anything that
    ///       is not a squad screen or a per-peer brief/outcome, so no other window in the modal family can be
    ///       closed by this rule.
    ///   (e) <c>modal-arm-not-wired</c> — the <c>UIStateGeoModal</c> entry in <c>UiNativeRepaint.Table</c> must
    ///       reach <c>CloseDepartedBrief</c>; a queued modal is skipped by the Exit+Enter fallback, so the
    ///       table arm is the only place a CURRENT brief is ever re-examined.
    ///
    /// Falsify: delete the <c>Servable</c> test from <c>CloseDepartedBrief</c> → (a); delete the
    /// <c>DropUnservableQueued</c> call from <c>OpenUiRepaint</c> → (b); make <c>CloseCurrent</c> call
    /// <c>ToPreviousScreen</c> → (c); make <c>MissionBehind</c> return any modal's data → (d).
    /// </summary>
    internal static class L350_ADepartedBriefIsGoneEverywhere
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var close = typeof(DeploymentWindowClose);
            var mod = close.Assembly;
            var brief = close.GetMethod("CloseDepartedBrief", All);
            var queued = close.GetMethod("DropUnservableQueued", All);
            var closer = close.GetMethod("CloseCurrent", All);
            var behind = close.GetMethod("MissionBehind", All);
            var flush = typeof(OpenUiRepaint).GetMethod("RepaintOpenGeoscapeScreen", All);
            if (brief == null || queued == null || closer == null || behind == null || flush == null)
            {
                yield return "L350 premise-changed: DeploymentWindowClose.CloseDepartedBrief / " +
                             "DropUnservableQueued / CloseCurrent / MissionBehind or " +
                             "OpenUiRepaint.RepaintOpenGeoscapeScreen no longer resolve whole. Re-point this law " +
                             "at whatever carries them; do not delete it — a brief that outlives its aircraft " +
                             "leads to a squad screen nobody can deploy from, on every peer.";
                yield break;
            }

            // ── (a) the CURRENT brief ───────────────────────────────────────────────────────────────
            var briefCalls = Program.Callees(brief, mod).Where(c => c != null).Select(c => c.Name).ToList();
            foreach (var needed in new[] { "MissionBehind", "Servable", "CloseCurrent" })
                if (!briefCalls.Contains(needed))
                    yield return "L350 brief-outlives-its-aircraft: CloseDepartedBrief no longer calls " + needed +
                                 ", so a mission brief stays up on a peer whose last deployment container has " +
                                 "gone. Its Confirm opens an empty squad screen (L354) and the player is left " +
                                 "with a window that can only mislead.";

            // ── (b) the QUEUED brief, and the sweep is actually reached ─────────────────────────────
            var queuedCalls = Program.Callees(queued, mod).Where(c => c != null).Select(c => c.Name).ToList();
            foreach (var needed in new[] { "MissionBehind", "Servable" })
                if (!queuedCalls.Contains(needed))
                    yield return "L350 queued-brief-survives: DropUnservableQueued no longer calls " + needed +
                                 ". A window that is not CURRENT is invisible to every repaint path (measured: " +
                                 "'SKIP re-enter of queued window UIStateRosterDeployment', host 22:04:10), so " +
                                 "without this half the empty window is merely DEFERRED to the moment the player " +
                                 "leaves the screen that was holding it.";
            if (!CalleeNames(queued).Contains("RemoveAt"))
                yield return "L350 queued-brief-survives: DropUnservableQueued no longer removes anything from " +
                             "the game's own pending list, so it reads the queue and changes nothing.";
            if (!Program.Callees(flush, mod).Any(c => c != null && c.Name == "DropUnservableQueued"))
                yield return "L350 sweep-never-runs: OpenUiRepaint's flush no longer calls DropUnservableQueued, " +
                             "so the queued half is proved about code nothing executes. The sweep must sit " +
                             "OUTSIDE the open-screen question — a queued window is not `current`, so the " +
                             "per-screen table below it can never see one.";

            // ── (c) the close is not a cancellation ─────────────────────────────────────────────────
            var closerCalls = CalleeNames(closer);
            if (!closerCalls.Contains("FinishQueriedState"))
                yield return "L350 close-bypasses-the-queue: CloseCurrent no longer calls FinishQueriedState:2164. " +
                             "Popping the state stack directly leaves _currentStateSwitchRequest set, which wedges " +
                             "the window queue for the rest of the session and turns L93 arm C red.";
            foreach (var forbidden in new[] { "ToPreviousScreen", "Cancel" })
                if (closerCalls.Contains(forbidden))
                    yield return "L350 close-cancels-the-mission: CloseCurrent reaches " + forbidden + ". " +
                                 "UIStateRosterDeployment.ToPreviousScreen:256-268 begins with _mission.Cancel(), " +
                                 "and GeoMission.Cancel:253-265 nulls Site.ActiveMission and can DestroySite — so " +
                                 "\"this peer has no aircraft here any more\" would delete the mission for the " +
                                 "WHOLE TEAM. That is L351's bug, arriving through L350's fix.";

            // ── (d) NARROWNESS: nothing else is closable by this rule ───────────────────────────────
            if (DeploymentWindowClose.MissionBehind(null) != null ||
                DeploymentWindowClose.MissionBehind(new object()) != null)
                yield return "L350 every-modal-closable: MissionBehind answers with a mission for a state that is " +
                             "neither the squad screen nor a per-peer brief/outcome. The sweep would then drop " +
                             "research-complete notifications, ability confirmations and the event picker out of " +
                             "the queue whenever an aircraft moved.";

            // ── (e) the modal table arm is wired ────────────────────────────────────────────────────
            if (!UiNativeRepaint.Table.TryGetValue(typeof(UIStateGeoModal), out var entry) || entry == null ||
                !Program.Callees(entry.Method, mod).Any(c => c != null && c.Name == "CloseDepartedBrief"))
                yield return "L350 modal-arm-not-wired: the UIStateGeoModal arm of UiNativeRepaint.Table does not " +
                             "reach CloseDepartedBrief. A modal holds the current queued slot, so the Exit+Enter " +
                             "fallback skips it (OpenUiRepaint:586) — that table arm at :554 is the ONLY place a " +
                             "brief already on screen is ever re-examined.";
        }

        private static HashSet<string> CalleeNames(MethodBase caller)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return names;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                try { names.Add(caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)).Name); } catch { }
            }
            return names;
        }
    }
}
