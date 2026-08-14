using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Entities.Abilities;
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
    /// …AND THEN IT WAS TOLD TOO LOUDLY (second pass, same day). The first fix showed EVERY reject, and
    /// <c>SessionNotifier.ShowToast(modalFallback: true)</c> finds no <c>NotificationController</c> in
    /// tactical or replenish, so it falls through to <c>GameUtl.GetMessageBox().ShowSimplePrompt</c>: a
    /// release-visible ERROR BOX per refusal. Most refusals are not news — <c>ReplenishAll</c> ships every
    /// un-full item as its own intent, including the un-researched crossbow clip vanilla would have silently
    /// returned false on, and the host's re-run of the same affordability test rejects each one. A reject is a
    /// CONVERGENCE mechanism; the reason always crosses the wire and is always logged, and only a refusal the
    /// host marked <c>notify</c> reaches the screen. Which refusals those are is arm (g), and it is a closed
    /// allowlist rather than a judgement call at 40 call sites.
    ///
    /// THE ARMS:
    ///   (a) PREMISE — the settle, reject and AI-gate families resolve.
    ///   (b) THE SWEEP IS ON THE TURN EDGE: <c>HostBroadcastTurn</c> reaches <c>HostSweepTick</c>, which
    ///       reaches <c>HostSettleAllLive</c>, which reaches <c>HostSettle</c>. (The middle hop is the
    ///       2026-08-05 deferral: the sweep waits for this host's own <c>IsPlayingTurn</c> so it ships
    ///       post-AP-restore numbers — L98's sweep-epoch arm owns WHEN, this one owns WHETHER.) This is the
    ///       "no keyed live actor's last settle is older than the current faction turn" arm, asserted where
    ///       it is decidable.
    ///   (c) THE SWEEP IS OVER EVERY ACTOR, NOT ONE: it enumerates the map and asks each actor for its key,
    ///       rather than settling a single subject. A sweep of one is the bug with a new name.
    ///   (d) THE CLIENT CANNOT RUN AI EVALUATION FROM EITHER DOOR: the gate is on
    ///       <c>AIEvaluationAbility.Activate</c>, the funnel BOTH doors below the turn coroutine pass
    ///       through, and the effect route into <c>ExecuteAIEvaluationAbilities</c> still reaches it. This is the checkable form of "on a client,
    ///       SetTransform for an AI faction's actor happens only inside SyncApplyScope": the runtime
    ///       statement is not decidable headless, but its one known cause is.
    ///   (e) THE REFUSAL REACHES THE ONE PEER THAT GESTURED, AND NOBODY ELSE: unicast to the sender, never a
    ///       broadcast, through the ONE codec pair both ends use — and the screen surface must still be
    ///       reachable, or the notify bit is a flag nothing reads.
    ///   (f) EXECUTED: THE REASON SURVIVES THE ENVELOPE, AND SO DOES THE NOTIFY BIT. A reject whose text is
    ///       dropped arrives as the empty nudge it used to be; a notify bit lost in one direction turns every
    ///       ordinary refusal into an error box, and in the other puts the ones that matter back in silence.
    ///   (g) A MODAL ONLY FOR A MOD-PROTOCOL REFUSAL. The notifying overload's call sites ARE the allowlist,
    ///       read off the IL: turn ownership, a site another peer took, an event another peer answered — the
    ///       refusals co-op invented, where the player holds a live control that did nothing and vanilla has
    ///       nothing to grey. Never a refusal the game itself expresses by DISABLING the control.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • drop HostSweepTick's HostSettleAllLive call          → settle-not-swept-at-the-turn-edge
    ///   • send the reject nudge with a null payload            → refusal-never-reaches-the-player
    ///   • flip the notify bit's polarity in EncodeNudge        → modal-flag-lost-on-the-wire
    ///   • pass notify:true from any ordinary reject call site  → modal-for-an-ordinary-refusal
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
            var aiEvalGate = mod.GetType("Multiplayer.Tactical.ClientAiEvaluationSeamGate");

            var broadcastTurn = turnSync?.GetMethod("HostBroadcastTurn", All);
            // The sweep is ARMED at the announcement and FIRED once this host's own turn has started
            // (TacticalTurnSync.HostSweepTick, 2026-08-05) — emitting it from OnNewTurn shipped pre-AP-restore
            // numbers, which L98's sweep-epoch arm now owns. The edge still has to produce a sweep; it just
            // reaches it one hop further on.
            var sweepTick = turnSync?.GetMethod("HostSweepTick", All);
            var sweep = cmdSync?.GetMethod("HostSettleAllLive", All);
            var settle = cmdSync?.GetMethod("HostSettle", All);
            var keyOf = actorKey?.GetMethod("Of", All);
            var getActors = typeof(Base.Levels.BaseMap).GetMethod("GetActors", All);

            // The two Reject overloads are the whole opt-in mechanism: the params-only one CANNOT notify, the
            // bool one is the only way to ask for a popup. Resolved by signature so a rename cannot blur them.
            var rejects = typeof(IntentRail).GetMethods(All).Where(m => m.Name == "Reject").ToList();
            var reject = rejects.FirstOrDefault(m => m.GetParameters().Any(p => p.ParameterType == typeof(bool)));
            var rejectQuiet = rejects.FirstOrDefault(m => !m.GetParameters().Any(p => p.ParameterType == typeof(bool)));
            var inbound = typeof(IntentRail).GetMethod("HandleInbound", All);
            var toast = typeof(SessionNotifier).GetMethod("ShowToast", All);
            var encodeNudge = typeof(IntentRail).GetMethod("EncodeNudge", All);
            var decodeNudge = typeof(IntentRail).GetMethod("DecodeNudge", All);
            var sendToClient = typeof(NetworkEngine).GetMethod("SendToClient", All);
            var broadcast = typeof(NetworkEngine).GetMethod("BroadcastToAll", All);

            var aiEval = typeof(TacticalLevelController).GetMethod("ExecuteAIEvaluationAbilities", All);
            var queuedSeq = typeof(TacticalLevelController).GetMethod("ExecuteQueuedAbilitiesSequence", All);
            var gatePrefix = aiEvalGate?.GetMethod("Prefix", All);

            if (broadcastTurn == null || sweepTick == null || sweep == null || settle == null || keyOf == null || getActors == null ||
                reject == null || rejectQuiet == null || inbound == null || toast == null ||
                encodeNudge == null || decodeNudge == null || sendToClient == null || broadcast == null ||
                aiEval == null || queuedSeq == null || gatePrefix == null)
            {
                yield return "L123 premise-changed: the settle/refusal family no longer resolves " +
                             "(TacticalTurnSync.HostBroadcastTurn/HostSweepTick, TacticalCommandSync.HostSettleAllLive/" +
                             "HostSettle, TacticalActorKey.Of, BaseMap.GetActors, IntentRail.Reject in BOTH " +
                             "its quiet and its notifying overload / HandleInbound / EncodeNudge / " +
                             "DecodeNudge, SessionNotifier.ShowToast, NetworkEngine.SendToClient/" +
                             "BroadcastToAll, TacticalLevelController.ExecuteAIEvaluationAbilities/" +
                             "ExecuteQueuedAbilitiesSequence, ClientAiEvaluationSeamGate.Prefix). Every arm below " +
                             "would pass vacuously, so 'drift is corrected and refusals are heard' is " +
                             "UNCHECKED rather than satisfied";
                yield break;
            }

            // ═══ (b) THE SWEEP IS ON THE TURN EDGE ═══
            if (!Reaches(broadcastTurn, sweepTick, mod) || !Reaches(sweepTick, sweep, mod))
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
            // The gate moved OFF this method on 2026-08-14: ExecuteAIEvaluationAbilities is only one of the
            // two doors (AIEvaluationStatus.OnApply is the other), so the verdict now sits on the funnel both
            // pass through, AIEvaluationAbility.Activate. This arm keeps the half that is L123's own — that
            // the effect route still reaches evaluation, so the funnel below it is still load-bearing — and
            // leaves the completeness claim to L320/L472, which sweep for a third door.
            var patchAttr = aiEvalGate.GetCustomAttributesData()
                                      .FirstOrDefault(a => a.AttributeType.Name == "HarmonyPatch" &&
                                                           a.ConstructorArguments.Count == 3);
            var patchedType = patchAttr == null ? null : patchAttr.ConstructorArguments[0].Value as Type;
            string patched = patchAttr == null ? null : patchAttr.ConstructorArguments[1].Value as string;
            if (patchedType != typeof(AIEvaluationAbility) || patched != "Activate")
                yield return "L123 client-can-still-run-enemy-ai: ClientAiEvaluationSeamGate patches '" +
                             (patchedType?.Name ?? "<nothing resolvable>") + "." + (patched ?? "?") +
                             "' rather than AIEvaluationAbility.Activate. That method is the ONLY thing both " +
                             "routes into an AI decision share below the turn coroutine — " +
                             "ExecuteQueuedAbilitiesEffect.OnApply:22 -> ExecuteAIEvaluationAbilities -> " +
                             "TacticalAbility.Execute:1159 -> Activate, and AIEvaluationStatus.OnApply:17 -> " +
                             "Activate directly. Gating either caller instead leaves the other door open, " +
                             "and the client moves the aliens on its own screen";
            if (!Reaches(queuedSeq, aiEval, game) &&
                !Program.Callees(Iterator(queuedSeq), game).Any(c => Same(c, aiEval)))
                yield return "L123 premise-changed: ExecuteQueuedAbilitiesSequence no longer runs " +
                             "ExecuteAIEvaluationAbilities, so the gate is placed on a method that route can " +
                             "no longer reach. Re-derive where enemy AI is decided before trusting the gate";

            // ═══ (e) THE REFUSAL REACHES THE ONE PEER THAT GESTURED, AND NOBODY ELSE ═══
            if (!Reaches(reject, sendToClient, mod))
                yield return "L123 refusal-never-reaches-the-player: IntentRail.Reject does not unicast the " +
                             "nudge to the peer that gestured. The host composes the refusal in the game's own " +
                             "localized words, logs it to its own console, and tells nobody — so the player " +
                             "who clicked gets a wind-up, a cancel, and no word, five times in a row";
            if (Reaches(reject, broadcast, mod))
                yield return "L123 refusal-told-to-everyone: IntentRail.Reject BROADCASTS the refusal. A reject " +
                             "is about one peer's own gesture: every other player would be interrupted by the " +
                             "reason a stranger's click died, and on the surface that falls back to a native " +
                             "prompt in tactical that is a modal in the middle of somebody else's turn";
            if (!Reaches(reject, encodeNudge, mod) || !Reaches(inbound, decodeNudge, mod))
                yield return "L123 refusal-never-reaches-the-player: the nudge no longer goes through the ONE " +
                             "EncodeNudge/DecodeNudge pair (Reject encodes / HandleInbound decodes). Two " +
                             "hand-rolled halves can agree with each other while disagreeing with the wire, and " +
                             "arm (f) would then be executing a codec nothing actually uses";
            if (!Reaches(inbound, toast, mod))
                yield return "L123 refusal-never-reaches-the-player: IntentRail.HandleInbound's client branch " +
                             "decodes the reason and has no way to put it on screen at all. The notify bit " +
                             "would then be a flag nothing reads — the refusals that DO need a word (turn " +
                             "ownership, a mission another peer took) are back to reaching a log file the " +
                             "player mid-battle does not have open";

            // ═══ (f) EXECUTED: THE REASON SURVIVES THE ENVELOPE — AND SO DOES THE NOTIFY BIT ═══
            // The production codec, not a copy of it. Both values, because the bit is what separates "the
            // convergence mechanism did its job quietly" from "the player is looking at an error box".
            const string why = "command for Soldier_9: the game's own gate refuses this ability: Нет подходящей цели";
            byte surface;
            SyncKind kind;
            byte[] body;
            foreach (var wanted in new[] { false, true })
            {
                var wire = SyncProtocol.EncodeEnvelope(SurfaceIds.TacCommandIntent, SyncKind.ActionRequest,
                                                       IntentRail.EncodeNudge(why, wanted));
                bool got = false;
                string reason = SyncProtocol.TryDecodeEnvelope(wire, out surface, out kind, out body)
                                    ? IntentRail.DecodeNudge(body, out got) : null;
                if (reason != why)
                    yield return "L123 refusal-lost-on-the-wire: a reject reason does not survive the " +
                                 "encode→envelope→decode round-trip (notify=" + wanted + ", decoded='" +
                                 (reason ?? "<null>") + "'). It arrives as the empty nudge the client treats " +
                                 "as 'repaint and say nothing' — the identical silent failure one layer down, " +
                                 "with the code that sends it looking correct";
                if (got != wanted)
                    yield return "L123 modal-flag-lost-on-the-wire: the notify bit does not survive the " +
                                 "round-trip (sent " + wanted + ", read " + got + "). Lost in one direction " +
                                 "every ordinary refusal — an unaffordable reload, an un-researched clip — " +
                                 "becomes a native error prompt the player must dismiss; lost in the other, " +
                                 "the refusals that have no other surface go silent again";
            }
            bool quiet;
            if (IntentRail.DecodeNudge(null, out quiet) != null || quiet)
                yield return "L123 refusal-lost-on-the-wire: a NULL nudge does not decode to 'no reason, no " +
                             "modal'. A malformed or legacy envelope must still repaint the client and must " +
                             "never raise a blank prompt";
            if (IntentRail.DecodeNudge(new byte[0], out quiet) != null || quiet)
                yield return "L123 refusal-lost-on-the-wire: an EMPTY nudge does not decode to 'no reason, no " +
                             "modal' — the client would show a blank prompt for a reject that carries no words";

            // ═══ (g) A MODAL ONLY FOR A MOD-PROTOCOL REFUSAL ═══
            // The notifying overload is the ONLY way to raise a popup, so its call sites ARE the allowlist and
            // the law can read them off the IL. Every entry is a refusal co-op invented, where vanilla has no
            // control to grey because single-player cannot produce the situation, and the player is left with
            // a live button that did nothing. Everything else — unaffordable, un-researched, no storage, no
            // power, the game's own ability gate — is a refusal vanilla expresses by DISABLING the control,
            // and popping an error box for each is a worse bug than the silence L123 originally replaced
            // (live: ReplenishAll ships every un-full item, so one click produced one prompt per item).
            var allowed = new[]
            {
                "Multiplayer.Tactical.TacticalTurnSync.HandleEndTurn",
                "Multiplayer.Tactical.TacticalTurnSync.HandleLeaveBattle",
                "Multiplayer.Network.Sync.MissionSync.Reject",
                // DWI-10: a peer pressed a live Start button, but host-serialized membership/offer/mission
                // identity or durable persistence refused it. Every case is co-op/protocol-only and the
                // exact offer carrier deliberately stays open for retry, so without this line the click is dead.
                "Multiplayer.Network.Sync.MissionSync.RejectDurableStart",
                "Multiplayer.Network.Sync.EventSync.HandleAnswer",
                // L411: a peer clicked the site's LIVE re-entry row and the host had nothing to re-offer
                // (another peer took the mission, or the encounter record is no longer free). Co-op-invented
                // exactly: single player cannot have a second peer consume the offer between the row being
                // drawn and the click landing, so vanilla has no control to grey — the row is live and the
                // click does nothing. The DECLINE side of the same handler rejects WITHOUT notify.
                "Multiplayer.Network.Sync.EventSync.HandleReoffer",
                // L146: two peers commanded ONE soldier and this peer lost the race. Purely co-op-invented —
                // single player cannot produce a second commander, so vanilla has no control to grey and the
                // player is left with a soldier that wound up and cancelled for no visible reason (measured:
                // the reject nudge reached the client's log and never its screen, 22:28:30). The notify bit is
                // computed per refusal (ReferenceEquals(refusal, BusyRefusal)), so every OTHER arm of Validate
                // — the game's own ability gate, AP, WP, target-not-offered — still crosses quietly.
                "Multiplayer.Tactical.TacticalCommandSync.HandleActivate",
                // The give-up arm of the SAME hold L146's entry is about, one ceiling later. An order held
                // behind its own peer's previous one is a co-op-only object: it exists because that peer plays
                // speculatively and is a whole order ahead of the host's presentation, which single player
                // cannot produce — there is no second machine. So vanilla has nothing to grey; it never saw
                // the click. What makes this one worse than the arm it follows is the delay: the verdict lands
                // up to DeferCeilingSeconds (10 s) after the gesture, on an order that is then DISCARDED, and
                // the forced settle beside it snaps the soldier back to where the host left him. Without a
                // line that reads as the host rubber-banding a soldier for no reason ten seconds later — a
                // silent drop is exactly what this arm's popup exists to prevent. Fires only from HostTick's
                // ceiling, i.e. only when the running ability is genuinely STUCK; every ordinary busy refusal
                // still crosses quietly through the Validate arm.
                "Multiplayer.Tactical.TacticalCommandSync.HostTick",
                // L187: a loadout change this peer MADE was undone on the host by a stale open-screen flush
                // (2026-08-07, seven applies in eleven seconds). Co-op-invented in the strongest sense —
                // single player has no second screen to flush over your change, so vanilla has no control to
                // grey — and it is the one refusal that arrives AFTER the player was told yes: he watched the
                // item move and the host's own log carried the only word that it had not. Reject's re-emit
                // snaps his screen back, which without a line reads as a bug rather than a verdict. Fires
                // only from CheckLastApplyStuck, i.e. only when the guard that now blocks the flush outright
                // has already failed; every other equip refusal (no storage, unknown def, not a Phoenix
                // soldier) still crosses quietly through EquipSync.Reject, which is a SEPARATE call site on
                // the non-notifying overload precisely so this entry cannot widen into the whole family.
                "Multiplayer.Network.Sync.EquipSync.RejectAndNotify",
            };
            var optIns = mod.GetTypes()
                            .SelectMany(Methods)
                            .Where(m => m.DeclaringType != typeof(IntentRail) && Reaches(m, reject, mod))
                            .Select(m => (m.DeclaringType == null ? "?" : m.DeclaringType.FullName) + "." + m.Name)
                            .Distinct()
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();
            var added = optIns.Where(n => !allowed.Contains(n)).ToList();
            var gone = allowed.Where(n => !optIns.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (added.Count > 0)
                yield return "L123 modal-for-an-ordinary-refusal: [" + string.Join(", ", added) + "] now asks " +
                             "IntentRail.Reject to NOTIFY. A modal belongs only to a refusal co-op invented — " +
                             "turn ownership, a site another peer already took, an event another peer already " +
                             "answered — where the player is left holding a live control that did nothing and " +
                             "vanilla has nothing to grey. If this call site is one of those, add it to the " +
                             "allowlist WITH its reason; otherwise it is an error box for something the game " +
                             "itself would have refused in silence";
            if (gone.Count > 0)
                yield return "L123 refusal-never-reaches-the-player: [" + string.Join(", ", gone) + "] no " +
                             "longer notifies. These are the refusals with NO other surface: the End Turn " +
                             "button is not greyed for a peer whose turn it is not, the Launch button is not " +
                             "greyed for a site another peer just took, and the event popup is already gone " +
                             "from the screen. Dropping the notify here is dropping the player's only word";
        }

        private static IEnumerable<MethodBase> Methods(Type t)
        {
            try { return t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)); }
            catch { return Enumerable.Empty<MethodBase>(); }
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
