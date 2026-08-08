using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L335 — A SCOPED RESEND LEAVES THE HOST'S ENTRIES IN THIS PEER'S MIRROR.
    ///
    /// THE OUTCOME, not the call: it is not enough that a request went out, nor that the host answered.
    /// After a scoped resend for a root, the thing that had been lost must be in this peer's mirror.
    ///
    /// THE DEFECT (2026-08-07 co-op session, 4 of 4 failed). Every backfill asked for the root that had
    /// just been CREATED — client log :2535/:2537/:2539 (U#6/U#7/U#8, the three rescued soldiers) and
    /// :3287 (U#9, the faction-base recruit). The host received and answered all four (host log :5030
    /// `SCOPED re-emit of root 'U#9'`). Ten seconds later each one reported UNANSWERED, and every new
    /// character in that session kept a permanently incomplete mirror.
    ///
    /// Two independent halves, both asserted below:
    ///   • THE SCOPE NAMED THE WRONG ENTITY. A create does not leave the NEW entity broken — its whole
    ///     graph arrives in the create blob (19288 B for U#6). What it leaves broken is every entry that
    ///     already shipped a REFERENCE to it and had that reference dropped for being unresolvable
    ///     (`RailMeta.Unresolved` kept the live value; `ApplyListCore` removed the hole). Those entries
    ///     live on the REFERRER — client log :2528 `unresolved entity ref U#6`, 0.3 s BEFORE the create
    ///     — and the host re-emits only on CHANGE, so nothing ever restates them unless they are asked
    ///     for BY NAME. Re-emitting the created root restates only what the client already holds.
    ///   • THE ACKNOWLEDGEMENT WAS KEYED ON A WRITE. `Unchanged` re-encodes the LIVE value with the
    ///     host's own codec, so an answer that re-states values the create blob just delivered is
    ///     byte-identical in every entry and applied nothing — which is the request SATISFIED (the
    ///     mirror holds the host's entry), reported as unanswered.
    ///
    /// Falsify: pass the created root as the scope again (`RequestResync(engine, ..., rootKey)`) →
    /// <c>L335 backfill-asks-for-the-wrong-entity</c> + <c>L335 backfill-scope-unconsulted</c>; return
    /// the created root from <c>BackfillScopes</c> → <c>L335 backfill-restates-the-create-blob</c>; drop
    /// the whole-segment rule → <c>L335 backfill-scope-eats-a-sibling</c>; stop recording ref drops →
    /// <c>L335 dropped-refs-unrecorded</c>; delete the acknowledgement in the <c>Unchanged</c> branch →
    /// <c>L335 an-answered-request-reads-as-lost</c>; stop counting discarded entries →
    /// <c>L335 give-up-is-evidence-free</c>.
    /// </summary>
    internal static class L335_AScopedResendLandsInTheMirror
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var ga = typeof(GenericApplier);
            var scopes = ga.GetMethod("BackfillScopes", All, null,
                             new[] { typeof(string), typeof(IEnumerable<string>) }, null);
            var applyStructural = ga.GetMethod("ApplyStructural", All);
            var applyEntry = ga.GetMethod("ApplyEntry", All);
            var noteDrop = ga.GetMethod("NoteRefDrop", All, null, new[] { typeof(string) }, null);
            var noteAnswer = ga.GetMethod("NoteScopedAnswer", All, null, new[] { typeof(string) }, null);
            var expire = ga.GetMethod("ExpireScopedRequests", All, null, System.Type.EmptyTypes, null);
            var dropN = ga.GetField("_scopedDropN", All);
            if (scopes == null || applyStructural == null || applyEntry == null || noteDrop == null ||
                noteAnswer == null || expire == null || dropN == null)
            {
                yield return "L335 premise-changed: GenericApplier.BackfillScopes / ApplyStructural / " +
                             "ApplyEntry / NoteRefDrop / NoteScopedAnswer / ExpireScopedRequests or the " +
                             "_scopedDropN book no longer resolves. The scoped backfill is the ONLY " +
                             "recovery for a reference that shipped before the entity it names existed — " +
                             "the host's diff never restates it, so a backfill that asks for the wrong " +
                             "thing loses it permanently and silently.";
                yield break;
            }

            // ── (a) THE OUTCOME, executed: which scope does a create's backfill ask for ──
            var referrer = new[] { "S#170.SerializationData" };
            var asked = GenericApplier.BackfillScopes("U#6", referrer);
            if (!asked.Contains("S#170.SerializationData"))
                yield return "L335 backfill-asks-for-the-wrong-entity: a create backfill no longer asks for " +
                             "the REFERRER whose entry dropped the reference. That entry is the only thing " +
                             "the create left broken, and the host — whose own value never changed — will " +
                             "never re-emit it unasked. All four backfills of the 2026-08-07 session were " +
                             "answered in full and converged nothing for exactly this reason.";
            if (asked.Contains("U#6"))
                yield return "L335 backfill-restates-the-create-blob: the backfill asks for the root that was " +
                             "just created. The create blob delivered that entire graph byte for byte, so " +
                             "every entry of the answer matches the mirror and re-states nothing — while the " +
                             "referrer that dropped the ref to it stays dropped for the rest of the session.";
            if (GenericApplier.BackfillScopes("U#6", new string[0]).Count != 0)
                yield return "L335 backfill-asks-unprompted: a create with no recorded ref drop asks for " +
                             "something anyway. Nothing was lost, so the request can only be answered by " +
                             "entries that already match — the shape that made the give-up warning fire on " +
                             "four requests the host had answered.";
            if (GenericApplier.BackfillScopes("U#6", new[] { "U#6.Statuses" }).Count != 0)
                yield return "L335 backfill-asks-inside-the-create: a drop recorded UNDER the created root is " +
                             "asked for anyway, although the create blob just restated that whole subtree.";
            if (!GenericApplier.BackfillScopes("U#6", new[] { "U#60.Progression" }).Contains("U#60.Progression"))
                yield return "L335 backfill-scope-eats-a-sibling: the under-the-root test is not whole-SEGMENT, " +
                             "so creating 'U#6' silently swallows the referrer 'U#60...'. Same rule the host's " +
                             "own scope uses (DiffEngine.PrefixMatchOne); the two must not disagree.";

            // ── (b) the seams: the decision is consulted, and the evidence it needs is recorded ──
            if (!Program.Callees(applyStructural, ga.Assembly).Any(c => c.MetadataToken == scopes.MetadataToken))
                yield return "L335 backfill-scope-unconsulted: ApplyStructural no longer asks BackfillScopes " +
                             "what to request, so arm (a) is proved about a decision the live seam does not " +
                             "make — which is how the created root became the scope in the first place.";
            if (!Program.Callees(applyEntry, ga.Assembly).Any(c => c.MetadataToken == noteDrop.MetadataToken))
                yield return "L335 dropped-refs-unrecorded: nothing records WHERE a reference was dropped, so " +
                             "the recorded set is always empty and the backfill asks for nothing at all. That " +
                             "is strictly worse than asking for the wrong root: it is silent.";

            // ── (c) the acknowledgement: a mirror that ALREADY holds the host's value answers the request ──
            if (Program.Callees(applyEntry, ga.Assembly).Count(c => c.MetadataToken == noteAnswer.MetadataToken) < 2)
                yield return "L335 an-answered-request-reads-as-lost: ApplyEntry marks a scoped request " +
                             "answered from only ONE place. The other one is the Unchanged branch, and it is " +
                             "the branch a re-emit actually lands on: Unchanged re-encodes the LIVE value with " +
                             "the host's own codec, so an answer that re-states what this peer already holds " +
                             "writes nothing — and the outcome the request asked for (the mirror holds the " +
                             "host's entry) is met exactly there.";
            if (!Program.ReadsField(expire, dropN))
                yield return "L335 give-up-is-evidence-free: the 10 s give-up no longer reads how many entries " +
                             "under that root arrived and were discarded here. Without it the warning cannot " +
                             "tell 'the answer never came' from 'the answer came and died on this side' — the " +
                             "distinction the whole 2026-08-07 RCA had to guess at.";
        }
    }
}
