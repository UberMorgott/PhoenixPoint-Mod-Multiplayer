using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L422 — A TARGET THIS MOD PREFIXES TWICE MUST DECLARE THE ORDER, AND THE CANCELLER CANNOT BE TRUSTED
    /// TO DO IT.
    ///
    /// THE FAILURE (e017408, then 7e88d0d because the first attempt was a runtime NO-OP). Two of the mod's own
    /// prefixes sit on <c>TacticalView.GoToGeoscape</c>: <c>ReturnCountdown.ReturnHoldPatch</c>, which returns
    /// FALSE while its five-second strip counts, and <c>TacLeaveBattleCapture</c>, which latches
    /// <c>LeftBattle</c> and announces the leave to every peer. With no declared order, whether the leave went
    /// out at the CLICK or five seconds later at the RELEASE came down to Harmony's registration order —
    /// and the hold can be ABANDONED, so an announcement at the click carried every other peer out of a battle
    /// this peer never left.
    ///
    /// AND THEN THE OBVIOUS FIX DID NOTHING. <c>[HarmonyPriority(Priority.First)]</c> on the hold was shipped
    /// as a whole commit that changed no behaviour at runtime. MEASURED against the ModSDK's own
    /// <c>0Harmony.dll</c> (HarmonyLib 2.2.0.0, 2026-08-11): a prefix returning false cancels only the
    /// prefixes that CAN AFFECT THE ORIGINAL — ones returning <c>bool</c>, or taking a <c>ref</c>/<c>out</c>
    /// the body reads. A <c>void</c> prefix is emitted outside that skip guard and runs at ANY priority. So
    /// the priority was real and inert at the same time, which is the worst kind of fix: it reads as correct
    /// forever. The capture cannot become <c>bool</c> either — L64 forbids that return type on this funnel,
    /// because a capture able to block it would strand the clicking peer on his own summary screen — so it
    /// READS the holder's state (<c>ReturnCountdown.Holding</c>) instead, and the priority stays load-bearing
    /// for a different reason: the hold has to ARM before the capture asks.
    ///
    /// THE ARMS, both with ZERO domain knowledge — they catch the next collision anywhere in the mod:
    ///   (a) <c>unordered-prefix-collision</c> — group every <c>[HarmonyPatch]</c> class by the (declaring
    ///       type, method name) it names. Where two or more of OUR prefixes land on one target, each must
    ///       declare <c>[HarmonyPriority]</c>, <c>[HarmonyBefore]</c> or <c>[HarmonyAfter]</c> (on the prefix
    ///       or on its class). Registration order is <c>GetTypesFromAssembly</c> order — a file rename decides
    ///       it — and nothing anywhere says which one won.
    ///   (b) <c>void-prefix-trusts-the-skip</c> — where one of those prefixes returns <c>bool</c> (it can
    ///       cancel) and another returns <c>void</c>, the void one must READ some static member of the
    ///       canceller's own type family. Harmony will not skip it; if it does not ask, it runs behind a
    ///       cancelled original and acts on something that did not happen. This is the 7e88d0d lesson in the
    ///       only form that generalises.
    ///
    /// RUNNING ON A CANCELLED ORIGINAL IS SOMETIMES CORRECT, and arm (b) must not push those toward a fake
    /// read. Three of this mod's captures are in that position — <c>DeploymentCapture</c> (its Postfix is
    /// gated on <c>NativeSucceeded</c>, which a cancelled open cannot satisfy), <c>GeoTeardownResetGate</c>
    /// (the only cancelled case is a tactical→tactical restart, where its <c>view == null</c> arm
    /// self-returns) and <c>MissionBriefCapture</c> (its own Finalizer clears the latch, and finalizers run
    /// on a cancelled original — measured). Two of them cannot read the canceller even in principle: those
    /// cancellers expose no state at all. So arm (b) accepts a WRITTEN-DOWN excuse instead: a non-empty
    /// <c>private const string RunsBehindACancelledOriginBecause</c> on the void prefix's own patch class.
    /// It is not a suppression flag — it is the reason, forced to live beside the code and to be re-read by
    /// whoever next changes either half. A capture that can cheaply ask (as <c>TacLeaveBattleCapture</c> asks
    /// <c>ReturnCountdown.Holding</c>) should still ask; the excuse is for the ones with nothing to ask.
    ///
    /// GUARD: POSITIVE CONTROL — three sentinel patch classes at the bottom of this file, all prefixing one
    /// unpatched game method: a <c>bool</c> canceller, a <c>void</c> prefix behind it that declares neither an
    /// order nor an excuse, and a third that carries the excuse const. The same grouping and the same
    /// predicates must report BOTH arms on the first two AND must NOT report arm (b) on the third. Arms (a)
    /// and (b) are absence claims about attributes and IL, and an attribute reader that has stopped resolving
    /// reports a mod full of collisions as perfectly ordered; an excuse reader stuck at "excused" retires arm
    /// (b) just as silently, so the control pins it from both sides.
    ///
    /// Falsify: remove <c>[HarmonyPriority(Priority.First)]</c> from <c>ReturnHoldPatch.Prefix</c> → (a);
    /// delete the <c>ReturnCountdown.Holding</c> read from <c>TacLeaveBattleCapture.Prefix</c> → (b); delete
    /// any of the three <c>RunsBehindACancelledOriginBecause</c> consts → (b) on that capture.
    /// </summary>
    internal static class L422_AMultiPatchedTargetDeclaresItsOrder
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(DiffEngine).Assembly;
            var self = typeof(L422_AMultiPatchedTargetDeclaresItsOrder);

            // ── POSITIVE CONTROL, FIRST ─────────────────────────────────────────────
            var control = Violations(Group(self.GetNestedTypes(All)), "CONTROL").ToList();
            if (control.Count(v => v.Contains("unordered-prefix-collision")) < 3 ||
                !control.Any(v => v.Contains("void-prefix-trusts-the-skip") &&
                                  v.Contains("SentinelTrustingPrefix")) ||
                control.Any(v => v.Contains("void-prefix-trusts-the-skip") &&
                                 v.Contains("SentinelExcusedPrefix")))
            {
                yield return "L422 premise-changed: POSITIVE CONTROL failed — the grouping did not report this " +
                             "law's own three sentinel patch classes exactly as constructed: all three prefix " +
                             "ONE method with no declared order, one is void behind a cancelling bool one and " +
                             "must draw arm (b), and the third carries the RunsBehindACancelledOriginBecause " +
                             "excuse and must NOT (saw: " + (control.Count == 0 ? "nothing" : string.Join(" | ", control.ToArray())) +
                             "). Both arms below are ABSENCE claims over attributes and IL, so a reader that has " +
                             "stopped resolving reports a mod full of undeclared collisions as ordered — and an " +
                             "excuse reader stuck at 'excused' retires arm (b) just as quietly.";
                yield break;
            }

            var groups = Group(mod.GetTypes());
            if (!groups.Any(g => g.Value.Count > 1))
            {
                yield return "L422 premise-changed: no game method carries two of this mod's prefixes any more. " +
                             "That may be true — but it is also what a broken attribute reader looks like, and " +
                             "this repo has had a whole commit be a runtime no-op on this exact seam. Re-derive " +
                             "how the patch classes name their targets before trusting a clean run.";
                yield break;
            }
            foreach (var v in Violations(groups, "L422")) yield return v;
        }

        /// <summary>Prefix methods by the target their class names, keyed <c>Type::Method</c>. Classes that
        /// derive their target in <c>TargetMethod()</c> declare no name here and are out of scope — this arm
        /// is about the ones two authors can collide on without either reading the other's file.</summary>
        private static Dictionary<string, List<MethodInfo>> Group(IEnumerable<Type> types)
        {
            var byTarget = new Dictionary<string, List<MethodInfo>>(StringComparer.Ordinal);
            foreach (var t in types)
            {
                var prefix = Declared(t, "Prefix");
                if (prefix == null) continue;
                foreach (var key in Targets(t).Concat(Targets(prefix)).Distinct())
                {
                    List<MethodInfo> list;
                    if (!byTarget.TryGetValue(key, out list)) byTarget[key] = list = new List<MethodInfo>();
                    if (!list.Contains(prefix)) list.Add(prefix);
                }
            }
            return byTarget;
        }

        private static IEnumerable<string> Violations(Dictionary<string, List<MethodInfo>> groups, string tag)
        {
            foreach (var g in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (g.Value.Count < 2) continue;
                foreach (var p in g.Value)
                {
                    if (!Ordered(p))
                        yield return tag + " unordered-prefix-collision: " + Owner(p) + ".Prefix shares " +
                                     g.Key + " with [" +
                                     string.Join(", ", g.Value.Where(o => o != p).Select(Owner).ToArray()) +
                                     "] and declares no [HarmonyPriority]/[HarmonyBefore]/[HarmonyAfter]. " +
                                     "Which of them runs first is then GetTypesFromAssembly order — a file " +
                                     "rename decides it — and nothing in either file says the other exists. On " +
                                     "TacticalView.GoToGeoscape that decided whether a leave was announced at " +
                                     "the click or five seconds later at the release, and an announcement at the " +
                                     "click carries every other peer out of a battle this peer never left.";
                }

                var cancellers = g.Value.Where(p => p.ReturnType == typeof(bool)).ToList();
                if (cancellers.Count == 0) continue;
                foreach (var p in g.Value)
                {
                    if (p.ReturnType != typeof(bool) && !AsksAny(p, cancellers) && !Excused(p))
                        yield return tag + " void-prefix-trusts-the-skip: " + Owner(p) + ".Prefix returns void " +
                                     "on " + g.Key + ", where [" + string.Join(", ", cancellers.Select(Owner).ToArray()) +
                                     "] can cancel the original — and it never reads their state. HarmonyLib " +
                                     "2.2.0.0 (the ModSDK's own 0Harmony.dll, probed 2026-08-11) emits the skip " +
                                     "guard ONLY around prefixes that can affect the original: ones returning " +
                                     "bool, or taking a ref/out the body reads. A void prefix runs at any " +
                                     "priority, cancelled original or not. So it acts on something that did not " +
                                     "happen — and the priority attribute beside it reads like the fix while " +
                                     "changing nothing at runtime, which is how this shipped as a whole no-op " +
                                     "commit once already. Read the canceller's state (as " +
                                     "TacLeaveBattleCapture reads ReturnCountdown.Holding) — or, if running on " +
                                     "a cancelled original is genuinely correct here, say WHY in a non-empty " +
                                     "private const string RunsBehindACancelledOriginBecause on this patch " +
                                     "class. Writing the reason down is the whole point; a bare suppression " +
                                     "would just be the no-op commit again with a different spelling.";
                }
            }
        }

        /// <summary>Does this prefix consult the CANCELLER's own type family — the canceller's class, or the
        /// outer class the patch class is nested in, which is where the state a hold exposes actually
        /// lives (<c>ReturnCountdown.Holding</c> beside <c>ReturnCountdown.ReturnHoldPatch</c>)?</summary>
        private static bool AsksAny(MethodInfo voidPrefix, List<MethodInfo> cancellers)
        {
            var family = new HashSet<Type>();
            foreach (var c in cancellers)
                for (var t = c.DeclaringType; t != null; t = t.DeclaringType) family.Add(t);
            return Program.CalleeSequence(voidPrefix).Any(x => x != null && family.Contains(x.DeclaringType)) ||
                   Program.FieldSites(voidPrefix).Any(x => family.Contains(x.Value.DeclaringType));
        }

        /// <summary>A written-down reason that this void prefix is SUPPOSED to run behind a cancelled
        /// original. Deliberately a compile-time <c>const string</c> with a length floor: it has to be a
        /// sentence somebody wrote, not a flag somebody flipped, and it cannot be set at runtime.</summary>
        private static bool Excused(MethodInfo p)
        {
            var f = p.DeclaringType == null
                ? null
                : p.DeclaringType.GetField("RunsBehindACancelledOriginBecause", All);
            if (f == null || !f.IsLiteral || f.FieldType != typeof(string)) return false;
            var why = f.GetRawConstantValue() as string;
            return why != null && why.Trim().Length >= 40;
        }

        /// <summary>An order declared on the prefix itself or on its patch class — Harmony merges the two.</summary>
        private static bool Ordered(MethodInfo p) =>
            Declares(p) || (p.DeclaringType != null && Declares(p.DeclaringType));

        private static bool Declares(MemberInfo m) =>
            m.GetCustomAttributesData().Any(a => a.AttributeType.Name == "HarmonyPriority" ||
                                                 a.AttributeType.Name == "HarmonyBefore" ||
                                                 a.AttributeType.Name == "HarmonyAfter");

        private static IEnumerable<string> Targets(MemberInfo m)
        {
            object[] attrs;
            try { attrs = m.GetCustomAttributes(typeof(HarmonyPatch), false); }
            catch { yield break; }
            foreach (HarmonyPatch a in attrs)
            {
                var info = a.info;
                if (info == null || info.declaringType == null || string.IsNullOrEmpty(info.methodName)) continue;
                yield return info.declaringType.FullName + "::" + info.methodName;
            }
        }

        private static MethodInfo Declared(Type t, string name)
        {
            try { return t.GetMethod(name, All | BindingFlags.DeclaredOnly); }
            catch { return null; }
        }

        private static string Owner(MethodInfo m) => m.DeclaringType == null ? "?" : m.DeclaringType.Name;

        // ─── POSITIVE CONTROL ────────────────────────────────────────────────────────
        // Three classes on ONE target, none declaring an order. The second is void behind the first's bool
        // and must draw arm (b); the third is the same shape but carries the excuse const and must NOT. They
        // are never registered with Harmony (nothing calls PatchAll over this assembly); they exist so every
        // predicate — including the escape hatch — has a known answer to produce, in both directions.

        [HarmonyPatch(typeof(UnityEngine.GameObject), "SetActive")]
        private static class SentinelCancellingPrefix
        {
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(UnityEngine.GameObject), "SetActive")]
        private static class SentinelTrustingPrefix
        {
            private static void Prefix() { }
        }

        [HarmonyPatch(typeof(UnityEngine.GameObject), "SetActive")]
        private static class SentinelExcusedPrefix
        {
            private const string RunsBehindACancelledOriginBecause =
                "this sentinel exists to prove the excuse is honoured; it patches nothing and runs never";
            private static void Prefix() { }
        }
    }
}
