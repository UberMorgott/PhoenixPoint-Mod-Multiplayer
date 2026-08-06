using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Transport;

namespace RailCheck
{
    /// <summary>
    /// L136 — A PEER THE SESSION KNOWS IS A PEER THE TRANSPORT WILL BROADCAST TO.
    ///
    /// THE OUTCOME. Every peer with a roster row must be in the set the transport fans a Broadcast out to.
    /// Unicast reach and broadcast reach are two different sets in every transport, and when they drift
    /// apart NOTHING looks wrong: the peer answers, its packets arrive, every unicast reply lands — and the
    /// broadcast-only messages silently miss it.
    ///
    /// WHAT IT COST (open beta, 2026-08-06). A real remote player could not join over Steam. The host saw
    /// him join and later announced he had left; he sat on "Connecting…" for ~75 s and dropped. No
    /// exception, no failed deserialize, no rejection, nothing in either Player.log.
    ///   • <c>SteamTransport._connectedPeers</c> was written in exactly TWO places — the client's
    ///     <c>Connect</c> and <c>OnSessionRequest</c>. Nothing added a peer discovered only from an INBOUND
    ///     PACKET.
    ///   • Steam raises <c>OnP2PSessionRequest</c> only when NO P2P session with that SteamId exists yet,
    ///     and that session is PROCESS-global — it outlives our transport. The host had already opened one
    ///     on an earlier attempt ("P2P session to …195 failed (P2PSessionError=4) — dropping peer") and
    ///     re-created its transport five times. So on every RETRY — which is exactly what a beta user does —
    ///     the callback never fired again and the peer was never registered. Proof: the unconditional
    ///     "invite lobby joinable →" line (NetworkEngine.OnPeerConnected → SteamInvite.SetLobbyJoinable) is
    ///     present at 20:52:28 and 20:53:28 and ABSENT at all three real attempts.
    ///   • <c>Broadcast</c> iterates <c>_connectedPeers</c>; <c>Send</c>/<c>SendPacket</c> do not. So the
    ///     host's JOIN handling worked perfectly — roster row built, <c>ConnectionAccepted</c> unicast and
    ///     received ("Connection accepted by host") — and then <c>BroadcastPeerList → FlushPeerList →
    ///     BroadcastToAll</c> skipped the joiner. PEER_LIST is the ONE signal that ends the client's
    ///     "Connecting…" box (MultiplayerUI.Update reads <c>GetLobbyRoster().Count > 0</c>), so he waited
    ///     forever and left on his heartbeat.
    ///
    /// WHY NO EXISTING LAW CAUGHT IT. Nothing related the SESSION roster to the TRANSPORT's peer set. L84
    /// polices who may be REMOVED from a session, L91 who may WAIT on whom, L120 that one departure keeps
    /// one identity. All three stayed green: nobody was kicked, nobody waited on a quorum, and the peer's
    /// identity never changed. The peer was simply invisible to one of the two send paths.
    ///
    /// THE INVARIANT THE FIX INSTALLS, stated at the transport layer rather than for this one message:
    /// ANY PEER WE HAVE EVER RECEIVED A PACKET FROM IS A KNOWN PEER. DirectTransport already upheld it via
    /// accept(); StunTransport upholds it by discarding packets from endpoints not in its table; Steam's
    /// shared per-process read queue is the only one that can deliver bytes from a peer we never
    /// registered, so <c>SteamTransport.Update</c>'s drain loop now registers the sender and raises
    /// <c>OnPeerConnected</c> before delivering the packet.
    ///
    /// ARMS. (a)-(c) EXECUTE the real shipped <c>SteamTransport</c> and the real <c>SessionManager</c>
    /// through the exact beta scenario; (d) is executed on the real <c>CompositeTransport</c>; (e) and (f)
    /// are structural. The transport's peer set is runtime state, so the law drives it rather than reading
    /// it: <c>ITransport.CanReach</c> is the predicate under test, and arm (e) is what stops it from
    /// becoming a comforting lie — it pins <c>CanReach</c> and <c>Broadcast</c> to the SAME field in every
    /// transport that owns a peer set.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • delete the registration from SteamTransport.Update's drain loop → <c>packet-peer-never-onboarded</c>
    ///     + <c>roster-peer-unreachable-by-broadcast</c> (arm a)
    ///   • drop the <c>_connectedPeers.Add</c> gate and raise unconditionally → <c>peer-onboarded-twice</c> (arm b)
    ///   • register on the client too (<c>if (true)</c> instead of <c>IsHost || steamId == _dialedHost</c>)
    ///     → <c>client-adopts-a-stranger</c> (arm c)
    ///   • make <c>CompositeTransport.CanReach</c> return true for any mapped id without asking the child
    ///     → <c>composite-launders-the-child</c> (arm d)
    ///   • point <c>SteamTransport.CanReach</c> at any set other than the one Broadcast iterates
    ///     → <c>reach-answered-from-another-set</c> (arm e)
    ///   • remove the CanReach call from <c>FlushPeerList</c> → <c>fan-out-is-silent-again</c> (arm f)
    /// </summary>
    internal static class L136_RosterPeerIsBroadcastable
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(SteamTransport).Assembly;

            // ─── POSITIVE CONTROL — a law that cannot reach its subject must say so, not pass ───
            var canReach = typeof(ITransport).GetMethod("CanReach", new[] { typeof(ulong) });
            var incoming = typeof(SteamTransport).GetField("_incomingQueue", All);
            if (canReach == null || incoming == null)
            {
                yield return "L136 premise-changed: ITransport.CanReach" + (canReach == null ? " (gone)" : "") +
                             " and/or SteamTransport._incomingQueue" + (incoming == null ? " (gone)" : "") +
                             " no longer exist, so every arm below is vacuous. The relation this law asserts " +
                             "— the session roster is a SUBSET of the transport's broadcast set — has no other " +
                             "expression: unicast reach and broadcast reach are separate sets in every " +
                             "transport, and their drift is invisible from outside";
                yield break;
            }

            foreach (var v in HostAdoptsAPacketPeer(incoming)) yield return v;
            foreach (var v in ClientAdoptsNobody(incoming)) yield return v;
            foreach (var v in CompositeAsksTheChild()) yield return v;
            foreach (var v in ReachIsTheBroadcastSet(mod)) yield return v;
            foreach (var v in FanOutIsNotSilent(mod)) yield return v;
        }

        /// <summary>ARMS (a) + (b), EXECUTED on the real transport + the real SessionManager.
        ///
        /// The exact beta scenario: a host whose Steam session-request callback never fires (the process-global
        /// session already existed from the failed attempt before), so the ONLY evidence the peer exists is the
        /// packet it sent. Drive the real drain loop with that packet and demand both halves of the invariant —
        /// the session must learn the peer, AND the transport must be willing to broadcast to it. Either half
        /// alone is the bug: a roster row nobody can broadcast to is precisely what shipped.</summary>
        private static IEnumerable<string> HostAdoptsAPacketPeer(FieldInfo incoming)
        {
            const ulong Joiner = 76561198036100195UL;   // the real SteamId from the beta report

            var transport = new SteamTransport();
            transport.Host();
            var session = new SessionManager(null);

            // Mirror NetworkEngine.OnPeerConnected's HOST branch — the one consumer that turns a transport
            // peer into a roster row. Counted, because "exactly once" is arm (b).
            int raises = 0;
            transport.OnPeerConnected += (id, ep) => { raises++; session.AddClient(id, ep); };

            Enqueue(incoming, transport, Joiner);
            transport.Update();

            if (!session.Clients.ContainsKey(Joiner))
                yield return "L136 packet-peer-never-onboarded: a HOST received a packet from " + Joiner +
                             " and never onboarded it. Steam's OnP2PSessionRequest is edge-triggered on " +
                             "session CREATION and the session is process-global, so on any retry with the " +
                             "same SteamId it never fires again — the inbound packet is then the only proof " +
                             "the peer exists, and ignoring it is how a real player joined a lobby that never " +
                             "knew he was there";

            foreach (var id in session.Clients.Keys)
                if (!transport.CanReach(id))
                    yield return "L136 roster-peer-unreachable-by-broadcast: peer " + id + " has a session " +
                                 "roster row but the transport will NOT broadcast to it. Unicast still " +
                                 "works — Send/SendPacket never consult the peer set — so the JOIN is " +
                                 "accepted and the acceptance arrives, and then every broadcast-only message " +
                                 "silently misses this peer. PEER_LIST is broadcast-only and is the ONE " +
                                 "signal that ends the client's \"Connecting…\" box, so this peer waits until " +
                                 "its heartbeat gives up, with no error on either side";

            // ARM (b) — IDEMPOTENCY. The registration sits in a per-packet loop, so it must be gated by the
            // set's own Add. A second raise would re-run the whole onboarding: a duplicate roster row, a
            // second SteamLobbySetJoinable evaluation and another PEER_LIST broadcast per packet.
            Enqueue(incoming, transport, Joiner);
            Enqueue(incoming, transport, Joiner);
            transport.Update();
            if (raises != 1)
                yield return "L136 peer-onboarded-twice: " + raises + " OnPeerConnected raises for ONE peer " +
                             "across three of its packets. The registration lives in the per-packet drain " +
                             "loop, so only the peer set's own Add may gate it — otherwise every packet " +
                             "re-onboards the sender and the roster, the lobby-advertising toggle and the " +
                             "PEER_LIST fan-out all fire per packet";
            if (session.Clients.Count != 1)
                yield return "L136 peer-onboarded-twice: the session ended with " + session.Clients.Count +
                             " roster rows for a single peer";
        }

        /// <summary>ARM (c), EXECUTED — THE ASYMMETRY. A HOST may learn a peer from its packets; a CLIENT may
        /// not. Every peer in a session knows this client's SteamId (PEER_LIST broadcasts it), and
        /// NetworkEngine's client OnPeerConnected re-points the host link (SetHostPeer + JOIN), so adopting an
        /// arbitrary sender would hand a stranger the host seat. The predicate is deliberately the SAME one
        /// OnSessionRequest already used for inbound sessions: only the host we DIALED.</summary>
        private static IEnumerable<string> ClientAdoptsNobody(FieldInfo incoming)
        {
            const ulong DialedHost = 76561198000000001UL;
            const ulong Stranger = 76561198000000999UL;

            var client = new SteamTransport();
            client.Connect(DialedHost.ToString(), 0);   // client leg: no Host(), so IsHost stays false

            int raises = 0;
            client.OnPeerConnected += (id, ep) => raises++;   // subscribed AFTER the dial's own raise

            Enqueue(incoming, client, Stranger);
            client.Update();

            if (client.CanReach(Stranger) || raises != 0)
                yield return "L136 client-adopts-a-stranger: a CLIENT registered " + Stranger + " as a peer " +
                             "because it sent a packet, although the host it dialed is " + DialedHost + ". " +
                             "Client-side that is a host-link hijack: NetworkEngine's client OnPeerConnected " +
                             "does SetHostPeer + JOIN, and every peer learns this client's SteamId from the " +
                             "PEER_LIST broadcast. The packet may still be DELIVERED — the guard is on " +
                             "registration, exactly as OnSessionRequest guards inbound sessions";

            if (!client.CanReach(DialedHost))
                yield return "L136 client-loses-its-host: the client can no longer broadcast to the host it " +
                             "dialed (" + DialedHost + "), so the guard above was bought by breaking the one " +
                             "link a client actually has";
        }

        /// <summary>ARM (d), EXECUTED on the real CompositeTransport. Composite.Broadcast delegates to the
        /// CHILDREN, so the composite's own outward map proves nothing about reach — the child's peer set is
        /// the authority. This is also why HandleChildPacket deliberately does NOT raise OnPeerConnected for
        /// the id it lazily mints: that would announce a peer the child still cannot broadcast to, which is
        /// this bug rather than a fix for it.</summary>
        private static IEnumerable<string> CompositeAsksTheChild()
        {
            var child = new ReachableSet();
            var composite = new CompositeTransport(new ITransport[] { child });

            ulong announced = 0;
            composite.OnPeerConnected += (id, ep) => announced = id;
            ulong delivered = 0;
            composite.OnPacketReceived += (id, data) => delivered = id;

            const ulong Announced = 7UL, Silent = 9UL;
            child.Reachable.Add(Announced);
            child.RaiseConnected(Announced);
            if (!composite.CanReach(announced))
                yield return "L136 composite-loses-a-peer: the composite says it cannot broadcast to a peer " +
                             "its own child announced and holds in its peer set — every broadcast-only " +
                             "message would be reported as missing this peer";

            // A packet from a peer the child never announced and cannot broadcast to. The composite maps it
            // so the payload is not dropped, and must NOT report reach it does not have.
            child.RaisePacket(Silent);
            if (composite.CanReach(delivered))
                yield return "L136 composite-launders-the-child: the composite claims it can broadcast to " +
                             "peer " + delivered + " although the owning child's peer set does not contain " +
                             "it. Composite.Broadcast delegates to the children, so a mapping minted by " +
                             "HandleChildPacket is a delivery id, never evidence of reach — laundering it " +
                             "would hide exactly the unicast-works/broadcast-misses split this law exists for";
        }

        /// <summary>ARM (e), STRUCTURAL — CanReach MUST ANSWER FROM THE SET Broadcast ITERATES. Everything
        /// above rests on CanReach telling the truth, and a predicate that answers from its own private
        /// bookkeeping would keep every executed arm green while the real fan-out missed the peer. Swept over
        /// every ITransport in the mod rather than a hand-listed set, so a new transport is covered the day it
        /// is added. Lock objects are excluded (both methods take the same lock, which would make any pairing
        /// trivially true). CompositeTransport is exempt BY CONSTRUCTION — it owns no peer set, it delegates,
        /// and arm (d) executes that delegation instead.</summary>
        private static IEnumerable<string> ReachIsTheBroadcastSet(Assembly mod)
        {
            var impls = mod.GetTypes()
                           .Where(t => t.IsClass && !t.IsAbstract && typeof(ITransport).IsAssignableFrom(t)
                                       && t != typeof(CompositeTransport))
                           .OrderBy(t => t.Name, StringComparer.Ordinal)
                           .ToList();

            if (impls.Count == 0)
            {
                yield return "L136 no-transport-owns-a-peer-set: no concrete ITransport with its own peer set " +
                             "resolves in the mod assembly, so this arm passes vacuously";
                yield break;
            }

            foreach (var t in impls)
            {
                var broadcast = t.GetMethod("Broadcast", All, null, new[] { typeof(byte[]), typeof(bool) }, null);
                var reach = t.GetMethod("CanReach", All, null, new[] { typeof(ulong) }, null);
                if (broadcast == null || reach == null)
                {
                    yield return "L136 premise-changed: " + t.Name + " no longer declares both Broadcast and " +
                                 "CanReach, so its reach cannot be tied to its fan-out";
                    continue;
                }

                var shared = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                              .Where(f => f.FieldType != typeof(object))   // not the lock both methods take
                              .Where(f => Program.ReadsField(broadcast, f) && Program.ReadsField(reach, f))
                              .Select(f => f.Name)
                              .ToList();

                if (shared.Count == 0)
                    yield return "L136 reach-answered-from-another-set: " + t.Name + ".CanReach and " +
                                 t.Name + ".Broadcast read no field in common, so CanReach is answering " +
                                 "about something other than the set the fan-out actually walks. That makes " +
                                 "it a comforting lie: every roster/reach check downstream stays green while " +
                                 "the broadcast keeps missing the peer, which is the failure this whole law " +
                                 "was written after";
            }
        }

        /// <summary>ARM (f), STRUCTURAL — THE FAN-OUT MUST NOT BE SILENT AGAIN. This mod's dominant bug class
        /// is a swallow with no log line, and this one cost a beta cycle precisely because the host printed a
        /// perfectly normal join. PEER_LIST is the message a joiner cannot do without, so the flush that ships
        /// it has to state who it actually reached.</summary>
        private static IEnumerable<string> FanOutIsNotSilent(Assembly mod)
        {
            var flush = typeof(SessionManager).GetMethod("FlushPeerList", All);
            if (flush == null)
            {
                yield return "L136 premise-changed: SessionManager.FlushPeerList is gone — the single place " +
                             "the roster is fanned out, and the only place a peer that gets none can be named";
                yield break;
            }

            if (!Program.Callees(flush, mod).Any(c => c.Name == "CanReach"))
                yield return "L136 fan-out-is-silent-again: FlushPeerList ships the roster without asking " +
                             "whether the transport can actually reach the peers it has rows for. A peer that " +
                             "receives no roster then produces no line on either side — the host logs an " +
                             "ordinary join, the joiner logs nothing at all, and the only visible symptom is " +
                             "a player stuck on \"Connecting…\" until a 20 s heartbeat times him out";
        }

        /// <summary>Put a packet into the transport's inbound queue exactly as ReadP2PPacket would, without a
        /// Steam runtime. The drain loop under test is the REAL one — Update() is called on the shipped
        /// object; only the source of the bytes is substituted.</summary>
        private static void Enqueue(FieldInfo incoming, SteamTransport transport, ulong from)
        {
            var queue = incoming.GetValue(transport);
            queue.GetType().GetMethod("Enqueue")
                 .Invoke(queue, new object[] { ValueTuple.Create(from, new byte[] { 1 }) });
        }

#pragma warning disable 0067
        /// <summary>A child transport with a REAL peer set, so the composite's delegation can be observed.
        /// (L120's RawPeerSource answers CanReach unconditionally, which is the one thing arm (d) must not
        /// assume.)</summary>
        private sealed class ReachableSet : ITransport
        {
            internal readonly HashSet<ulong> Reachable = new HashSet<ulong>();

            public TransportType TransportType => TransportType.SteamP2P;
            public ConnectionState State => ConnectionState.Connected;
            public bool IsHost => true;
            public string LocalEndpoint => "railcheck";
            public System.Net.IPEndPoint PublicEndPoint => null;

            public event Action<ConnectionState> OnStateChanged;
            public event Action<ulong, byte[]> OnPacketReceived;
            public event Action<ulong, string> OnPeerConnected;
            public event Action<ulong, string> OnPeerDisconnected;

            public void Initialize() { }
            public void Shutdown() { }
            public void Host(int port = 0) { }
            public void Connect(string address, int port) { }
            public void Disconnect() { }
            public void Send(ulong peerId, byte[] data, bool reliable = true) { }
            public void Broadcast(byte[] data, bool reliable = true) { }
            public bool CanReach(ulong peerId) => Reachable.Contains(peerId);
            public bool DisconnectPeer(ulong peerId) => true;
            public void Update() { }

            internal void RaiseConnected(ulong raw) => OnPeerConnected?.Invoke(raw, "railcheck");
            internal void RaisePacket(ulong raw) => OnPacketReceived?.Invoke(raw, new byte[] { 0 });
        }
#pragma warning restore 0067
    }
}
