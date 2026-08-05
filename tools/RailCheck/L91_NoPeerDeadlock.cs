using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Transport;
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

            // ─── (e) THE SEAT COUNT IS ONE NUMBER, AND BOTH SITES READ IT ───
            // Law 91 says any player must at any moment be able to play everything. A player who cannot
            // JOIN is the degenerate case, and it is what a second hard-coded capacity produced: nothing in
            // this repo capped a session at two (SlotAllocator has no ceiling, SteamTransport keys peers in
            // a HashSet, a join is a direct P2P connect and the lobby is discovery only) — two literals did.
            // A `const` would be inlined at both sites and become indistinguishable from someone typing the
            // digit, so the seats are static readonly fields and this arm asserts the ldsfld.
            foreach (var v in SeatCountArm()) yield return v;

            // ─── (f) EVERY HOLD THIS MOD CAN ENTER IS BOUNDED BY A NAMED NUMBER ───
            foreach (var v in BoundedHoldArm(railTypes)) yield return v;

            // ─── (g) EVERY NATIVE WAIT FUNNEL THE SHARED TURN COROUTINE REACHES IS PATCHED ───
            foreach (var v in NativeWaitFunnelArm(mod)) yield return v;
        }

        /// <summary>
        /// ARM (f) — NO UNBOUNDED HOLD, DERIVED AND NOT LISTED. Arms (b)-(e) are about the SHAPE of a
        /// decision, which is exactly why they were green through 2026-08-05: a coroutine that stands still
        /// takes no decision at all and has no peer-id anywhere near it, yet <c>ClientAiGate</c>'s
        /// <c>HoldUntilHostHandsOn</c> sat in an alien turn "with NO timeout BY DESIGN", warning every 60 s
        /// and waiting forever on the host. A wait with no ceiling IS a peer unable to act.
        ///
        /// THE CRITERION, so a new hold is covered the day it is written: a coroutine that yields
        /// <c>NextUpdate.NextFrame</c> is BY CONSTRUCTION a hold — it exists to let wall-clock time pass. So
        /// the swept set is every compiler-generated iterator in the rail namespaces whose <c>MoveNext</c>
        /// references <c>NextUpdate.NextFrame</c>. Nothing is hand-listed; <c>yield break</c> stubs
        /// (<c>Nothing</c>/<c>NoWait</c>) never touch the field and are never swept.
        ///
        /// THE TEST is a named bound actually COMPARED against: an <c>ldsfld</c> of a static <c>Int32</c>
        /// immediately followed by a comparison. That is what distinguishes a ceiling
        /// (<c>frames >= HoldCeilingFrames</c> → <c>ldsfld</c> + <c>blt</c>) from a diagnostic period
        /// (<c>frames % HoldWarnFrames == 0</c> → <c>ldsfld</c> + <c>rem</c>), so deleting the ceiling and
        /// keeping the warning goes RED. It must be a FIELD and not a <c>const</c> for the same reason the
        /// seat count is: a const is inlined and becomes indistinguishable from a typed digit.
        /// </summary>
        private static IEnumerable<string> BoundedHoldArm(List<Type> railTypes)
        {
            var nextUpdate = typeof(PhoenixPoint.Tactical.Levels.TacticalFaction).Assembly
                                 .GetType("Base.Core.NextUpdate");
            if (nextUpdate == null || nextUpdate.GetField("NextFrame", All) == null)
            {
                yield return "L91 hold-premise-changed: Base.Core.NextUpdate.NextFrame no longer resolves as a " +
                             "field, so 'this coroutine yields a frame' — the criterion that finds every hold " +
                             "without listing one — cannot be asked. Re-derive it before trusting this arm";
                yield break;
            }

            var holds = railTypes.Where(t => t.Name.IndexOf("d__", StringComparison.Ordinal) >= 0 &&
                                             t.Name.Length > 0 && t.Name[0] == '<')
                                 .Select(t => new { Type = t, Move = t.GetMethod("MoveNext", All) })
                                 .Where(x => x.Move != null && ReadsNamedField(x.Move, "NextFrame"))
                                 .ToList();
            if (holds.Count == 0)
            {
                yield return "L91 no-holds-swept: not one coroutine in the rail namespaces yields " +
                             "NextUpdate.NextFrame, so this arm is vacuous. Either every hold was deleted — " +
                             "unlikely while the client still follows the host's turn cursor — or the criterion " +
                             "no longer finds them and the ceiling rule is UNCHECKED rather than satisfied";
                yield break;
            }
            foreach (var h in holds)
                if (!ComparesAgainstStaticInt(h.Move))
                    yield return "L91 unbounded-hold: " + Readable(h.Type) + " yields NextUpdate.NextFrame in a " +
                                 "loop but never compares anything against a named static int bound. It can " +
                                 "therefore stand still forever, and a peer standing still forever is a peer " +
                                 "unable to act — the prime rule, whatever the hold is nominally waiting for. " +
                                 "This is verbatim what HoldUntilHostHandsOn did on 2026-08-05 while two players " +
                                 "watched a frozen battlefield for 125 s. Give it a static readonly ceiling and a " +
                                 "defined give-up (a const is inlined and does not count)";
        }

        /// <summary>
        /// ARM (g) — A NATIVE COROUTINE MAY NOT STOP THE SHARED TURN ON ONE PEER'S SCREEN. The other half of
        /// the same 2026-08-05 report, and the half no structural rule could have seen: the thing that froze
        /// three peers was not our code at all. <c>TacticalFaction.AIUpdateCrt</c> — the alien turn, which
        /// only the host runs — yields on <c>TacticalView.WaitUntilHintsAreConfirmed</c>, whose loop spins
        /// while the LOCAL UI state stack is showing a hint popup. One player's un-dismissed Umbra panel held
        /// the turn for everybody.
        ///
        /// THE CRITERION, derived not listed: take the shared turn coroutine's own state machine, take every
        /// callee of it that is itself a <c>IEnumerator&lt;NextUpdate&gt;</c> coroutine, and ask of each
        /// whether ITS body reads the local UI state stack (<c>get_CurrentState</c>) or the hint pump
        /// (<c>TryShowContextHint</c>). Every funnel that does MUST carry a patch from this mod. A new engine
        /// wait added to the AI turn tomorrow is swept the same day; nothing here names a hint.
        /// </summary>
        private static IEnumerable<string> NativeWaitFunnelArm(Assembly mod)
        {
            var faction = typeof(PhoenixPoint.Tactical.Levels.TacticalFaction);
            var game = faction.Assembly;
            var aiTurn = faction.GetNestedTypes(All)
                                .Select(t => t.GetMethod("MoveNext", All))
                                .FirstOrDefault(m => m != null &&
                                                     m.DeclaringType.Name.IndexOf("AIUpdateCrt",
                                                         StringComparison.Ordinal) >= 0);
            if (aiTurn == null)
            {
                yield return "L91 turn-coroutine-gone: TacticalFaction.AIUpdateCrt's state machine no longer " +
                             "resolves. It is the shared alien turn — the one coroutine whose stalling stops " +
                             "every peer at once — so this arm can no longer find the waits inside it";
                yield break;
            }

            var patched = PatchedGameMethods(mod);
            bool sweptAny = false;
            foreach (var callee in Callees(aiTurn, game).Distinct())
            {
                var mi = callee as MethodInfo;
                if (mi == null || mi.ReturnType == null ||
                    !mi.ReturnType.Name.StartsWith("IEnumerator", StringComparison.Ordinal)) continue;
                var body = mi.DeclaringType.GetNestedTypes(All)
                             .Where(t => t.Name.IndexOf(mi.Name, StringComparison.Ordinal) >= 0)
                             .Select(t => t.GetMethod("MoveNext", All))
                             .FirstOrDefault(m => m != null);
                if (body == null) continue;
                var reached = Callees(body, game).Select(c => c.Name).ToList();
                if (!reached.Contains("get_CurrentState") && !reached.Contains("TryShowContextHint")) continue;
                sweptAny = true;
                if (!patched.Contains(mi.DeclaringType.Name + "." + mi.Name))
                    yield return "L91 turn-waits-on-local-ui: TacticalFaction.AIUpdateCrt yields on " +
                                 mi.DeclaringType.Name + "." + mi.Name + ", which spins on this peer's own UI " +
                                 "state stack — and this mod does not patch it. The alien turn then runs at the " +
                                 "speed of one player's mouse: on 2026-08-05 a host-side Umbra hint stopped the " +
                                 "turn for 125 s on all three peers, and the two clients had no popup to dismiss " +
                                 "because a hint is per-peer presentation. Make the wait LOCAL (a prefix " +
                                 "returning an immediately-finished enumerator); do not replicate the pause";
            }
            if (!sweptAny)
                yield return "L91 no-wait-funnels-swept: no coroutine reached from AIUpdateCrt reads the UI " +
                             "state stack or the hint pump any more, so this arm found nothing to check. Either " +
                             "the engine stopped waiting on local UI inside the shared turn — verify it — or the " +
                             "criterion has drifted and the rule is UNCHECKED rather than satisfied";
        }

        /// <summary>Every game method this mod declares a Harmony patch on, as "Type.Method".
        /// <c>HarmonyPatch</c> is AllowMultiple and the type/name split across attributes, so the pair is
        /// assembled per patch class rather than per attribute.</summary>
        private static HashSet<string> PatchedGameMethods(Assembly mod)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in mod.GetTypes())
            {
                Type declaring = null;
                string method = null;
                foreach (HarmonyPatch a in t.GetCustomAttributes(typeof(HarmonyPatch), inherit: false))
                {
                    if (a.info == null) continue;
                    if (a.info.declaringType != null) declaring = a.info.declaringType;
                    if (!string.IsNullOrEmpty(a.info.methodName)) method = a.info.methodName;
                }
                if (declaring != null && method != null) set.Add(declaring.Name + "." + method);
            }
            return set;
        }

        private static string Readable(Type stateMachine)
        {
            var name = stateMachine.Name;
            int open = name.IndexOf('<'), close = name.IndexOf('>');
            var inner = open == 0 && close > 1 ? name.Substring(1, close - 1) : name;
            return (stateMachine.DeclaringType?.Name ?? "?") + "." + inner;
        }

        /// <summary>Does <paramref name="m"/> load a static field with this name?</summary>
        private static bool ReadsNamedField(MethodBase m, string name)
        {
            foreach (var step in Walk(m))
            {
                if (step.Op.OperandType != OperandType.InlineField) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Il, step.Pos)); } catch { }
                if (f != null && f.Name == name) return true;
            }
            return false;
        }

        /// <summary>Is a static <c>Int32</c> field loaded and IMMEDIATELY compared? A ceiling reads
        /// <c>ldsfld</c> + a branch/compare opcode; a diagnostic period reads <c>ldsfld</c> + <c>rem</c>. That
        /// one-instruction difference is what stops this arm going green on a hold that only knows how to
        /// complain about itself.</summary>
        private static bool ComparesAgainstStaticInt(MethodBase m)
        {
            bool armed = false;
            foreach (var step in Walk(m))
            {
                if (armed)
                {
                    var op = step.Op;
                    if (op == OpCodes.Clt || op == OpCodes.Clt_Un || op == OpCodes.Cgt || op == OpCodes.Cgt_Un ||
                        op == OpCodes.Ceq || op == OpCodes.Blt || op == OpCodes.Blt_S || op == OpCodes.Blt_Un ||
                        op == OpCodes.Blt_Un_S || op == OpCodes.Bge || op == OpCodes.Bge_S ||
                        op == OpCodes.Bge_Un || op == OpCodes.Bge_Un_S || op == OpCodes.Bgt ||
                        op == OpCodes.Bgt_S || op == OpCodes.Bgt_Un || op == OpCodes.Bgt_Un_S ||
                        op == OpCodes.Ble || op == OpCodes.Ble_S || op == OpCodes.Ble_Un ||
                        op == OpCodes.Ble_Un_S || op == OpCodes.Beq || op == OpCodes.Beq_S) return true;
                    armed = false;
                }
                if (step.Op.OperandType != OperandType.InlineField) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Il, step.Pos)); } catch { }
                armed = f != null && f.IsStatic && f.FieldType == typeof(int);
            }
            return false;
        }

        private struct IlStep { public byte[] Il; public OpCode Op; public int Pos; }

        /// <summary>The same operand-size walk the arms above use; anything unparseable ABANDONS the method
        /// rather than guessing, so this under-reports and never invents a red.</summary>
        private static IEnumerable<IlStep> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE) { if (i >= il.Length) yield break; code = (short)(0xFE00 | il[i++]); }
                if (!OpCodeByValue.TryGetValue(code, out var op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                yield return new IlStep { Il = il, Op = op, Pos = i };
                i += size;
            }
        }

        private static IEnumerable<string> SeatCountArm()
        {
            var engine = typeof(NetworkEngine);
            var maxPlayers = engine.GetField("MaxPlayers", All);
            var maxClients = engine.GetField("MaxClients", All);
            var create = typeof(SteamInvite).GetMethod("HostPublish", All);
            var connected = engine.GetMethod("OnPeerConnected", All);
            var disconnected = engine.GetMethods(All)
                .FirstOrDefault(m => m.Name.StartsWith("OnPeerDisconnected", StringComparison.Ordinal));
            if (maxPlayers == null || maxClients == null || create == null || connected == null || disconnected == null)
            {
                yield return "L91 seat-count-premise-changed: NetworkEngine.MaxPlayers / MaxClients, " +
                             "SteamInvite.HostPublish or NetworkEngine's peer connect/disconnect handlers no " +
                             "longer all resolve. Whether the co-op seat count is still ONE number is now " +
                             "unprovable — re-find the sites before assuming a third player can join";
                yield break;
            }

            // The two fields agree, by derivation and not by luck. Read live: they are static readonly, so
            // the values here ARE the shipped ones.
            int players = (int)maxPlayers.GetValue(null);
            int clients = (int)maxClients.GetValue(null);
            if (clients != players - 1)
                yield return "L91 seat-count-inconsistent: MaxClients is " + clients + " but MaxPlayers is " +
                             players + ". The host holds one seat, so clients must be MaxPlayers - 1 — with " +
                             "these two the lobby advertises room it will not let anyone into (or lets in one " +
                             "more than it advertised), and the player who is refused has no way to tell why";
            if (players < 2)
                yield return "L91 seat-count-degenerate: MaxPlayers is " + players + ", which is not a co-op " +
                             "session at all";

            // Both capacity sites REFERENCE the field rather than carrying their own number.
            if (!ReadsField(create, maxPlayers))
                yield return "L91 lobby-size-hardcoded: SteamInvite.HostPublish does not read " +
                             "NetworkEngine.MaxPlayers. It creates the Steam lobby with a number of its own, so " +
                             "the advertised capacity and the capacity the host will actually accept are two " +
                             "independent facts — and the one that refuses a friend's Join Game is the lobby's";
            foreach (var site in new[] { connected, disconnected })
                if (!ReadsField(site, maxClients))
                    yield return "L91 joinable-gate-hardcoded: NetworkEngine." + site.Name + " does not read " +
                                 "MaxClients. That gate is what opens and closes the invite lobby as peers come " +
                                 "and go — with its own literal it is exactly the bug this arm exists for: " +
                                 "`ClientCount == 0` closed the lobby on the FIRST connect, so a third player " +
                                 "could never be invited no matter what the lobby was created with";

            // ─── THE SEAT COUNT HAS EXACTLY ONE CONSUMER SET ───
            // The three arms above prove the three known sites READ the field. They cannot prove there is no
            // FOURTH site, and that is the half that matters for "raise MaxPlayers and you are done": a new
            // consumer that re-derives its own bound would leave the policy number editable in two places
            // again — the precise shape of the two-literal bug that capped this repo at two players. So the
            // reader set is CLOSED here: exactly the declared three, no more and no fewer.
            var declared = new HashSet<string>(StringComparer.Ordinal)
            {
                "SteamInvite.HostPublish", "NetworkEngine.OnPeerConnected", "NetworkEngine.OnPeerDisconnected",
                // The DERIVATION, not a gate: the static ctor is where `MaxClients = MaxPlayers - 1` runs.
                // It is the one read that must exist — it is what stops the two numbers being typed twice.
                "NetworkEngine..cctor",
            };
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in engine.Assembly.GetTypes())
                foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    if (!ReadsFieldDirect(m, maxPlayers) && !ReadsFieldDirect(m, maxClients)) continue;
                    actual.Add(OwnerName(t, m));
                }

            foreach (var extra in actual.Except(declared).OrderBy(x => x, StringComparer.Ordinal))
                yield return "L91 seat-count-second-consumer: " + extra + " reads NetworkEngine.MaxPlayers/" +
                             "MaxClients and is not one of the three declared capacity sites. Either it is a " +
                             "second capacity gate — in which case the seat count is once again a fact written " +
                             "in more than one place, and raising it is no longer a one-line change — or the " +
                             "declared list above is stale. Resolve it; do not widen the list to silence this";
            foreach (var missing in declared.Except(actual).OrderBy(x => x, StringComparer.Ordinal))
                yield return "L91 seat-count-consumer-vanished: " + missing + " no longer reads the seat count " +
                             "at all, so a capacity site this arm believes is covered is now deciding admission " +
                             "on something else (or has stopped deciding it)";
        }

        /// <summary>The method a reader really belongs to. An <c>async</c> body compiles to a
        /// <c>&lt;Name&gt;d__N.MoveNext</c> state machine, so the field read shows up on a generated type;
        /// naming it raw would make the closed-set arm above report noise instead of a second gate.</summary>
        private static string OwnerName(Type t, MethodBase m)
        {
            var owner = t;
            var name = m.Name;
            while (owner != null && owner.Name.Length > 0 && owner.Name[0] == '<' && owner.DeclaringType != null)
            {
                int close = owner.Name.IndexOf('>');
                if (close > 1) name = owner.Name.Substring(1, close - 1);
                owner = owner.DeclaringType;
            }
            return (owner?.Name ?? t.Name) + "." + name;
        }

        /// <summary>Does <paramref name="m"/> load <paramref name="field"/> (ldsfld/ldfld)? A FIELD read, not
        /// a constant: an inlined const is a magic number by the time the IL exists, which is the whole
        /// reason the seat count is a static readonly.</summary>
        private static bool ReadsField(MethodBase m, FieldInfo field)
        {
            if (ReadsFieldDirect(m, field)) return true;
            // An `async void` body is a stub that starts a compiler-generated state machine — the real IL
            // lives in that machine's MoveNext, nested under the SAME type. SteamInvite.HostPublish is one,
            // so without this the arm would report a hard-coded lobby size for a method that has no IL of
            // its own at all: a FALSE red, which this harness treats as worse than a missed one.
            foreach (var t in m.DeclaringType.GetNestedTypes(All))
                foreach (var mn in t.GetMethods(All))
                    if (mn.Name == "MoveNext" && ReadsFieldDirect(mn, field)) return true;
            return false;
        }

        private static bool ReadsFieldDirect(MethodBase m, FieldInfo field)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return false;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE) { if (i >= il.Length) return false; code = (short)(0xFE00 | il[i++]); }
                if (!OpCodeByValue.TryGetValue(code, out var op)) return false;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) return false;
                if (op.OperandType == OperandType.InlineField)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i)); } catch { }
                    if (f != null && f.MetadataToken == field.MetadataToken && f.Module == field.Module) return true;
                }
                i += size;
            }
            return false;
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
