using System.Collections.Generic;
using Multiplayer.Transport;

namespace Multiplayer.Util
{
    /// <summary>One transport attempt in a client-join cascade: which transport + the address/port
    /// handed to <c>NetworkEngine.JoinGame</c> (STUN takes "ip:port"; Direct takes ip + port; Steam
    /// takes the SteamID64 string).</summary>
    public readonly struct JoinAttempt
    {
        public TransportType Transport { get; }
        public string Address { get; }
        public int Port { get; }

        public JoinAttempt(TransportType transport, string address, int port)
        { Transport = transport; Address = address; Port = port; }
    }

    /// <summary>How the join was INITIATED. Not a detail of the target — the same unified code can
    /// arrive both ways — but the thing that decides which transports the code is allowed to try, so
    /// it is an explicit parameter rather than a flag read out of some global. An enum and not a bool
    /// on purpose: <c>Build(target, alive, true)</c> at a call site says nothing about which way the
    /// "true" points, and getting that backwards is silent (the plan still connects, just over the
    /// wrong technology for the user who asked).</summary>
    public enum JoinOrigin
    {
        /// <summary>The user pasted a code/address into the lobby's join box.</summary>
        PastedCode,
        /// <summary>A Steam invite was accepted (overlay / friends list / cold-start "+connect_lobby").</summary>
        SteamInvite
    }

    /// <summary>
    /// Pure decision: from a classified <see cref="JoinTarget"/> (+ whether local Steam is alive and
    /// HOW the join was started), produce the ORDERED list of transports the client should try.
    /// Legacy single-format codes yield a single attempt of their own transport; a unified code
    /// (steam id and/or endpoint) is pinned by its ORIGIN — see the Unified branch. No networking —
    /// just the ordered plan the UI executes attempt-by-attempt.
    /// </summary>
    public static class JoinPlan
    {
        public static List<JoinAttempt> Build(JoinTarget target, bool steamAlive, JoinOrigin origin)
        {
            var plan = new List<JoinAttempt>();
            switch (target.Kind)
            {
                case JoinKind.DirectIp:
                case JoinKind.DirectHost:
                    plan.Add(new JoinAttempt(TransportType.DirectIP, target.Ip, target.Port));
                    break;
                case JoinKind.StunCode:
                    // A SHORT CODE IS AN ENDPOINT, SO IT GETS BOTH LEGS TO THAT ENDPOINT — Direct TCP
                    // first, then the punch — exactly like the Unified branch below, and for the same
                    // reason: STUN's "reliable" send is a duplicated UDP datagram with no sequencing,
                    // ACK or retransmit (StunTransport.Send), and the save transfer's 32 KB chunks
                    // IP-fragment on top of it, so one lost fragment fails the whole transfer with no
                    // recovery. Recorded 2026-08-07 over ZeroTier: STUN won the race to a host that was
                    // directly reachable the whole time and the client's save arrived 131072 of 169071
                    // bytes, 2 chunks missing. This arm carried the punch ALONE until 2026-08-10 — not a
                    // fallback ORDERING problem like the Unified branch's, but no TCP leg at all — so
                    // every pasted 10-symbol ConnectCode landed on best-effort UDP even on a LAN, and it
                    // is now the only plan a code-joiner gets (the lobby publishes a ConnectCode and
                    // nothing else, MultiplayerUI.GetSessionInviteCode). Same technology with and without the
                    // punch; this is not a cross-technology cascade (L155).
                    plan.Add(new JoinAttempt(TransportType.DirectIP, target.Ip, target.Port));
                    plan.Add(new JoinAttempt(TransportType.StunUDP, target.Ip + ":" + target.Port, 0));
                    break;
                case JoinKind.SteamId:
                    plan.Add(new JoinAttempt(TransportType.SteamP2P, target.SteamId.ToString(), 0));
                    break;
                case JoinKind.Unified:
                    // ONE ENTRY POINT, ONE TECHNOLOGY. Each way into a session is pinned to the transport
                    // it actually names: the lobby's "Invite via Steam" button → Steam P2P; a pasted
                    // invite CODE (the GOG/Epic route — no Steam to invite through) → Direct TCP to that
                    // endpoint, then the STUN hole-punch to the SAME endpoint, which is not a second
                    // technology but the same address WITH the punch, for a host that is not directly
                    // reachable (the ordering note below the branch says why TCP goes first); and a DIRECTLY
                    // REACHABLE host → Direct TCP. Directly reachable is the broad case, not the LAN one:
                    // LAN, virtual LAN (Hamachi/ZeroTier and the like), a public white IP, a forwarding
                    // domain, a port-forwarded or UPnP-mapped host — JoinKind.DirectIp and
                    // JoinKind.DirectHost above both land there. So a pasted code gets NO Steam attempt.
                    // The old cascade tried Steam first for everyone, and "Steam as a fallback" is
                    // illusory value: a GOG/Epic player has no Steam to succeed on, so the leg can only
                    // ever spend that player's connect timeout before the STUN attempt they needed all
                    // along even starts — and for a player who HAS Steam it hijacks the code they were
                    // handed, which is the report this came from: "copy code" in the lobby, and the
                    // session came up over Steam anyway. It is self-consistent from the other side too:
                    // a GOG/Epic HOST has no Steam lobby, so its code carries no steam id at all.
                    // The Steam id stays IN the code and stays used — by the SteamInvite arm below,
                    // whose order is deliberately unchanged, because there Steam is what the user pressed.
                    if (origin == JoinOrigin.SteamInvite && target.SteamId != 0 && steamAlive)
                        plan.Add(new JoinAttempt(TransportType.SteamP2P, target.SteamId.ToString(), 0));
                    // DIRECT TCP BEFORE THE PUNCH, over the same endpoint. Both legs are "that address";
                    // the only difference is that Direct is length-prefixed TCP and STUN is raw UDP whose
                    // "reliable" send is a duplicated datagram with no sequencing, ACK or retransmit
                    // (StunTransport.Send). Every reliable message in the session rides that — and the save
                    // transfer's 32 KB chunks IP-fragment on top of it, so one lost fragment fails the whole
                    // transfer with no recovery (SaveTransferCoordinator.ChunkSize, and its own start-up
                    // warning says as much). On loopback that never bites. It bit on the first real path:
                    // 2026-08-07 over ZeroTier, STUN won the race to a host that was directly reachable the
                    // whole time, and the client's save arrived 131072/169071 bytes with 2 chunks missing.
                    // So order these by what the link can CARRY, not by which connects first: TCP when the
                    // endpoint is reachable — LAN, virtual LAN, white IP, port-forwarded — and the punch as
                    // the fallback for the NAT'd host that genuinely needs it, which is the only case that
                    // now pays DirectTransport.ConnectTimeoutMs first.
                    if (!string.IsNullOrEmpty(target.Ip))
                    {
                        plan.Add(new JoinAttempt(TransportType.DirectIP, target.Ip, target.Port));
                        plan.Add(new JoinAttempt(TransportType.StunUDP, target.Ip + ":" + target.Port, 0));
                    }
                    break;
            }
            return plan;
        }
    }
}
