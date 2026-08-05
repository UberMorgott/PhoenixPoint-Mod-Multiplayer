using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Transport;

namespace RailCheck
{
    /// <summary>
    /// L84 — NOBODY IS KICKED, AND NOBODY WAITS FOR ANYBODY.
    ///
    /// THE MANDATE (developer, 2026-08-05, first-class alongside reactivity and universal-first): "Say 50
    /// players. Everyone joins, presses ready, it loads, everyone plays. What goes on under the hood must
    /// concern nobody. NOBODY may be kicked or disconnected. Maximally stable, reactive for everyone, no lag,
    /// no ping problems — some players WILL have bad internet or high ping. The game must not be a fight to
    /// be able to play."
    ///
    /// WHY THIS LAW EXISTS AND L91 DOES NOT COVER IT. L91 is the same principle one namespace over, and it
    /// says so in its own comment: it sweeps <c>Multiplayer.Network.Sync</c> and <c>Multiplayer.Tactical</c>
    /// and DELIBERATELY excludes <c>Multiplayer.Network</c>, "because peer bookkeeping is its whole job".
    /// That exclusion is exactly where all three quorums lived — the lobby's ready gate, the save-transfer
    /// LOADED barrier and its straggler kick — so the rule was enforced everywhere except the place that
    /// broke it. Bookkeeping is indeed the lobby's job; DECIDING who gets to play from that bookkeeping is
    /// not. This law draws the line there, on the shapes rather than on the feature.
    ///
    /// WHAT WAS ACTUALLY THERE, so the arms are not abstract:
    ///   • <c>_readyClients.Count >= _clients.Count</c> gated the start — one AFK or parity-blocked player
    ///     held fifty shut, and the bigger the roster the more certain that is.
    ///   • <c>BarrierReleased(hostLoaded, loadedClientCount, expectedClientCount)</c> made everyone sit
    ///     behind a loading screen for the slowest download, up to three minutes.
    ///   • ...and then <c>ConnectionRejected + DisconnectPeer</c> for whoever had not made it, which is the
    ///     mandate's forbidden sentence written out in code.
    ///   • Six paths in total could take a player out of the session (heartbeat silence, straggler timeout,
    ///     send-queue overflow, socket write stall, N Steam send failures, two JOIN refusals). All six are
    ///     now ONE rule — <c>SessionManager.PausePeer</c> — and a peer keeps its row, slot, permissions and
    ///     guid binding while it is away.
    ///
    /// THE ONE KICK THAT SURVIVES is a peer that LEFT: <c>SessionManager.HandleLeave</c>, on the ClientLeave
    /// packet. Arm (a) is that sentence made mechanical — the caller SET of <c>ITransport.DisconnectPeer</c>
    /// must be exactly that one method. It is a set equality on purpose, not a "does it still get called":
    /// the failure this law guards against is a NEW caller appearing, which any weaker check would miss.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • add <c>Transport.DisconnectPeer(id)</c> anywhere outside HandleLeave → <c>kick-outside-a-leave</c>
    ///   • put <c>DropPeer</c> back in the send-queue overflow branch → <c>backpressure-removes-the-peer</c>
    ///   • restore the ready fact to <c>LobbyController.UpdateLobby/CanStart</c> → <c>ready-quorum-back</c>
    ///   • restore the client counts to <c>SaveTransferMath.BarrierReleased</c> → <c>loaded-quorum-back</c>
    ///   • swap <c>PausePeer</c> back to <c>RemoveClient</c> in the disconnect funnel → <c>drop-removes-the-peer</c>
    /// </summary>
    internal static class L84_NoPeerRemoval
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The ONE method allowed to sever a peer's transport link: the ClientLeave handler.</summary>
        private const string VoluntaryLeave = "SessionManager.HandleLeave";

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(SessionManager).Assembly;
            var lobbyTypes = mod.GetTypes()
                                .Where(t => t.Namespace == "Multiplayer.Network")
                                .ToList();

            // ─── POSITIVE CONTROL — a sweep that finds nothing proves nothing ───
            if (lobbyTypes.Count == 0)
            {
                yield return "L84 lobby-namespace-gone: no types resolve in Multiplayer.Network, so every arm " +
                             "below passes vacuously. The namespace L91 excludes is the one this law covers — " +
                             "if it moved, re-point this law at it rather than letting it read green";
                yield break;
            }

            // ─── (a) THE ONLY KICK IS A LEAVE ───
            var kick = typeof(ITransport).GetMethod("DisconnectPeer", All);
            if (kick == null)
                yield return "L84 premise-changed: ITransport.DisconnectPeer no longer exists, so this law can " +
                             "no longer tell a kick from a leave";
            else
            {
                // The transports IMPLEMENT the mechanism; they are not deciding anything. Everything else is.
                var callers = mod.GetTypes()
                                 .Where(t => t.Namespace != "Multiplayer.Transport")
                                 .SelectMany(SafeMethods)
                                 .Where(m => CallsMethod(m, kick))
                                 .Select(m => m.DeclaringType.Name + "." + m.Name)
                                 .Distinct(StringComparer.Ordinal)
                                 .OrderBy(n => n, StringComparer.Ordinal)
                                 .ToList();

                foreach (var caller in callers.Where(c => c != VoluntaryLeave))
                    yield return "L84 kick-outside-a-leave: " + caller + " calls ITransport.DisconnectPeer. " +
                                 "The mandate allows exactly one — " + VoluntaryLeave + ", on the ClientLeave " +
                                 "packet, i.e. a player who actually left. Every other severance was collateral " +
                                 "for something the peer could not control (heartbeat silence on a bad link, a " +
                                 "download that took longer than someone's timeout, a JOIN that landed at an " +
                                 "inconvenient moment) and is now SessionManager.PausePeer, which keeps the row, " +
                                 "the slot, the permissions and the guid binding so the peer resumes its own seat";

                if (!callers.Contains(VoluntaryLeave, StringComparer.Ordinal))
                    yield return "L84 leave-no-longer-disconnects: nothing calls ITransport.DisconnectPeer any " +
                                 "more, including " + VoluntaryLeave + ". A peer that genuinely LEFT must still " +
                                 "be severed, or the host keeps writing to a dead id forever and this arm has " +
                                 "stopped being able to tell a kick from a leave at all";
            }

            // ─── (b) BACKPRESSURE DEGRADES THE PEER, IT NEVER REMOVES IT ───
            var direct = mod.GetType("Multiplayer.Transport.DirectTransport");
            var drop = direct?.GetMethod("DropPeer", All);
            var enqueue = direct?.GetMethod("EnqueueBuiltFrame", All);
            if (direct == null || drop == null || enqueue == null)
                yield return "L84 premise-changed: DirectTransport.DropPeer / EnqueueBuiltFrame no longer both " +
                             "exist, so this law can no longer prove a full send queue does not remove a player";
            else if (CallsMethod(enqueue, drop))
                yield return "L84 backpressure-removes-the-peer: DirectTransport's enqueue path calls DropPeer. " +
                             "A full queue means the peer is behind — high ping, a bad link, a stalled read — " +
                             "which is the exact condition the mandate says must not cost anybody the session. " +
                             "Discard the OLDEST queued frames instead: stale is what the rail is built to " +
                             "survive (idempotent Apply, seq'd delivery, resync on gap)";

            // ─── (c) NO READY QUORUM — EXECUTED against the real gate ───
            var update = typeof(LobbyController).GetMethod("UpdateLobby", All);
            if (update == null)
                yield return "L84 premise-changed: LobbyController.UpdateLobby is gone — this law can no longer " +
                             "prove the start gate takes no ready vote";
            else if (update.GetParameters().Any(p => p.ParameterType == typeof(bool) &&
                                                     p.Name.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0))
                yield return "L84 ready-quorum-back: LobbyController.UpdateLobby takes a ready fact again. " +
                             "Whatever it is called, a start gate that reads whether OTHER peers are ready is a " +
                             "quorum, and a quorum is a hostage: one player who is AFK, on a slow machine or " +
                             "parity-blocked holds fifty shut. The host starts on its OWN readiness";

            var lobby = new LobbyController();
            lobby.BeginHost();
            lobby.UpdateLobby(connectedClientCount: 49, saveChosen: true);
            if (!lobby.CanStart)
                yield return "L84 ready-quorum-back: with 49 peers connected and a save chosen — and NOT ONE of " +
                             "them having readied — CanStart is false. Something still makes the host's start " +
                             "depend on what the other 49 did, which is the join wall this law exists to keep shut";

            // ─── (d) NO LOADED QUORUM — EXECUTED against the real predicate ───
            var released = typeof(SaveTransferMath).GetMethod("BarrierReleased", All);
            if (released == null)
                yield return "L84 premise-changed: SaveTransferMath.BarrierReleased is gone — this law can no " +
                             "longer prove the load barrier waits on nobody";
            else
            {
                var ps = released.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(bool))
                    yield return "L84 loaded-quorum-back: SaveTransferMath.BarrierReleased takes " + ps.Length +
                                 " parameter(s) (" + string.Join(", ", ps.Select(p => Pretty(p.ParameterType))) +
                                 "). It may read the HOST's own readiness and nothing else. Counting how many " +
                                 "clients have acked against how many are expected is what made everybody sit " +
                                 "behind a loading screen for the slowest download — and then get kicked for it";
                else if (!SaveTransferMath.BarrierReleased(true))
                    yield return "L84 loaded-quorum-back: BarrierReleased(hostLoaded: true) is false, so the host " +
                                 "is still waiting on something other than itself";
            }

            // ─── (e) AN INVOLUNTARY LOSS PAUSES THE PEER, IT DOES NOT REMOVE IT ───
            var funnel = typeof(NetworkEngine).GetMethod("OnPeerDisconnected", All);
            var pause = typeof(SessionManager).GetMethod("PausePeer", All);
            if (funnel == null || pause == null)
                yield return "L84 premise-changed: NetworkEngine.OnPeerDisconnected / SessionManager.PausePeer no " +
                             "longer both exist. That funnel is where a dead socket, a stalled write and a failed " +
                             "send channel all arrive, and pausing THERE is what covers all three at once";
            else if (!CallsMethod(funnel, pause))
                yield return "L84 drop-removes-the-peer: the transport-disconnect funnel does not call PausePeer. " +
                             "None of the things that reach it means the player left — they mean the network did " +
                             "something. Removing the roster row throws away the slot, the permissions and the " +
                             "guid binding, so the peer comes back as a stranger who has to fight its way in";
        }

        // ── IL probe. Flat token scan, same deliberate shortcut L94 documents: the methods under test are
        //    short and the failure direction of an unaligned hit is a token ResolveMethod rejects. ──
        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { return false; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;   // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        private static IEnumerable<MethodBase> SafeMethods(Type t)
        {
            try
            {
                return t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                        .Concat(t.GetConstructors(All | BindingFlags.DeclaredOnly).Cast<MethodBase>());
            }
            catch { return Enumerable.Empty<MethodBase>(); }
        }

        private static string Pretty(Type t) =>
            t.IsGenericType
                ? t.Name.Substring(0, t.Name.IndexOf('`')) + "<" +
                  string.Join(", ", t.GetGenericArguments().Select(Pretty)) + ">"
                : t.Name;
    }
}
