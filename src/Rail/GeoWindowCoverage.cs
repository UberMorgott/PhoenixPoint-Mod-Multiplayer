using System;
using System.Collections.Generic;
using HarmonyLib;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>How a window the GAME pushes at the player relates to the rail.</summary>
    internal enum WindowSync
    {
        /// <summary>Replicated host→client with its own payload; both peers get the window.</summary>
        Mirrored,
        /// <summary>Deliberately NOT replicated, and that is correct — per-peer presentation, a local
        /// navigation gesture, or a decision that belongs to one peer alone. Silent by design.</summary>
        LocalOnly,
        /// <summary>A known, reviewed HOLE: this window SHOULD reach the other peer and does not yet.
        /// Announced once per type per session — a gap the player can see must never be a gap only the
        /// player can see.</summary>
        Gap,
    }

    internal sealed class WindowRule
    {
        public WindowSync Sync;
        public string Why;
    }

    /// <summary>
    /// COVERAGE for every window the game PUSHES at the player on the geoscape (law 11, universal seam).
    ///
    /// THE CHOKEPOINT. <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>
    /// (GeoscapeViewSwitchQuery.cs:75) is the one queue a pushed window goes through, and every caller in
    /// the shipped game is a method on <c>GeoscapeView</c> itself — nine of them
    /// (:596 deployment, :666 game-over, :675 cutscene, :861 modal-persistent, :881 modal, :1321 asset
    /// deploy, :1617 tutorial, :2062 event window, :2071 replenish). That partition is the useful one and
    /// it is not cosmetic: a state pushed HERE is the game interrupting the player, while a state pushed
    /// through <c>_statesStack.SwitchToState</c> directly (ToGeoRosterState:591, ToVehicleRosterState:602,
    /// SetNothingState:661, OpenModal's own forceOnTop/replaceTop branches:885-893 …) is the LOCAL player
    /// navigating their own geoscape, which must never be replicated.
    ///
    /// It is NOT, however, a single REPLICATION point, and that is the finding that decides this file's
    /// shape. <c>GeoscapeViewStateSwitchRequest</c> carries a LIVE <c>IState</c> instance
    /// (GeoscapeViewStateSwitchRequest.cs:7) and nothing else — no ids, no defs. The states behind it hold
    /// exactly what law 2 addressing cannot reach: <c>UIStateGeoModal</c> is built from a
    /// <c>DialogCallback</c> CLOSURE over the caller's own locals (GeoscapeView.cs:849-852, :1987-1990) plus
    /// an arbitrary <c>object modalData</c> that is a different class per <c>ModalType</c> — 41 of them
    /// (ModalType.cs), from a <c>GeoResearchCompleteData</c> built inline at :1984 to a live
    /// <c>GeoMission</c>, <c>GeoSite</c> or ability context. So "capture at the chokepoint and ship the
    /// request" cannot exist; each kind needs its own payload, exactly as <see cref="EventPopup"/>'s 0xB6
    /// raise does.
    ///
    /// What the chokepoint CAN be — and is, here — is the one place that makes coverage TOTAL and LOUD.
    /// Every kind that reaches the queue must be DECLARED below with a reviewed reason; an undeclared kind
    /// (a game update, a DLC, a mod's own view state) is announced as an ERROR instead of quietly appearing
    /// on one peer's screen and nowhere else, and RailCheck L48 fails the build for it rather than waiting
    /// for someone to notice in a co-op session. That is the mandate's rule applied to windows: a swallow
    /// becomes a falsified law.
    /// </summary>
    internal static class GeoWindowCoverage
    {
        /// <summary>Keyed on the view-state type; a SUBCLASS inherits its base's rule (a mod that derives
        /// from <c>UIStateGeoModal</c> is the same window with the same reason, while a genuinely new
        /// <c>GeoscapeViewState</c> still has to be reviewed). RailCheck L48 asserts this table covers every
        /// type the game can actually queue, and that it holds nothing it cannot.</summary>
        internal static readonly Dictionary<Type, WindowRule> Declared = new Dictionary<Type, WindowRule>
        {
            [typeof(UIStateGeoscapeEvent)] = new WindowRule
            {
                Sync = WindowSync.Mirrored,
                Why = "the event picker — captured at GeoscapeView.OnGeoscapeEventRaised:2034 (EventRaiseBroadcast) " +
                      "and shipped as surface 0xB6 with the site/vehicle root refs, the host-resolved texts and " +
                      "the host's own queue priority; answers ride back as the 0xB4 intent",
            },
            [typeof(UIStateMarketplaceGeoscapeEvent)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "the marketplace is a LOCAL gesture on either peer (MarketplaceAbility.ActivateInternal:43 → " +
                      "GeoscapeView.ToMarketplace:734-738 calls the view directly) and its offer list is not " +
                      "replicated (docs/rail-baseline.txt:14, GeoMarketplace.MarketplaceOptions EXCLUDED) — " +
                      "mirroring it would open a shop over rows the other peer does not have. EventPopup" +
                      ".HostBroadcast declines it and MarketplaceChoiceClientLock blocks a client purchase",
            },
            [typeof(UIStateRosterDeployment)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "tactical deployment is law 5 quarantine — the mission reaches the other peer through the " +
                      "tactical deploy channel, not as a geoscape window, and the roster picked here is the " +
                      "deploying peer's own",
            },
            [typeof(UIStateGeoscapeTutorial)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "tutorial steps are per-PEER progress, not campaign state (GeoscapeView.OnShowTutorialStep" +
                      ":1614 fires off the local tutorial system) — a peer that already knows the game must not be " +
                      "stopped by the other's tutorial",
            },
            [typeof(UIStateGeoModal)] = new WindowRule
            {
                Sync = WindowSync.Gap,
                Why = "THE BIG ONE, deferred: 41 ModalTypes ride this single state (research complete, mission " +
                      "outcome, haven/alien-base/base-defence briefs and outcomes, faction soldier join, " +
                      "diplomacy…). Each needs its OWN payload — the request carries a DialogCallback closure over " +
                      "the raiser's locals (GeoscapeView.cs:849-852) and an `object modalData` whose class differs " +
                      "per ModalType — so it is a payload FAMILY, not a field, and it is the next window work",
            },
            [typeof(UIStateGeoCutscene)] = new WindowRule
            {
                Sync = WindowSync.Gap,
                Why = "story + game-over cinematics (GeoscapeView.ToCutsceneState:673, ToGameOverState:664). " +
                      "Addressable in principle — a VideoPlaybackSourceDef is a def GUID (law 2) — but the " +
                      "game-over variant also carries an end-of-playback Action closure, and a mirrored cutscene " +
                      "pauses the client while the host's TimeAnchor says otherwise. Deferred with the modal family",
            },
            [typeof(UIStateAssetDeployment)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "\"where does this newly manufactured vehicle / recruited soldier go\" (GeoscapeView" +
                      ".PrepareDeployAsset:1308, self-gated to faction == ViewerFaction). It is a HOST decision on " +
                      "host-owned assets; the placement it produces reaches the client as ordinary value/structural " +
                      "deltas, so mirroring the prompt would ask the client to decide something it cannot apply",
            },
            [typeof(UIStateReplenish)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "the post-mission replenish screen, raised by the RETURNING peer's own UIStateInitial:127 as " +
                      "it comes back from tactical — per-peer arrival UI over an aircraft that peer just flew; the " +
                      "restocking it performs rides the value rail like any other",
            },
        };

        /// <summary>The rule for a state type, inherited from the nearest declared base. Null = undeclared.</summary>
        internal static WindowRule RuleFor(Type stateType)
        {
            for (var t = stateType; t != null; t = t.BaseType)
                if (Declared.TryGetValue(t, out var rule)) return rule;
            return null;
        }

        // Once per TYPE per session: these fire on a queue that runs all game long, and a line the player
        // scrolls past a hundred times is a line nobody reads.
        private static readonly HashSet<Type> _announced = new HashSet<Type>();

        /// <summary>Announce what this queued window means for the OTHER peer — the whole point of the gate.
        /// Silent for a Mirrored or a reviewed LocalOnly kind; loud, once, for a known Gap; louder for a kind
        /// nobody has reviewed at all.</summary>
        internal static void Announce(Type stateType)
        {
            if (stateType == null || !_announced.Add(stateType)) return;
            var rule = RuleFor(stateType);
            if (rule == null)
                Debug.LogError("[MP][windows] UNDECLARED geoscape window '" + stateType.FullName + "' was queued at " +
                               "GeoscapeViewSwitchQuery.QueryStateSwitch — nothing in GeoWindowCoverage.Declared says " +
                               "whether it should reach the other peer, so it is almost certainly showing on ONE " +
                               "screen only. Declare it (Mirrored / LocalOnly / Gap) with a reason; RailCheck L48 " +
                               "fails on it until someone does");
            else if (rule.Sync == WindowSync.Gap)
                Debug.LogWarning("[MP][windows] '" + stateType.Name + "' is a KNOWN un-mirrored window — the other " +
                                 "peer does not get it: " + rule.Why);
        }

        /// <summary>Full session teardown: a rejoin should re-announce, so a gap is visible in the log of the
        /// session it actually happened in.</summary>
        public static void Reset() => _announced.Clear();
    }

    /// <summary>
    /// The coverage gate itself (law 4c, presentation): a POSTFIX on the one queue every PUSHED geoscape
    /// window passes through. It changes nothing — it only makes the answer to "does the other peer see
    /// this?" exist for every window kind, including ones that do not exist yet.
    ///
    /// A postfix, and never a prefix: the window must queue exactly as it always did on both peers whatever
    /// the verdict is. Suppressing an un-mirrored window on the host would hide the host's own game from it
    /// to make two screens match, which is the opposite of the fix.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.QueryStateSwitch))]
    internal static class GeoWindowCoverageGate
    {
        private static void Postfix(GeoscapeViewStateSwitchRequest request)
        {
            if (!EventPopup.InSession) return;   // solo: there is no other peer to be out of sync with
            try { GeoWindowCoverage.Announce(request?.State?.GetType()); }
            catch (Exception ex) { Debug.LogError("[MP][windows] coverage gate threw: " + ex); }
        }
    }
}
