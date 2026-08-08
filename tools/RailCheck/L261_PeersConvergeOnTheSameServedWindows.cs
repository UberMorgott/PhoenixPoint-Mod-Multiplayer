using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L261 — HOST AND CLIENTS DECIDE A WINDOW IS DEAD BY THE SAME PEER-BLIND RULE.
    ///
    /// THE MEASURED DIVERGENCE (2026-08-08, tactical → geoscape return, one wall clock across three local
    /// instances):
    ///
    ///     02:49:11.734  HOST      "1 entries in the save, 1 KEPT — 0 dropped (subject already resolved)"
    ///     02:49:11.584  client-2  "1 entries in the save, 0 KEPT — 1 dropped (Mirrored kind)"
    ///     02:49:09.968  client-3  "1 entries in the save, 0 KEPT — 1 dropped (Mirrored kind)"
    ///     02:49:12.015  HOST      "[MP][outcome] … site=S#213 … activeMission=none"   ← 281 ms LATER
    ///
    /// The host ended holding one window the clients did not have, and it was the START-MISSION window for
    /// the mission all three had just finished. NEITHER HALF OF THAT IS THE MIRRORED SPLIT'S FAULT, and this
    /// law is careful about that because L191 already pins the opposite: the restore KEPT COUNTS are MEANT to
    /// differ (host restores its own blob, a client re-carries its own copy), and "make the counts agree" is
    /// a regression in both directions. The divergence here is downstream of a single defect — the staleness
    /// verdict ran once, at restore, 281 ms before the mission resolved — so the host kept a window it should
    /// also have dropped. Fix the moment and both peers reach zero without ever comparing notes.
    ///
    /// SO THE CONVERGENCE PROPERTY IS: THE VERDICT CANNOT SEE A PEER. Two peers agree about a dead window for
    /// the same reason two calculators agree about a sum — not because they exchange one, but because the
    /// question has no peer in it. That is also the NO-QUORUM guarantee stated as an invariant instead of a
    /// promise: a verdict with nothing peer-shaped in reach cannot be made to wait for anyone to act.
    ///
    ///   (a) <c>verdict-asks-a-peer</c> — no member on the staleness path reaches <c>NetworkEngine</c>, and
    ///       none of them takes a host/peer/session parameter. Adding one is precisely how "the host keeps
    ///       its own, the clients drop theirs" comes back.
    ///   (b) <c>prune-is-session-gated</c> — in <c>ReadyToDequeue</c> the prune is reached BEFORE the first
    ///       <c>NetworkEngine</c> read. Behind it, a peer with no session answers differently from one with,
    ///       and solo/host/client stop sharing a rule.
    ///   (c) <c>two-verdicts-can-drift</c> — restore and serve route through ONE <c>HasResolved</c>. Two
    ///       copies is how the peers' answers diverge again a month from now, quietly.
    ///   (d) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> reaches <c>NetworkEngine</c> on purpose;
    ///       arm (a)'s scan is run over it and MUST come back red.
    ///
    /// Falsify (each verified RED, then restored): read <c>NetworkEngine.Instance</c> inside
    /// <c>ResolvedSubjectName</c> → (a); move the prune below the <c>IsActiveSession</c> gate → (b); give
    /// <c>WindowOrder</c> its own <c>HasResolved</c> → (c); empty <see cref="FakeSeam"/> → (d).
    /// </summary>
    internal static class L261_PeersConvergeOnTheSameServedWindows
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(WindowOrder).Assembly;
            var restore = mod.GetType("Multiplayer.Network.Sync.RestoreDropsResolvedSubjects");
            var verdict = restore?.GetMethod("ResolvedSubjectName", All);
            var resolved = restore?.GetMethod("HasResolved", All);
            var subject = restore?.GetMethod("SubjectMission", All);
            var prune = typeof(WindowOrder).GetMethod("DropResolvedSubjects", All);
            var gate = typeof(WindowOrder).GetMethod("ReadyToDequeue", All);
            if (verdict == null || resolved == null || subject == null || prune == null || gate == null)
            {
                yield return "L261 premise-changed: the staleness path (ResolvedSubjectName / HasResolved / " +
                             "SubjectMission / DropResolvedSubjects / ReadyToDequeue) no longer resolves whole. " +
                             "Every arm below reads that path; losing a member silently un-asserts the " +
                             "convergence the 2026-08-08 host/client window split cost a session to find.";
                yield break;
            }

            // ── (a) nothing on the staleness path may see a peer ───────────────────────────────────
            foreach (var m in new MethodInfo[] { verdict, resolved, subject, prune })
            {
                foreach (var red in PeerBlind(m, mod, "L261")) yield return red;
                var peerParam = m.GetParameters().FirstOrDefault(p =>
                    p.ParameterType == typeof(bool) || p.ParameterType == typeof(ulong) ||
                    p.Name.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Name.IndexOf("peer", StringComparison.OrdinalIgnoreCase) >= 0);
                if (peerParam != null)
                    yield return "L261 verdict-asks-a-peer: " + m.Name + " takes '" + peerParam.Name + "'. " +
                                 "Whether a mission is over is a fact about the campaign, not about who is " +
                                 "asking; the moment the verdict has a peer in its signature, host and clients " +
                                 "can answer it differently, which is the 1-kept/0-kept split the report was.";
            }

            // ── (d) POSITIVE CONTROL, executed: the same scan over a member that DOES read the engine ──
            if (!PeerBlind(typeof(FakeSeam).GetMethod("AsksTheEngine", All), mod, "control").Any())
                yield return "L261 control-not-red: FakeSeam.AsksTheEngine reads NetworkEngine.Instance and the " +
                             "peer-blindness scan did not flag it. Arm (a) is decorative — it would stay green " +
                             "over a verdict that asks who is running it.";

            // ── (b) the prune is reached before the session is ever consulted ──────────────────────
            var order = Program.Callees(gate, mod).ToList();
            int pruneAt = order.FindIndex(c => c != null && c.Name == "DropResolvedSubjects");
            int engineAt = order.FindIndex(c => c != null && c.DeclaringType == typeof(NetworkEngine));
            if (pruneAt < 0)
                yield return "L261 prune-is-session-gated: ReadyToDequeue does not reach DropResolvedSubjects " +
                             "at all, so there is no serve-time verdict for any peer to converge on.";
            else if (engineAt >= 0 && engineAt < pruneAt)
                yield return "L261 prune-is-session-gated: ReadyToDequeue consults NetworkEngine before it " +
                             "prunes resolved subjects. Below that gate the prune stops running for a peer " +
                             "with no active session — and the restore-time half it completes has never been " +
                             "session-gated, so the two moments would start answering differently on the same " +
                             "window. The whole point of a peer-blind rule is that it runs everywhere.";

            // ── (c) one verdict, not two that can drift ────────────────────────────────────────────
            if (!Program.Callees(verdict, mod).Any(c => c != null && c.Name == "HasResolved"))
                yield return "L261 two-verdicts-can-drift: ResolvedSubjectName does not reach HasResolved. " +
                             "HasResolved is the GAME's own predicate (UIStateInitial.EnterState:102); a second " +
                             "opinion beside it is how the serve-time filter and the restore-time filter start " +
                             "disagreeing about the same mission, which is a divergence between peers because " +
                             "the host reaches one moment and a client the other.";
            var strays = mod.GetTypes()
                .Where(t => t != restore)
                .SelectMany(t => t.GetMethods(All).Where(m => m.DeclaringType == t && m.Name == "HasResolved"))
                .ToList();
            if (strays.Count > 0)
                yield return "L261 two-verdicts-can-drift: a second HasResolved exists on " +
                             string.Join(", ", strays.Select(m => m.DeclaringType.Name)) + ". One question, one " +
                             "answer: the restore filter and the drain filter must not be able to reach " +
                             "different rules for 'this mission is over', or the peer that hits one moment and " +
                             "the peer that hits the other end up with different windows on screen.";
        }

        /// <summary>The scan, run over production members in arm (a) and over <see cref="FakeSeam"/> in
        /// arm (d) — same code both times, which is what makes the control a control.</summary>
        private static IEnumerable<string> PeerBlind(MethodInfo m, Assembly mod, string id)
        {
            if (m == null) yield break;
            var engine = Program.Callees(m, mod).FirstOrDefault(c => c != null && c.DeclaringType == typeof(NetworkEngine));
            if (engine != null)
                yield return id + " verdict-asks-a-peer: " + m.Name + " reaches NetworkEngine." + engine.Name +
                             ". The staleness verdict must read nothing but this peer's own campaign state — " +
                             "that is simultaneously the no-quorum guarantee (a rule with no peer in it cannot " +
                             "wait for one to act) and the convergence guarantee (peers agree because the " +
                             "question is the same, not because they compared answers). On 2026-08-08 the host " +
                             "kept a window both clients had dropped; a peer-shaped verdict is how that becomes " +
                             "permanent instead of a timing bug.";
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: a method on the shape of a verdict that asks who is running it.
            /// The scan MUST flag this, or it is not scanning.</summary>
            internal static string AsksTheEngine(object holder) =>
                NetworkEngine.Instance != null && NetworkEngine.Instance.IsHost ? "PROG_AN0" : null;
        }
    }
}
