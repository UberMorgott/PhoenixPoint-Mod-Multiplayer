using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L417 — A REFUSAL THAT LEAVES A PEER WAITING MUST RELEASE IT.
    ///
    /// THE FAILURE (6c700cc, live 3-instance battle). Since L230 a clicked order is PUBLISHED with its native
    /// activation suppressed and its state switch swallowed, and <c>_awaitingEcho</c> is armed for that actor:
    /// only the host's MIRROR or a SETTLE takes it back off. A reject takes neither off. The generic
    /// <c>Validate</c> arm in <c>HandleActivate</c> already forced a settle beside its reject; the
    /// unresolved-target arm above it and the give-up arm in <c>HostTick</c> did not. So the refused peer lost
    /// that soldier for the whole EchoCeiling — a second click was thrown away — and <c>TickEchoWaits</c> then
    /// printed "ECHO LOST … the host never mirrored it back", which is a LIE: the host had answered with a
    /// reason 12 s earlier and the reason had even reached the player's screen.
    ///
    /// That is the shape worth a law and not a code review. The bug is not "a reject is missing"; it is a
    /// reject that is PRESENT, correct, delivered and logged, beside a wait it does not clear — and the
    /// symptom is a diagnostic accusing the wrong peer. Nothing in review looks wrong at either site.
    ///
    /// THE RULE, as it is decidable from IL. Every <c>IntentRail.Reject</c> call site inside
    /// <c>TacticalCommandSync</c> must be followed — before that method's NEXT reject, or before it runs out
    /// of body — by a call to <c>TacticalCommandSync.HostSettle</c>. Per-METHOD would be too coarse to catch
    /// the reported bug at all: <c>HandleActivate</c> settled from its other arm the whole time it was broken,
    /// so "this method also calls HostSettle somewhere" was TRUE while the soldier was frozen. The unit has to
    /// be the call site.
    ///
    /// THE ARMS:
    ///   (a) POSITIVE CONTROL, FIRST AND EXECUTED — this law's whole claim is about a call the IL does NOT
    ///       contain, and an offset walker that has stopped resolving reports every site as clean. So the same
    ///       scanner is run over two sentinels in this file, which call the REAL <c>IntentRail.Reject</c> and
    ///       the REAL <c>HostSettle</c> by construction: the unpaired one must be FLAGGED and the paired one
    ///       must come back CLEAN. One sentinel proves the scanner can see a violation; the other proves it is
    ///       not simply flagging everything.
    ///   (b) <c>refusal-leaves-the-soldier-waiting</c> — the rule itself, over every method and closure of
    ///       <c>TacticalCommandSync</c>, with an explicit allowlist. An entry says why that refusal cannot
    ///       strand anybody; it is not a to-do list.
    ///
    /// Falsify: delete either forced settle 6c700cc added (HandleActivate's unresolved arm, HostTick's
    /// give-up arm) → (b); rename HostSettle or Reject → (a).
    /// </summary>
    internal static class L417_ARefusedOrderReleasesTheSoldierItLeftWaiting
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>A reject that CANNOT leave a peer waiting, with the reason it cannot. Keyed
        /// <c>Type.Method#n</c>, n counting reject call sites in IL order within that method.</summary>
        private static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // A weapon switch is not an order: OnEquipmentSelected relays it and nothing arms _awaitingEcho
            // for it, so there is no wait to clear. The client's own selection is reconciled by
            // ReconcileSelection off the next settle sweep, not by this refusal.
            { "TacticalCommandSync.HandleSelectEquipment#0",
              "a weapon switch arms no _awaitingEcho — nothing is holding the soldier to release" },
        };

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(IntentRail).Assembly;
            var cmdSync = mod.GetType("Multiplayer.Tactical.TacticalCommandSync");
            var settle = cmdSync?.GetMethod("HostSettle", All);
            var reject = typeof(IntentRail).GetMethods(All).FirstOrDefault(m => m.Name == "Reject");
            var self = typeof(L417_ARefusedOrderReleasesTheSoldierItLeftWaiting);

            if (cmdSync == null || settle == null || reject == null)
            {
                yield return "L417 premise-changed: TacticalCommandSync.HostSettle or IntentRail.Reject no " +
                             "longer resolves, so the pairing this law is about cannot be read at all. The claim " +
                             "is an ABSENCE — 'no reject site is missing its settle' — and a scanner that sees " +
                             "neither call reports the whole file as clean while every refused soldier is frozen " +
                             "for the echo ceiling. Re-point this law before believing arm (b).";
                yield break;
            }

            // ═══ (a) POSITIVE CONTROL — the scanner can see both a break and a fix ═══
            var bad = self.GetMethod("SentinelRejectsWithoutSettling", All);
            var good = self.GetMethod("SentinelRejectsThenSettles", All);
            if (bad == null || good == null || !Unpaired(bad).Any() || Unpaired(good).Any())
            {
                yield return "L417 premise-changed: POSITIVE CONTROL failed — the offset scanner did not flag " +
                             "this law's own SentinelRejectsWithoutSettling (which rejects and returns, by " +
                             "construction) or it DID flag SentinelRejectsThenSettles (which pairs them, by " +
                             "construction). Either way the walk below proves nothing: an absence claim from a " +
                             "blind scanner reads exactly like a clean file, and a scanner that flags everything " +
                             "gets the law suppressed instead of read.";
                yield break;
            }
            if (!Program.CalleeSequence(cmdSync.GetMethod("HandleActivate", All) ?? (MethodBase)bad)
                        .Any(c => Is(c, "Reject", typeof(IntentRail))))
            {
                yield return "L417 premise-changed: TacticalCommandSync.HandleActivate refuses nothing — the " +
                             "arbitration that produced this bug has moved out of the type this law walks, so " +
                             "arm (b) would be quantifying over the wrong file.";
                yield break;
            }

            // ═══ (b) EVERY REFUSAL RELEASES, OR IS ON THE LIST ═══
            foreach (var m in Bodies(cmdSync))
                foreach (var n in Unpaired(m))
                {
                    var key = (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "." + m.Name + "#" + n;
                    if (Allowed.ContainsKey(key)) continue;
                    yield return "L417 refusal-leaves-the-soldier-waiting: " + key + " calls IntentRail.Reject " +
                                 "and reaches its next reject (or its return) without a HostSettle. Since L230 " +
                                 "the clicked order was published with its native activation suppressed and " +
                                 "_awaitingEcho armed for that actor; only a mirror or a settle disarms it, and a " +
                                 "reject is neither. The refused peer keeps a soldier it cannot order for the " +
                                 "whole EchoCeiling, its next click on him is discarded, and TickEchoWaits then " +
                                 "reports 'the host never mirrored it back' — untrue, and it blames the peer that " +
                                 "answered. Force the settle beside the reject (HostSettle(actor, forced: true), " +
                                 "as HandleActivate's Validate arm does), or add this site to Allowed WITH the " +
                                 "reason no wait can exist here.";
                }
        }

        /// <summary>Indices of the reject call sites in <paramref name="m"/> that are not followed by a settle
        /// before the next reject. IL order, which for these straight-line refusal arms is source order — a
        /// reject and its settle sit in one <c>if</c> block ending in <c>return</c>/<c>continue</c>.</summary>
        private static List<int> Unpaired(MethodBase m)
        {
            var seq = Program.CalleeSequence(m);
            var rejects = new List<int>();
            for (int i = 0; i < seq.Count; i++) if (Is(seq[i], "Reject", typeof(IntentRail))) rejects.Add(i);

            var bare = new List<int>();
            for (int r = 0; r < rejects.Count; r++)
            {
                int from = rejects[r] + 1;
                int to = r + 1 < rejects.Count ? rejects[r + 1] : seq.Count;
                bool settled = false;
                for (int i = from; i < to; i++)
                    if (seq[i] != null && seq[i].Name == "HostSettle") { settled = true; break; }
                if (!settled) bare.Add(r);
            }
            return bare;
        }

        private static bool Is(MethodBase c, string name, Type owner) =>
            c != null && c.Name == name && c.DeclaringType == owner;

        /// <summary>ARM (a). Rejects and returns — the defect verbatim. Never called; it exists to be walked,
        /// and it calls the PRODUCTION Reject so the control shares arm (b)'s predicate exactly.</summary>
        private static void SentinelRejectsWithoutSettling()
        {
            IntentRail.Reject(SurfaceIds.TacCommandIntent, 0UL, "sentinel");
        }

        /// <summary>ARM (a). The shipped shape: refuse, then release. Must come back CLEAN, or the scanner is
        /// flagging call sites rather than reading them.</summary>
        private static void SentinelRejectsThenSettles()
        {
            IntentRail.Reject(SurfaceIds.TacCommandIntent, 0UL, "sentinel");
            TacticalCommandSync.HostSettle(null, true);
        }

        /// <summary>A type plus its compiler-generated nests — a refusal inside a lambda or an iterator has
        /// its IL in a closure class, not in the declaring type.</summary>
        private static IEnumerable<MethodBase> Bodies(Type root)
        {
            foreach (var t in Nest(root))
            {
                IEnumerable<MethodBase> ms;
                try { ms = t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                             .Concat(t.GetConstructors(All)).ToList(); }
                catch { continue; }
                foreach (var m in ms) yield return m;
            }
        }

        private static IEnumerable<Type> Nest(Type root)
        {
            yield return root;
            foreach (var n in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var d in Nest(n)) yield return d;
        }
    }
}
