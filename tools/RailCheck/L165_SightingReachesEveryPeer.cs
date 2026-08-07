using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L165 — ONE PEER'S SIGHTING REACHES EVERY PEER'S SCREEN, INCLUDING THE ONE THE HOST HAS ALREADY SEEN
    /// AND THE ONE THAT WAS NOT READY YET.
    ///
    /// THE REPORT (2026-08-06, owner): the elite-enemy detection notice — "elite spotted" plus its abilities,
    /// the panel on the left — does not appear on every peer. Reported as fixed several times before.
    ///
    /// AND A LAW WAS GREEN THROUGH IT, WHICH IS THE POINT. L130 executes <c>HintMirror.ShouldSend</c> and
    /// asserts it is true exactly once per name per battle. That is an assertion about the MECHANISM, and
    /// the mechanism was the bug: <c>ShouldSend</c> was the send gate, the display gate AND the relay gate
    /// at once, so proving it dedupes proved nothing about whether the panel arrives. This law asserts the
    /// OUTCOME the mechanism exists for.
    ///
    /// THE TWO ROOTS, both in <c>HintMirror.HandleInbound</c> and both silent by construction:
    ///   • THE HOST'S OWN EYES SILENCED THE THIRD PEER. The relay sat BELOW the choke
    ///     (<c>if (!ShouldSend(name)) return true;</c> then <c>if (IsHost) BroadcastToAll</c>). The host runs
    ///     the real sim, so it is normally the first peer to sight a boss and the first to mark the name; a
    ///     client that sighted it independently then handed the host a 0x8A the choke swallowed, and the
    ///     third peer — which had seen nothing — never got a copy. Measured: the 2026-08-06 client log holds
    ///     ONE <c>[MP][hint]</c> line for the whole session, across five battles, and not a single
    ///     "triggered on this peer — mirrored to every peer".
    ///   • A CLAIM THE PEER COULD NOT HONOUR WAS PERMANENT. <c>Show</c> consumed the name through the same
    ///     set and could then fail — no live <c>TacContextHelpManager</c> (still loading its battle), or no
    ///     def by that name (TFTV's per-mission gang/elite defs are minted by host-only turn-0 code and a
    ///     client's battle is a loaded save, which is what <c>TftvMissionHints.RebuildForThisClient</c>
    ///     exists to repair — and it runs LATER than a raise can arrive). The name stayed consumed, so that
    ///     peer's OWN later sighting of the same boss was dropped too, for the rest of the battle.
    ///
    /// THE ARMS, all EXECUTED over the real pure gate — no game, no session, no battle:
    ///   (a) <c>claim-is-permanent</c> — claim / re-claim / <c>Forget</c> / claim again. The release is what
    ///       turns a failed delivery into a retry instead of a life sentence.
    ///   (b) <c>relay-behind-the-choke</c> — inside <c>HandleInbound</c> the broadcast is reached BEFORE the
    ///       choke. Asserted as IL ORDER because that is exactly what was wrong: both calls were present,
    ///       and every arm of L130 passed.
    ///   (c) <c>failure-not-released</c> — <c>HandleInbound</c> calls <c>Forget</c> at all. Without it the
    ///       second root returns whole while (a) still passes, because (a) tests the primitive and this
    ///       tests that the primitive is wired to the failure.
    ///   (d) <c>choke-is-vacuous</c>, NON-VACUITY — the choke still dedupes (a name claimed twice in a row
    ///       is refused) and <c>Reset</c> still releases the battle. Fixing a swallowed relay by deleting
    ///       the dedupe would show every panel twice on every peer, which is the failure L130 was written
    ///       for; both laws have to hold at once.
    ///   (e) <c>relay-can-echo</c> — the ONLY caller that puts a name on the wire is <c>Capture</c>, which
    ///       is itself gated by the choke, and <c>HandleInbound</c> reaches <c>Send</c> from nowhere. That
    ///       is the structural reason an unconditional relay cannot loop, and it is asserted rather than
    ///       argued: a client that ever re-sent what it received would echo forever between two peers.
    ///
    /// Falsify (each verified to go RED, then restored): delete <c>Forget</c>'s body → (a); move the
    /// broadcast back under the choke → (b); drop the <c>if (!Show(name)) Forget(name)</c> wiring → (c);
    /// make <c>ShouldSend</c> always true, or empty <c>Reset</c> → (d); call <c>Send</c> from
    /// <c>HandleInbound</c> → (e).
    /// </summary>
    internal static class L165_SightingReachesEveryPeer
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var mirror = typeof(HintMirror);
            var shouldSend = mirror.GetMethod("ShouldSend", AllMembers);
            var forget = mirror.GetMethod("Forget", AllMembers);
            var reset = mirror.GetMethod("Reset", AllMembers);
            var inbound = mirror.GetMethod("HandleInbound", AllMembers);
            var send = mirror.GetMethod("Send", AllMembers);
            var capture = mirror.GetMethod("Capture", AllMembers);
            var broadcast = typeof(NetworkEngine).GetMethod("BroadcastToAll", AllMembers);

            if (shouldSend == null || forget == null || reset == null || inbound == null || send == null ||
                capture == null || broadcast == null)
            {
                yield return "L165 premise-changed: HintMirror.{ShouldSend,Forget,Reset,HandleInbound,Send," +
                             "Capture} / NetworkEngine.BroadcastToAll did not all resolve. The 0x8A hint seam " +
                             "has moved and every arm below would pass vacuously — the same way L130 stayed " +
                             "green through the breakage this law is written for.";
                yield break;
            }

            // ── (a) + (d) the gate, EXECUTED: claim, refuse, release, re-claim, and the battle reset ──
            const string name = "L165_ProbeHint";
            reset.Invoke(null, null);
            bool first = (bool)shouldSend.Invoke(null, new object[] { name });
            bool second = (bool)shouldSend.Invoke(null, new object[] { name });
            forget.Invoke(null, new object[] { name });
            bool afterForget = (bool)shouldSend.Invoke(null, new object[] { name });
            reset.Invoke(null, null);
            bool afterReset = (bool)shouldSend.Invoke(null, new object[] { name });
            reset.Invoke(null, null);   // leave no probe name behind for the live seam

            if (!first)
                yield return "L165 choke-is-vacuous: the FIRST claim of a fresh hint name was refused, so no " +
                             "hint can ever be shown. Every other arm of this law is about a name that was " +
                             "claimed once.";
            if (second)
                yield return "L165 choke-is-vacuous: the SECOND claim of the same name was granted. The dedupe " +
                             "is gone and every peer shows the same boss panel twice — the failure L130 was " +
                             "written for. An unconditional relay is only safe while this holds.";
            if (!afterForget)
                yield return "L165 claim-is-permanent: after Forget, the name could not be claimed again. A peer " +
                             "that took a mirrored hint off the wire and then could not show it — still loading " +
                             "its battle, or the TFTV def not rebuilt here yet — stays silenced for the rest of " +
                             "the mission, including for its OWN later sighting of the same enemy.";
            if (!afterReset)
                yield return "L165 choke-is-vacuous: after Reset the name is still claimed, so the NEXT battle " +
                             "cannot show the same boss panel again. Reset is the per-battle release " +
                             "(TacticalTurnSync.Reset drives it) and a name held across a teardown silences a " +
                             "whole mission with no line anywhere.";

            // ── (b) the relay is reached BEFORE the choke ───────────────────────────────────────────
            int relayAt = FirstCallOffset(inbound, broadcast);
            int chokeAt = FirstCallOffset(inbound, shouldSend);
            if (relayAt < 0)
                yield return "L165 relay-behind-the-choke: HandleInbound never calls " +
                             "NetworkEngine.BroadcastToAll. A client cannot address its fellow clients, so " +
                             "without the host's relay a sighting reaches exactly the peer that saw it and the " +
                             "host — never the third player.";
            else if (chokeAt < 0)
                yield return "L165 choke-is-vacuous: HandleInbound never calls ShouldSend, so an arriving hint " +
                             "is shown however many times it is delivered.";
            else if (relayAt > chokeAt)
                yield return "L165 relay-behind-the-choke: HandleInbound reaches ShouldSend (IL offset " +
                             chokeAt + ") before BroadcastToAll (offset " + relayAt + "). That ordering is the " +
                             "reported bug: the host normally sights the enemy FIRST — it runs the real sim — " +
                             "so it has already claimed the name, and a client's 0x8A for the same hint is then " +
                             "dropped at the choke and never fanned out to the peer that saw nothing.";

            // ── (c) the failure path is wired to the release ────────────────────────────────────────
            if (FirstCallOffset(inbound, forget) < 0)
                yield return "L165 failure-not-released: HandleInbound never calls Forget. The primitive exists " +
                             "and nothing uses it, so a delivery this peer could not show is still permanent — " +
                             "arm (a) passes and the panel is still missing, which is precisely how this seam " +
                             "shipped green last time.";

            // ── (e) the structural reason an unconditional relay cannot echo ────────────────────────
            if (FirstCallOffset(inbound, send) >= 0)
                yield return "L165 relay-can-echo: HandleInbound calls Send. The relay is unconditional now, so " +
                             "the ONLY thing that may put a name on the wire is Capture (this peer's own " +
                             "trigger, gated by the choke). A peer that re-sends what it received turns the " +
                             "relay into an echo between two peers with nothing to stop it.";
            if (FirstCallOffset(capture, shouldSend) < 0)
                yield return "L165 relay-can-echo: Capture does not call ShouldSend. The send side is what keeps " +
                             "the unconditional relay finite — a peer must put each name on the wire at most " +
                             "once per battle — and without it the host's fan-out and a client's re-trigger can " +
                             "feed each other.";
        }

        /// <summary>IL offset of the first call/callvirt to <paramref name="target"/>, or -1. Offsets are
        /// comparable within one body, which is all arm (b) needs.</summary>
        private static int FirstCallOffset(MethodBase caller, MethodBase target)
        {
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null || target == null) return -1;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;   // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return i;
            }
            return -1;
        }
    }
}
