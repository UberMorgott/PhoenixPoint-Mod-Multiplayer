using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L96 — A SETTLE CARRIES THE HOST'S VISION IN BOTH DIRECTIONS, AND A DROP LIST MAY NOT SWALLOW AN ORDER.
    ///
    /// THE REPORT (3 instances, 2026-08-04, build 843264 B @ 22:09:44). Two failures, one shape: something
    /// that is true on the ACTING peer and on no other. (1) A sniper ran on a client and saw a bandit; on
    /// every other window that bandit stayed in fog for rounds, then reappeared and killed a soldier —
    /// measured in the peer logs as <c>MIRROR play Scab Move_AbilityDef shownMode=Hidden</c> and
    /// <c>shownMode=Located</c> on BOTH clients at 22:23. (2) A melee specialist's Dash sprinted on the
    /// acting client while every other window kept the soldier at his old tile, until a later reload settled
    /// him BACK — <c>22:23:17.758 'Dash_AbilityDef' is DECLARED LOCAL</c>.
    ///
    /// WHY L81 WAS GREEN THROUGH ALL OF IT. L81 proves the SEAM: <c>ApplySettle</c> calls
    /// <c>RefreshVisionTowards</c>, which calls the game's own
    /// <c>TacticalFactionVision.UpdateVisibilityOfAllTowardsActor</c> rather than a hand-rolled visibility
    /// system. Every one of those facts stayed true while the behaviour was broken, because native vision is
    /// a RELATION recomputed from BOTH ends — <c>TacticalFactionVision.OnActorMoved</c>:273-306 sends an
    /// actor of the observing faction to <c>UpdateVisibilityForImpl</c> ("what it now SEES") and a foreign one
    /// to <c>ReUpdateVisibilityTowardsActorImpl</c> ("who now sees IT") — and the repair implemented only the
    /// second. A settle for this peer's own soldier therefore re-tested who could see him and never re-tested
    /// what he could see. L81 asked "is the call there"; the call was there and covered half the graph.
    ///
    /// HALF 1 — REWRITTEN 2026-08-08, SAME OUTCOME, DIFFERENT MECHANISM. The old wording asserted that the
    /// settle's repair must RE-TEST vision in both directions, and the arms looked for a second call site and
    /// for the inverted foreign sweep the own-faction half needed (<c>UpdateVisibilityForImpl</c> being
    /// private). That whole construction is retired: the client no longer tests anything. The host's
    /// per-(looking faction, actor) <c>KnownState</c> now RIDES the settle and is assigned onto the client's
    /// counters, so both directions fall out of the payload instead of out of two native methods — the settle
    /// for a soldier carries what the ALIEN faction knows about HIM, the settle for the alien carries what the
    /// PLAYER faction knows about IT, and the turn-edge sweep sends both for every keyed live actor. The
    /// re-test could not have converged anyway: it was monotone (<c>IncrementCounterTo</c>:55-67 is a maximum,
    /// so a reveal the host lacked could never be removed) and the peers do not share the geometry it was
    /// computed from (<c>SceneObjectIdsComponent.MergeWith</c>:29-34 re-mints a colliding destructible guid at
    /// RANDOM per peer). So the arms below assert the CARRY: the codec round-trips the rows without loss and
    /// the settle payload really has a slot for them. L81 asserts the applier is reached and stops deciding;
    /// L338 asserts the direction that used to be unreachable.
    ///
    /// HALF 2 — <c>LocalAbilities</c> is a per-CLASS answer to a per-ACTIVATION question. "Ambient" is a
    /// property of the ACTIVATION, exactly as A5 already established for <c>AttackType</c>: the
    /// <c>TacticalHurtReactionAbility</c> row was added for damage-triggered reactions and swallowed Dash,
    /// a clicked ability of the same family, because <c>RepositionAbility</c> is both. The discriminator is
    /// the game's own <c>TacticalHurtReactionAbilityDef.TriggerOnDamage</c>, which
    /// <c>TacticalHurtReactionAbility.Activate</c>:43-53 branches on and <c>SubscribeEvents</c>:146-152 uses
    /// to decide whether the ability is wired to damage at all. The GENERIC arm is the one that matters: no
    /// drop-list row may unconditionally swallow a game ability that MOVES an actor
    /// (<c>PhoenixPoint.Tactical.Entities.Abilities.IMoveAbility</c>), because a displacement that never
    /// crosses is the failure mode this whole arc exists to prevent — it fires for the row that caused this
    /// report and for the next one anybody adds.
    ///
    /// Falsify: drop the <c>Revealed</c> byte from <c>WriteVision</c>/<c>ReadVision</c> →
    /// <c>settle-vision-codec-lossy</c>; delete the <c>Vision</c> field from <c>PendingSettle</c> →
    /// <c>settle-vision-not-carried</c>; revert <c>LocalReason</c> to the unconditional lookup →
    /// <c>drop-list-unconditional</c> and <c>drop-list-swallows-movement</c>; add <c>MoveAbility</c> to
    /// <c>LocalAbilities</c> → <c>drop-list-swallows-movement</c>; make
    /// <c>TacticalHurtReactionAbility.Activate</c> stop reading <c>TriggerOnDamage</c> →
    /// <c>premise-hurt-reaction-undiscriminated</c>.
    /// </summary>
    internal static class L96_VisionAndDisplacement
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The drop-list rows that are allowed to cover a moving ability BECAUSE the reason is
        /// re-decided per activation rather than per class. Anything else covering an
        /// <c>IMoveAbility</c> is the Dash bug again.</summary>
        private static readonly string[] ConditionalDropRows = { "TacticalHurtReactionAbility" };

        internal static IEnumerable<string> Check(Assembly game)
        {
            var cmd = typeof(Multiplayer.Tactical.TacticalCommandSync);

            // ─── HALF 1: the settle CARRIES the host's vision, losslessly, in both directions ───

            foreach (var v in VisionCarriedArm(cmd)) yield return v;

            var vision = game.GetType("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (vision == null)
            {
                yield return "L96 premise-vision-type-gone: PhoenixPoint.Tactical.Levels.TacticalFactionVision did " +
                             "not resolve — nothing about the settle's vision carry can be checked.";
            }
            else
            {
                // PREMISE: the ONE faction the two ends deliberately agree to skip is still the one the game
                // itself refuses to write. Both CollectVision and ApplyVision skip a faction for which
                // IsAlwaysRevealedForThisFaction is true, because IncrementKnownCounterImpl:388-391 and
                // ResetKnownCounterImpl:456-459 both return early there and the entry is minted identically on
                // every peer by OnActorEnteredPlay:336-348. If the engine ever started honouring writes for
                // those, the symmetric skip would silently exclude real, divergeable state.
                var always = vision.GetMethod("IsAlwaysRevealedForThisFaction", AllMembers);
                if (always == null || !always.IsPublic || always.ReturnType != typeof(bool))
                    yield return "L96 premise-always-revealed-gone: " +
                                 "TacticalFactionVision.IsAlwaysRevealedForThisFaction is no longer a public bool " +
                                 "method. Both ends of the settle's vision carry skip exactly the factions it " +
                                 "names, so without it the host and the client can no longer agree on WHAT is " +
                                 "being carried.";
                foreach (var name in new[] { "IncrementKnownCounterImpl", "ResetKnownCounterImpl" })
                {
                    var impl = vision.GetMethod(name, AllMembers);
                    if (impl == null || !Calls(impl).Any(c => c.Name == "IsAlwaysRevealedForThisFaction"))
                        yield return "L96 premise-always-revealed-writable: TacticalFactionVision." + name +
                                     " no longer returns early on IsAlwaysRevealedForThisFaction. The settle's " +
                                     "vision carry skips those factions on BOTH ends precisely because the game " +
                                     "refuses to write them — if it now accepts the write, that skip is silently " +
                                     "dropping state two peers can disagree about.";
                }
            }

            // ─── HALF 2: a per-class drop may not swallow a per-activation order ───

            var localReason = cmd.GetMethod("LocalReason", AllMembers);
            if (localReason == null)
            {
                yield return "L96 drop-decision-gone: TacticalCommandSync.LocalReason no longer exists — the one " +
                             "place that decides whether an ability crosses is not there to be checked.";
            }
            else if (!ReadsField(localReason, "TriggerOnDamage") &&
                     !Calls(localReason).Any(c => c.Name == "IsOrderedHurtReaction"))
            {
                yield return "L96 drop-list-unconditional: LocalReason drops purely by CLASS again. 'Ambient' is a " +
                             "property of the ACTIVATION — the same distinction IsAutonomous already draws with " +
                             "AttackType — and TacticalHurtReactionAbility is a FAMILY that contains both damage " +
                             "reactions and ordinary clicked abilities. Without the game's own " +
                             "TacticalHurtReactionAbilityDef.TriggerOnDamage discriminator, a player's Dash is " +
                             "declared local and its displacement never crosses (2026-08-04).";
            }

            var move = game.GetType("PhoenixPoint.Tactical.Entities.Abilities.IMoveAbility");
            if (move == null)
            {
                yield return "L96 premise-move-marker-gone: IMoveAbility did not resolve, so 'this drop row " +
                             "swallows an ability that MOVES an actor' cannot be asked at all.";
            }
            else
            {
                var movers = SafeTypes(game).Where(t => !t.IsInterface && !t.IsAbstract && move.IsAssignableFrom(t))
                                            .ToList();
                foreach (var row in Multiplayer.Tactical.TacticalCommandSync.LocalAbilities.Keys)
                {
                    if (ConditionalDropRows.Contains(row.Name)) continue;
                    var swallowed = movers.Where(t => row.IsAssignableFrom(t)).Select(t => t.Name).ToArray();
                    if (swallowed.Length > 0)
                        yield return "L96 drop-list-swallows-movement: LocalAbilities row " + row.Name +
                                     " unconditionally drops " + string.Join(", ", swallowed) + ", which " +
                                     "implement(s) IMoveAbility. A displacement that never crosses leaves every " +
                                     "other peer holding a stale position until a settle rubber-bands the actor " +
                                     "back — the Dash report. Either the row must re-decide per activation (add it " +
                                     "to ConditionalDropRows with the game marker that discriminates it) or it must " +
                                     "not cover a moving ability.";
                }
            }

            // ─── HALF 3: THE OUTCOME, EXECUTED — a known-state survives the turn edge as the host's does ───
            foreach (var v in TurnEpochArm()) yield return v;

            var hurt = game.GetType("PhoenixPoint.Tactical.Entities.Abilities.TacticalHurtReactionAbility");
            var activate = hurt == null ? null : hurt.GetMethod("Activate", AllMembers);
            if (hurt == null || activate == null || !ReadsField(activate, "TriggerOnDamage"))
                yield return "L96 premise-hurt-reaction-undiscriminated: TacticalHurtReactionAbility.Activate no " +
                             "longer branches on TriggerOnDamage. That branch IS the discriminator the un-drop " +
                             "relies on — false takes PlayAction(HurtReaction_Implementation, parameter), i.e. the " +
                             "caller's own target, and true ignores the parameter for GetHurtReactionTarget(). " +
                             "Without it there is no sound way to tell an ordered reposition from an ambient one.";
        }

        /// <summary>HALF 1's arms, and the codec one EXECUTES the shipped writer and reader rather than reading
        /// their call graph. Two claims. (1) The settle payload has a SLOT for the host's known-state — a
        /// <c>PendingSettle.Vision</c> of the row type — because an applier that is reached and correct is
        /// worth nothing if the value never reached the queue. (2) The codec is LOSSLESS over a two-faction
        /// board: guid, <c>Located</c> and <c>Revealed</c> all survive the round trip. That second one is the
        /// arm the old shape could not have: dropping the <c>Revealed</c> byte would leave every client's
        /// enemies at <c>Located</c> — the orange beacon and no model — while every structural arm in this file
        /// and in L81 stayed green.</summary>
        private static IEnumerable<string> VisionCarriedArm(Type cmd)
        {
            var pending = cmd.GetNestedType("PendingSettle", AllMembers);
            var slot = pending == null ? null : pending.GetField("Vision", AllMembers);
            if (slot == null ||
                slot.FieldType != typeof(List<Multiplayer.Tactical.TacticalCommandSync.VisionRow>))
                yield return "L96 settle-vision-not-carried: TacticalCommandSync.PendingSettle has no " +
                             "List<VisionRow> Vision field, so the host's per-faction KnownState does not ride " +
                             "the settle at all. Whatever the applier does, it is applying something this peer " +
                             "decided for itself — which is the pre-2026-08-08 contract that could only ever ADD " +
                             "a reveal and never remove one.";

            var sent = new List<Multiplayer.Tactical.TacticalCommandSync.VisionRow>
            {
                new Multiplayer.Tactical.TacticalCommandSync.VisionRow
                    { FactionGuid = "Px_TacticalFactionDef_guid", Located = 1, Revealed = 0 },
                new Multiplayer.Tactical.TacticalCommandSync.VisionRow
                    { FactionGuid = "Alien_TacticalFactionDef_guid", Located = 0, Revealed = 2 },
            };
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Multiplayer.Tactical.TacticalCommandSync.WriteVision(w, sent);
            w.Flush();
            ms.Position = 0;
            var back = Multiplayer.Tactical.TacticalCommandSync.ReadVision(new BinaryReader(ms));

            string lost = null;
            if (back == null || back.Count != sent.Count) lost = "row count " + (back == null ? "null" : back.Count.ToString());
            else
                for (int i = 0; i < sent.Count && lost == null; i++)
                    if (back[i].FactionGuid != sent[i].FactionGuid ||
                        back[i].Located != sent[i].Located || back[i].Revealed != sent[i].Revealed)
                        lost = "row " + i + " came back as '" + back[i].FactionGuid + "' (" + back[i].Located +
                               "," + back[i].Revealed + ") instead of '" + sent[i].FactionGuid + "' (" +
                               sent[i].Located + "," + sent[i].Revealed + ")";
            if (lost != null)
                yield return "L96 settle-vision-codec-lossy: WriteVision/ReadVision did not round-trip the host's " +
                             "known-state — " + lost + ". Every peer's visibility is assigned from exactly these " +
                             "bytes, so a lost Revealed leaves an enemy at Located (an orange beacon and no " +
                             "model), a lost Located loses the beacon, and a mangled guid addresses the wrong " +
                             "faction or none at all — silently, since the applier cannot tell a dropped field " +
                             "from a host that genuinely knows nothing.";
        }

        /// <summary>
        /// HALF 3 — THE OUTCOME ARM, and the only one here that RUNS the shipped code rather than reading it.
        /// Halves 1 and 2 assert CALLS: a second call site exists, a foreign sweep exists, a discriminator is
        /// read. All of that stayed true on 2026-08-05 while an enemy's sound beacon was <c>Located</c> on
        /// the host and <c>Hidden</c> on both clients, because the defect was not a missing call — it was
        /// ORDER. The host raised the trace from a bleed tick AFTER its faction-turn decay
        /// (<c>Player.log</c>:8227 turn, :8272 damage); the clients applied the mirrored raise BEFORE their
        /// own decay (:6946 damage, :7051 turn) and wiped it. The settle could not repair it either: the
        /// actor was cloaked, and both <c>LocateByDistance</c> and <c>ReUpdateHearingImpl</c> skip cloaked
        /// actors.
        ///
        /// So this arm drives the REAL production chokepoint — <c>SurfaceRouter.OnInbound</c>, with the real
        /// <c>ClientBehindTurnEdge</c> gate armed — and asserts the four facts the fix consists of:
        /// (1) the turn cursor itself is never held (holding it would deadlock the gate that opens it);
        /// (2) a record that arrives while this peer is behind the host's edge is NOT applied;
        /// (3) crossing the edge applies the whole backlog IN ARRIVAL ORDER;
        /// (4) the hold is BOUNDED — a peer that never crosses still gets its records, late and loudly.
        /// Delete the hold from <c>OnInbound</c> and (2) goes red; drop the <c>TacTurn</c> exemption and (1)
        /// goes red; drop the ceiling and (4) hangs at red.
        /// </summary>
        private static IEnumerable<string> TurnEpochArm()
        {
            var router = new Multiplayer.Network.Sync.SurfaceRouter();
            var applied = new List<byte>();
            var prevTac = Multiplayer.Network.Sync.SurfaceRouter.TacticalInbound;
            var prevGate = Multiplayer.Network.Sync.SurfaceRouter.ClientBehindTurnEdge;
            bool behind = true;
            try
            {
                Multiplayer.Network.Sync.SurfaceRouter.TacticalInbound =
                    (peer, sid, payload) => { applied.Add(sid); return true; };
                Multiplayer.Network.Sync.SurfaceRouter.ClientBehindTurnEdge = () => behind;

                // (1) the cursor is never held — it is the message that OPENS the gate.
                router.OnInbound(1, Envelope(Multiplayer.Network.Sync.SurfaceIds.TacTurn));
                if (!applied.Contains(Multiplayer.Network.Sync.SurfaceIds.TacTurn))
                    yield return "L96 turn-epoch-holds-the-cursor: the 0x80 turn cursor was HELD by the " +
                                 "turn-epoch gate. That surface is the only thing that can move this peer past " +
                                 "the edge the gate is waiting for, so holding it is a permanent deadlock — the " +
                                 "exact failure the gate exists to avoid.";

                // (2) a record stamped on the far side of the host's edge does not land early.
                applied.Clear();
                router.OnInbound(1, Envelope(Multiplayer.Network.Sync.SurfaceIds.TacResult));
                router.OnInbound(1, Envelope(Multiplayer.Network.Sync.SurfaceIds.TacCommand));
                if (applied.Count != 0)
                    yield return "L96 turn-epoch-not-gated: a record arriving while this peer is still BEHIND " +
                                 "the faction-turn edge the host already crossed was applied immediately (" +
                                 applied.Count + " surface(s)). It is then applied in the wrong epoch and this " +
                                 "peer's own turn edge decays what the host had already raised — a KnownState " +
                                 "the host shows and no client does (2026-08-05, an invisible enemy whose sound " +
                                 "beacon existed only on the host).";

                // (3) crossing the edge releases the backlog, in arrival order.
                behind = false;
                router.ReleaseHeld();
                if (applied.Count != 2 ||
                    applied[0] != Multiplayer.Network.Sync.SurfaceIds.TacResult ||
                    applied[1] != Multiplayer.Network.Sync.SurfaceIds.TacCommand)
                    yield return "L96 turn-epoch-backlog-lost: after this peer crossed its own turn edge the " +
                                 "held records did not replay as [0x84, 0x82] (got " + Describe(applied) + "). " +
                                 "A gate that drops or reorders what it held is worse than no gate: the peers " +
                                 "then differ by whatever went missing, permanently and silently.";

                // (4) the hold is bounded — a peer that never crosses still gets its records.
                behind = true;
                applied.Clear();
                router.OnInbound(1, Envelope(Multiplayer.Network.Sync.SurfaceIds.TacResult));
                for (int i = 0; i <= Multiplayer.Network.Sync.SurfaceRouter.HeldFrameCeiling; i++)
                    router.ReleaseHeld();
                if (applied.Count == 0)
                    yield return "L96 turn-epoch-hold-unbounded: a peer that never crosses its turn edge holds " +
                                 "inbound records forever (" + router.HeldCount + " still queued after " +
                                 Multiplayer.Network.Sync.SurfaceRouter.HeldFrameCeiling + " pumps). Law L91: " +
                                 "no wait in this mod may be unbounded, including one that waits on nothing but " +
                                 "this peer's own stuck turn machine.";
            }
            finally
            {
                Multiplayer.Network.Sync.SurfaceRouter.TacticalInbound = prevTac;
                Multiplayer.Network.Sync.SurfaceRouter.ClientBehindTurnEdge = prevGate;
            }
        }

        private static byte[] Envelope(byte surfaceId) =>
            Multiplayer.Network.Sync.SyncProtocol.EncodeEnvelope(
                surfaceId, Multiplayer.Network.Sync.SyncKind.ActionApply, new byte[] { 7 });

        private static string Describe(List<byte> ids) =>
            ids.Count == 0 ? "nothing" : "[" + string.Join(", ", ids.Select(b => "0x" + b.ToString("X2"))) + "]";

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        /// <summary>True if <paramref name="m"/> reads a field with this name. Def flags are public FIELDS in
        /// this codebase (<c>TacticalHurtReactionAbilityDef.TriggerOnDamage</c> is
        /// <c>public bool TriggerOnDamage = true;</c>), so the read is an <c>ldfld</c> and no getter token
        /// exists to scan for — the same correction L80's <c>ReadsGuid</c> had to make.</summary>
        private static bool ReadsField(MethodBase m, string name)
        {
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineField) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                typeArgs, methodArgs); } catch { }
                if (f != null && f.Name == name) return true;
            }
            return false;
        }

        private static List<MethodBase> Calls(MethodBase m)
        {
            var seq = new List<MethodBase>();
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                      typeArgs, methodArgs); } catch { }
                if (callee != null) seq.Add(callee);
            }
            return seq;
        }

        private struct Step { public OpCode Op; public int Pos; }

        /// <summary>A naive byte scan would match operand bytes and invent edges, and a law that cries wolf is
        /// a law that gets ignored. Anything unparseable ABANDONS the method rather than guessing.</summary>
        private static IEnumerable<KeyValuePair<byte[], Step>> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                OpCode op;
                if (!OpCodeByValue.TryGetValue(code, out op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                yield return new KeyValuePair<byte[], Step>(il, new Step { Op = op, Pos = i });
                i += size;
            }
        }

        private static int OperandSize(OperandType t, byte[] il, int pos)
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
                    if (pos + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
            }
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }
    }
}
