using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Transport;

namespace RailCheck
{
    /// <summary>
    /// L152 — A CHAT LINE DELIVERED TO A PEER APPEARS EXACTLY ONCE.
    ///
    /// THE REPORT (2026-08-06): "at the moment he connected I wrote '1, 2, 3' in the chat, and instead of
    /// one message he received TWO identical messages from me — but it happened only ONCE; however many
    /// messages we sent afterwards it never duplicated again."
    ///
    /// THE WINDOW, and why it is exactly one message wide. The host keeps an authoritative backlog and
    /// replays it to a joiner in <c>OnSessionRequest</c> (<c>ReplayChatHistoryTo</c>) — deliberately BEFORE
    /// the "joined" notice, so that notice is never doubled. But the joiner is already in the TRANSPORT's
    /// broadcast set well before that: <c>SteamTransport.Update</c> registers any peer it has received a
    /// packet from (5e6ab9d, and the invariant is right — a peer that sent us bytes exists), and the JOIN it
    /// sent is one of those packets. So from the joiner's first packet until its JOIN has been handled,
    /// <c>BroadcastChat</c> reaches it LIVE and the same line is then replayed to it from the backlog. Two
    /// deliveries of one line, and the receiver could not tell: same sender, same text, no key. Afterwards
    /// the window is closed forever, which is why it never happened again.
    ///
    /// THE FIX IS THE MISSING GENERIC, NOT A TIME WINDOW: a chat line now HAS an identity. The host stamps
    /// a session-monotonic <c>Seq</c> in <c>BroadcastChat</c> — the one place a line becomes a line — and
    /// the BACKLOG carries it, so a replayed copy is the same line as its live copy and a receiver drops the
    /// second one. This also covers the other replay shapes for free (a repeated JOIN, a retransmit).
    ///
    /// THE ARMS, all EXECUTED against real SessionManagers and the real serializer:
    ///   (a) <c>same-line-delivered-twice</c> — the exact reported sequence: a host stamps and fans out a
    ///       line, then replays its backlog to a joiner that already got it live. The joiner must render it
    ///       ONCE.
    ///   (b) <c>dedup-eats-real-messages</c> — the other direction, because a dedup that swallows traffic is
    ///       a worse bug than the one it fixes: two DIFFERENT lines must both render, and the same TEXT sent
    ///       twice on purpose must render twice.
    ///   (c) <c>identity-does-not-survive-the-wire</c> — the stamp must round-trip through
    ///       <c>Serialize/DeserializeChat</c>. Without it every line arrives unstamped and (a) is decided by
    ///       an accident.
    ///
    /// Falsify: drop <c>Seq = ++_chatSeq</c> in <c>BroadcastChat</c>, drop <c>Seq = chat.Seq</c> in
    /// <c>ReplayChatHistoryTo</c>, drop <c>bw.Write(chat.Seq)</c> in <c>SerializeChat</c>, or drop the
    /// <c>_seenChatSeq</c> guard in <c>HandleChat</c> → (a) goes red. Dedup on TEXT instead of identity →
    /// (b) goes red.
    /// </summary>
    internal static class L152_ChatDeliveredExactlyOnce
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private const ulong Joiner = 76561198036100195UL;

        internal static IEnumerable<string> Check()
        {
            var sm = typeof(SessionManager);
            var replay = sm.GetMethod("ReplayChatHistoryTo", All);
            var handle = sm.GetMethod("HandleChat", All);
            var send = sm.GetMethod("SendChat", All);
            var seen = sm.GetField("_seenChatSeq", All);
            var seqProp = typeof(ChatMessageData).GetProperty("Seq", All);

            if (replay == null || handle == null || send == null || seen == null || seqProp == null)
            {
                yield return "L152 premise-changed: SessionManager.{SendChat,ReplayChatHistoryTo,HandleChat," +
                             "_seenChatSeq} or ChatMessageData.Seq no longer resolves. Chat identity has moved " +
                             "and this law is asserting something about a shape it no longer has.";
                yield break;
            }

            foreach (var v in IdentityRoundTrips(seqProp)) yield return v;
            foreach (var v in TheJoinWindow(replay, handle, send)) yield return v;
        }

        /// <summary>ARM (c) — the stamp must survive the wire. Everything else is decided by it.</summary>
        private static IEnumerable<string> IdentityRoundTrips(PropertyInfo seqProp)
        {
            var line = new ChatMessageData
            {
                SenderSteamId = Joiner, SenderNick = "Morgott", Text = "1, 2, 3", IsSystem = false, Seq = 7u
            };
            var back = MessageSerializer.DeserializeChat(MessageSerializer.SerializeChat(line));
            if (back.Seq != 7u)
                yield return "L152 identity-does-not-survive-the-wire: a line stamped 7 came back as " + back.Seq +
                             ". The stamp IS the dedup key — unstamped lines are rendered unconditionally (a " +
                             "receiver may not guess), so a wire that drops it silently restores the duplicate.";
            if (back.Text != line.Text || back.SenderNick != line.SenderNick || back.IsSystem != line.IsSystem)
                yield return "L152 identity-does-not-survive-the-wire: appending the stamp broke the rest of the " +
                             "chat frame (text/nick/kind no longer round-trip). It is appended LAST for exactly " +
                             "this reason.";
        }

        /// <summary>ARMS (a) and (b), EXECUTED — the reported sequence, end to end, on real SessionManagers
        /// and the real serializer. A capture transport stands in for the link so both the LIVE broadcast and
        /// the BACKLOG replay are taken as the bytes they really are.</summary>
        private static IEnumerable<string> TheJoinWindow(MethodInfo replay, MethodInfo handle, MethodInfo send)
        {
            var wire = new CaptureTransport();
            SessionManager host = null, client = null;
            string buildFailure = null;
            try
            {
                host = BuildSession(isHost: true, transport: wire);
                client = BuildSession(isHost: false, transport: null);
            }
            catch (Exception e) { buildFailure = (e.InnerException ?? e).Message; }

            if (host == null || client == null)
            {
                yield return "L152 premise-changed: could not build host/client SessionManagers to execute the " +
                             "join window against (" + buildFailure + "). This law has no " +
                             "other executed evidence.";
                yield break;
            }

            int rendered = 0;
            host.HostNickname = "Morgott";
            client.OnChatReceived += (nick, text, isSystem) => rendered++;

            // The host types the line. It is fanned out LIVE — and the joiner, already in the transport's
            // broadcast set from its own JOIN packet, is one of the recipients.
            wire.Broadcasts.Clear();
            send.Invoke(host, new object[] { "1, 2, 3" });
            if (wire.Broadcasts.Count != 1)
            {
                yield return "L152 premise-changed: a host SendChat produced " + wire.Broadcasts.Count +
                             " broadcast(s); the fan-out is supposed to be exactly one packet.";
                yield break;
            }
            Deliver(handle, client, wire.Broadcasts[0]);

            // …and then its JOIN is handled, and the backlog — which already contains that line — is
            // replayed to it. This is the whole bug, in order.
            wire.Unicasts.Clear();
            replay.Invoke(host, new object[] { Joiner });
            foreach (var packet in wire.Unicasts) Deliver(handle, client, packet);

            if (rendered != 1)
                yield return "L152 same-line-delivered-twice: the joiner rendered ONE line " + rendered +
                             " time(s). A line broadcast in the window between a peer's first packet (which is " +
                             "what puts it in the transport's broadcast set) and its JOIN being handled is " +
                             "delivered live AND replayed from the backlog. Without an identity on the line the " +
                             "receiver cannot tell the two copies apart — the reported '1, 2, 3' twice, once, at " +
                             "the moment he connected, and never again.";

            // ARM (b) — the dedup may not eat traffic. A different line, and the SAME TEXT sent again on
            // purpose, must both reach the screen.
            var before = rendered;
            wire.Broadcasts.Clear();
            send.Invoke(host, new object[] { "4, 5, 6" });
            send.Invoke(host, new object[] { "1, 2, 3" });
            foreach (var packet in wire.Broadcasts) Deliver(handle, client, packet);
            if (rendered - before != 2)
                yield return "L152 dedup-eats-real-messages: two further lines (one new, one repeating earlier " +
                             "TEXT on purpose) rendered " + (rendered - before) + " time(s) instead of 2. The key " +
                             "is the line's identity, never its content — a chat that silently swallows a repeat " +
                             "is a worse failure than the duplicate this law exists to stop.";
        }

        private static void Deliver(MethodInfo handle, SessionManager client, byte[] frame)
        {
            var msg = NetworkMessage.Deserialize(frame);
            msg.SenderSteamId = 1UL;   // the host's peer id, as the receive path stamps it
            handle.Invoke(client, new object[] { msg });
        }

        private static SessionManager BuildSession(bool isHost, ITransport transport)
        {
            var engine = Activator.CreateInstance(typeof(NetworkEngine), true);
            typeof(NetworkEngine).GetProperty("IsHost", All).GetSetMethod(true).Invoke(engine, new object[] { isHost });
            if (transport != null)
                typeof(NetworkEngine).GetProperty("Transport", All).GetSetMethod(true).Invoke(engine, new object[] { transport });
            var session = new SessionManager((NetworkEngine)engine);
            typeof(NetworkEngine).GetProperty("Session", All).GetSetMethod(true).Invoke(engine, new object[] { session });
            return session;
        }

        /// <summary>The link, reduced to the only thing this law needs from it: what went out by broadcast
        /// and what went out by unicast. Every other member is inert.</summary>
        private sealed class CaptureTransport : ITransport
        {
            public readonly List<byte[]> Broadcasts = new List<byte[]>();
            public readonly List<byte[]> Unicasts = new List<byte[]>();

            public Multiplayer.Transport.TransportType TransportType => Multiplayer.Transport.TransportType.DirectIP;
            public ConnectionState State => ConnectionState.Connected;
            public bool IsHost => true;
            public string LocalEndpoint => "law-152";
            public IPEndPoint PublicEndPoint => null;

            public event Action<ConnectionState> OnStateChanged;
            public event Action<ulong, byte[]> OnPacketReceived;
            public event Action<ulong, string> OnPeerConnected;
            public event Action<ulong, string> OnPeerDisconnected;

            public void Initialize() { }
            public void Shutdown() { }
            public void Host(int port = 0) { }
            public void Connect(string address, int port) { }
            public void Disconnect() { }
            public void Send(ulong peerId, byte[] data, bool reliable = true) => Unicasts.Add(data);
            public void Broadcast(byte[] data, bool reliable = true) => Broadcasts.Add(data);
            public bool CanReach(ulong peerId) => true;
            public bool DisconnectPeer(ulong peerId) => true;
            public void Update() { }

            // Never raised — declared only because the interface does. Touched once so the compiler does
            // not warn them away, which would change the type's shape for no reason.
            private void Unused()
            {
                OnStateChanged?.Invoke(State);
                OnPacketReceived?.Invoke(0, null);
                OnPeerConnected?.Invoke(0, null);
                OnPeerDisconnected?.Invoke(0, null);
            }
        }
    }
}
