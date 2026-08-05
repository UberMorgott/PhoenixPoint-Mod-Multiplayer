using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RailCheck
{
    /// <summary>
    /// L126 — A TRANSPILER OF OURS SUBSTITUTES A COMPUTATION. IT NEVER WRITES ONE DOWN. A transpiler is the
    /// only patch shape that edits the game's own control flow in place, and the only one whose mistake has
    /// no seam a reviewer can see: the diff shows C#, the damage is IL. This law fixes the one thing our
    /// transpilers are allowed to do — swap a computation for a deterministic one of ours, so the value
    /// flows on exactly where the game was going to send it — and makes everything else a build failure.
    ///
    /// THE NIGHT IT WAS WORTH WRITING (2026-08-05, commit <c>b3d9269</c>, since reverted). A transpiler on
    /// <c>TacticalLevelController.FireWeaponAtTargetCrt</c>'s coroutine body reached the local
    /// <c>stepOutNeeded</c> — the flag deciding whether the shooter walks out of cover before firing — and
    /// forced the ACTING peer's answer into every mirror's copy of it. On a mirror already standing at the
    /// target position that is an instruction to navigate to where it already is: the coroutine entered a
    /// move with nowhere to walk and parked there forever. The shot fired, the actor never left
    /// <c>ExecutingAbilities</c>, the HUD held its ability state and the player's input went dead — with no
    /// exception, no log line and a GREEN harness, because nothing here read what a transpiler emits.
    ///
    /// ARM (a) — <c>transpiler-stores</c> / <c>transpiler-hijacks-store</c>. EXECUTED, not reasoned: each
    /// declared <c>Transpiler</c> is resolved to its real <c>[HarmonyPatch]</c> targets, handed the REAL
    /// instruction stream of each (<c>PatchProcessor.GetOriginalInstructions</c>, the same call L125's
    /// arm (c) makes), and the output is diffed against the input for two things.
    ///
    ///   1. NO STORE THAT WAS NOT ALREADY THERE. Any <c>stloc*</c>, <c>starg*</c>, <c>stfld</c> or
    ///      <c>stsfld</c> in the output that the input did not carry is a violation.
    ///   2. NO STORE WHOSE INPUT VALUE WE CHANGED. This is the shape <c>b3d9269</c> actually had, and it is
    ///      why check 1 alone would not have caught it: the transpiler ADDED NO STORE. It inserted
    ///      <c>ldarg.0; call StepOutForShot</c> immediately BEFORE the <c>stfld</c> the game already had, so
    ///      the game's own store wrote OUR value. Emitting the store and hijacking the store are the same
    ///      act, so the arm pairs the k-th store of the output with the k-th of the input and requires the
    ///      instruction feeding it to be unchanged.
    ///
    /// WHAT IT DELIBERATELY OVER-COVERS. A legitimate store goes RED too — there is no attempt to decide
    /// whether a stored value is peer-derived, because that decision is exactly the one <c>b3d9269</c> got
    /// wrong while looking correct in C#. A transpiler that genuinely must write takes a NAMED entry in
    /// <see cref="Exempt"/> plus a written argument, in the law, for why the value it stores is the same on
    /// every peer. The list starts EMPTY and is empty today.
    ///
    /// WHAT IT DOES NOT COVER, on purpose (this is a tripwire, not a dataflow analyser):
    ///   · A substituted value that reaches a store SEVERAL instructions later still passes check 2, which
    ///     only reads the store's immediate predecessor.
    ///   · PREFIXES AND POSTFIXES. A prefix writing a <c>ref</c> argument or a <c>__state</c> the postfix
    ///     hands back can strand a coroutine identically — same hazard, different opcode, no IL to diff.
    ///     That is a different arm and it is not written yet.
    ///
    /// A TRANSPILER THAT MATCHED NOTHING is the sibling failure and is ALREADY a build failure here:
    /// <c>L125_EveryPatchBinds</c>'s arm (c) <c>transpiler-missed</c> runs every transpiler against every
    /// real target and fails when the output is byte-for-byte the input. It is not repeated in this law —
    /// one fault, one red line.
    ///
    /// FALSIFIABILITY (both observed 2026-08-05, quoted in full in the commit that added this file).
    /// Restoring <c>b3d9269</c>'s <c>RelayedStepOutIsTheSameOnEveryPeer</c> turns check 2 red naming the
    /// hijacked <c>stfld</c> for <c>stepOutNeeded</c>; adding a bare <c>stloc</c> to it turns check 1 red.
    /// </summary>
    internal static class L126_TranspilerSubstitutesNeverStores
    {
        /// <summary>Patch classes allowed to emit or feed a store. EMPTY ON PURPOSE — see the law above:
        /// an entry here is only half of an exemption, the other half is the written argument for why the
        /// stored value is identical on every peer.</summary>
        private static readonly HashSet<string> Exempt = new HashSet<string>();

        internal static IEnumerable<string> Check()
        {
            foreach (var pair in TranspilerTargets())
            {
                var transpiler = pair.Key;
                var target = pair.Value;
                var owner = transpiler.DeclaringType.Name;
                if (Exempt.Contains(owner)) continue;

                List<string> before = null, after = null;
                try
                {
                    // Keys are taken BEFORE the run on purpose: a transpiler is handed the very instruction
                    // objects it edits (b3d9269's cleared their labels), so "what it was given" has to be
                    // recorded while it still is that.
                    var input = PatchProcessor.GetOriginalInstructions(target);
                    before = input.Select(Key).ToList();
                    after = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { input }))
                            .Select(Key).ToList();
                }
                catch
                {
                    continue;   // L125's transpiler-threw owns this; one fault, one red line
                }

                var added = Stores(after);
                foreach (var s in Stores(before)) added.Remove(s);   // multiset difference
                if (added.Count > 0)
                {
                    yield return "L126 transpiler-stores: " + owner + " on " + Describe(target) +
                                 " emits a store the game's own IL did not have (" +
                                 string.Join(", ", added.Distinct().ToArray()) + ") — a transpiler of ours " +
                                 "substitutes a computation, it never writes one down";
                    continue;   // the k-th-store pairing below is meaningless once the counts differ
                }

                var inStores = StoreSites(before);
                var outStores = StoreSites(after);
                for (int k = 0; k < Math.Min(inStores.Count, outStores.Count); k++)
                {
                    if (inStores[k].Key != outStores[k].Key || inStores[k].Value == outStores[k].Value) continue;
                    yield return "L126 transpiler-hijacks-store: " + owner + " on " + Describe(target) +
                                 " changes what feeds the game's own '" + outStores[k].Key + "' — it was fed by '" +
                                 inStores[k].Value + "' and is now fed by '" + outStores[k].Value +
                                 "' — the game stores OUR value into its own state";
                }
            }
        }

        /// <summary>Every (Transpiler, real target) pair the mod declares. Resolution failures are SILENT
        /// here by design: L125 is the law that fails on a target it cannot name or cannot emit into, and a
        /// second red line for the same fault is noise.</summary>
        private static IEnumerable<KeyValuePair<MethodInfo, MethodBase>> TranspilerTargets()
        {
            var mod = typeof(Multiplayer.Network.Sync.DiffEngine).Assembly;
            var harmony = new HarmonyLib.Harmony("railcheck.L126");
            var pcpType = typeof(PatchClassProcessor);
            var getBulk = AccessTools.Method(pcpType, "GetBulkMethods");
            var containerField = AccessTools.Field(pcpType, "containerAttributes");
            var patchMethodsField = AccessTools.Field(pcpType, "patchMethods");
            var getOriginal = AccessTools.Method(typeof(HarmonyLib.Harmony).Assembly.GetType("HarmonyLib.PatchTools"),
                                                 "GetOriginalMethod");

            foreach (var type in AccessTools.GetTypesFromAssembly(mod))
            {
                var transpiler = AccessTools.GetDeclaredMethods(type)
                                            .FirstOrDefault(m => m.Name == "Transpiler" &&
                                                                 L125_EveryPatchBinds.IsTranspiler(m));
                if (transpiler == null) continue;

                var pcp = new PatchClassProcessor(harmony, type);
                if (containerField.GetValue(pcp) == null) continue;

                List<MethodBase> targets = null;
                try
                {
                    // Prepare() first, exactly as PatchClassProcessor.Patch does — a gated class saying
                    // "not this run" has no IL to check.
                    var prepare = AccessTools.GetDeclaredMethods(type)
                                             .FirstOrDefault(m => m.Name == "Prepare" && m.GetParameters().Length == 0);
                    if (prepare == null || Equals(prepare.Invoke(null, null), true))
                        targets = (List<MethodBase>)getBulk.Invoke(pcp, null);
                }
                catch { continue; }
                if (targets == null) continue;

                if (targets.Count == 0)
                {
                    targets = new List<MethodBase>();
                    foreach (var ap in (IEnumerable<object>)patchMethodsField.GetValue(pcp))
                    {
                        var info = (HarmonyMethod)AccessTools.Field(ap.GetType(), "info").GetValue(ap);
                        var resolved = (MethodBase)getOriginal.Invoke(null, new object[] { info });
                        if (resolved != null) targets.Add(resolved);
                    }
                }

                foreach (var t in targets)
                    if (L125_EveryPatchBinds.Emittable(t))
                        yield return new KeyValuePair<MethodInfo, MethodBase>(transpiler, t);
            }
        }

        /// <summary>The four families that write somewhere the game reads back. Matched by opcode NAME, so
        /// every short form (<c>stloc.0</c>, <c>stloc.s</c>, <c>starg.s</c>) is covered without listing them.</summary>
        private static bool IsStore(string key) =>
            key.StartsWith("stloc", StringComparison.Ordinal) || key.StartsWith("starg", StringComparison.Ordinal) ||
            key.StartsWith("stfld", StringComparison.Ordinal) || key.StartsWith("stsfld", StringComparison.Ordinal);

        private static List<string> Stores(List<string> il) => il.Where(IsStore).ToList();

        /// <summary>Each store paired with the instruction that feeds it — its immediate predecessor, which
        /// is where the value being written comes from.</summary>
        private static List<KeyValuePair<string, string>> StoreSites(List<string> il)
        {
            var sites = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < il.Count; i++)
                if (IsStore(il[i]))
                    sites.Add(new KeyValuePair<string, string>(il[i], i == 0 ? "<nothing>" : il[i - 1]));
            return sites;
        }

        private static string Key(CodeInstruction i) =>
            i.opcode.Name + "|" + (i.operand == null ? "" : i.operand.ToString());

        private static string Describe(MethodBase m) =>
            m.DeclaringType.FullName.Replace('+', '/') + "::" + m.Name;
    }
}
