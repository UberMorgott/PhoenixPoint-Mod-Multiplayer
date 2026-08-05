using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RailCheck
{
    /// <summary>
    /// L130 — A HINT TRIGGERED ON ONE PEER IS DISPLAYED ON EVERY PEER, EXACTLY ONCE EACH, AND GATES NOBODY.
    ///
    /// THE REPORT this law is built from (live 3-instance run, 2026-08-05). An Umbra was sighted: the TFTV
    /// panel appeared on ONE CLIENT — not the host, not the third peer. A second mission: an elite/gang
    /// panel on a DIFFERENT single client. The cause is not host-vs-client at all —
    /// <c>TacContextHelpManager.OnActorSeen</c>:199-205 hangs off each peer's OWN
    /// <c>ActorSawOtherFactionActorEvent</c> and returns unless <c>viewer.IsFromViewerFaction</c>, so
    /// whichever peer first reveals the actor is the ONLY one whose <c>EventTypeTriggered</c>:53 ever runs.
    ///
    /// WHY THIS LAW IS NOT L129. L129 asserts the popup SURVIVES de-blockering — that the display pump
    /// still exists and this mod does not gate it. Every one of its arms was green through the run above,
    /// because a hint that is never TRIGGERED here is never queued here, and an unpatched pump with an
    /// empty queue shows nothing while looking perfectly healthy. L129 watches the second half of the path;
    /// this law watches the first, and the two together are the whole of it.
    ///
    /// ASSERTED AS AN OUTCOME, WHICH IN THIS HARNESS MEANS REACHABILITY + AN EXECUTED PROBE — never "some
    /// call site exists". This repo has shipped laws that stayed green through the exact breakage they
    /// named (L81/L96) by asserting a call rather than its effect, so:
    ///   (a) <c>hint-never-sent</c> / <c>hint-sent-by-host-only</c> — from the CAPTURE seam, the mod must
    ///       reach BOTH <c>NetworkEngine.BroadcastToAll</c> and <c>NetworkEngine.SendToHost</c>. Every other
    ///       tactical surface in this repo is host→all, so "host broadcasts, client sends nothing" is the
    ///       shape a future edit falls into by habit — and it is precisely the bug: the two panels that
    ///       went missing were seen by CLIENTS.
    ///   (b) <c>relay-missing</c> — from the INBOUND seam, the mod must reach <c>BroadcastToAll</c>. A
    ///       client cannot address its fellow clients; without the host re-fanning what it receives, a
    ///       three-peer session shows the panel on two screens out of three and nothing says so.
    ///   (c) <c>mirror-shows-nothing</c> — from the INBOUND seam, the mod must reach
    ///       <c>ContextHelpManager.RegisterContextHelpHint</c>. Decoding a hint name and queueing nothing is
    ///       a wire with no outcome, which is exactly what the pre-fix build did (it had no wire at all).
    ///   (d) <c>shown-twice</c> / <c>echo-loop</c> / <c>not-reset-per-battle</c> — EXECUTED, not read. The
    ///       mirror's dedupe gate is invoked for real: it must say yes exactly ONCE per hint name, must say
    ///       no to the same name arriving back off the wire (that "no" IS the loop stop between peers), and
    ///       must say yes again after the per-battle reset — otherwise the same boss never announces itself
    ///       in the next mission.
    ///   (e) <c>inbound-unrouted</c> — the inbound seam must be called by something else in this mod. An
    ///       unrouted surface id is silently dropped by <c>SurfaceRouter</c> (forward-compat), so the wire
    ///       would exist, send correctly, and arrive nowhere.
    ///   (f) <c>mirror-forces-the-panel</c> — the mirror must NOT reach
    ///       <c>TacticalView.TryShowContextHint</c>. Registering leaves the panel to each peer's own idle
    ///       frame; pushing it means a peer is put in front of a modal it did not cause, mid-anything,
    ///       which is the "never gate a peer on a click" rule (L91) failing from the other direction.
    ///
    /// THE SEAMS ARE DERIVED, NOT NAMED: the capture is whatever type patches
    /// <c>TacContextHelpManager.EventTypeTriggered</c>, the mirror is that type's owner, and the inbound is
    /// its method carrying this repo's router-hook signature <c>(NetworkEngine, ulong, byte, byte[]) → bool</c>.
    /// Rename the class and the law follows it; delete the seam and the law goes red rather than vacuous.
    ///
    /// CEILING: reachability is walked only through the mirror type's OWN methods. Move a step of the send
    /// into a helper class and this under-reports (never a false red) — the same trade L129 arm (b) takes.
    ///
    /// Falsify: delete the <c>else engine.SendToHost(msg)</c> arm in <c>HintMirror.Send</c> →
    /// <c>hint-sent-by-host-only</c>; delete the <c>if (engine.IsHost) engine.BroadcastToAll(...)</c> line in
    /// <c>HandleInbound</c> → <c>relay-missing</c>; make <c>ShouldSend</c> return true unconditionally →
    /// <c>shown-twice</c>.
    /// </summary>
    internal static class L130_HintReachesEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var mod = typeof(Multiplayer.Network.Sync.DiffEngine).Assembly;
            var tacManager = game.GetType("PhoenixPoint.Tactical.ContextHelp.TacContextHelpManager");
            var baseManager = game.GetType("PhoenixPoint.Common.ContextHelp.ContextHelpManager");
            var view = game.GetType("PhoenixPoint.Tactical.View.TacticalView");
            if (tacManager == null || baseManager == null || view == null ||
                tacManager.GetMethod("EventTypeTriggered", All) == null ||
                baseManager.GetMethod("RegisterContextHelpHint", All) == null)
            {
                yield return "L130 premise-changed: TacContextHelpManager.EventTypeTriggered / " +
                             "ContextHelpManager.RegisterContextHelpHint no longer resolve. The hint TRIGGER " +
                             "path has moved, so this law is asserting something about a shape the game no " +
                             "longer has — re-read it before trusting that a boss sighted by one player is " +
                             "still announced to the others.";
                yield break;
            }

            // ── locate the seams by what they DO, never by their names ──
            var patchClass = mod.GetTypes().FirstOrDefault(
                t => L129_HintStillShows.Patches(t).Contains(tacManager.Name + ".EventTypeTriggered"));
            if (patchClass == null)
            {
                yield return "L130 no-trigger-capture: no type in this mod patches " + tacManager.Name +
                             ".EventTypeTriggered. That is the ONE funnel every tactical hint registration " +
                             "passes through, and it runs only on the peer whose own vision revealed the " +
                             "actor — with nothing capturing it, a boss panel is shown to exactly one player " +
                             "and the others never learn it happened.";
                yield break;
            }
            var mirror = patchClass.DeclaringType ?? patchClass;
            var capture = patchClass.GetMethods(All).FirstOrDefault(m => m.Name == "Postfix" || m.Name == "Prefix");
            var inbound = mirror.GetMethods(All).FirstOrDefault(IsRouterHook);

            if (capture == null)
            {
                yield return "L130 no-trigger-capture: " + patchClass.Name + " patches EventTypeTriggered but " +
                             "declares no Prefix/Postfix, so nothing observes the trigger and no other peer " +
                             "is told about the sighting.";
                yield break;
            }
            if (inbound == null)
            {
                yield return "L130 no-inbound-seam: " + mirror.Name + " declares no method with this repo's " +
                             "router-hook signature (NetworkEngine, ulong, byte, byte[]) -> bool, so nothing " +
                             "can receive a mirrored hint. Sending into a surface nobody handles is the " +
                             "silent-swallow class this repo's laws exist for.";
                yield break;
            }

            var sendSide = Closure(capture, mirror, mod);
            var recvSide = Closure(inbound, mirror, mod);

            // ── (a) EVERY peer announces its own sighting — not just the host ──
            bool broadcasts = ReachesMod(sendSide, mod, "NetworkEngine", "BroadcastToAll");
            bool toHost = ReachesMod(sendSide, mod, "NetworkEngine", "SendToHost");
            if (!broadcasts && !toHost)
                yield return "L130 hint-never-sent: the capture on EventTypeTriggered reaches neither " +
                             "NetworkEngine.BroadcastToAll nor SendToHost. It observes the trigger and tells " +
                             "nobody, which is the pre-fix build exactly: the panel stays on the single peer " +
                             "whose soldiers happened to have line of sight.";
            else if (!toHost)
                yield return "L130 hint-sent-by-host-only: the capture reaches BroadcastToAll but never " +
                             "SendToHost, so a CLIENT that sights the boss announces it to nobody. Every " +
                             "other tactical surface here is host->all and this one must not be: in the live " +
                             "2026-08-05 run BOTH missing panels were seen by clients, once by one client and " +
                             "once by another, with the host seeing neither.";
            else if (!broadcasts)
                yield return "L130 hint-never-fans-out: the capture reaches SendToHost but never " +
                             "BroadcastToAll, so a HOST sighting reaches no client at all.";

            // ── (b) the host relays what a client cannot address ──
            if (!ReachesMod(recvSide, mod, "NetworkEngine", "BroadcastToAll"))
                yield return "L130 relay-missing: the inbound seam never reaches NetworkEngine.BroadcastToAll. " +
                             "A client can only send to the host, so without the host re-fanning what it " +
                             "receives, a client's sighting is shown on two screens out of three and the " +
                             "third player sees a boss walk in with no announcement — half of the reported " +
                             "bug, and the half no single-client test can catch.";

            // ── (c) THE OUTCOME: a received hint is actually queued for display on this peer ──
            if (!ReachesGame(recvSide, game, baseManager, "RegisterContextHelpHint"))
                yield return "L130 mirror-shows-nothing: the inbound seam never reaches " +
                             "ContextHelpManager.RegisterContextHelpHint. The hint crossed the wire and was " +
                             "put nowhere — the per-frame pump L129 guards pops the PENDING queue, so a hint " +
                             "that never enters it is a hint that was mirrored on paper and displayed nowhere.";

            // ── (f) …and queued, never forced up ──
            if (ReachesGame(recvSide, game, view, "TryShowContextHint") ||
                ReachesGame(sendSide, game, view, "TryShowContextHint"))
                yield return "L130 mirror-forces-the-panel: this mod's hint mirror calls TacticalView." +
                             "TryShowContextHint itself. Registering hands the panel to each peer's own idle " +
                             "frame; pushing it drops a modal on a player mid-action for something that " +
                             "happened on someone else's screen. Queue it and let the native pump pop it.";

            // ── (d) EXECUTED: exactly once each, no echo, and a new battle starts clean ──
            foreach (var line in Dedupe(mirror, sendSide, recvSide, mod)) yield return line;

            // ── (e) the surface is actually routed ──
            bool routed = mod.GetTypes()
                             .SelectMany(t => t.GetMethods(All).Cast<MethodBase>()
                                               .Concat(t.GetConstructors(All)))
                             .Any(m => m.MetadataToken != inbound.MetadataToken &&
                                       Program.Callees(m, mod).Any(c => c.MetadataToken == inbound.MetadataToken &&
                                                                        c.Module == inbound.Module));
            if (!routed)
                yield return "L130 inbound-unrouted: nothing in this mod calls " + mirror.Name + "." +
                             inbound.Name + ", so the surface is never added to SurfaceRouter's tactical " +
                             "chain. An unclaimed surface id is DROPPED silently by design (forward-compat), " +
                             "so every peer would send correctly and every peer would receive nothing.";
        }

        /// <summary>This repo's one inbound contract, shared by every synced family:
        /// <c>(NetworkEngine, ulong senderPeerId, byte surfaceId, byte[] payload) -&gt; bool consumed</c>.</summary>
        private static bool IsRouterHook(MethodInfo m)
        {
            if (m.ReturnType != typeof(bool)) return false;
            var p = m.GetParameters();
            return p.Length == 4 && p[1].ParameterType == typeof(ulong) &&
                   p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte[]);
        }

        /// <summary>Every method REACHED from one seam, walking only the mirror type's own helpers (see the
        /// CEILING note). Terminates: the node set is bounded by that one type's method list.</summary>
        private static List<MethodBase> Closure(MethodBase root, Type mirror, Assembly mod)
        {
            var seen = new List<MethodBase> { root };
            var queue = new Queue<MethodBase>();
            queue.Enqueue(root);
            while (queue.Count > 0)
                foreach (var callee in Program.Callees(queue.Dequeue(), mod))
                    if (Owns(mirror, callee.DeclaringType) && !seen.Contains(callee))
                    {
                        seen.Add(callee);
                        queue.Enqueue(callee);
                    }
            return seen;
        }

        /// <summary>The mirror type or any type nested in it (the Harmony patch class, an iterator, a
        /// closure) — a lambda's display class is where half the real IL ends up.</summary>
        private static bool Owns(Type mirror, Type t)
        {
            for (; t != null; t = t.DeclaringType)
                if (t == mirror) return true;
            return false;
        }

        private static bool ReachesMod(List<MethodBase> closure, Assembly mod, string type, string method)
            => closure.Any(m => Program.Callees(m, mod)
                                       .Any(c => c.Name == method && c.DeclaringType != null &&
                                                 c.DeclaringType.Name == type));

        private static bool ReachesGame(List<MethodBase> closure, Assembly game, Type type, string method)
            => closure.Any(m => Program.Callees(m, game)
                                       .Any(c => c.Name == method && c.DeclaringType == type));

        /// <summary>ARM (d), RUN rather than read. The gate is the mirror's static <c>(string) -&gt; bool</c>
        /// and the per-battle reset its static parameterless <c>void</c>; both are derived from their shapes
        /// so the law does not depend on either name.</summary>
        private static IEnumerable<string> Dedupe(Type mirror, List<MethodBase> sendSide,
                                                 List<MethodBase> recvSide, Assembly mod)
        {
            var gate = mirror.GetMethods(All).FirstOrDefault(
                m => m.IsStatic && m.ReturnType == typeof(bool) && m.GetParameters().Length == 1 &&
                     m.GetParameters()[0].ParameterType == typeof(string));
            var reset = mirror.GetMethods(All).FirstOrDefault(
                m => m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 0);
            if (gate == null || reset == null)
            {
                yield return "L130 no-dedupe-gate: " + mirror.Name + " exposes no static (string) -> bool gate " +
                             "plus parameterless reset. Without ONE such choke point the same hint is shown " +
                             "twice on a peer that saw it locally AND received it, and the host's relay echoes " +
                             "back to the peer that sent it — the two failure modes a scattered per-call-site " +
                             "flag always eventually reintroduces.";
                yield break;
            }

            // The probe below proves the GATE counts correctly; these two prove both seams actually go
            // through it. Either half alone is the trap this repo already fell into twice — a gate nobody
            // consults, or a call site nobody checked the behaviour of.
            if (!ReachesMod(sendSide, mod, mirror.Name, gate.Name))
                yield return "L130 shown-twice: the capture on EventTypeTriggered never consults " +
                             mirror.Name + "." + gate.Name + ", so this peer re-announces every hint on every " +
                             "trigger — the queue is walked whole each time, and one Umbra becomes one " +
                             "message per hint per event for the rest of the battle.";
            if (!ReachesMod(recvSide, mod, mirror.Name, gate.Name))
                yield return "L130 echo-loop: the inbound seam never consults " + mirror.Name + "." +
                             gate.Name + ". A peer that does not mark what it received will re-broadcast it " +
                             "from its own trigger capture, the host relays it again, and a peer that saw the " +
                             "boss itself is handed the same panel once per other peer.";

            bool first = false, again = false, other = false, afterReset = false, offTheWire = false;
            string threw = null;
            try
            {
                reset.Invoke(null, null);
                first = (bool)gate.Invoke(null, new object[] { "L130_Probe_Hint" });
                again = (bool)gate.Invoke(null, new object[] { "L130_Probe_Hint" });
                other = (bool)gate.Invoke(null, new object[] { "L130_Probe_Other" });
                // "arrived off the wire" is the SAME gate: taking a name marks it, which is what stops the
                // receiving peer from broadcasting back what it was just told.
                offTheWire = (bool)gate.Invoke(null, new object[] { "L130_Probe_Other" });
                reset.Invoke(null, null);
                afterReset = (bool)gate.Invoke(null, new object[] { "L130_Probe_Hint" });
            }
            catch (Exception ex) { threw = ex.GetBaseException().Message; }
            finally { try { reset.Invoke(null, null); } catch { } }

            if (threw != null)
            {
                yield return "L130 dedupe-gate-threw: invoking " + mirror.Name + "." + gate.Name + " failed (" +
                             threw + "). The gate must be PURE — no engine, no Unity, no logging — precisely " +
                             "so this arm can execute it instead of reading its IL and hoping.";
                yield break;
            }

            if (!first || !other)
                yield return "L130 hint-never-sent: " + gate.Name + " refused a hint name it had never seen. " +
                             "The gate is what admits a sighting to the wire, so a false here means NO peer is " +
                             "ever told about ANY hint.";
            if (again)
                yield return "L130 shown-twice: " + gate.Name + " admitted the same hint name twice. The same " +
                             "boss panel is then queued once by this peer's own sighting and again by the " +
                             "mirrored copy, so a player who saw the Umbra himself dismisses the same panel " +
                             "two or three times, once per other peer.";
            if (offTheWire)
                yield return "L130 echo-loop: " + gate.Name + " admitted a name that had already passed " +
                             "through it. A peer that re-announces what it was told bounces the hint back to " +
                             "the host, which relays it again — the surface never settles.";
            if (!afterReset)
                yield return "L130 not-reset-per-battle: after the reset, " + gate.Name + " still refuses a " +
                             "name from the previous battle. The next mission's first sighting of that same " +
                             "boss is then announced to nobody, and the mission after that too — the mirror " +
                             "goes quiet a little more with every battle in a session.";
        }
    }
}
