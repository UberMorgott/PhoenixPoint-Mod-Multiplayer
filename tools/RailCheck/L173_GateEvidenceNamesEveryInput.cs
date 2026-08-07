using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L173 — THE CURTAIN GATE'S EVIDENCE LINE NAMES EVERY INPUT OF THE DECISION IT REPORTS.
    ///
    /// <c>CurtainTakedownGate.State()</c> exists because of a specific, expensive failure: a lift that
    /// sailed through the gate and a lift that never fired at all produced the SAME log — nothing — and
    /// five successive fixes were written blind. Its own doc says so. The line it prints is therefore not
    /// decoration; it is the only instrument this project has for "was that peer's screen actually held?".
    ///
    /// AND IT WENT BLIND AGAIN THE DAY THE ARM GREW. <c>17bf9fe</c> widened
    /// <c>SaveTransferMath.CurtainHoldArmed</c> from two inputs to three — adding
    /// <c>loadBoundaryAnnounced</c>, which is the whole of that commit — and did not touch
    /// <c>State()</c>. So every <c>curtain lift PASSED gate unheld</c> line kept printing
    /// <c>holdArmed=</c> followed by a parenthesis that accounts for TWO of three terms. That is worse
    /// than silence, because it reads like a complete account: nine such lines across the 2026-08-07
    /// host+client capture, every one of them mute about the term the newest fix turns on.
    ///
    /// IT IS THE ONLY TERM THAT CAN BE TRUE AT THE BOUNDARY IT GUARDS. On a lobby FIRST start and on the
    /// new-campaign arm, <c>_begun</c> is false (Begin() is several yields away, past ReadSavegameBinary,
    /// SendBlob and the host's own PrepareEntryFromBlobCrt) and the bootstrap latch is already spent by
    /// <c>ConcludeNewCampaignBootstrap</c> — that pair IS L86's report. So on exactly the boundary the fix
    /// was written for, the printed parenthesis is <c>(sessionStarted=False newCampaignPending=False)</c>
    /// next to <c>holdArmed=True</c>: a line that contradicts itself and explains nothing. Two full logs
    /// from both sides of one session could not settle whether the host's screen had been held.
    ///
    /// WHY THIS IS A LAW AND NOT A LOGGING NICETY. This repo's dominant bug class is the silent swallow,
    /// and the standing counter-measure is to falsify each one with a law. Here the swallow is INSIDE the
    /// instrument built to catch swallows — and it is regenerative: the arm is exactly the kind of
    /// predicate that grows a term whenever a new boundary is found (two → three already), and every
    /// growth silently re-opens the same hole. Asserting the OUTCOME — the line names every input — makes
    /// the fourth term cost a red line instead of another session of blind fixes.
    ///
    /// THE SUBJECT IS THE PREDICATE'S PARAMETER LIST, NOT THE COORDINATOR'S FIELD NAMES, and that is
    /// deliberate: the fields are <c>_begun</c> / <c>_newCampaign</c> / <c>_loadBoundaryAnnounced</c> while
    /// the line says <c>sessionStarted</c> / <c>newCampaignPending</c> / <c>loadBoundaryAnnounced</c> —
    /// the reader's names are the ones the shared predicate declares, so those are the ones that must
    /// appear. It also means a term added to <c>CurtainHoldArmed</c> is caught wherever its backing state
    /// happens to live.
    ///
    /// Falsify: add a parameter to <c>SaveTransferMath.CurtainHoldArmed</c> and not to <c>State()</c> →
    /// <c>input-unnamed</c>; delete the announcement from the printed line → <c>input-unnamed</c>; make
    /// <c>State()</c> stop reporting the ANSWER it is explaining → <c>answer-unreported</c>; rename either
    /// member → <c>premise-changed</c>. POSITIVE CONTROL: the same probe is run against a name that is not
    /// in the line and must NOT find it, so a probe that has degenerated into "always true" — the way an
    /// unguarded law passes while checking nothing — is itself red.
    /// </summary>
    internal static class L173_GateEvidenceNamesEveryInput
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var state = typeof(Multiplayer.Harmony.CurtainTakedownGate).GetMethod("State", All);
            var armed = typeof(SaveTransferMath).GetMethod("CurtainHoldArmed", All);
            var wide = typeof(SaveTransferCoordinator).GetMethod("get_CurtainHoldArmed", All);

            if (state == null || armed == null || wide == null)
            {
                yield return "L173 premise-changed: CurtainTakedownGate.State, " +
                             "SaveTransferMath.CurtainHoldArmed or SaveTransferCoordinator.CurtainHoldArmed " +
                             "no longer resolves. This law's whole claim is that the gate's evidence line and " +
                             "the gate's decision have the SAME input list — it can no longer read either one, " +
                             "so re-establish which is which before trusting a 'PASSED gate unheld' line again.";
                yield break;
            }

            var literals = StringLiterals(state).ToList();
            if (literals.Count == 0)
            {
                yield return "L173 premise-changed: CurtainTakedownGate.State carries no string literals at " +
                             "all, so it can no longer be printing the inputs — the evidence line has been " +
                             "reshaped into something this law cannot read.";
                yield break;
            }

            // ── (a) EVERY INPUT OF THE DECISION IS NAMED BY THE LINE THAT EXPLAINS IT ──
            foreach (var p in armed.GetParameters())
                if (!Names(literals, p.Name))
                    yield return "L173 input-unnamed: SaveTransferMath.CurtainHoldArmed decides the curtain " +
                                 "hold on '" + p.Name + "', and CurtainTakedownGate.State — the ONE line the " +
                                 "logs carry for that decision — never names it. Every 'curtain lift PASSED " +
                                 "gate unheld' then prints holdArmed= followed by a parenthesis accounting for " +
                                 "fewer terms than the answer was made of, which reads as a complete account " +
                                 "and is not one. That is exactly how 17bf9fe's loadBoundaryAnnounced went " +
                                 "unreported on the only boundary where it is the sole true term, and why two " +
                                 "full session logs could not say whether the host's screen was held.";

            // ── (b) …AND THE LINE STILL REPORTS THE ANSWER, not only the terms ──
            if (!Names(literals, "holdArmed"))
                yield return "L173 answer-unreported: CurtainTakedownGate.State no longer prints holdArmed. " +
                             "The inputs without the conclusion cannot be checked against each other, so a gate " +
                             "that computes one thing and narrates another is invisible again.";

            // ── (c) POSITIVE CONTROL: the probe must be able to say NO ──
            // An unguarded/degenerate probe passes while checking nothing (this repo's vacuity rule, one
            // level in). If a name that is deliberately absent still 'matches', arm (a) proves nothing.
            if (Names(literals, "thisTermIsNotInTheEvidenceLine"))
                yield return "L173 POSITIVE CONTROL failed: the literal probe reports a term that is not in " +
                             "CurtainTakedownGate.State at all, so arm (a) would pass for any input list " +
                             "whatsoever — the law would be green while the evidence line said nothing.";

            // ── (d) THE LINE MUST EXPLAIN THE DECISION THAT WAS ACTUALLY TAKEN ──
            // L94 already asserts Hold() asks CurtainHoldArmed. This asserts the REPORT asks the same
            // question: a State() that recomputed the arm from its own terms could drift from the gate and
            // narrate a hold that never happened.
            if (!Program.Callees(state, typeof(SaveTransferCoordinator).Assembly).Any(Same(wide)))
                yield return "L173 answer-unreported: CurtainTakedownGate.State does not read " +
                             "SaveTransferCoordinator.CurtainHoldArmed, so the line describes a hold it worked " +
                             "out for itself rather than the one the gate acted on. The two can then disagree, " +
                             "and the log would be evidence for a decision nothing made.";
        }

        private static Func<MethodBase, bool> Same(MethodBase b) =>
            a => a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        /// <summary>Case-insensitive: the line writes the parameter's own name, but a future term may be
        /// printed with a different capitalisation and that is not what this law is about.</summary>
        private static bool Names(IEnumerable<string> literals, string term) =>
            literals.Any(s => s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Every ldstr operand in a method body. Flat scan with the same tolerance for an
        /// unaligned hit failing to resolve that L86/L94's field probes use — a false NEGATIVE here can
        /// only cost a spurious red, never a silent green.</summary>
        private static IEnumerable<string> StringLiterals(MethodBase m)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x72) continue;                 // ldstr
                int tok = BitConverter.ToInt32(il, i + 1);
                string s = null;
                try { s = m.Module.ResolveString(tok); } catch { }
                if (s != null) yield return s;
            }
        }
    }
}
