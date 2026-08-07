using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L188 — A FIELD THE RAIL EXCLUDES ON EVERY WALK IS REPORTED AS PERMANENT, NOT AS A WARNING.
    ///
    /// THE DEFECT (2026-08-07 co-op session, HOST log). Three lines, once each, at the very first campaign
    /// walk:
    ///   23:09:05.481 WARN [rail] DiffEngine excluded: FactionDiplomacy._factionsDiplomacyState:
    ///                entity-list encode failed: polymorphic value PPFactionDef as IDiplomaticPartyKey
    /// and then nothing, ever again, for the remaining sixteen minutes — while the field kept failing on
    /// EVERY walk, twice a second, for the whole session. The client's faction-relations screen ran on
    /// whatever the save transfer held at 23:09 and never updated, and neither peer's log said so.
    ///
    /// The mechanism is the shape, not the field. <c>Incident</c> dedups through a <c>HashSet</c> and that set
    /// covers the LOG, never the RETRY: the walk re-enters the field, the encode throws again, the exception
    /// is caught into the same Incident and swallowed because the line is already known. So a genuinely
    /// one-off exclusion during a load and a field that has NEVER crossed and never will produce byte-
    /// identical output — one Warning — and the reader has no way to tell them apart. That is the whole
    /// content of such a report, so it is now stated: <see cref="DiffEngine.EscalateAt"/> escalates once,
    /// at the count where "transient" is dead, at Error.
    ///
    /// Why exactly once and not every walk: an exclusion that recurs ~2×/s would otherwise turn into a
    /// per-frame error storm, which is the noise that makes a log unreadable and gets real lines ignored.
    /// One line, at a threshold, is the whole fix.
    ///
    /// This law asserts the REPORTING, deliberately. Whether `PPFactionDef` can ride as an
    /// `IDiplomaticPartyKey` at all is an encode question owned by the codec (`RailMeta.EncodeValue`'s
    /// declared-type refusal, and `docs/rail-contract.txt`'s frozen `polymorphic-codec: no`); this arm is
    /// what guarantees that the NEXT field to die that way is not silent for sixteen minutes.
    ///
    /// Falsify: escalate on the first sighting → <c>L188 one-off-cries-permanent</c>; never escalate →
    /// <c>L188 permanent-stays-a-warning</c>; escalate on every walk past the threshold →
    /// <c>L188 escalation-storms</c>; unwire the decision from Incident → <c>L188 verdict-is-decorative</c>;
    /// downgrade the escalation to a warning → <c>L188 verdict-is-not-loud</c>; keep the counter across a
    /// session teardown → <c>L188 verdict-outlives-its-session</c>.
    /// </summary>
    internal static class L188_APermanentExclusionIsNotAOneOffWarning
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var de = typeof(DiffEngine);
            var escalate = de.GetMethod("EscalateAt", All, null, new[] { typeof(int) }, null);
            var note = de.GetMethod("NoteIncidentWalk", All, null, new[] { typeof(string) }, null);
            var forget = de.GetMethod("ForgetIncidentCounts", All, null, System.Type.EmptyTypes, null);
            var incident = de.GetMethod("Incident", All);
            var reset = de.GetMethod("Reset", All, null, System.Type.EmptyTypes, null);
            var logError = typeof(UnityEngine.Debug).GetMethod("LogError", All, null,
                               new[] { typeof(object) }, null);
            int threshold = DiffEngine.PermanentAfterWalks;

            if (escalate == null || note == null || forget == null || incident == null || reset == null ||
                logError == null || threshold < 2)
            {
                yield return "L188 premise-changed: DiffEngine.EscalateAt / NoteIncidentWalk / " +
                             "ForgetIncidentCounts / Incident / Reset or UnityEngine.Debug.LogError no longer " +
                             "resolves, or PermanentAfterWalks has collapsed below 2 (" + threshold + "). " +
                             "Incident is the ONE channel every walk-time exclusion is funnelled into — a " +
                             "version of it that cannot separate a one-off from a dead field is the " +
                             "silent-swallow class this harness exists to outlaw, wearing a Warning.";
                yield break;
            }

            // ── (a) THE OUTCOME: exactly one escalation, and only once transience is dead ──
            if (DiffEngine.EscalateAt(1))
                yield return "L188 one-off-cries-permanent: the FIRST sighting escalates. A field excluded " +
                             "once during a load boundary is ordinary and already carries its warn-once line; " +
                             "shouting PERMANENT at it is how a real permanent exclusion gets read as noise.";
            if (!DiffEngine.EscalateAt(threshold))
                yield return "L188 permanent-stays-a-warning: nothing escalates at " + threshold + " walks. " +
                             "That is the 2026-08-07 defect verbatim — FactionDiplomacy._factionsDiplomacyState " +
                             "failed every walk for sixteen minutes and said so exactly once, at Warning, in " +
                             "the same shape a genuinely one-off exclusion uses.";
            if (DiffEngine.EscalateAt(threshold + 1) || DiffEngine.EscalateAt(threshold * 3))
                yield return "L188 escalation-storms: the verdict fires again past the threshold. At a walk " +
                             "cadence of ≤0.5 s that is an Error twice a second for the rest of the session — " +
                             "noise that buries the line it is trying to make visible.";

            // ── (b) THE REAL COUNTER, driven: one line, one verdict, at the right walk ──
            // Two distinct lines in flight at once, because the counter is per-line: a busy walk excludes
            // several fields and one of them reaching its verdict must not move another one's count.
            DiffEngine.ForgetIncidentCounts();
            const string one = "L188probe.A: driven [probe]", two = "L188probe.B: driven [probe]";
            int fired = 0, firedAt = 0;
            for (int walk = 1; walk <= threshold + 5; walk++)
            {
                if (walk % 2 == 0) DiffEngine.NoteIncidentWalk(two);   // the other line advances independently
                if (!DiffEngine.NoteIncidentWalk(one)) continue;
                fired++;
                if (firedAt == 0) firedAt = walk;
            }
            if (fired == 0)
                yield return "L188 permanent-stays-silent: the real counter never returned a verdict across " +
                             (threshold + 5) + " walks of the same exclusion. That is the 2026-08-07 defect " +
                             "in the live state machine rather than in the predicate — a field failing every " +
                             "walk for sixteen minutes with exactly one Warning to show for it.";
            else if (fired > 1)
                yield return "L188 escalation-storms-live: the counter answered true " + fired + " times for " +
                             "ONE exclusion. At ≤0.5 s per walk that is an Error twice a second for the rest " +
                             "of the session.";
            if (firedAt != 0 && firedAt != threshold)
                yield return "L188 verdict-fires-early: the driven counter escalated on walk " + firedAt +
                             " against a threshold of " + threshold + ". The whole claim of the escalation is " +
                             "that transience has been ruled out by repetition; firing before that makes it a " +
                             "guess, and firing later leaves the field dead and quiet for longer than stated.";
            if (DiffEngine.NoteIncidentWalk(two))
                yield return "L188 counts-are-shared: advancing one exclusion moved another's count to its " +
                             "verdict. The counter is per-line by construction — a shared one would let a " +
                             "noisy field spend a silent field's escalation.";

            // ── (c) POSITIVE CONTROL: teardown really forgets, so the next session re-earns its verdict ──
            DiffEngine.ForgetIncidentCounts();
            if (DiffEngine.NoteIncidentWalk(one))
                yield return "L188 verdict-survives-teardown: after ForgetIncidentCounts the very first walk " +
                             "of a line escalates again, i.e. the count was not actually cleared and the " +
                             "control above proves nothing about a fresh session.";
            DiffEngine.ForgetIncidentCounts();

            // ── (d) the seam: the decision is CONSULTED, the escalation is LOUD, and teardown clears ──
            var calls = Program.Callees(incident, de.Assembly).ToList();
            if (!calls.Any(m => m.MetadataToken == note.MetadataToken))
                yield return "L188 verdict-is-decorative: Incident no longer routes through NoteIncidentWalk, " +
                             "so arms (a)-(c) are proved about a counter the live exclusion path does not " +
                             "advance.";
            // Debug lives in another assembly, so this one question needs the unfiltered walker.
            if (!Program.CalleeSequence(incident).Any(m => m.MetadataToken == logError.MetadataToken))
                yield return "L188 verdict-is-not-loud: Incident reaches no Debug.LogError. A permanently " +
                             "excluded field reported at the same level as a transient one is the defect, " +
                             "not the fix — the level IS the distinction being drawn.";
            if (!Program.Callees(reset, de.Assembly).Any(m => m.MetadataToken == forget.MetadataToken))
                yield return "L188 verdict-outlives-its-session: DiffEngine.Reset no longer forgets the " +
                             "per-line walk counts, so a field that earned its verdict in one session can " +
                             "never earn it again in the next — the second session gets the silence the " +
                             "first one was fixed for.";
        }
    }
}
