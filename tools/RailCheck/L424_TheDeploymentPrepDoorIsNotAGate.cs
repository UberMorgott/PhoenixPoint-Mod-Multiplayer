using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L424 — THE DEPLOYMENT-PREP DOOR IS A DOOR, NEVER A GATE.
    ///
    /// The mission-start window is unicast to the dispatching peer (<c>EventPopup</c>:1896-1916), so every
    /// other peer's only entrance into <c>UIStateRosterDeployment</c> is the persistent geoscape button
    /// driven by <see cref="DeployPrep.ShowsButton"/>. That is exactly the shape a quorum grows out of —
    /// "wait until the others have joined too" is one extra boolean away — and the mandate (P13, and the
    /// arms L84/L114 already stand on) is that it must never appear.
    ///
    /// ARMS
    ///   (a) <c>door-became-a-gate</c> — <see cref="DeployPrep.ShowsButton"/> must be a function of exactly
    ///       three LOCAL facts: this peer is in a session, a site was announced, and THIS peer's geoscape is
    ///       idle. Arity is the check: a fourth parameter is where "…and peer N is ready" would have to go.
    ///   (b) <c>door-never-opens</c> — the truth table. All three true shows it; each one alone false hides
    ///       it. A door that cannot open is the pre-2026-08-12 bug (no entrance at all) coming back.
    ///   (c) <c>withdrawal-steals-the-door</c> — <see cref="DeployPrep.ClearsOnWithdraw"/> must clear only
    ///       for the peer whose own announcement is still the live one, and never on an empty announcement.
    ///       Without it a stale close from a superseded screen takes somebody else's door away.
    ///   (d) <c>root-unregistered</c> — <c>"M#prep"</c> must still be the registered mod root, because the
    ///       whole mechanism is that one replicated string and nothing else.
    ///
    /// Falsify: add a "everyone else is ready" parameter to <c>ShowsButton</c> → (a); invert any term → (b);
    /// make <c>ClearsOnWithdraw</c> return true unconditionally → (c); rename the root → (d).
    /// </summary>
    internal static class L424_TheDeploymentPrepDoorIsNotAGate
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var shows = typeof(DeployPrep).GetMethod("ShowsButton", All);
            var clears = typeof(DeployPrep).GetMethod("ClearsOnWithdraw", All);
            var root = typeof(DeployPrep).GetField("RootKey", All);
            if (shows == null || clears == null || root == null)
            {
                yield return "L424 premise-changed: DeployPrep.ShowsButton / ClearsOnWithdraw / RootKey no " +
                             "longer resolve. Those three ARE the join door — re-point this law at whatever " +
                             "carries them now; do not delete it, because the door is one boolean away from " +
                             "being the launch quorum P13 forbids.";
                yield break;
            }

            // ── (a) arity ───────────────────────────────────────────────────────────────────────
            var ps = shows.GetParameters();
            if (ps.Length != 3 || ps[0].ParameterType != typeof(bool) ||
                ps[1].ParameterType != typeof(string) || ps[2].ParameterType != typeof(bool))
                yield return "L424 door-became-a-gate: DeployPrep.ShowsButton no longer takes exactly " +
                             "(bool inSession, string siteRef, bool idleGeoscape). A fourth term is where " +
                             "'…and the other peers have joined/acknowledged' would live, and that is the " +
                             "launch quorum P13 forbids and L84/L114 already keep out.";

            // ── (b) truth table ─────────────────────────────────────────────────────────────────
            if (!DeployPrep.ShowsButton(true, "S#7", true))
                yield return "L424 door-never-opens: an announced site + an idle geoscape + a live session " +
                             "does NOT show the button, so a peer that was never sent the unicast mission " +
                             "window has no entrance into UIStateRosterDeployment at all — the exact defect " +
                             "this door was added for.";
            if (DeployPrep.ShowsButton(false, "S#7", true))
                yield return "L424 door-never-opens: the button shows outside a co-op session, where the " +
                             "mod root it reads is stale by construction.";
            if (DeployPrep.ShowsButton(true, "", true) || DeployPrep.ShowsButton(true, null, true))
                yield return "L424 door-never-opens: the button shows with NO announced site, so its click " +
                             "would resolve nothing (DeployPrep.Join's 'no ActiveMission there' arm) and the " +
                             "geoscape carries a permanent dead offer.";
            if (DeployPrep.ShowsButton(true, "S#7", false))
                yield return "L424 door-never-opens: the button shows off the bare idle geoscape " +
                             "(GeoscapeView.CurrentViewState is UIStateNothingSelected), i.e. over popups, " +
                             "modals, submenus and the deployment screen itself.";

            // ── (c) whose announcement may be withdrawn ─────────────────────────────────────────
            if (!DeployPrep.ClearsOnWithdraw("S#7", "S#7"))
                yield return "L424 withdrawal-steals-the-door: a peer leaving the prep screen it announced " +
                             "no longer clears the root, so the join button outlives the window it points at " +
                             "and every click lands on a mission that is gone.";
            if (DeployPrep.ClearsOnWithdraw("S#7", "S#9") || DeployPrep.ClearsOnWithdraw("", "S#9") ||
                DeployPrep.ClearsOnWithdraw(null, "S#9"))
                yield return "L424 withdrawal-steals-the-door: a withdrawal clears an announcement that is " +
                             "not its own, so one peer closing a superseded screen takes the live door away " +
                             "from everybody else.";

            // ── (d) the root ────────────────────────────────────────────────────────────────────
            if (!string.Equals(root.GetValue(null) as string, "M#prep", StringComparison.Ordinal))
                yield return "L424 root-unregistered: DeployPrep.RootKey is no longer \"M#prep\". The whole " +
                             "mechanism is that one replicated string on the 0xAC value rail; a renamed root " +
                             "is a root neither peer mirrors and the button never appears on anybody.";
        }
    }
}
