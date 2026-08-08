using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L368 — A RESEND NOBODY ANSWERED SAYS SO, AND THE ROOTLESS ONE SAYS IT TOO.
    ///
    /// The scoped backfill has had a 10 s unanswered-deadline since the 2026-08-07 session; the FULL resend
    /// had no counterpart at all. So the request this peer sends when it cannot even NAME what it lost — a
    /// seq gap, a torn batch, an unknown kindId — was the one request nothing ever reported as unanswered. A
    /// scoped miss got a warning and a full miss got silence, which is the dominant bug class of this rail
    /// wearing its own diagnostics.
    ///
    /// Driven as the pure decision <c>GenericApplier.GiveUpLine</c>, the same function the live expiry path
    /// prints, so the law and the runtime line cannot drift.
    ///
    /// ARMS
    ///   (a) <c>full-give-up-silent</c> — a rootless request past its deadline must produce a line, and that
    ///       line must SAY it was the full one (a give-up that does not name which request it was cannot be
    ///       acted on).
    ///   (b) <c>early-give-up</c> — NEGATIVE CONTROL: inside the deadline it must say nothing, or every full
    ///       resend would announce failure the instant it was sent.
    ///   (c) <c>scoped-give-up-unnamed</c> — the scoped line still names its ROOT, i.e. folding the two into
    ///       one function did not cost the older half its content.
    ///   (d) <c>arrived-and-discarded-unsaid</c> — when entries DID arrive and were discarded here, the line
    ///       must say so and carry the first reason. That distinction ("never came" vs "came and died on
    ///       this side") is the whole reason the deadline exists.
    ///
    /// Falsify: <c>GiveUpLine => ""</c> → (a) and (c) red; drop the <c>now &lt; deadline</c> test → (b) red;
    /// drop the root from the scoped text → (c) red; drop the dropped-count branch → (d) red.
    /// </summary>
    internal static class L368_AnUnansweredFullResendSaysSo
    {
        private const string Root = "S#88";

        internal static IEnumerable<string> Check()
        {
            string full = null, early = null, scoped = null, discarded = null, threw = null;
            try
            {
                full = GenericApplier.GiveUpLine(null, 100f, 10f, 0, null);
                early = GenericApplier.GiveUpLine(null, 5f, 10f, 0, null);
                scoped = GenericApplier.GiveUpLine(Root, 100f, 10f, 0, null);
                discarded = GenericApplier.GiveUpLine(Root, 100f, 10f, 3, "kind 47 unknown");
            }
            catch (Exception ex) { threw = ex.GetType().Name; }

            if (threw != null || full == null || early == null || scoped == null || discarded == null)
            {
                yield return "L368 premise-changed: GenericApplier.GiveUpLine threw (" + threw + ") or answered " +
                             "null. That function IS the give-up this law asserts — re-point it at whatever " +
                             "reports an unanswered resend now; do not delete it, because without it a full " +
                             "resend the host never answers is indistinguishable from one it answered silently, " +
                             "and the peer keeps playing on a mirror nobody repaired.";
                yield break;
            }

            if (full.Length == 0 || full.IndexOf("full resend", StringComparison.Ordinal) < 0)
                yield return "L368 full-give-up-silent: a ROOTLESS resend past its deadline produced " +
                             (full.Length == 0 ? "no line at all" : "a line that does not say it was the full " +
                             "one (\"" + full + "\")") + ". That is the request sent when this peer cannot name " +
                             "what it lost, so nothing else in the log can stand in for it.";

            if (early.Length != 0)
                yield return "L368 early-give-up: a request still INSIDE its deadline produced a give-up line " +
                             "(\"" + early + "\"). Every resend would then announce its own failure the frame it " +
                             "was sent, and the warning stops meaning anything.";

            if (scoped.Length == 0 || scoped.IndexOf(Root, StringComparison.Ordinal) < 0)
                yield return "L368 scoped-give-up-unnamed: the SCOPED give-up no longer names its root. The " +
                             "root is the only actionable part — it is what the reader re-emits by hand and what " +
                             "the next investigation greps for.";

            if (discarded.IndexOf("kind 47 unknown", StringComparison.Ordinal) < 0)
                yield return "L368 arrived-and-discarded-unsaid: a give-up whose answer DID arrive and was " +
                             "discarded on this side no longer says why. \"The resend never came\" and \"it came " +
                             "and died here\" are opposite bugs with opposite fixes, and this line is the only " +
                             "place they are ever told apart.";
        }
    }
}
