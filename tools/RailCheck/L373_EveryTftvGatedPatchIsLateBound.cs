using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RailCheck
{
    /// <summary>
    /// L373 — A PATCH THAT GATES ON TFTV IS EITHER LATE-BOUND OR DEAD, AND NOTHING ELSE SAYS WHICH.
    ///
    /// THE BUG SHAPE, already paid for once. Phoenix Point enables "Morgott.Multiplayer" BEFORE
    /// "phoenixrising.tftv", so at <c>PatchAll</c> time every TFTV type is unresolvable: a <c>[HarmonyPatch]</c>
    /// class gated on one has its <c>Prepare()</c> answer false, Harmony skips it WITHOUT A WORD, and the
    /// guard is dead in every real game. On 2026-07-12 fourteen guards were found dead exactly that way — the
    /// 126x geoscape-teardown NRE storm had never stopped. <c>TftvLateBinder</c> is the fix: it re-runs
    /// <c>PatchClassProcessor.Patch()</c> one frame after TFTV's assembly loads. But its member list is a
    /// HAND-WRITTEN array, and its own source says so in a <c>ponytail:</c> comment — "a NEW TFTV-gated
    /// [HarmonyPatch] class MUST be added here or it silently dies the same way".
    ///
    /// AND L125 CANNOT SEE IT. <c>L125_EveryPatchBinds</c> resolves every patch class's targets for real, but
    /// it runs <c>Prepare()</c> first and SKIPS the class when it answers false — deliberately, because a
    /// gated class is allowed to say "not this run" and RailCheck is a console host with no TFTV loaded. So
    /// the one class of patch that has a silent-death mode is precisely the one class L125 steps over. This
    /// law is that hole.
    ///
    /// THE DISCRIMINATOR IS EXACT, not a heuristic, and it is narrower than "mentions TFTV". This assembly
    /// holds NO reference to TFTV — it cannot, TFTV is not a build dependency — so a class that gates on TFTV
    /// names it as a STRING and there is no other way to write one
    /// (<c>AccessTools.TypeByName("TFTV.TFTVLogger")</c>, or a <c>private const</c> holding the same, which
    /// C# inlines at the use site). But the literal is only asked for inside HARMONY'S OWN TWO QUESTIONS —
    /// <c>Prepare</c> ("may I bind?") and <c>TargetMethod</c>/<c>TargetMethods</c> ("bind to what?"). A patch
    /// on a VANILLA method that merely reads TFTV state at runtime binds fine at <c>PatchAll</c> time and
    /// must NOT be in the array; scanning its whole body would drag it in and the law would demand it be
    /// patched twice. So the walk reads those three methods and nothing else.
    ///
    /// THE OUTCOME, both directions:
    ///   (a) EVERY TFTV-naming <c>[HarmonyPatch]</c> class is in <c>TftvLateBinder._patchClasses</c>.
    ///       Missing = a patch that binds in no real game, reported by nobody.
    ///   (b) EVERY class IN that list is a real patch class AND still names TFTV. A stale entry that stopped
    ///       gating on TFTV is worse than a missing one: <c>PatchAll</c> binds it at startup and
    ///       <c>BindAll</c> binds it AGAIN a frame later, so its prefix runs twice on every call.
    ///   (c) VACUITY: the walk must actually find the TFTV classes it is supposed to police. A discriminator
    ///       that silently matches nothing — a changed literal, an IL walk that abandons the body — turns
    ///       (a) into "no offenders found" forever, which is how a law gets suppressed instead of read.
    ///
    /// Falsify: delete any entry from <c>TftvLateBinder._patchClasses</c> → (a) red naming it. Add a class
    /// that names no TFTV type → (b) red. Change the <c>"TFTV."</c> prefix the classes use → (c) red.
    /// </summary>
    internal static class L373_EveryTftvGatedPatchIsLateBound
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>How a TFTV-gated class in this assembly can possibly name its target.</summary>
        private const string TftvLiteral = "TFTV.";

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(Multiplayer.Network.Sync.DiffEngine).Assembly;
            var binder = mod.GetType("Multiplayer.Harmony.TftvLateBinder");
            var listField = binder?.GetField("_patchClasses", All);
            var registered = listField?.GetValue(null) as Type[];

            if (binder == null || registered == null)
            {
                yield return "L373 premise-changed: Multiplayer.Harmony.TftvLateBinder._patchClasses no longer " +
                             "resolves as a Type[]. The whole point of that array is that a TFTV-gated patch " +
                             "binds in no real game unless it is listed, so with the array gone this law " +
                             "checks NOTHING rather than being satisfied";
                yield break;
            }

            var harmony = new HarmonyLib.Harmony("railcheck.L373");
            var containerField = AccessTools.Field(typeof(PatchClassProcessor), "containerAttributes");
            var namesTftv = new List<Type>();

            foreach (var type in AccessTools.GetTypesFromAssembly(mod))
            {
                bool isPatchClass = false;
                try { isPatchClass = containerField.GetValue(new PatchClassProcessor(harmony, type)) != null; }
                catch { continue; }
                if (!isPatchClass) continue;
                if (Mentions(type)) namesTftv.Add(type);
            }

            // ═══ (c) VACUITY FIRST — an empty scan makes (a) meaningless ═══
            if (namesTftv.Count == 0)
            {
                yield return "L373 discriminator-found-nothing: not one [HarmonyPatch] class in the mod " +
                             "assembly loads a string starting with \"" + TftvLiteral + "\". Either every " +
                             "TFTV guard is gone, or the way they name TFTV changed — and in the second case " +
                             "arm (a) below can never fail again, so the fourteen-dead-guards bug of " +
                             "2026-07-12 would come back unseen";
                yield break;
            }

            // ═══ (a) EVERY TFTV-GATED CLASS IS LATE-BOUND ═══
            foreach (var t in namesTftv.Where(t => !registered.Contains(t))
                                       .OrderBy(t => t.FullName, StringComparer.Ordinal))
                yield return "L373 tftv-patch-not-late-bound: " + t.FullName + " is a [HarmonyPatch] class " +
                             "that names a TFTV type, but it is NOT in TftvLateBinder._patchClasses. Phoenix " +
                             "Point enables this mod BEFORE TFTV, so at PatchAll time its target is " +
                             "unresolvable, Prepare() answers false and Harmony skips it in silence — the " +
                             "patch exists in the source, is dead in every real game, and no log line " +
                             "anywhere says so (14 guards died exactly this way on 2026-07-12)";

            // ═══ (b) AND EVERY LISTED CLASS STILL BELONGS THERE ═══
            foreach (var t in registered)
            {
                if (t == null)
                {
                    yield return "L373 late-bind-list-has-a-null: TftvLateBinder._patchClasses contains a " +
                                 "null entry, which BindAll will walk into";
                    continue;
                }
                bool isPatchClass = false;
                try { isPatchClass = containerField.GetValue(new PatchClassProcessor(harmony, t)) != null; }
                catch { isPatchClass = false; }
                if (!isPatchClass)
                    yield return "L373 late-bind-list-holds-a-non-patch: " + t.FullName + " is registered for " +
                                 "late binding but carries no [HarmonyPatch] container attribute, so " +
                                 "PatchClassProcessor.Patch() binds nothing for it and the entry is a " +
                                 "reassuring line in a list that does nothing";
                else if (!namesTftv.Contains(t))
                    yield return "L373 late-bind-list-is-stale: " + t.FullName + " is registered for late " +
                                 "binding but no longer names a TFTV type, so it is NOT gated any more: " +
                                 "PatchAll binds it at startup and BindAll binds it a SECOND time one frame " +
                                 "after TFTV loads. Its prefix then runs twice on every call — a duplicate " +
                                 "far harder to see than a missing patch";
            }
        }

        /// <summary>Harmony's own binding hooks, and ONLY these. The question this law asks is not "does the
        /// class know about TFTV" — a patch on a VANILLA method that merely reads TFTV state at runtime binds
        /// perfectly well at <c>PatchAll</c> time and must NOT be late-bound (it would then be patched twice).
        /// The question is whether HARMONY'S DECISION depends on TFTV, and Harmony asks exactly two things:
        /// <c>Prepare</c> (may I bind?) and <c>TargetMethod</c>/<c>TargetMethods</c> (bind to what?).</summary>
        private static readonly string[] BindingHooks = { "Prepare", "TargetMethod", "TargetMethods" };

        /// <summary>Does this patch class's BINDING depend on TFTV — i.e. does one of Harmony's two questions
        /// resolve a <c>"TFTV.…"</c> type name? Const strings inline at the use site, so a class that keeps
        /// its type name in a <c>private const</c> (most of them do) still carries the literal here.</summary>
        private static bool Mentions(Type t)
        {
            try
            {
                return t.GetMethods(All)
                        .Where(m => BindingHooks.Contains(m.Name, StringComparer.Ordinal))
                        .Any(m => Program.StringRefs(m)
                                         .Any(s => s != null &&
                                                   s.StartsWith(TftvLiteral, StringComparison.Ordinal)));
            }
            catch { return false; }
        }
    }
}
