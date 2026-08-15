using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;

namespace RailCheck
{
    /// <summary>
    /// L546 — A HOST-MINTED JOURNAL POSITION IS PUBLISHED, AND A DISCARD IS NEVER SILENT.
    ///
    /// THE REPORT (2026-08-15): the host's journal showed pos=1 UIStateGeoModal, pos=2 UIStateGeoModal,
    /// pos=3 UIStateGeoscapeEvent; both clients only ever received pos=3, and the two research-completed
    /// windows never appeared on any client. The seam mints a position for EVERY queued window
    /// (GeoWindowCoverageGate, src/Rail/GeoWindowCoverage.cs), hands it over through
    /// WindowJournal.SetHostPending/TakeHostPending — and GeoModalMirror.HostBroadcast then answered a
    /// non-Mirrored declaration with a bare `return`. The position was destroyed with no line anywhere.
    /// ModalType.GeoResearchComplete was declared LocalOnly on 2026-08-05 because a SECOND, mod-served
    /// producer existed on the client; §A.9/L520 deleted that producer, and nothing noticed that the
    /// declaration was now a claim about a producer that no longer exists.
    ///
    /// THE INVARIANT, IN TWO HALVES, BOTH EXECUTED — no IL is read here:
    ///   (a) every ModalType in GeoWindowCoverage.HostAuthoritativeRaisers — the modals raised ONLY from
    ///       GeoscapeView's own faction/site event subscriptions, which a client's game never runs — is
    ///       DECLARED, and never LocalOnly. Mirrored publishes it; Gap is the ANNOUNCED hole
    ///       (AnnounceModal warns once per session with its reason) and stays legal. LocalOnly is the one
    ///       verdict that is both false and silent: it says "each peer raises its own" about a window no
    ///       peer but the host can raise.
    ///   (b) the real seam, run end to end with no game: mint a position, hand it over, take it back, and
    ///       ask the REAL publish decision (GeoModalMirror.PublishRefusal) with the REAL rule out of the
    ///       REAL declaration table. Mirrored must publish (null). Anything else must return a NON-EMPTY
    ///       reason — the bare `return` this law replaces would come back as a silent null and fail here.
    ///
    /// NOT A "MIRROR EVERYTHING" LAW (arm (c)): a gesture family — ModalType.ActivateBase, raised by the
    /// clicking peer's own ability view — must still be LocalOnly, or arm (a) would be asserting that
    /// every window in the game is replicated and would be green for the wrong reason.
    ///
    /// Falsify (compile-valid src mutations, each named): declare ModalType.GeoResearchComplete
    /// `WindowSync.LocalOnly` again in GeoWindowCoverage.DeclaredModals → (a); make PublishRefusal return
    /// null instead of the reason string (the pre-fix bare `return`) → (b); declare
    /// ModalType.ActivateBase Mirrored → (c); empty HostAuthoritativeRaisers → (d).
    /// </summary>
    internal static class L546_AHostMintedPositionIsPublishedOrStated
    {
        internal static IEnumerable<string> Check()
        {
            var set = GeoWindowCoverage.HostAuthoritativeRaisers;

            // (a) THE DECLARATION. A host-authoritative raiser is never LocalOnly.
            foreach (var modal in set)
            {
                var rule = GeoWindowCoverage.RuleForModal(modal);
                if (rule == null)
                {
                    yield return "L546 host-raiser-undeclared: modal '" + modal + "' is raised by " +
                                 "GeoscapeView's own faction/site event subscription and GeoWindowCoverage " +
                                 "declares nothing for it, so the seam mints it a journal position and " +
                                 "HostBroadcast discards it against a null rule.";
                    continue;
                }
                if (rule.Sync == WindowSync.LocalOnly)
                    yield return "L546 host-raiser-declared-localonly: modal '" + modal + "' is declared " +
                                 "LocalOnly, i.e. 'each peer raises its own'. No client's game ever runs the " +
                                 "handler that raises it — the rail writes the mirrored state directly and " +
                                 "never travels the native completion path — and §A.9/L520 deleted the last " +
                                 "mod-served local replay. So the host mints a journal position, publishes " +
                                 "nothing, and the window exists on exactly one screen with no line saying " +
                                 "so. This is the measured defect: host pos=1/pos=2 UIStateGeoModal, both " +
                                 "clients only pos=3. Mirrored publishes it; Gap announces the hole. " +
                                 "LocalOnly does neither.";
            }

            // (b) THE SEAM, EXECUTED. Real mint, real hand-off, real publish decision, real table.
            foreach (var modal in set)
            {
                WindowJournal.Reset();
                uint minted = WindowJournal.MintHostPosition();
                WindowJournal.SetHostPending(minted, "UIStateGeoModal");
                uint taken;
                string family;
                WindowJournal.TakeHostPending(out taken, out family);
                if (taken != minted || family != "UIStateGeoModal")
                {
                    yield return "L546 seam-handoff-lost: the position minted for '" + modal + "' (" + minted +
                                 ") came back from TakeHostPending as " + taken + "/'" + (family ?? "<null>") +
                                 "'. The publisher runs later in the mint's own call stack and reads exactly " +
                                 "this hand-off; if it cannot see the position, nothing it ships carries one.";
                    continue;
                }
                var rule = GeoWindowCoverage.RuleForModal(modal);
                var sync = rule == null ? (WindowSync?)null : rule.Sync;
                string refusal = GeoModalMirror.PublishRefusal("Modal:" + modal, taken, sync);
                if (sync == WindowSync.Mirrored)
                {
                    if (refusal != null)
                        yield return "L546 mirrored-refused: '" + modal + "' is declared Mirrored and the " +
                                     "real publish decision still refused position " + taken + ": " + refusal;
                }
                else if (string.IsNullOrEmpty(refusal))
                    yield return "L546 silent-discard: '" + modal + "' is declared " +
                                 (sync == null ? "UNDECLARED" : sync.ToString()) + " and the publish " +
                                 "decision destroyed journal position " + taken + " WITHOUT A REASON. A bare " +
                                 "`return` here is how two research windows went missing for ten days with " +
                                 "nothing in any log; a discard must state itself.";
            }
            WindowJournal.Reset();

            // (c) NOT A "MIRROR EVERYTHING" LAW. A gesture family stays LocalOnly, or arm (a) is vacuous.
            var gesture = GeoWindowCoverage.RuleForModal(ModalType.ActivateBase);
            if (gesture == null || gesture.Sync != WindowSync.LocalOnly)
                yield return "L546 localonly-verdict-gone: ModalType.ActivateBase is declared " +
                             (gesture == null ? "nothing" : gesture.Sync.ToString()) + " instead of " +
                             "LocalOnly. It is raised by the clicking peer's own ability view to confirm a " +
                             "gesture that peer just made; if even that is not LocalOnly, arm (a) is no " +
                             "longer distinguishing a false LocalOnly from any LocalOnly at all.";

            // (d) POSITIVE CONTROL: the set must be non-empty and must contain the family that was broken.
            bool hasResearch = false;
            foreach (var modal in set) if (modal == ModalType.GeoResearchComplete) hasResearch = true;
            if (set.Length == 0 || !hasResearch)
                yield return "L546 positive-control: HostAuthoritativeRaisers holds " + set.Length +
                             " entries and " + (hasResearch ? "does" : "does NOT") + " contain " +
                             "GeoResearchComplete, so arms (a) and (b) ran over a set that cannot contain " +
                             "the reported defect and proved nothing.";
        }
    }
}
