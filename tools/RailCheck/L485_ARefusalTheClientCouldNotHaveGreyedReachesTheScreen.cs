using System.Collections.Generic;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L485 — A REFUSAL THE CLIENT'S OWN UI COULD NOT HAVE GREYED IS PUT ON THAT PLAYER'S SCREEN.
    ///
    /// L123 arm (g) already holds the CALL SITES: which methods are allowed to ask
    /// <c>IntentRail.Reject</c> for a popup at all. What it cannot see is the arm-by-arm decision INSIDE
    /// those handlers — both compute the notify bit per refusal, and a widening (notify everything) or a
    /// narrowing (drop one arm) is invisible to a call-site allowlist. This law executes that decision.
    ///
    /// The criterion is L123's own, and it is not "the host refused" — it is "vanilla had no control to
    /// grey on the peer that clicked":
    ///   • <c>VehicleSync.AlreadyExploringReason</c> — <c>ExploreSiteAbility.GetDisabledStateInternal</c>:24
    ///     tests ONLY <c>Units.Any()</c>; "already exploring" is enforced nowhere but
    ///     <c>ActivateInternal</c>:12's silent <c>if (!IsExploringSite)</c>. The button stays LIT. Single
    ///     player survives that because the player is watching his own spinner; a client whose mirror says
    ///     NOT exploring has neither spinner nor word (live 2026-08-14: five refused explore clicks on one
    ///     aircraft, 21:44:12 → 22:09:42).
    ///   • <c>VehicleSync.NotExplorableReason</c> — the same click one step later, after the host finished
    ///     and the site's inspected state has not landed here yet (same session, 22:19:01).
    ///   • <c>TacticalCommandSync.TargetNotOfferedRefusal</c> — the client's targeting UI drew that target
    ///     because the client's OWN <c>GetTargets()</c> published it, so that peer had nothing greyed. The
    ///     refusal exists only because the two boards disagree, which single player cannot produce
    ///     (same session, 21:40:34 and 21:41:15).
    ///   • <c>TacticalCommandSync.BusyRefusal</c> — L146's two-commanders race, notifying since L123.
    ///
    /// And the negative half matters as much: every refusal vanilla DOES express by disabling the control
    /// — no crew, nothing that can explore, not docked, cannot redirect, already parked, the game's own
    /// ability gate, AP, WP — must still cross QUIETLY. An error box per click for those is a worse bug
    /// than the silence this family replaced.
    ///
    /// SEMANTIC KILLS (src/, compile-valid):
    ///   • <c>VehicleSync.ShouldNotify</c> → <c>false</c>        → vehicle-refusal-never-reaches-the-player
    ///   • <c>VehicleSync.ShouldNotify</c> → <c>why != null</c>  → vehicle-modal-for-a-greyed-refusal
    ///   • drop <c>TargetNotOfferedRefusal</c> from <c>TacticalCommandSync.ShouldNotify</c>
    ///                                                          → command-refusal-never-reaches-the-player
    ///   • <c>TacticalCommandSync.ShouldNotify</c> → <c>refusal != null</c>
    ///                                                          → command-modal-for-a-greyed-refusal
    /// </summary>
    internal static class L485_ARefusalTheClientCouldNotHaveGreyedReachesTheScreen
    {
        internal static IEnumerable<string> Check()
        {
            // ═══ (a) The vehicle surface: the REAL validator decides, the REAL predicate answers ═══
            // Driving Validate rather than naming the consts is the point — the identity test in
            // HandleExploreSite only fires if the validator hands back the const ITSELF, so a well-meaning
            // edit that rebuilds the sentence inline would silently kill every explore popup.
            var alreadyExploring = VehicleSync.Validate(VehicleSync.OpExploreSite, Explorable(exploring: true));
            var notExplorable = VehicleSync.Validate(VehicleSync.OpExploreSite, Explorable(explorable: false));
            if (!ReferenceEquals(alreadyExploring, VehicleSync.AlreadyExploringReason) ||
                !ReferenceEquals(notExplorable, VehicleSync.NotExplorableReason))
                yield return "L485 explore-refusal-lost-its-identity: VehicleSync.Validate no longer returns " +
                             "the named const for the already-exploring / not-explorable arms, so the " +
                             "handler's ReferenceEquals can never fire and the popup is unreachable code";

            if (!VehicleSync.ShouldNotify(alreadyExploring) || !VehicleSync.ShouldNotify(notExplorable))
                yield return "L485 vehicle-refusal-never-reaches-the-player: an explore click the client's " +
                             "own UI never greyed (ExploreSiteAbility.GetDisabledStateInternal:24 tests only " +
                             "Units.Any()) is refused into the host's log alone — the player sees a live " +
                             "button do nothing, which is the bug this arm exists to end";

            // The quiet half. Each of these IS greyed by the game's own gate on the clicking peer.
            foreach (var quiet in new[]
            {
                VehicleSync.Validate(VehicleSync.OpExploreSite, Explorable(crew: false)),
                VehicleSync.Validate(VehicleSync.OpExploreSite, Explorable(canExplore: false)),
                VehicleSync.Validate(VehicleSync.OpSetEquipment, new VehicleSync.Facts
                                     { Resolved = true, OwnedByPlayer = true, Docked = false }),
                VehicleSync.Validate(VehicleSync.OpTravelTo, new VehicleSync.Facts
                                     { Resolved = true, OwnedByPlayer = true, TargetResolved = true,
                                       CanRedirect = true, TargetIsIdleCurrentSite = true }),
            })
            {
                if (quiet == null)
                    yield return "L485 control-not-red: a case built to be REFUSED was accepted, so the " +
                                 "quiet-half assertions below prove nothing";
                else if (VehicleSync.ShouldNotify(quiet))
                    yield return "L485 vehicle-modal-for-a-greyed-refusal: '" + quiet + "' now pops a box. " +
                                 "Vanilla already says this one by disabling the control, so the box is the " +
                                 "second word for a refusal the player was told about the first time";
            }

            // ═══ (b) The tactical command surface ═══
            var notOffered = TacticalCommandSync.Validate(true, true, true, true, true, true, false, null,
                                                          targetIsOffered: false, actionPoints: 4f,
                                                          actionPointCost: 0f, willPoints: 4f, willPointCost: 0f);
            if (!ReferenceEquals(notOffered, TacticalCommandSync.TargetNotOfferedRefusal))
                yield return "L485 target-refusal-lost-its-identity: TacticalCommandSync.Validate no longer " +
                             "returns the named const for the target-not-offered arm";
            if (!TacticalCommandSync.ShouldNotify(notOffered) ||
                !TacticalCommandSync.ShouldNotify(TacticalCommandSync.BusyRefusal))
                yield return "L485 command-refusal-never-reaches-the-player: the client's own GetTargets() " +
                             "published that target, so nothing was greyed on the peer that clicked — the " +
                             "order died into the host's log while the player watched a wind-up and a cancel";

            // The quiet half: the game's OWN gate, AP and WP all grey their controls before the click.
            var gated = TacticalCommandSync.Validate(true, true, true, true, true, true, false,
                                                     "NoValidTarget", true, 4f, 0f, 4f, 0f);
            var noAp = TacticalCommandSync.Validate(true, true, true, true, true, true, false, null,
                                                    true, 0f, 2f, 4f, 0f);
            var noWp = TacticalCommandSync.Validate(true, true, true, true, true, true, false, null,
                                                    true, 4f, 0f, 0f, 2f);
            foreach (var quiet in new[] { gated, noAp, noWp })
            {
                if (quiet == null)
                    yield return "L485 control-not-red: a command case built to be REFUSED was accepted";
                else if (TacticalCommandSync.ShouldNotify(quiet))
                    yield return "L485 command-modal-for-a-greyed-refusal: '" + quiet + "' now pops a box for " +
                                 "a refusal the game itself expresses by disabling the ability";
            }

            // POSITIVE CONTROL: the validators must still ACCEPT a clean case, or every arm above is
            // asserting against a function that refuses unconditionally.
            if (VehicleSync.Validate(VehicleSync.OpExploreSite, Explorable()) != null ||
                TacticalCommandSync.Validate(true, true, true, true, true, true, false, null,
                                             true, 4f, 0f, 4f, 0f) != null)
                yield return "L485 control-not-green: a legal order is refused, so the refusal arms above " +
                             "cannot distinguish their own case from a validator that says no to everything";
        }

        /// <summary>A vehicle parked at an explorable site with a crew — legal by default, one flag flipped
        /// per case so each arm is reached for exactly the reason it names.</summary>
        private static VehicleSync.Facts Explorable(bool explorable = true, bool canExplore = true,
                                                    bool crew = true, bool exploring = false) =>
            new VehicleSync.Facts
            {
                Resolved = true,
                OwnedByPlayer = true,
                SiteExplorable = explorable,
                CanExploreSites = canExplore,
                HasCrew = crew,
                AlreadyExploring = exploring,
            };
    }
}
