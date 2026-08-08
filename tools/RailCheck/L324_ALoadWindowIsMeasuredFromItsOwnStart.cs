using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L324 — A LOAD WINDOW'S STALL DEADLINE IS MEASURED FROM THAT WINDOW'S OWN START, AND A WINDOW THAT
    /// REALLY STOPS STILL RE-ARMS THE DETECTORS.
    ///
    /// THE REPORT (2026-08-08, live 3-instance run). The tac→geo return armed the transfer/load suspension at
    /// 15:11:03.136 and <c>transfer/load shows no progress for 60s — re-arming liveness detectors</c> fired
    /// ONE MILLISECOND later. Nothing had stalled: <c>SaveTransferCoordinator.LastProgressMs</c> is ONE field
    /// for the whole session, only <c>NoteProgress()</c> moves it, and the last thing to move it was the
    /// PREVIOUS load at 15:01:41 — 562 s of another era's staleness. Every load after the first therefore
    /// opened already past its own deadline: the suspension was off for exactly the load it exists to cover,
    /// and the host-loss and client-reaper detectors ran ARMED straight through a window in which a peer's
    /// main thread is legitimately blocked for tens of seconds. Client-2 survived only by frame ordering — its
    /// <c>RosterProgress</c> happened to land in the same frame as the arm, ahead of <c>Update()</c> — which
    /// is a race, not a difference.
    ///
    /// WHY THE OUTCOME AND NOT THE CALL. "Update stamps a window-open field" would have been green over the
    /// bug in both directions: green while the stamp was never READ into the deadline, and green again over a
    /// stamp that slid forward with <c>now</c> every tick — which reads like a fix and is strictly worse, a
    /// deadline that can never be reached and a dead peer that suspends the detectors forever. So this law
    /// EXECUTES the two decisions on their corners instead:
    ///   • <c>SessionManager.LoadWindowIsStalled</c> — a window born beside 562 s of foreign staleness is NOT
    ///     stalled, a window open longer than <c>TransferStallMs</c> with nothing moving IS, and a transfer
    ///     still moving inside a long-open window is not.
    ///   • <c>SessionManager.LoadWindowStamp</c> — no window is 0, an opening window takes <c>now</c>, and an
    ///     OPEN window keeps the stamp it already has. That last corner is the one that keeps the detectors
    ///     re-armable at all.
    /// Both directions of the defect are therefore red: drop the window term and the fresh-window corner
    /// fails; let the stamp slide and the still-open corner fails.
    ///
    /// THE STAMP MUST COME OFF THE RAW FLAGS. It is taken before the stall override clears
    /// <c>loadInFlight</c>, because a stalled window fed its own overridden flag would zero its stamp and
    /// re-open a brand-new window on the very next tick — the suspension would then never end. That ordering
    /// is not IL-checkable here; it is why <c>LoadWindowStamp</c> takes the flag as a PARAMETER, so the corner
    /// above pins what the function must answer and the single call site is one line under the comment saying
    /// so (<c>SessionManager.Update</c>).
    ///
    /// Falsify: drop the window term from <c>LoadWindowLiveMs</c> (i.e. return <c>lastProgressMs</c>, the
    /// defect verbatim) → <c>stale-era-arms-the-detectors</c>; make <c>LoadWindowStamp</c> return <c>now</c>
    /// for an already-open window → <c>window-slides-forward</c>; make <c>LoadWindowIsStalled</c> return
    /// <c>false</c> → <c>stall-never-re-arms</c>; stop calling the decision from <c>Update</c> →
    /// <c>decision-unreached</c>; stop keeping the per-window field → <c>decision-unfed</c>; give the control
    /// the real decision → <c>control-not-red</c>.
    /// </summary>
    internal static class L324_ALoadWindowIsMeasuredFromItsOwnStart
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>POSITIVE CONTROL: the PRE-FIX decision, verbatim — one session-wide progress stamp and no
        /// window term. If the corner runner below answers "sound" for this, it proves nothing about the real
        /// one.</summary>
        private static bool PreFixDecision(long now, long windowOpenedMs, long lastProgressMs, long stallMs) =>
            now - lastProgressMs > stallMs;

        internal static IEnumerable<string> Check()
        {
            var sm = typeof(SessionManager);
            var update = sm.GetMethod("Update", All);
            var stamp = sm.GetMethod("LoadWindowStamp", All);
            var live = sm.GetMethod("LoadWindowLiveMs", All);
            var stalled = sm.GetMethod("LoadWindowIsStalled", All);
            var opened = sm.GetField("_loadWindowOpenedMs", All);
            var stallField = sm.GetField("TransferStallMs", All);

            if (update == null || stamp == null || live == null || stalled == null || opened == null ||
                stallField == null || !stallField.IsLiteral)
            {
                yield return "L324 premise-changed: SessionManager.{Update,LoadWindowStamp,LoadWindowLiveMs," +
                             "LoadWindowIsStalled,_loadWindowOpenedMs,TransferStallMs} no longer resolves as " +
                             "the shape this law executes. The transfer/load liveness suspension has been " +
                             "restructured, and a law that cannot run its corners is asserting nothing about " +
                             "whether a load window is still measured from its own start — re-read " +
                             "SessionManager.Update before trusting this green.";
                yield break;
            }

            long stallMs = Convert.ToInt64(stallField.GetRawConstantValue());
            if (stallMs <= 0)
            {
                yield return "L324 premise-changed: TransferStallMs is " + stallMs + " — a non-positive " +
                             "deadline suspends nothing and every corner below is meaningless.";
                yield break;
            }

            // ── (a) THE DECISION, executed on its corners ──────────────────────────────────
            foreach (var v in Corners((now, o, p, s) => (bool)stalled.Invoke(null, new object[] { now, o, p }),
                                      stallMs, "L324")) yield return v;

            // ── (b) THE WINDOW STAMP, executed on its corners ──────────────────────────────
            const long T = 1_700_000_000_000L;
            long noWindow = Convert.ToInt64(stamp.Invoke(null, new object[] { false, T - 5_000L, T }));
            if (noWindow != 0L)
                yield return "L324 window-never-closes: with no load in flight LoadWindowStamp answers " +
                             noWindow + " instead of 0, so the window from the LAST load stays open. The next " +
                             "load then inherits a stamp older than itself — which is this bug wearing a new " +
                             "field instead of LastProgressMs.";

            long fresh = Convert.ToInt64(stamp.Invoke(null, new object[] { true, 0L, T }));
            if (fresh != T)
                yield return "L324 window-never-opens: a load in flight with no window open answers " + fresh +
                             " instead of now (" + T + "), so the window edge is never stamped and the " +
                             "deadline falls back on whatever the previous load last touched.";

            long held = Convert.ToInt64(stamp.Invoke(null, new object[] { true, T - 5_000L, T }));
            if (held != T - 5_000L)
                yield return "L324 window-slides-forward: an ALREADY-OPEN window answers " + held +
                             " instead of the stamp it was opened with (" + (T - 5_000L) + "). A stamp that " +
                             "advances with `now` every tick is never more than one tick old, so " +
                             "now - max(stamp, progress) can never exceed TransferStallMs: the suspension " +
                             "would hold forever and a genuinely dead host would keep every liveness detector " +
                             "switched off — the failure the deadline exists to prevent, reintroduced by " +
                             "something that reads like the fix.";

            // ── (c) Update actually consults it, and still keeps a per-window clock ────────
            var mod = sm.Assembly;
            if (!Program.Callees(update, mod).Any(c => c.MetadataToken == stalled.MetadataToken &&
                                                       c.Module == stalled.Module))
                yield return "L324 decision-unreached: SessionManager.Update never calls " +
                             "LoadWindowIsStalled, so the suspension deadline it applies is some other " +
                             "arithmetic than the one this law just proved sound. The corners above then " +
                             "describe a function nothing runs.";
            if (!Program.Callees(update, mod).Any(c => c.MetadataToken == stamp.MetadataToken &&
                                                       c.Module == stamp.Module))
                yield return "L324 window-edge-unstamped: SessionManager.Update never calls LoadWindowStamp, " +
                             "so nothing marks the rising edge of the (TransferActive || InPhase2 || " +
                             "LoadPhaseStarted) window and the clock is back to one session-wide field.";
            if (!Program.ReadsField(update, opened))
                yield return "L324 decision-unfed: SessionManager.Update no longer references " +
                             "_loadWindowOpenedMs, so whatever it feeds the deadline is not THIS window's " +
                             "start. That is the reported defect exactly: the tac->geo return armed at " +
                             "15:11:03.136 and warned 1 ms later against progress from 15:01:41.";

            // ── (d) POSITIVE CONTROL: the corner runner can go red ─────────────────────────
            if (!Corners(PreFixDecision, stallMs, "control").Any())
                yield return "L324 control-not-red: the corner runner reports the PRE-FIX decision " +
                             "(now - lastProgressMs > TransferStallMs — the shipped bug, verbatim) as sound. " +
                             "Arm (a) therefore cannot tell the fix from the defect and the green above is " +
                             "decoration; fix the corners before trusting them.";
        }

        /// <summary>The four corners every "is this load window stalled" decision has to answer. Run over the
        /// real one and over the pre-fix one, so a green here means the test discriminates.</summary>
        private static IEnumerable<string> Corners(Func<long, long, long, long, bool> decide, long stallMs, string id)
        {
            const long T = 1_700_000_000_000L;

            // THE REPORTED CASE: a window that opened this very tick, beside a progress stamp from the
            // previous load 562 s ago. Alive by construction — it has had no time to prove anything yet.
            if (decide(T, T, T - 562_000L, stallMs))
                yield return id + " stale-era-arms-the-detectors: a load window opened at `now` is called " +
                             "stalled because the SESSION's last progress was 562 s ago, under a different " +
                             "load. That is the 2026-08-08 report verbatim — armed 15:11:03.136, warned " +
                             "15:11:03.137 — and it switches the host-loss and client-reaper detectors back " +
                             "on for exactly the window they are meant to be off for.";

            // A window that really has stopped: open longer than the deadline, nothing moving. The detectors
            // MUST come back, or a silently dead host parks every peer at "Waiting for players..." forever.
            if (!decide(T, T - stallMs - 1L, T - stallMs - 1L, stallMs))
                yield return id + " stall-never-re-arms: a window open for longer than TransferStallMs with " +
                             "no progress at all is not reported as stalled, so the suspension never lifts. " +
                             "A silently dead host (STUN, missing Steam fail callback, TCP half-open) leaves " +
                             "the transfer flags latched and every liveness detector off — the client waits " +
                             "at 'Waiting for players...' for the rest of the session.";

            // A long-open window whose transfer is still moving stays suspended: progress is the other half
            // of the clock, and taking the LATER of the two must not throw it away.
            if (decide(T, T - 10L * stallMs, T, stallMs))
                yield return id + " progress-ignored: a window that has been open a long time but whose " +
                             "transfer moved this very tick is called stalled. The clock is the LATER of the " +
                             "window's start and the last progress; a big save over a slow link legitimately " +
                             "outlives TransferStallMs and proves itself alive chunk by chunk.";

            // The boundary belongs to the live side: exactly TransferStallMs is not yet PAST the deadline.
            if (decide(T, T - stallMs, 0L, stallMs))
                yield return id + " boundary-reaps-early: a window exactly TransferStallMs old is already " +
                             "stalled, so the budget is off by one whole tick at the only moment it matters.";
        }
    }
}
