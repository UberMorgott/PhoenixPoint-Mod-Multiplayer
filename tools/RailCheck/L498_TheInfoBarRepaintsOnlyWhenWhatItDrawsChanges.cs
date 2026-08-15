using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L498 — THE TOP-RIGHT INFO BAR REPAINTS ONLY WHEN WHAT IT DRAWS CHANGES, THROUGH THE SAME ONE GATE
    /// THE AGENDA STRIP USES.
    ///
    /// THE DEFECT (architecture audit, 2026-08-15). `RefreshAgendaTracker` got a change key in be95ff9
    /// (L492) because its rebuild destroys and re-creates every row and the strip visibly flickered.
    /// `RefreshInfoBar`, added one commit earlier in 149d9d6, got none and still ran on EVERY rail flush —
    /// measured `marks=10`, i.e. about ten times a second on a live geoscape. Same defect, one half fixed.
    /// The cost is not flicker here, it is what hangs off the method: `UIModuleInfoBar.UpdatePopulation` is
    /// the seam TFTV's `TopInforBar`:127 postfixes, and that postfix does string `Transform.Find` lookups, a
    /// LINQ walk of every alien base, localisation lookups and a sprite load — ten times a second, forever.
    ///
    /// ONE GATE, NOT A SECOND MECHANISM. `OpenUiRepaint.RepaintNeeded(strip, key)` is now the only
    /// repaint-if-changed decision in the file and both strips are expressed through it —
    /// `AgendaNeedsRebuild` is a one-line wrapper over it. That consolidation is the point: the owner's
    /// standing complaint is that this repo keeps growing a bespoke patch per panel instead of one generic
    /// mechanism, and a THIRD strip must cost a key function, not a mechanism.
    ///
    /// REACTIVITY IS STILL THE HARD MANDATE, so the arms below run BOTH ways: an unchanged key must skip and
    /// a changed or unreadable key must repaint. Since §B.8/L543 the bar's key is not a hand-rolled
    /// `InfoBarKey` string — that read-set-written-backwards, one-second `Time.realtimeSinceStartup` floor
    /// and all, is deleted — but `OpenUiRepaint.ScopeKey("UIModuleInfoBar")`, a generation that moves when a
    /// path under one of the bar's DECLARED prefixes was touched. Arm (e) therefore checks the DECLARATION
    /// covers every source the bar draws, instead of IL-scanning a builder that no longer exists.
    ///
    /// THE ARMS (all EXECUTED over the real methods):
    ///   (a) <c>gate-unwired</c> — `RefreshInfoBar` actually calls `InfoBarNeedsRefresh`, and that routes
    ///       through `RepaintNeeded`. Without this the key could be perfect and never consulted. The
    ///       indirection is L543's ordering fix: the module's LIVENESS is asked before the memory is
    ///       touched, so a key recorded against a bar that is not yet Init'd cannot burn the refresh it
    ///       owes.
    ///   (b) <c>unchanged-repaints</c> — the same key twice: the second call skips.
    ///   (c) <c>changed-skipped</c>, THE REACTIVITY HALF — a CHANGED key repaints, and a NULL key (model
    ///       unreadable) repaints. A gate that answered "skip" once seeded would satisfy (b) and freeze the
    ///       strip for the session, which is strictly worse than repainting too often.
    ///   (d) <c>strips-share-one-key</c> — two different strips passing the SAME key must both repaint. The
    ///       consolidation replaced a single `_agendaSignature` field, and a shared slot would make one
    ///       strip's repaint swallow the other's.
    ///   (e) <c>declaration-blind-to-<i>source</i></c> — the bar DECLARES the rail roots the three values
    ///       `UpdatePopulation` draws land under ("S#": world population is counted off the sites/havens),
    ///       the diplomacy value the postfix draws ("F#": `FactionDiplomacy` descends from `GeoFaction`,
    ///       docs/rail-baseline.txt:527/561/594) and the alien base list behind its meter ("S#"). A
    ///       declaration missing one of them would go green on every other arm while that half of the
    ///       strip froze.
    ///   (f) <c>agenda-off-the-shared-gate</c> — `AgendaNeedsRebuild` still routes through `RepaintNeeded`,
    ///       so the two strips cannot drift back into two mechanisms.
    ///
    /// Falsify (verified RED, then restored):
    ///   • drop the `RepaintNeeded` call from `RefreshInfoBar` → (a)
    ///   • make `RepaintNeeded` return true unconditionally → (b)
    ///   • make it return false unconditionally, or drop the null arm → (c)
    ///   • key `RepaintNeeded` on the key alone instead of (strip, key) → (d)
    ///   • drop "F#" or "S#" from the bar's row in `UiNativeRepaint.DeclaredPrefixes` → (e)
    ///   • give `AgendaNeedsRebuild` its own field again → (f)
    /// </summary>
    internal static class L498_TheInfoBarRepaintsOnlyWhenWhatItDrawsChanges
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var gate = typeof(OpenUiRepaint).GetMethod("RepaintNeeded", Any);
            var refresh = typeof(OpenUiRepaint).GetMethod("RefreshInfoBar", Any);
            var live = typeof(OpenUiRepaint).GetMethod("InfoBarNeedsRefresh", Any);
            var agenda = typeof(OpenUiRepaint).GetMethod("AgendaNeedsRebuild", Any);
            if (gate == null || refresh == null || live == null || agenda == null)
            {
                yield return "L498 premise-changed: OpenUiRepaint.RepaintNeeded / .RefreshInfoBar / " +
                             ".InfoBarNeedsRefresh / .AgendaNeedsRebuild did not resolve. The one repaint-if-changed " +
                             "gate has been renamed, removed, or split back into a mechanism per strip, so " +
                             "every arm below would pass vacuously — and both failure directions are silent: " +
                             "a strip that repaints ten times a second says nothing in the log, and neither " +
                             "does one that never repaints again.";
                yield break;
            }
            var mod = typeof(OpenUiRepaint).Assembly;

            // ── (a) the gate is actually consulted ─────────────────────────────────────────────────────
            if (!Program.Callees(refresh, mod).Any(c => Same(c, live)) ||
                !Program.Callees(live, mod).Any(c => Same(c, gate)))
                yield return "L498 gate-unwired: RefreshInfoBar does not reach RepaintNeeded through " +
                             "InfoBarNeedsRefresh, so it runs on " +
                             "EVERY rail flush — about ten times a second on a live geoscape — and drives " +
                             "every postfix hung on UIModuleInfoBar.UpdatePopulation at that rate. That is " +
                             "the 2026-08-15 audit finding verbatim: the agenda strip got its key in be95ff9 " +
                             "and this one, added in 149d9d6, never did.";

            // ── (b) unchanged skips ────────────────────────────────────────────────────────────────────
            string slot = "L498-probe-" + Guid.NewGuid().ToString("N");
            Gate(gate, slot, "same");
            if (Gate(gate, slot, "same"))
                yield return "L498 unchanged-repaints: RepaintNeeded returned true for a key it had just " +
                             "been given. The gate exists to answer \"nothing this strip draws has moved\", " +
                             "and a gate that never says so is the un-keyed refresh with extra steps.";

            // ── (c) THE REACTIVITY HALF: changed and unreadable both repaint ────────────────────────────
            if (!Gate(gate, slot, "moved"))
                yield return "L498 changed-skipped: RepaintNeeded returned false for a CHANGED key. " +
                             "Reactivity is a hard mandate — a peer's change must reach every other peer's " +
                             "already-open UI — so a strip that skips a real change is a defect, not a " +
                             "cosmetic issue, and it would be invisible: nothing logs a repaint that did not " +
                             "happen.";
            if (!Gate(gate, slot, null))
                yield return "L498 changed-skipped: RepaintNeeded returned false for a NULL key. Null means " +
                             "the model could not be read, and the safe direction there is to repaint: an " +
                             "unreadable model may cost a redundant repaint and may never cost a stale strip.";

            // ── (d) each strip keeps its own memory ────────────────────────────────────────────────────
            string a = "L498-a-" + Guid.NewGuid().ToString("N"), b = "L498-b-" + Guid.NewGuid().ToString("N");
            if (!Gate(gate, a, "shared") || !Gate(gate, b, "shared"))
                yield return "L498 strips-share-one-key: two DIFFERENT strips passing the same key did not " +
                             "both repaint. The gate replaced a single _agendaSignature field with a slot per " +
                             "strip; collapse that back to one slot and each strip's repaint swallows the " +
                             "other's, which is a stale strip produced by the very consolidation meant to " +
                             "make a third one cheap.";

            // ── (e) the DECLARATION covers everything the strip draws ──────────────────────────────────
            string[] declared;
            if (!UiNativeRepaint.DeclaredPrefixes.TryGetValue("UIModuleInfoBar", out declared) ||
                declared == null || declared.Length == 0)
                yield return "L498 premise-changed: UIModuleInfoBar has no row in " +
                             "UiNativeRepaint.DeclaredPrefixes, so the coverage arm cannot run. An undeclared " +
                             "surface repaints on everything — safe, but that is the ten-times-a-second " +
                             "refresh this law exists to stop; declare it rather than leaving it out.";
            else
                foreach (var source in Sources())
                {
                    if (source.Getter == null)
                    {
                        yield return "L498 premise-changed: " + source.Name + " did not resolve on the game " +
                                     "type, so the coverage arm for it cannot run. Re-ground it against the " +
                                     "current decompile rather than dropping the source.";
                        continue;
                    }
                    if (Array.IndexOf(declared, source.Prefix) >= 0) continue;
                    yield return "L498 declaration-blind-to-" + source.Name + ": the info bar does not declare " +
                                 QUOTE + source.Prefix + QUOTE + ", the rail root it lands under, so " +
                                 source.What + " can change without moving the bar's ScopeKey and the strip " +
                                 "keeps drawing the old number until something else moves. Every other arm " +
                                 "stays green while that half of the bar is frozen.";
                }

            // ── (f) the agenda strip still rides the same gate ─────────────────────────────────────────
            if (!Program.Callees(agenda, mod).Any(c => Same(c, gate)))
                yield return "L498 agenda-off-the-shared-gate: AgendaNeedsRebuild no longer routes through " +
                             "RepaintNeeded. Two strips answering the same question with two mechanisms is " +
                             "exactly the per-panel sprawl this consolidation removed, and the second copy " +
                             "is always the one that silently stops matching the first.";
        }

        private const string QUOTE = "\"";

        private sealed class Source
        {
            internal string Name;
            internal MethodInfo Getter;
            /// <summary>The rail-root prefix this drawn value lands under, so the coverage arm is a
            /// statement about the DECLARATION rather than about a deleted string builder.</summary>
            internal string Prefix;
            internal string What;
        }

        /// <summary>The values the bar actually draws — the three <c>UpdatePopulation</c>:276-288 reads, plus
        /// the first-party state TFTV's <c>TopInforBar</c>:127 postfix draws on top of them.</summary>
        private static IEnumerable<Source> Sources()
        {
            yield return Get(typeof(GeoscapeView), "WorldPopulation", "S#",
                "the live world population, i.e. the bar's own fill and its percentage text");
            yield return Get(typeof(GeoscapeView), "GameOverWorldPopulation", "S#",
                "the doom threshold the bar marks and names in its tooltip");
            yield return Get(typeof(GeoscapeView), "StartingWorldPopulation", "S#",
                "the denominator both of the bar's two ratios are taken against");
            yield return new Source
            {
                Name = "PartyDiplomacy.GetDiplomacy",
                Getter = typeof(PartyDiplomacy).GetMethod("GetDiplomacy", Any, null,
                    new[] { typeof(IDiplomaticParty) }, null),
                Prefix = "F#",
                What = "each faction's standing toward Phoenix — the reputation percentages, and the STORED " +
                       "field the rail writes directly, which is the whole reason this repaint exists",
            };
            yield return Get(typeof(GeoAlienFaction), "Bases", "S#",
                "the alien base list the infestation meter is counted from");
        }

        private static Source Get(Type owner, string property, string prefix, string what) => new Source
        {
            Name = owner.Name + "." + property,
            Getter = owner.GetProperty(property, Any)?.GetGetMethod(nonPublic: true),
            Prefix = prefix,
            What = what,
        };

        private static bool Gate(MethodInfo gate, string strip, string key) =>
            (bool)gate.Invoke(null, new object[] { strip, key });

        /// <summary>Token scan RESOLVED through the calling module — the L492 helper, and it has to be this
        /// one: a call into the game assembly is a MemberRef whose token never equals the callee's def
        /// token, so a same-assembly comparison reports every game-side source as uncovered.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                int token = BitConverter.ToInt32(il, i);
                if (token == callee.MetadataToken && m.Module == callee.Module) return true;
                MethodBase resolved = null;
                try { resolved = m.Module.ResolveMethod(token); } catch { }
                if (resolved != null && resolved.MetadataToken == callee.MetadataToken &&
                    resolved.Module == callee.Module) return true;
            }
            return false;
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
