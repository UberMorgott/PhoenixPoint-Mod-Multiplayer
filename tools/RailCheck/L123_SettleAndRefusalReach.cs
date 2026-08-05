using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Levels;

namespace RailCheck
{
    /// <summary>
    /// L123 — AN ACTOR THE HOST IS NOT ANIMATING IS STILL CORRECTED, AND A REFUSED ORDER SAYS SO.
    ///
    /// The failure (live 3-instance test, 2026-08-05): the user shot an enemy, it cloaked and ran. On the
    /// host it went one way, on the clients another. The client then legitimately saw an enemy where the host
    /// had nobody, so the game's own gate refused every aimed shot at it — <c>HOST tac-cmd REJECT peer=1 —
    /// the game's own gate refuses this ability: Нет подходящей цели</c> — and the soldier wound up and
    /// cancelled five times in 45 s with no explanation on the screen of the player doing it. The geometry
    /// proves the mechanism: the failed shots pointed due WEST from (-2.5,0,-19.5) while the host's only
    /// living enemy was north-east, and the lock broke on the exact settle <c>HOST settle Fishman_20 @
    /// (9.5,0,-12.5)</c> with the very next shot accepted and the mission won.
    ///
    /// THREE THINGS WERE WRONG, AND THIS LAW HOLDS ALL THREE — they are one story, not three tickets.
    ///
    ///   THE DIVERGENCE GOT IN. <c>ClientAiGate</c> blocks <c>TacticalFaction.AIUpdateCrt</c> on the belief
    ///   that it is how AI decisions are reached. It is one of two. <c>TacticalLevelController.
    ///   ExecuteQueuedAbilitiesSequence</c>:1226 also runs from <c>ExecuteQueuedAbilitiesEffect.OnApply</c>:22
    ///   — an authored effect started straight on Timing, outside any turn coroutine — and its
    ///   <c>ExecuteAIEvaluationAbilities</c> arm executes an <c>AIEvaluationAbility</c> on every
    ///   <c>CurrentFaction</c> actor with an <c>AIEvaluationStatus</c>. On a client in the alien turn, that is
    ///   the aliens: <c>this CLIENT activated 'Move_AbilityDef' on Fishman_17 … the client ran enemy AI of its
    ///   own</c>, 10 ms before the host's mirror and 8 s before <c>client AI turn SUPPRESSED</c> appeared.
    ///
    ///   AND IT NEVER GOT OUT. <c>HostSettle</c> had exactly two callers — the end-of-action rider and the
    ///   reject path — and BOTH are about an actor the host is ANIMATING. Nothing in the arc corrected an
    ///   actor the host is not animating, so a divergence that got in by any other door stayed in for the
    ///   rest of the battle. The fix is not another guard on another funnel: it is a sweep at the one moment
    ///   every peer agrees on, which heals the class regardless of which funnel leaked it.
    ///
    ///   AND THE PLAYER WAS NEVER TOLD. <c>IntentRail.Reject</c> sent the nudge as an EMPTY envelope and the
    ///   client branch only logged "repainting open UI". The refusal REASON — which the host had already
    ///   composed, in the game's own localized words — never crossed the wire. Silent swallow is this repo's
    ///   dominant bug class; a refusal the player cannot see is its purest form.
    ///
    /// THE ARMS:
    ///   (a) PREMISE — the settle, reject and AI-gate families resolve.
    ///   (b) THE SWEEP IS ON THE TURN EDGE: <c>HostBroadcastTurn</c> reaches <c>HostSettleAllLive</c>, which
    ///       reaches <c>HostSettle</c>. This is the "no keyed live actor's last settle is older than the
    ///       current faction turn" arm, asserted where it is decidable.
    ///   (c) THE SWEEP IS OVER EVERY ACTOR, NOT ONE: it enumerates the map and asks each actor for its key,
    ///       rather than settling a single subject. A sweep of one is the bug with a new name.
    ///   (d) THE CLIENT CANNOT RUN AI EVALUATION FROM EITHER DOOR: the gate is on
    ///       <c>ExecuteAIEvaluationAbilities</c> itself, which is the ONLY thing both entry points into
    ///       <c>ExecuteQueuedAbilitiesSequence</c> share. This is the checkable form of "on a client,
    ///       SetTransform for an AI faction's actor happens only inside SyncApplyScope": the runtime
    ///       statement is not decidable headless, but its one known cause is.
    ///   (e) THE REFUSAL IS PUT ON THE WIRE, AND TAKEN OFF IT, AND SHOWN. All three links, because any one
    ///       of them missing restores the silence exactly.
    ///   (f) EXECUTED: THE REASON SURVIVES THE ENVELOPE. A reject whose text is dropped or mangled by the
    ///       codec arrives as the empty nudge it used to be — the same failure, one layer down, with nothing
    ///       in any log.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • drop HostSettleAllLive from HostBroadcastTurn        → settle-not-swept-at-the-turn-edge
    ///   • send the reject nudge with a null payload            → refusal-never-reaches-the-player
    ///   • drop the ShowToast from the client reject branch     → refusal-never-reaches-the-player
    /// </summary>
    internal static class L123_SettleAndRefusalReach
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var mod = typeof(IntentRail).Assembly;
            var unity = typeof(UnityEngine.GameObject).Assembly;
            var cmdSync = mod.GetType("Multiplayer.Tactical.TacticalCommandSync");
            var turnSync = mod.GetType("Multiplayer.Tactical.TacticalTurnSync");
            var actorKey = mod.GetType("Multiplayer.Tactical.TacticalActorKey");
            var aiEvalGate = mod.GetType("Multiplayer.Tactical.ClientAiEvaluationGate");

            var broadcastTurn = turnSync?.GetMethod("HostBroadcastTurn", All);
            var sweep = cmdSync?.GetMethod("HostSettleAllLive", All);
            var settle = cmdSync?.GetMethod("HostSettle", All);
            var keyOf = actorKey?.GetMethod("Of", All);
            var getActors = typeof(Base.Levels.BaseMap).GetMethod("GetActors", All);

            var reject = typeof(IntentRail).GetMethod("Reject", All);
            var inbound = typeof(IntentRail).GetMethod("HandleInbound", All);
            var toast = typeof(SessionNotifier).GetMethod("ShowToast", All);
            var toBytes = typeof(Encoding).GetMethods(All)
                                          .FirstOrDefault(m => m.Name == "GetBytes" &&
                                                               m.GetParameters().Length == 1 &&
                                                               m.GetParameters()[0].ParameterType == typeof(string));
            var toString = typeof(Encoding).GetMethods(All)
                                           .FirstOrDefault(m => m.Name == "GetString" &&
                                                                m.GetParameters().Length == 1);

            var aiEval = typeof(TacticalLevelController).GetMethod("ExecuteAIEvaluationAbilities", All);
            var queuedSeq = typeof(TacticalLevelController).GetMethod("ExecuteQueuedAbilitiesSequence", All);
            var gatePrefix = aiEvalGate?.GetMethod("Prefix", All);

            if (broadcastTurn == null || sweep == null || settle == null || keyOf == null || getActors == null ||
                reject == null || inbound == null || toast == null || toBytes == null || toString == null ||
                aiEval == null || queuedSeq == null || gatePrefix == null)
            {
                yield return "L123 premise-changed: the settle/refusal family no longer resolves " +
                             "(TacticalTurnSync.HostBroadcastTurn, TacticalCommandSync.HostSettleAllLive/" +
                             "HostSettle, TacticalActorKey.Of, BaseMap.GetActors, IntentRail.Reject/" +
                             "HandleInbound, SessionNotifier.ShowToast, Encoding.GetBytes/GetString, " +
                             "TacticalLevelController.ExecuteAIEvaluationAbilities/" +
                             "ExecuteQueuedAbilitiesSequence, ClientAiEvaluationGate.Prefix). Every arm below " +
                             "would pass vacuously, so 'drift is corrected and refusals are heard' is " +
                             "UNCHECKED rather than satisfied";
                yield break;
            }

            // ═══ (b) THE SWEEP IS ON THE TURN EDGE ═══
            if (!Reaches(broadcastTurn, sweep, mod))
                yield return "L123 settle-not-swept-at-the-turn-edge: TacticalTurnSync.HostBroadcastTurn no " +
                             "longer settles every keyed live actor. The only other settles in this arc ride " +
                             "an action the host is ANIMATING (the end-of-action rider and the reject path), " +
                             "so an actor that drifted by any other route — a rogue local AI run, a missed " +
                             "mirror, an interrupted move — is never corrected at all. A peer that then sees " +
                             "an enemy where the host has nobody has every shot at it refused, which live " +
                             "read as a soldier locked solid for 45 s";
            if (!Reaches(sweep, settle, mod))
                yield return "L123 settle-not-swept-at-the-turn-edge: HostSettleAllLive does not reach " +
                             "HostSettle, so the sweep announces itself and ships nothing";

            // ═══ (c) THE SWEEP IS OVER EVERY ACTOR, NOT ONE ═══
            if (!Reaches(sweep, getActors, game) || !Reaches(sweep, keyOf, mod))
                yield return "L123 settle-sweep-is-not-a-sweep: HostSettleAllLive no longer enumerates the " +
                             "map (BaseMap.GetActors) and keys what it finds (TacticalActorKey.Of). A settle " +
                             "of one named subject is exactly what the two existing callers already do, and " +
                             "what left every actor the host is not animating uncorrected";

            // ═══ (d) THE CLIENT CANNOT RUN AI EVALUATION FROM EITHER DOOR ═══
            var patchAttr = aiEvalGate.GetCustomAttributesData()
                                      .FirstOrDefault(a => a.AttributeType.Name == "HarmonyPatch" &&
                                                           a.ConstructorArguments.Count == 2);
            string patched = patchAttr == null ? null : patchAttr.ConstructorArguments[1].Value as string;
            if (patched != "ExecuteAIEvaluationAbilities")
                yield return "L123 client-can-still-run-enemy-ai: ClientAiEvaluationGate patches '" +
                             (patched ?? "<nothing resolvable>") + "' rather than " +
                             "TacticalLevelController.ExecuteAIEvaluationAbilities. That method is the ONLY " +
                             "thing both routes into ExecuteQueuedAbilitiesSequence share — the turn " +
                             "coroutine (TacticalFaction.AIUpdateCrt:439, which ClientAiGate already covers) " +
                             "and ExecuteQueuedAbilitiesEffect.OnApply:22, an authored effect started " +
                             "straight on Timing that no gate touches. Patching either caller instead leaves " +
                             "the other door open, and the client moves the aliens on its own screen";
            if (!Reaches(queuedSeq, aiEval, game) &&
                !Program.Callees(Iterator(queuedSeq), game).Any(c => Same(c, aiEval)))
                yield return "L123 premise-changed: ExecuteQueuedAbilitiesSequence no longer runs " +
                             "ExecuteAIEvaluationAbilities, so the gate is placed on a method that route can " +
                             "no longer reach. Re-derive where enemy AI is decided before trusting the gate";

            // ═══ (e) THE REFUSAL IS PUT ON THE WIRE, TAKEN OFF IT, AND SHOWN ═══
            if (!Reaches(reject, toBytes, typeof(Encoding).Assembly))
                yield return "L123 refusal-never-reaches-the-player: IntentRail.Reject sends the nudge with no " +
                             "reason in it. The host composes the refusal in the game's own localized words, " +
                             "logs it to its own console, and ships an EMPTY envelope to the one peer that " +
                             "needs it — so the player who clicked gets a wind-up, a cancel, and no word, " +
                             "five times in a row, with nothing anywhere to say why";
            if (!Reaches(inbound, toString, typeof(Encoding).Assembly))
                yield return "L123 refusal-never-reaches-the-player: IntentRail.HandleInbound's client branch " +
                             "does not decode the reason off the nudge, so whatever the host shipped is " +
                             "dropped on arrival — the empty-nudge failure, one link further along";
            if (!Reaches(inbound, toast, mod))
                yield return "L123 refusal-never-reaches-the-player: IntentRail.HandleInbound's client branch " +
                             "decodes the reason and does not put it on screen. A refusal that only reaches a " +
                             "log file is the silence this arm exists to break — the player is mid-battle and " +
                             "has no log open";

            // ═══ (f) EXECUTED: THE REASON SURVIVES THE ENVELOPE ═══
            const string why = "command for Soldier_9: the game's own gate refuses this ability: Нет подходящей цели";
            byte surface;
            SyncKind kind;
            byte[] body;
            var wire = SyncProtocol.EncodeEnvelope(SurfaceIds.TacCommandIntent, SyncKind.ActionRequest,
                                                   Encoding.UTF8.GetBytes(why));
            if (!SyncProtocol.TryDecodeEnvelope(wire, out surface, out kind, out body) ||
                body == null || Encoding.UTF8.GetString(body) != why)
                yield return "L123 refusal-lost-on-the-wire: a reject reason does not survive the envelope " +
                             "round-trip (decoded='" + (body == null ? "<null>" : Encoding.UTF8.GetString(body)) +
                             "'). It would arrive as the empty nudge the client already treats as 'repaint and " +
                             "say nothing' — the identical silent failure, one layer down, and this time with " +
                             "the code that sends it looking correct";
            var empty = SyncProtocol.EncodeEnvelope(SurfaceIds.TacCommandIntent, SyncKind.ActionRequest, null);
            if (SyncProtocol.TryDecodeEnvelope(empty, out surface, out kind, out body) &&
                body != null && body.Length > 0)
                yield return "L123 refusal-lost-on-the-wire: an EMPTY nudge decodes to a non-empty payload, so " +
                             "the client would show a blank prompt for every reject that legitimately carries " +
                             "no words";
        }

        /// <summary>The MoveNext of a method's iterator state machine — where a coroutine's real calls live.
        /// Returns the method itself when it is not an iterator.</summary>
        private static MethodBase Iterator(MethodInfo m)
        {
            var owner = m.DeclaringType;
            if (owner == null) return m;
            string tag = "<" + m.Name + ">";
            foreach (var nested in owner.GetNestedTypes(All))
                if (nested.Name.IndexOf(tag, StringComparison.Ordinal) >= 0)
                {
                    var mn = nested.GetMethod("MoveNext", All);
                    if (mn != null) return mn;
                }
            return m;
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null && Program.Callees(from, asm).Any(c => Same(c, target));
    }
}
