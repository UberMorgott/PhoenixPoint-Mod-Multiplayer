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

    /// <summary>
    /// Pure decision: from a classified <see cref="JoinTarget"/> (+ whether local Steam is alive),
    /// produce the ORDERED list of transports the client should try. A unified code (steam id and/or
    /// endpoint) cascades Steam → STUN hole-punch → Direct TCP; legacy single-format codes yield a
    /// single attempt. No networking — just the ordered plan the UI executes attempt-by-attempt.
    /// </summary>
    public static class JoinPlan
    {
        public static List<JoinAttempt> Build(JoinTarget target, bool steamAlive)
        {
            var plan = new List<JoinAttempt>();
            switch (target.Kind)
            {
                case JoinKind.DirectIp:
                case JoinKind.DirectHost:
                    plan.Add(new JoinAttempt(TransportType.DirectIP, target.Ip, target.Port));
                    break;
                case JoinKind.StunCode:
                    plan.Add(new JoinAttempt(TransportType.StunUDP, target.Ip + ":" + target.Port, 0));
                    break;
                case JoinKind.SteamId:
                    plan.Add(new JoinAttempt(TransportType.SteamP2P, target.SteamId.ToString(), 0));
                    break;
                case JoinKind.Unified:
                    // Steam first (fastest + NAT-free) — only when the code carries a steam id AND we
                    // actually have Steam. Then the public endpoint over STUN hole-punch, then a plain
                    // Direct TCP to the SAME endpoint (works when the host UPnP-forwarded / port-forwarded).
                    if (target.SteamId != 0 && steamAlive)
                        plan.Add(new JoinAttempt(TransportType.SteamP2P, target.SteamId.ToString(), 0));
                    if (!string.IsNullOrEmpty(target.Ip))
                    {
                        plan.Add(new JoinAttempt(TransportType.StunUDP, target.Ip + ":" + target.Port, 0));
                        plan.Add(new JoinAttempt(TransportType.DirectIP, target.Ip, target.Port));
                    }
                    break;
            }
            return plan;
        }
    }
}
