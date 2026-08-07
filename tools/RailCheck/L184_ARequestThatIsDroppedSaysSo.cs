using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L184 — A RESEND REQUEST THIS PEER DROPS LEAVES A LINE, AND A SCOPED BACKFILL IS NOT A FULL RESEND.
    ///
    /// THE DEFECT (2026-08-07 co-op session). <c>RequestResync</c> had ONE global 5 s window and the warning
    /// sat INSIDE it:
    ///   if (engine == null || Time.realtimeSinceStartup &lt; _nextResyncReqAt) return;   // ← no line
    /// For the unknown-kindId burst that coalescing was correct and it worked — 39 warnings, one full
    /// resend, converged in 1.3 s. The hazard is the COUPLING: the same window also gated the SCOPED
    /// <c>structural create backfill</c> requests, which are about ONE root's ref-lists and are not answered
    /// by a full resend that went out five seconds ago for an unrelated reason. A scoped request eaten that
    /// way printed NOTHING AT ALL — a textbook instance of this repo's dominant bug class, in the function
    /// whose whole job is recovering from silent loss.
    ///
    /// THE SECOND HALF IS ANSWER-ABSENCE. Four scoped requests went out in that session and there is ZERO
    /// acknowledgement anywhere, in either direction: the host never logged receiving one, and the client
    /// has no positive line when the answer applies. The host's answer is an ordinary delta by design
    /// (<c>DiffEngine.ForceReemit</c>), so "the resend never came" and "it came and applied silently" are
    /// the SAME log — the requesting peer cannot tell them apart, which is itself the defect. The client
    /// now records what it asked for and reports both outcomes: <c>NoteScopedAnswer</c> on the first entry
    /// that applies under the root, <c>ExpireScopedRequests</c> when the deadline passes with none.
    ///
    /// Falsify: give the scoped request the full window back (<c>ResyncAllowed</c> reading nextFullAt when
    /// scoped) → <c>L184 a-full-resend-eats-a-backfill</c>; let the scoped window gate a full resend →
    /// <c>L184 a-backfill-eats-a-full-resend</c>; drop the full coalescing → <c>L184 burst-uncoalesced</c>;
    /// drop the per-root window → <c>L184 unthrottled-scoped-storm</c>; stop calling the decision →
    /// <c>L184 throttle-is-decorative</c>; put the request back behind a bare return →
    /// <c>L184 dropped-request-is-silent</c>; unwire the counting funnel → <c>L184 drops-are-uncountable</c>;
    /// drop either half of the answer report → <c>L184 answer-unobservable</c>.
    /// </summary>
    internal static class L184_ARequestThatIsDroppedSaysSo
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var ga = typeof(GenericApplier);
            var allowed = ga.GetMethod("ResyncAllowed", All, null,
                              new[] { typeof(bool), typeof(float), typeof(float), typeof(float) }, null);
            var request = ga.GetMethod("RequestResync", All);
            var missOnce = ga.GetMethod("LogMissOnce", All, null, new[] { typeof(string) }, null);
            var note = ga.GetMethod("NoteScopedAnswer", All, null, new[] { typeof(string) }, null);
            var expire = ga.GetMethod("ExpireScopedRequests", All, null, System.Type.EmptyTypes, null);
            var crcTick = ga.GetMethod("ClientCrcTick", All);
            var applyEntry = ga.GetMethod("ApplyEntry", All);
            var countMiss = typeof(RailMeta).GetMethod("CountMiss", All, null, new[] { typeof(string) }, null);
            if (allowed == null || request == null || missOnce == null || note == null || expire == null ||
                crcTick == null || applyEntry == null || countMiss == null)
            {
                yield return "L184 premise-changed: GenericApplier.ResyncAllowed / RequestResync / " +
                             "LogMissOnce / NoteScopedAnswer / ExpireScopedRequests / ClientCrcTick / " +
                             "ApplyEntry or RailMeta.CountMiss no longer resolves. The resync path is the " +
                             "one recovery this rail has from a silent loss; a version of it that can " +
                             "itself lose a request silently is the failure mode this law exists for.";
                yield break;
            }

            // ── (a) THE OUTCOME: the two request kinds never share a window ──
            // now=0: the full window closes at 5, the scoped one is open.
            if (!GenericApplier.ResyncAllowed(true, 0f, 5f, 0f))
                yield return "L184 a-full-resend-eats-a-backfill: a scoped 'structural create backfill' for " +
                             "a root nobody has asked about is dropped because an UNRELATED full resend went " +
                             "out inside the last " + GenericApplier.ResyncThrottleSec + "s. That is the " +
                             "2026-08-07 coupling verbatim — and a full resend does not answer a scoped " +
                             "request, because the client asked about ref-lists that shipped before their " +
                             "root existed.";
            if (GenericApplier.ResyncAllowed(false, 0f, 5f, 0f))
                yield return "L184 burst-uncoalesced: the full resend is no longer globally coalesced. A " +
                             "systematic miss hits every entry of every packet — 39 unknown-kindId warnings " +
                             "in 1.3 s in that session — and one resend answers all of them. Asking 39 times " +
                             "costs the host 39 full graph walks fanned out to every peer.";
            if (GenericApplier.ResyncAllowed(true, 0f, 0f, 5f))
                yield return "L184 unthrottled-scoped-storm: the per-root window is gone, so a root that " +
                             "keeps failing to resolve re-asks on every entry of every packet.";
            if (!GenericApplier.ResyncAllowed(false, 0f, 0f, 5f))
                yield return "L184 a-backfill-eats-a-full-resend: one root's pending backfill now blocks the " +
                             "full resend that a seq gap or a torn batch needs. Those can have touched " +
                             "anything, which is exactly why they name no scope.";

            // ── (b) the seam: the decision is consulted, and a drop is LOUD and COUNTED ──
            var calls = Program.Callees(request, ga.Assembly).ToList();
            if (!calls.Any(c => c.MetadataToken == allowed.MetadataToken))
                yield return "L184 throttle-is-decorative: RequestResync no longer consults ResyncAllowed, " +
                             "so arm (a) is proved about a decision the live seam does not make.";
            if (!calls.Any(c => c.MetadataToken == missOnce.MetadataToken))
                yield return "L184 dropped-request-is-silent: the throttled branch of RequestResync no " +
                             "longer logs. A request this peer decided not to send is state it decided not " +
                             "to recover, and the old code returned on it with no line whatsoever — after " +
                             "the fact the log gave no way to know it had ever happened.";
            if (!Program.Callees(missOnce, ga.Assembly).Any(c => c.MetadataToken == countMiss.MetadataToken))
                yield return "L184 drops-are-uncountable: LogMissOnce no longer routes through " +
                             "RailMeta.CountMiss, so every throttled-away request reports 1 for the whole " +
                             "session however often it fires — the same blindness that made all thirteen " +
                             "'dto-twin gap' families read as one occurrence each.";

            // ── (c) answer-absence: both outcomes of a scoped request are reported ──
            if (!Program.Callees(applyEntry, ga.Assembly).Any(c => c.MetadataToken == note.MetadataToken))
                yield return "L184 answer-unobservable: nothing marks a scoped request ANSWERED when the " +
                             "host's re-emit applies. The host answers with an ordinary delta, so without " +
                             "this the requesting peer cannot tell a resend that arrived from one that never " +
                             "came — four requests in that session had neither line.";
            if (!Program.Callees(crcTick, ga.Assembly).Any(c => c.MetadataToken == expire.MetadataToken))
                yield return "L184 unanswered-request-is-silent: nothing expires a scoped request that was " +
                             "never answered. The peer then keeps a root it KNOWS it lost, having asked for " +
                             "it once, with no further word about it in any log.";
        }
    }
}
