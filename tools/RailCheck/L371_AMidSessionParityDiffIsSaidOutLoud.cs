using System;
using System.Collections.Generic;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L371 — A NON-EMPTY PARITY DIFF ON A SESSION-STARTED JOIN PRODUCES A CLIENT-FACING NOTICE.
    ///
    /// The only carrier of a parity mismatch used to be the badge on the LOBBY roster row — and a
    /// mid-session joiner never sees a lobby: it goes from the accept straight into the save transfer. So its
    /// diff was computed, stored on the roster row, logged host-side, and shown to nobody. Silence about a
    /// known content difference is the exact failure class this rail keeps re-learning: the desync arrives
    /// later, and nothing said the reason was on the table at join time.
    ///
    /// Roster admission is soft, but campaign entry is blocking: a mismatched peer waits without blocking
    /// the running campaign. Auto-applied settings may clear the mismatch and release that peer.
    ///
    /// ARMS
    ///   (a) <c>diff-said-to-nobody</c> — a session-started join with diffs must produce a non-empty notice
    ///       that NAMES the peer (a nameless "someone differs" is not actionable).
    ///   (b) <c>clean-join-shouted-at</c> — NEGATIVE CONTROL: no diffs ⇒ no notice.
    ///   (c) <c>lobby-join-shouted-at</c> — NEGATIVE CONTROL: a pre-start join says nothing here, because its
    ///       diff is already on the roster badge the joiner is about to look at. This is also the arm that
    ///       keeps the notice a NOTICE: it is produced from the diff alone, never consulted as a decision.
    ///
    /// Falsify: make <c>JoinParityNotice</c> return "" unconditionally → (a) red; make it return a notice
    /// unconditionally → (b) and (c) red; drop the nickname from the text → (a) red.
    /// </summary>
    internal static class L371_AMidSessionParityDiffIsSaidOutLoud
    {
        private const string Nick = "Somebody";

        internal static IEnumerable<string> Check()
        {
            string midSession = null, clean = null, lobby = null, threw = null;
            try
            {
                midSession = SessionManager.JoinParityNotice(true, 2, Nick);
                clean = SessionManager.JoinParityNotice(true, 0, Nick);
                lobby = SessionManager.JoinParityNotice(false, 2, Nick);
            }
            catch (Exception ex) { threw = ex.GetType().Name; }

            if (threw != null || midSession == null || clean == null || lobby == null)
            {
                yield return "L371 premise-changed: SessionManager.JoinParityNotice threw (" + threw +
                             ") or answered null. That method IS the decision this law asserts — re-point it " +
                             "at whatever turns a join-time parity diff into words now; do not delete it, because " +
                             "the diff's only other carrier is a lobby badge a mid-session joiner never sees.";
                yield break;
            }

            if (midSession.Length == 0 || midSession.IndexOf(Nick, StringComparison.Ordinal) < 0)
                yield return "L371 diff-said-to-nobody: a session-started join carrying 2 parity differences " +
                             "produced " + (midSession.Length == 0 ? "no notice at all" : "a notice that does not " +
                             "name the peer") + ". That joiner never sees a lobby, so the roster badge holding its " +
                             "diff is rendered to nobody and the mismatch is silent until it desyncs somebody.";

            if (clean.Length != 0)
                yield return "L371 clean-join-shouted-at: a join with NO parity differences produced a notice " +
                             "(\"" + clean + "\"). Every mid-session join would then announce a problem that does " +
                             "not exist, and the line stops meaning anything the first time it is wrong.";

            if (lobby.Length != 0)
                yield return "L371 lobby-join-shouted-at: a pre-start (lobby) join produced the mid-session " +
                             "notice as well. Its diff already rides the roster badge the joiner is looking at — " +
                             "saying it twice is noise, and the second copy toasts every OTHER peer too.";

            if (SessionManager.CanEnterStartedSession("mod differs"))
                yield return "L371 mismatched-peer-enters-campaign: a non-empty parity diff can still start " +
                             "the mid-session save transfer. Roster admission is harmless; incompatible state " +
                             "entering the live campaign graph is not.";
            if (!SessionManager.CanEnterStartedSession(""))
                yield return "L371 positive-control: an empty parity diff cannot enter a started session.";
        }
    }
}
