using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.UI;

namespace RailCheck
{
    /// <summary>
    /// L158 — A PRESENTATION SEAM ALTERS NOTHING, AND A LATENCY READING REACHES THE PANEL AND NOTHING ELSE.
    ///
    /// P4c ("presentation seams alter nothing — replay what the model already says; never block, never
    /// write, never throw into game code") was, until this law, cited by NOTHING executable:
    /// <c>grep -rn "P4c" tools/RailCheck src</c> returned zero hits and the only mentions in the repo were
    /// <c>ARCHITECTURE.md:313</c> and <c>docs/laws.md</c>. That is a real coverage hole, not a principle
    /// that is only jointly checkable, because P11 and P4c point in OPPOSITE directions: every P11 law in
    /// the catalogue asserts the repaint HAPPENS (reach), and a seam that reaches the screen AND writes
    /// model state passes all forty of them. L126 is the nearest shape and it is scoped to transpilers.
    ///
    /// THE SEAM SET is the one <c>docs/laws.md:263</c> names — <see cref="UiEventMap"/>,
    /// <see cref="UiNativeRepaint"/>, <see cref="OpenUiRepaint"/>, <c>ResearchSync.PresentFromMirror</c> —
    /// plus <see cref="PingTable"/>, added at birth (2026-08-07) rather than retrofitted, because the RTT
    /// reading is P4c by construction and arrives with no law of its own.
    ///
    /// THE ARMS:
    ///   (a) <c>seam-blocks</c> — no member of the set is a Harmony PREFIX that can suppress the native
    ///       call. These types are repaint HELPERS; the day one is rewritten as a bool-returning prefix is
    ///       the day presentation can cancel the game, and P4c's first clause is gone with no other law
    ///       noticing (a prefix that returns false still repaints, so every reach-law stays green).
    ///   (b) <c>seam-writes-a-leaf</c> — no member of the set writes a <c>[SerializeMember]</c> leaf in its
    ///       own IL: no <c>stfld/stsfld</c> to an attributed field, no call to an attributed property's
    ///       setter. LIMIT, STATED PLAINLY: this set drives the native model almost entirely through
    ///       cached <c>MethodInfo.Invoke</c> / <c>FieldInfo.SetValue</c>, which no IL scan can follow.
    ///       The arm therefore pins the DIRECT shape — the day someone writes <c>gc._identity.Name = x</c>
    ///       into a repaint path it goes red — and does not claim to see through reflection. Arm (d) is
    ///       where this law's teeth are.
    ///   (c) <c>seam-uncontained</c> — every member of the set that reflectively CALLS INTO GAME CODE
    ///       (<c>MethodBase.Invoke</c>) must catch. <c>ARCHITECTURE.md:192</c> promises "a registered
    ///       screen that throws keeps the screen + logs once" and that promise was, until now, unasserted:
    ///       an escaping exception from a repaint unwinds into the game's own frame, which is the P4c
    ///       clause with the loudest consequence and the least evidence.
    ///   (d) <c>reading-escapes</c>, THE RTT ARM — <see cref="PingTable"/> is not reachable from
    ///       <c>DiffEngine</c>, <c>GenericApplier</c>, <c>SurfaceRouter</c> or <c>TimeAnchor</c>, holds no
    ///       <c>[SerializeMember]</c> member, and is not a <c>IdentityResolver.RootKinds</c> root. An SRTT
    ///       is a reading of THIS machine's clock taken against THIS machine's own stamp; replicate it and
    ///       every peer's jitter becomes rail churn (L73's inequality is the first casualty) — and it would
    ///       fail SILENTLY, this repo's dominant bug class. L153 exists because a runtime quantity nobody
    ///       could read "has been fixed ten times"; this arm is the same discipline applied before the fact.
    ///   (e) POSITIVE CONTROL, EXECUTED — arm (d) is run against a FAKE root inside this file that both
    ///       holds a <see cref="PingTable"/> field and calls its reader. The arm must go RED on it. Without
    ///       this, a walk that resolved nothing would pass arm (d) forever and the law would be a comment.
    ///
    /// Falsify: give <see cref="OpenUiRepaint"/> a <c>[HarmonyPrefix] static bool</c> → (a); assign a
    /// <c>[SerializeMember]</c> field directly from <see cref="UiEventMap"/> → (b); strip the catch off
    /// <c>ResearchSync.PresentFromMirror</c> → (c); have <c>DiffEngine</c> read
    /// <c>SessionManager.Ping.GetPingMs</c>, or hang a <c>PingTable</c> field off a rail root → (d);
    /// make the walk resolve nothing → (e).
    /// </summary>
    internal static class L158_PresentationSeamAltersNothing
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>Bound on the arm-(d) walk. The rail's callee closure is large and this law is one of
        /// ~130; a runaway walk would cost more than it proves. Reaching the cap is itself reported, so it
        /// can never quietly become the reason the arm found nothing.</summary>
        private const int WalkBudget = 8000;

        internal static IEnumerable<string> Check()
        {
            // PlayerPanel joined the set with the co-op player panel (2026-08-07). It is the ONE screen that
            // renders a latency reading, so it is where arm (d)'s containment would leak first if the panel
            // ever grew a decision — and it renders two advisory per-peer flags besides, which makes arms
            // (a)/(b) worth having over it for the same reason they are worth having over UiEventMap.
            var seam = new[]
            {
                typeof(UiEventMap), typeof(UiNativeRepaint), typeof(OpenUiRepaint),
                typeof(ResearchSync), typeof(PingTable), typeof(PlayerPanel),
            };
            var railRoots = new[]
            {
                typeof(DiffEngine), typeof(GenericApplier), typeof(SurfaceRouter), typeof(TimeAnchor),
            };
            var srtt = typeof(PingTable).GetField("_srtt", AllMembers);
            var containment = new MethodBase[]
            {
                typeof(UiEventMap).GetMethod("Fire", AllMembers),
                typeof(OpenUiRepaint).GetMethod("Repaint", AllMembers),
                typeof(ResearchSync).GetMethod("PresentFromMirror", AllMembers),
                // PlayerPanel.Sync is called from MultiplayerUI.Update, i.e. from inside the game's own
                // Update loop with no frame of ours between it and native code. Without its catch a panel
                // that threw would take the session down over a status column.
                typeof(PlayerPanel).GetMethod("Sync", AllMembers),
            };
            var dispatcher = typeof(UiNativeRepaint).GetMethod("TryRepaint", AllMembers);

            if (srtt == null || dispatcher == null || containment.Any(m => m == null) || seam.Any(t => t == null))
            {
                yield return "L158 premise-changed: PingTable._srtt / UiEventMap.Fire / OpenUiRepaint.Repaint / " +
                             "PlayerPanel.Sync / " +
                             "ResearchSync.PresentFromMirror / UiNativeRepaint.TryRepaint did not all resolve. " +
                             "The seam set or the latency reading has moved, and every arm below is asserting " +
                             "about a shape the repo no longer has.";
                yield break;
            }

            foreach (var v in NeverBlocks(seam)) yield return v;
            foreach (var v in NeverWrites(seam)) yield return v;
            foreach (var v in NeverThrows(containment, dispatcher)) yield return v;

            // ── arm (d): the reading is contained.
            foreach (var v in ReadingEscapes(typeof(PingTable), railRoots, "the rail")) yield return v;

            foreach (var m in typeof(PingTable).GetMembers(AllMembers))
                if (m.GetCustomAttributes(true).Any(a => a.GetType().Name.StartsWith("SerializeMember", StringComparison.Ordinal)))
                    yield return "L158 reading-escapes: PingTable." + m.Name + " carries [SerializeMember]. A " +
                                 "latency reading is not domain state and is not owned by the peer it describes; " +
                                 "serialising it puts every peer's jitter on the wire as a diff.";

            foreach (var root in IdentityResolver.RootKinds)
                if (root.Type == typeof(PingTable))
                    yield return "L158 reading-escapes: PingTable is declared as rail root '" + root.Key +
                                 "' in IdentityResolver.RootKinds. Roots are walked and diffed every slice; a " +
                                 "number that changes on every packet would make the rail churn on nothing.";

            // ── arm (e): the walk above must be able to FIND an escape, or it proves nothing.
            var control = ReadingEscapes(typeof(PingTable), new[] { typeof(FakeRailRoot) }, "the control root").ToList();
            if (control.Count == 0)
                yield return "L158 control-not-red: FakeRailRoot both HOLDS a PingTable field and CALLS " +
                             "PingTable.GetPingMs, and the containment walk did not flag it. Arm (d) cannot " +
                             "distinguish a contained reading from an escaped one, so its green above means " +
                             "nothing — this is precisely how an unfalsifiable law gets baselined and forgotten.";
        }

        // ── arm (a) ─────────────────────────────────────────────────────────────────────────────
        private static IEnumerable<string> NeverBlocks(Type[] seam)
        {
            foreach (var t in seam)
                foreach (var m in t.GetMethods(AllMembers))
                {
                    bool isPrefix = m.GetCustomAttributes(true).Any(a => a.GetType().Name == "HarmonyPrefix") ||
                                    m.Name == "Prefix";
                    if (isPrefix && m.ReturnType == typeof(bool))
                        yield return "L158 seam-blocks: " + t.Name + "." + m.Name + " is a bool-returning Harmony " +
                                     "prefix. A presentation seam replays what the model already says — it may " +
                                     "never decide the game's own method does not run. Returning false still " +
                                     "repaints, so every P11 reach-law stays green while the game is suppressed.";
                }
        }

        // ── arm (b) ─────────────────────────────────────────────────────────────────────────────
        private static IEnumerable<string> NeverWrites(Type[] seam)
        {
            foreach (var t in AllTypes(seam))
                foreach (var m in AllMethodsOf(t))
                {
                    foreach (var f in FieldTokens(m, 0x7D, 0x80))          // stfld / stsfld
                        if (IsSerialized(f))
                            yield return "L158 seam-writes-a-leaf: " + t.Name + "." + m.Name + " writes the " +
                                         "[SerializeMember] field " + f.DeclaringType?.Name + "." + f.Name +
                                         ". A presentation seam that writes model state is indistinguishable " +
                                         "from a rail apply on every peer that did not send it.";
                    foreach (var callee in CalleesOf(m))
                    {
                        if (!callee.Name.StartsWith("set_", StringComparison.Ordinal)) continue;
                        var p = callee.DeclaringType?.GetProperty(callee.Name.Substring(4),
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                        if (p != null && p.GetCustomAttributes(true)
                                .Any(a => a.GetType().Name.StartsWith("SerializeMember", StringComparison.Ordinal)))
                            yield return "L158 seam-writes-a-leaf: " + t.Name + "." + m.Name + " sets the " +
                                         "[SerializeMember] property " + p.DeclaringType?.Name + "." + p.Name +
                                         ". Same reason as the field case: presentation replays, it does not author.";
                    }
                }
        }


        // ── arm (c) ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Containment in this set is at the ENTRY, not at the leaf: the helpers that actually
        /// <c>MethodInfo.Invoke</c> native code (<c>UiNativeRepaint.Call</c>, <c>ReseedEquipScreen</c>,
        /// <c>UiEventMap.RefreshDerivedStats</c>…) deliberately carry no catch of their own, and demanding
        /// one at every leaf would be a style rule, not P4c. So the arm asserts the two things that ARE the
        /// contract: (1) each named containment point still catches, and (2) the native-rebuild dispatcher
        /// <c>UiNativeRepaint.TryRepaint</c> is entered from nowhere that does not. Part (2) is the general
        /// half — it is what stops a future caller from reaching the table around the containment point,
        /// which is how an uncontained repaint would actually arrive.</summary>
        private static IEnumerable<string> NeverThrows(MethodBase[] containmentPoints, MethodBase dispatcher)
        {
            foreach (var m in containmentPoints)
                if (!HasCatch(m))
                    yield return "L158 seam-uncontained: " + m.DeclaringType?.Name + "." + m.Name + " no longer " +
                                 "catches. It is a containment point — the frame that stands between a throwing " +
                                 "repaint and the game's own stack. ARCHITECTURE.md:192 promises a registered " +
                                 "screen that throws KEEPS the screen and logs once; without this catch the " +
                                 "exception unwinds into native code and the repaint takes the session with it.";

            foreach (var t in typeof(OpenUiRepaint).Assembly.GetTypes())
                foreach (var m in AllMethodsOf(t))
                {
                    if (!CalleesOf(m).Any(c => c.MetadataToken == dispatcher.MetadataToken &&
                                               c.Module == dispatcher.Module)) continue;
                    if (HasCatch(m)) continue;
                    yield return "L158 seam-uncontained: " + t.Name + "." + m.Name + " calls " +
                                 "UiNativeRepaint.TryRepaint with no catch. Every table entry runs arbitrary " +
                                 "native rebuild code on a MIRRORED model — the one shape guaranteed to hit " +
                                 "states the screen's own author never saw — so the dispatcher may only be " +
                                 "entered from inside a frame that contains the throw.";
                }
        }

        // ── arm (d) ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Callee-reachability walk in the shape of L153's behaviour-neutrality arm, run in reverse:
        /// from each root outward, does anything reach <paramref name="reading"/>? Also flags a root that
        /// merely HOLDS the type — a field never needs a call to be walked and diffed.</summary>
        private static IEnumerable<string> ReadingEscapes(Type reading, Type[] roots, string label)
        {
            foreach (var root in roots)
                foreach (var f in root.GetFields(AllMembers))
                    if (f.FieldType == reading)
                        yield return "L158 reading-escapes: " + root.Name + "." + f.Name + " is a " + reading.Name +
                                     ". Whatever " + label + " can reach is walked and diffed, and a number " +
                                     "that changes on every packet turns the rail into a jitter broadcaster.";

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var q = new Queue<MethodBase>();
            var origin = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var root in roots)
                foreach (var m in AllTypes(new[] { root }).SelectMany(AllMethodsOf))
                    if (seen.Add(Key(m))) { q.Enqueue(m); origin[Key(m)] = root.Name + "." + m.Name; }

            var budgetSpent = false;
            while (q.Count > 0)
            {
                if (seen.Count > WalkBudget) { budgetSpent = true; break; }
                var m = q.Dequeue();
                var from = origin.TryGetValue(Key(m), out var o) ? o : m.Name;
                foreach (var callee in CalleesOf(m))
                {
                    if (callee.DeclaringType == reading)
                    {
                        yield return "L158 reading-escapes: " + from + " reaches " + reading.Name + "." +
                                     callee.Name + ". A latency reading is local — it is measured against this " +
                                     "machine's own stamp and means nothing on any other peer. Once " + label +
                                     " can read it, it is replicated state, and nothing logs a line when it " +
                                     "becomes one.";
                        continue;
                    }
                    if (callee.DeclaringType?.Assembly != reading.Assembly) continue;
                    if (seen.Add(Key(callee))) { origin[Key(callee)] = from; q.Enqueue(callee); }
                }
            }
            if (budgetSpent)
                yield return "L158 walk-exhausted: the containment walk hit its " + WalkBudget + "-method budget " +
                             "before finishing, so a green arm (d) is 'not found yet', not 'not there'. Raise the " +
                             "budget or narrow the roots — never leave this printing.";
        }

        /// <summary>ARM (e). The shape arm (d) must be able to see: a rail root that holds the reading AND
        /// calls it. Never instantiated, never registered — it exists only to be walked.</summary>
        private sealed class FakeRailRoot
        {
            private readonly PingTable _ping = new PingTable();
            internal int Leak(ulong peerId) => _ping.GetPingMs(peerId);
        }

        // ── shared IL helpers (same primitives as L153) ──────────────────────────────────────────
        private static string Key(MethodBase m) => m.Module.Name + ":" + m.MetadataToken;

        /// <summary>A type plus its compiler-generated nests — lambdas (the repaint Table is all lambdas)
        /// compile into display classes, so their IL is not in the declaring type at all.</summary>
        private static IEnumerable<Type> AllTypes(IEnumerable<Type> roots)
        {
            foreach (var t in roots)
            {
                yield return t;
                foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    foreach (var d in AllTypes(new[] { n })) yield return d;
            }
        }

        private static IEnumerable<MethodBase> AllMethodsOf(Type t)
            => t.GetMethods(AllMembers).Cast<MethodBase>().Concat(t.GetConstructors(AllMembers));

        private static bool HasCatch(MethodBase m)
        {
            try
            {
                var body = m.GetMethodBody();
                return body != null && body.ExceptionHandlingClauses
                    .Any(c => c.Flags == ExceptionHandlingClauseOptions.Clause);
            }
            catch { return true; }   // unreadable body: do not accuse
        }

        private static bool IsSerialized(FieldInfo f)
            => f.GetCustomAttributes(true)
                .Any(a => a.GetType().Name.StartsWith("SerializeMember", StringComparison.Ordinal));

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static IEnumerable<FieldInfo> FieldTokens(MethodBase m, params byte[] opcodes)
        {
            foreach (var tok in TokensAfter(m, opcodes))
            {
                FieldInfo f = null;
                try { f = m.Module.ResolveField(tok); } catch { }
                if (f != null) yield return f;
            }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
