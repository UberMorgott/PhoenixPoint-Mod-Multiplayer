using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

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
    ///   (e) <c>root-never-crosses</c> — <c>DeployPrepState</c> must CLASSIFY with at least one rail field.
    ///       Registering a mod root is only half the contract (<c>IdentityResolver.RegisterModRoot</c>): the
    ///       state class must carry <c>[SerializeType(SerializeAll)]</c> or <c>RailMeta.HasPersistentMembers</c>
    ///       is false, <c>RailType.Source</c> is "none", the walk emits nothing (DiffEngine.cs:1113-1117) and
    ///       the announcement never leaves the host. It shipped that way; arms (a)-(d) all passed green over
    ///       a root no client could ever receive, which is why this arm executes the classifier instead of
    ///       reading the source.
    ///   (f) <c>door-only-on-the-bare-globe</c> — <see cref="DeployPrep.IdleGeoscape"/> must accept BOTH map
    ///       states and reject screens. The 2026-08-13 session: the root was published and live for seven
    ///       minutes while every peer sat in <c>UIStateVehicleSelected</c> (the aircraft the deployment is
    ///       about is what the player selects) and the gate only knew <c>UIStateNothingSelected</c>, so the
    ///       button never appeared once. Both halves are pinned: a narrower set is that bug, a wider one
    ///       hangs the offer over tabs, rosters and event windows.
    ///   (g) <c>reoffer-cancels-a-taken-start</c> — <see cref="DeployPrep.ReofferIsRedundant"/> must be true
    ///       exactly when the live door serves the SAME site. It is what stops the site's encounter row from
    ///       cancelling a mission whose start is already taken and re-raising a fresh START/CANCEL dialog on
    ///       peers that then get "another player already answered this event"; and it must be FALSE for a
    ///       different site or an empty door, or an ordinary re-offer becomes unreachable.
    ///
    /// Falsify: add a "everyone else is ready" parameter to <c>ShowsButton</c> → (a); invert any term → (b);
    /// make <c>ClearsOnWithdraw</c> return true unconditionally → (c); rename the root → (d); delete the
    /// <c>[SerializeType]</c> attribute on <c>DeployPrepState</c> → (e); drop <c>UIStateVehicleSelected</c>
    /// from <c>IdleGeoscape</c> → (f); make <c>ReofferIsRedundant</c> return true unconditionally → (g).
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
            var idle = typeof(DeployPrep).GetMethod("IdleGeoscape", All);
            var redundant = typeof(DeployPrep).GetMethod("ReofferIsRedundant", All);
            if (shows == null || clears == null || root == null || idle == null || redundant == null)
            {
                yield return "L424 premise-changed: DeployPrep.ShowsButton / ClearsOnWithdraw / RootKey / " +
                             "IdleGeoscape / ReofferIsRedundant no " +
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

            // ── (e) the root actually classifies ────────────────────────────────────────────────
            var rt = RailType.Get(typeof(DeployPrepState));
            if (rt == null || rt.Fields.Count == 0)
                yield return "L424 root-never-crosses: DeployPrepState classifies with " +
                             (rt == null ? "no RailType at all" : "ZERO rail fields (Source=" + rt.Source + ")") +
                             ". Registering \"M#prep\" is only half the mod-root contract — without " +
                             "[SerializeType(SerializeMembersByDefault = SerializeAll)] the walk emits nothing " +
                             "for it (DiffEngine.cs:1113-1117), the host's announcement never leaves the host, " +
                             "and every client's join button reads an empty siteRef forever.";

            // ── (f) which view states are the bare globe ────────────────────────────────────────
            if (!DeployPrep.IdleGeoscape(typeof(UIStateNothingSelected)) ||
                !DeployPrep.IdleGeoscape(typeof(UIStateVehicleSelected)))
                yield return "L424 door-only-on-the-bare-globe: DeployPrep.IdleGeoscape no longer accepts both " +
                             "map states. UIStateVehicleSelected is the one the player is actually in while a " +
                             "deployment is announced — the aircraft it is about is what he has selected — and " +
                             "excluding it is the 2026-08-13 defect where a correctly published root left the " +
                             "button invisible for seven minutes.";
            if (DeployPrep.IdleGeoscape(null) ||
                DeployPrep.IdleGeoscape(typeof(UIStateGeoscapeEvent)) ||
                DeployPrep.IdleGeoscape(typeof(UIStateRosterDeployment)) ||
                DeployPrep.IdleGeoscape(typeof(UIStateGeoRoster)) ||
                DeployPrep.IdleGeoscape(typeof(UIStateViewVehicle)))
                yield return "L424 door-only-on-the-bare-globe: DeployPrep.IdleGeoscape accepts a state that " +
                             "is a SCREEN (event window, deployment roster, geoscape roster, vehicle view). " +
                             "The offer would hang over tabs, panels and the very screen it opens.";

            // ── (g) a re-offer must not cancel a start somebody already took ────────────────────
            if (!DeployPrep.ReofferIsRedundant("S#7", "S#7"))
                yield return "L424 reoffer-cancels-a-taken-start: the site's own encounter row is no longer " +
                             "answered by the live door, so it runs the game's re-offer instead — which " +
                             "cancels the live mission, resets the shared record and re-raises a fresh " +
                             "START/CANCEL dialog on peers whose start has already been taken. That is the " +
                             "\"another player already answered this event\" reject, in the seam that causes it.";
            if (DeployPrep.ReofferIsRedundant("S#9", "S#7") || DeployPrep.ReofferIsRedundant("", "S#7") ||
                DeployPrep.ReofferIsRedundant(null, "S#7") || DeployPrep.ReofferIsRedundant("S#7", "") ||
                DeployPrep.ReofferIsRedundant("S#7", null))
                yield return "L424 reoffer-cancels-a-taken-start: a re-offer is refused with NO live door for " +
                             "that site, so a declined offer can never be re-opened at all and the mission is " +
                             "unreachable — a blocker (P13), which is the opposite of what this arm is for.";
        }
    }
}
