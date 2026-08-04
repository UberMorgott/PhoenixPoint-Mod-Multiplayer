using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L91 — NO PEER MAY BE LEFT UNABLE TO ACT. The project's first-class rule, made mechanical:
    /// AT ANY MOMENT ANY PLAYER MUST BE ABLE TO PLAY EVERYTHING. With 49 of 50 players AFK — the host
    /// included — the one active player still plays the whole campaign. So no state may exist in which a
    /// peer's input is dead because another peer did, or did not, do something.
    ///
    /// THE REGRESSION THIS LAW IS BUILT FROM (live 3-instance run 2026-08-04, DLL 22:09:44). Two clients
    /// declined the mission at S#388; the host was never touched. Afterwards neither client could fly the
    /// aircraft anywhere — permanently. The order was never the problem: the host log shows the SAME
    /// travel intent accepted three times over a minute (`HOST intent APPLIED op=travelTo V#1 -&gt; S#388
    /// legs=1` at t=365,4 / 381,4 / 421,6, nonces 30/36/39 — a player clicking again because nothing
    /// moved), each one sitting next to `[MP][pause] resume from peer=2 REFUSED — a blocking window is
    /// still open on 2 peer(s)`. The aircraft did not fly because the shared CLOCK was held by a
    /// cross-peer holder set and every resume was vetoed. "I cannot control my aircraft" was a deadlock
    /// wearing a vehicle costume — which is exactly why this law is phrased on the DECISION SHAPE and not
    /// on aircraft, windows or time.
    ///
    /// THE SHAPE, stated once: a host-side decision about peer P may read P's own intent and the shared
    /// GAME state, and nothing else. The moment it also reads WHICH OTHER PEERS are in some set, P's
    /// ability to act becomes theirs to withhold — and an AFK peer withholds forever, because nobody is
    /// there to release it. The broken code said it in one signature:
    /// <c>PauseHold.Decide(IEnumerable&lt;ulong&gt; holds, ulong peer, byte op, byte val)</c> over a
    /// <c>static readonly HashSet&lt;ulong&gt; _holds</c>. Arms (b) and (c) are that signature and that
    /// field, generalised: ANY peer-id COLLECTION reaching a rail decision, under any name.
    ///
    /// Arm (d) is the same rule at the seam the report actually names. <c>MissionCancelGate</c> refuses
    /// <c>GeoMission.Cancel</c> on a client — correctly: cancelling deletes shared campaign state
    /// (<c>Site.ActiveMission = null</c> / <c>Site.DestroySite()</c>, GeoMission.cs:253-265) that the diff
    /// could never correct, and one peer backing out of a screen must not delete the mission for everyone.
    /// That refusal is safe ONLY while the game's own screen teardown sits BESIDE the call we block and
    /// never INSIDE it: <c>UIStateRosterDeployment.ToPreviousScreen</c>:256-268 calls <c>_mission.Cancel()</c>
    /// first and then does its own <c>ResetViewState</c>/<c>SwitchToPreviousState</c> +
    /// <c>FinishQueriedState</c>, so the declining peer always gets its screen back. Should a patch ever
    /// move that teardown into <c>Cancel</c>, our gate would strand a declining client on the deployment
    /// screen with no Back button and no log line — a peer permanently unable to act, produced by our own
    /// gate. The premise is pinned here rather than trusted.
    ///
    /// Falsify: give any rail Validate/Decide a peer-id collection parameter → <c>decision-reads-peer-set</c>;
    /// re-add a <c>static HashSet&lt;ulong&gt;</c> holder to a rail type → <c>peer-set-on-the-rail</c>;
    /// delete every rail validator → <c>no-decisions-swept</c>; move the screen teardown into
    /// <c>GeoMission.Cancel</c> → <c>decline-strands-the-peer</c>; drop the teardown from
    /// <c>ToPreviousScreen</c> → <c>decline-premise-changed</c>.
    /// </summary>
    internal static class L91_NoPeerDeadlock
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The rail's two namespaces — the geoscape rail and the shared battle. Both arbitrate
        /// peers, so both are swept; the lobby/transport namespace (<c>Multiplayer.Network</c>) is NOT,
        /// because peer bookkeeping is its whole job (session membership, keepalive, rejoin pruning).</summary>
        private static readonly string[] RailNamespaces = { "Multiplayer.Network.Sync", "Multiplayer.Tactical" };

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(MissionSync).Assembly;
            var railTypes = mod.GetTypes()
                               .Where(t => RailNamespaces.Contains(t.Namespace, StringComparer.Ordinal))
                               .ToList();

            // ─── (a) POSITIVE CONTROL — a sweep that finds nothing proves nothing ───
            if (railTypes.Count == 0)
            {
                yield return "L91 rail-namespace-gone: no types resolve in " + string.Join(" / ", RailNamespaces) +
                             ". Every arm below would pass vacuously, so the deadlock rule is UNCHECKED rather " +
                             "than satisfied";
                yield break;
            }

            // ─── (b) NO RAIL DECISION TAKES OTHER PEERS AS INPUT ───
            // Validate/Decide are the rail's declared pure arbiters (the ones L32 drives). A decision that
            // is handed a COLLECTION of peer ids is, by construction, a decision other peers can withhold.
            var decisions = railTypes
                .SelectMany(t => t.GetMethods(All))
                .Where(m => m.IsStatic && (m.Name == "Validate" || m.Name == "Decide"))
                .ToList();
            if (decisions.Count == 0)
                yield return "L91 no-decisions-swept: not one static Validate/Decide remains in the rail " +
                             "namespaces, so arm (b) is vacuous. Either the arbitration layer was renamed — " +
                             "re-point this law at it — or host-side decisions are no longer pure, which is a " +
                             "bigger problem than this law";
            foreach (var m in decisions)
                foreach (var p in m.GetParameters().Where(p => IsPeerCollection(p.ParameterType)))
                    yield return "L91 decision-reads-peer-set: " + m.DeclaringType.Name + "." + m.Name +
                                 " takes '" + p.Name + "' (" + Pretty(p.ParameterType) + ") — a COLLECTION of " +
                                 "peer ids. A host-side decision about one peer may read that peer's own intent " +
                                 "and the shared game state, never the membership of the others: an AFK peer in " +
                                 "that collection blocks everybody forever, and nobody is there to release it. " +
                                 "This is the exact signature PauseHold.Decide(IEnumerable<ulong> holds, ...) had " +
                                 "when two clients declining a mission froze the shared clock and no aircraft " +
                                 "could be flown (2026-08-04)";

            // ─── (c) NO RAIL TYPE KEEPS A PEER-ID COLLECTION AT ALL ───
            // The storage half of the same bug: PauseHold's `static readonly HashSet<ulong> _holds`. Killing
            // the signature but keeping the set only moves the veto one call deeper.
            foreach (var t in railTypes)
                foreach (var f in t.GetFields(All).Where(f => f.IsStatic && IsPeerCollection(f.FieldType)))
                    yield return "L91 peer-set-on-the-rail: " + t.Name + "." + f.Name + " is a static " +
                                 Pretty(f.FieldType) + " — session-wide membership of PEERS held by the rail. " +
                                 "The rail arbitrates by arrival order and nothing else (first-to-act-wins); the " +
                                 "only thing such a set can add is a quorum, and a quorum is a hostage. Peer " +
                                 "bookkeeping belongs to the lobby, whose job it is";

            // ─── (d) THE DECLINE ALWAYS RELEASES ITS OWN SCREEN (the reported seam) ───
            var toPrev = typeof(UIStateRosterDeployment).GetMethod("ToPreviousScreen", All);
            var cancel = typeof(GeoMission).GetMethod("Cancel", All, null, Type.EmptyTypes, null);
            var finishQueried = typeof(GeoscapeView).GetMethod("FinishQueriedState", All);
            var resetView = typeof(GeoscapeView).GetMethod("ResetViewState", All);
            if (toPrev == null || cancel == null || finishQueried == null || resetView == null)
            {
                yield return "L91 decline-premise-changed: UIStateRosterDeployment.ToPreviousScreen / " +
                             "GeoMission.Cancel() / GeoscapeView.FinishQueriedState / ResetViewState no longer " +
                             "all resolve (" +
                             (toPrev == null ? "ToPreviousScreen" : cancel == null ? "Cancel" :
                              finishQueried == null ? "FinishQueriedState" : "ResetViewState") +
                             " missing). MissionCancelGate blocks the client's Cancel, and whether that leaves " +
                             "the declining peer a way off the deployment screen is now unproven";
                yield break;
            }
            if (!Calls(toPrev, cancel))
                yield return "L91 decline-premise-changed: ToPreviousScreen no longer calls GeoMission.Cancel, so " +
                             "MissionCancelGate is not on the decline path any more — re-derive which funnel a " +
                             "client's decline now reaches before trusting the gate";
            if (!Calls(toPrev, finishQueried) && !Calls(toPrev, resetView))
                yield return "L91 decline-strands-the-peer: ToPreviousScreen reaches NEITHER FinishQueriedState " +
                             "NOR ResetViewState. The client's Cancel is blocked by MissionCancelGate, so the " +
                             "screen teardown that runs BESIDE it is the only thing giving a declining peer its " +
                             "geoscape back — without it that peer sits on a deployment screen it cannot leave, " +
                             "silently, and everything it owns is unreachable";
            foreach (var teardown in new[] { finishQueried, resetView })
                if (Calls(cancel, teardown))
                    yield return "L91 decline-strands-the-peer: GeoMission.Cancel now calls GeoscapeView." +
                                 teardown.Name + ". MissionCancelGate skips Cancel WHOLE on a client, so the " +
                                 "screen teardown would be skipped with it and the declining peer is trapped on " +
                                 "the deployment screen — the gate itself producing a player who cannot act. " +
                                 "Split the teardown back out, or turn the gate into an intent";
        }

        /// <summary>Is <paramref name="t"/> a collection of PEER IDS — <c>ulong[]</c>, or a generic
        /// enumerable whose FIRST type argument is <c>ulong</c> (<c>HashSet</c>/<c>List</c>/<c>IEnumerable</c>,
        /// and <c>Dictionary&lt;ulong,*&gt;</c>, whose keys are the peers)? Deliberately NOT matched:
        /// <c>Dictionary&lt;int,ulong&gt;</c> — a per-actor owner like <c>TacticalCommandSync._cmdOwner</c> names
        /// who is mid-order on ONE actor, which law 5 still lets any other peer take by acting first.</summary>
        private static bool IsPeerCollection(Type t)
        {
            if (t == null) return false;
            if (t.IsArray) return t.GetElementType() == typeof(ulong);
            if (!t.IsGenericType || !typeof(IEnumerable).IsAssignableFrom(t)) return false;
            var args = t.GetGenericArguments();
            return args.Length > 0 && args[0] == typeof(ulong);
        }

        private static string Pretty(Type t) =>
            t.IsGenericType
                ? t.Name.Substring(0, t.Name.IndexOf('`')) + "<" +
                  string.Join(",", t.GetGenericArguments().Select(a => a.Name)) + ">"
                : t.Name;

        /// <summary>Does <paramref name="m"/> reference <paramref name="callee"/> in its IL? Same operand-size
        /// walk Program.Callees uses; anything unparseable ABANDONS the method rather than guessing, so this
        /// under-reports (a missed edge) and never invents one (a false red).</summary>
        private static bool Calls(MethodBase m, MethodBase callee) =>
            callee != null && Callees(m, callee.Module.Assembly)
                .Any(c => c.MetadataToken == callee.MetadataToken && c.Module == callee.Module);

        private static readonly Dictionary<short, OpCode> OpCodeByValue =
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                           .Where(f => f.FieldType == typeof(OpCode))
                           .Select(f => (OpCode)f.GetValue(null))
                           .GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First());

        private static IEnumerable<MethodBase> Callees(MethodBase m, Assembly asm)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                if (op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (callee != null && callee.Module.Assembly == asm) yield return callee;
                }
                i += size;
            }
        }

        private static int OperandSize(OperandType t, byte[] il, int at)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (at + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return -1;
            }
        }
    }
}
