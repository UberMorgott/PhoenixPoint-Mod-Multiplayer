using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Defs;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L367 — NO REFUSAL MAY BLAME MOD PARITY UNLESS IT ACTUALLY TESTED IT.
    ///
    /// THE COST, measured (2026-08-08). Three tactical refusals said "mod parity: law 10 should have blocked
    /// the join" from code that had asked the def repository NOTHING. A whole session's investigation went
    /// into the join gate on the strength of those sentences; the gate had worked correctly all along and all
    /// three were our own bugs — an empty item address, a body part with no rung, an equipment list that
    /// simply differed. The worst of them, <c>ResolveEquipment</c>, could not have been right by accident: it
    /// scanned one actor's <c>comp.Items</c> and never asked <c>DefRepository.GetDef</c>, so "this def does not
    /// exist here" and "this actor is not carrying it" were INDISTINGUISHABLE BY CONSTRUCTION and one sentence
    /// covered both.
    ///
    /// A log line is a diagnosis. Pointing it at the wrong subsystem is not a cosmetic defect: it is the most
    /// expensive kind of wrong this repo produces, because it is believed.
    ///
    /// THE RULE, and it is narrow on purpose: a method whose text blames parity must itself reach the def
    /// repository. Where the probe IS there, the citation stays and is correct (<c>Def&lt;T&gt;</c>,
    /// <c>TacticalDamageSync.ResolveItem</c>) — this law is not a ban on the phrase, it is a ban on asserting
    /// it untested. Scoped to the tactical rail's own files plus the one <c>GeoModalMirror</c> line named in
    /// the report; the rest of that file's citations are outside this arc's edit scope and are not judged here.
    ///
    /// Falsify (each verified RED, then restored): put "mod parity should have made that impossible (law 10)"
    /// back into <c>ResolveEquipment</c> → (a); into <c>TacAbilityTargetCodec.ResolveItem</c> or
    /// <c>ResolveReceiver</c> → (a); revert the <c>GeoModalMirror</c> class-mismatch sentence → (b); delete the
    /// def probe from <c>ResolveEquipment</c> while keeping its two-way message → (c).
    /// </summary>
    internal static class L367_NoMessageBlamesParityWithoutTestingIt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The ASSERTIVE forms, not the word "parity". A refusal that says "nothing here tested the
        /// def sets, so this says nothing about mod parity" is the fix, not the defect — matching on the noun
        /// would flag every disclaimer written to replace a blame and make the law un-satisfiable.</summary>
        private static readonly string[] Blames =
        {
            "should have made that impossible",
            "should have made the def impossible",
            "should have blocked the join",
        };

        internal static IEnumerable<string> Check()
        {
            var asm = typeof(TacticalActorKey).Assembly;
            var scope = new[]
            {
                typeof(TacticalCommandSync), typeof(TacticalActorKey), typeof(TacAbilityTargetCodec),
                typeof(TacticalDamageSync), typeof(TacticalStatusSet),
            };
            int scanned = 0;
            foreach (var t in scope.SelectMany(t => new[] { t }.Concat(t.GetNestedTypes(All))))
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                   .Concat(t.GetConstructors(All)))
                {
                    scanned++;
                    var blamed = Program.StringRefs(m)
                                        .Where(s => Blames.Any(b => s.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0))
                                        .ToList();
                    if (blamed.Count == 0 || TouchesDefRepository(m, asm, 0)) continue;
                    // ── (a) ──
                    yield return "L367 parity-blamed-untested: " + t.Name + "." + m.Name + " tells the reader that " +
                                 "mod parity should have prevented what it just refused, and it never asks the def " +
                                 "repository anything. It therefore cannot tell 'this def does not exist on this " +
                                 "peer' from 'this object does not have it' — the two failures that sentence covers " +
                                 "are indistinguishable by construction. Three lines of exactly this shape sent a " +
                                 "whole session after a join gate that was working. Offending text: \"" +
                                 Trim(blamed[0]) + "\"";
                }
            if (scanned < 50)
                yield return "L367 premise-changed: only " + scanned + " method(s) were scanned, so this law is " +
                             "passing over a rail it cannot see — the scoped types moved or lost their bodies. An " +
                             "unscanned file is not a clean one, and a text law with nothing to read is the purest " +
                             "form of vacuous green.";

            // ── (b) THE ONE GeoModalMirror LINE NAMED IN THE REPORT ──────────────────
            var mirror = asm.GetType("Multiplayer.Rail.GeoModalMirror") ?? asm.GetType("Multiplayer.Network.Sync.GeoModalMirror");
            if (mirror == null)
                yield return "L367 mirror-gone: GeoModalMirror no longer resolves, so the class-mismatch sentence " +
                             "named in the report cannot be checked.";
            else
            {
                bool reworded = mirror.GetNestedTypes(All).Concat(new[] { mirror })
                    .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>())
                    .SelectMany(Program.StringRefs)
                    .Any(s => s.IndexOf("nothing here tested the peers' def sets", StringComparison.Ordinal) >= 0);
                if (!reworded)
                    yield return "L367 mirror-still-blames: GeoModalMirror's class-mismatch refusal no longer says " +
                                 "that it tested no def set. That refusal fires when the SHIPPED PATH resolves to a " +
                                 "different class here — a graph difference, which parity has nothing to do with — " +
                                 "and blaming the join for it is one of the three sentences that cost the " +
                                 "2026-08-08 investigation.";
            }

            // ── (c) AND THE PROBE THAT REPLACED THE BLAME IS STILL THERE ─────────────
            var resolveEq = typeof(TacticalCommandSync).GetMethod("ResolveEquipment", All);
            if (resolveEq == null)
                yield return "L367 probe-subject-gone: TacticalCommandSync.ResolveEquipment no longer resolves.";
            else if (!TouchesDefRepository(resolveEq, asm, 0))
                yield return "L367 probe-removed: ResolveEquipment no longer asks the def repository whether the guid " +
                             "exists at all. Its message then collapses back into one sentence for two different " +
                             "failures — a def set that really differs, and a soldier who simply is not holding the " +
                             "thing — which is the exact ambiguity that made the original line unfalsifiable.";
        }

        /// <summary>Does this method reach <c>DefRepository</c>, directly or through one call? One level,
        /// deliberately: a probe worth citing is either here or in the helper immediately under it, and a
        /// deeper walk would let any method in a chain that eventually touches defs claim the excuse.</summary>
        private static bool TouchesDefRepository(MethodBase m, Assembly asm, int depth)
        {
            // CalleeSequence, not Callees: DefRepository is in the GAME assembly and the one-assembly walker
            // cannot see a single call into it — which is how the first draft of this law declared that a
            // method calling GetDef on the line above had never asked the def repository anything.
            var callees = Program.CalleeSequence(m);
            if (callees.Any(c => c.DeclaringType == typeof(DefRepository))) return true;
            if (depth >= 1) return false;
            return callees.Any(c => c.Module.Assembly == asm && TouchesDefRepository(c, asm, depth + 1));
        }

        private static string Trim(string s) => s.Length <= 90 ? s : s.Substring(0, 90) + "…";
    }
}
