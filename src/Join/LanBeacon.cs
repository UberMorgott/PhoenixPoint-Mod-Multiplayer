using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Multiplayer.Util
{
    /// <summary>
    /// LAN AUTO-DISCOVERY. The session browser had exactly one feed — <c>SteamProbe.Friends()</c> — so a
    /// host was visible only to Steam friends and every other player had to be TOLD an address out of band.
    /// This is the second feed: a host shouts its own endpoint on the local wire, a browser that is open
    /// listens, and the rows appear by themselves.
    ///
    /// THE BODY IS A <see cref="ConnectCode"/>, not a new format. That codec already is "an IPv4 endpoint,
    /// written down" (4 octets + port → 11 Crockford symbols WITH a check symbol), it is what the lobby
    /// already publishes, and a beacon carrying it decodes through the SAME <c>SmartJoinParser</c> →
    /// <c>JoinPlan</c> → Direct-TCP path a pasted address takes. Discovery therefore adds no join path and
    /// no second address format to keep in step — it only fills the box for the player.
    ///
    /// SUBNET-DIRECTED BROADCAST, NEVER 255.255.255.255. The limited broadcast is dropped by routed and
    /// bridged links (and by several virtual adapters that the whole point of this feature is to reach), so
    /// each local IPv4 is shouted at ITS OWN directed broadcast, computed from the address and its mask.
    ///
    /// TRUST BOUNDARY: every received packet is written by an unauthenticated stranger on the LAN. Nothing
    /// here acts on one — a valid packet only puts a ROW on a screen that the player still has to click.
    /// Malformed input is dropped silently, the table is capped, and the receive callback never throws.
    /// </summary>
    public static class LanBeacon
    {
        /// <summary>Discovery port. Deliberately NOT <c>SmartJoinParser.DefaultDirectPort</c> (14242): that
        /// one is the host's TCP listener, and a UDP bind beside it would be a second thing to explain in a
        /// firewall rule that is already the usual cause of "I see nobody".</summary>
        public const int Port = 14243;

        /// <summary>Wire tag. Version digit included so a future body change is a DIFFERENT tag rather than
        /// a mis-parse of the old one.</summary>
        private const string Magic = "PPMP1 ";

        private const int SendIntervalMs = 2000;
        private const int ExpireMs = 6000;      // three missed beacons
        private const int MaxEntries = 32;      // a cap, because the sender is a stranger
        private const int MaxPacketBytes = 64;
        private const int SilenceWarnMs = 5000;

        /// <summary>One discovered session, already in the shape the join field takes.</summary>
        public struct LanSession
        {
            /// <summary>"ip:port" — exactly what a player would have pasted by hand.</summary>
            public string Address;
        }

        private sealed class Entry
        {
            public string Address;
            public int SeenTick;
        }

        private static readonly Dictionary<string, Entry> _seen = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly object _lock = new object();
        private static int _version;

        private static UdpClient _sender;
        private static int _lastSendTick;

        private static UdpClient _listener;
        private static int _listenStartTick;
        private static bool _heardAnything;
        private static bool _warnedSilent;

        // ─── Payload codec (pure, and the only part with a law over it) ─────────────────────────────

        /// <summary>The bytes a host shouts, or null when the endpoint is not encodable.</summary>
        public static byte[] EncodePayload(IPEndPoint ep)
        {
            var code = ep == null ? null : ConnectCode.Encode(ep);
            return code == null ? null : Encoding.ASCII.GetBytes(Magic + code);
        }

        /// <summary>The endpoint a beacon names, or null for ANY packet that is not exactly one — wrong
        /// length, wrong tag, non-ASCII, a bad check symbol, or an address/port nobody can dial. Never
        /// throws: the caller is a socket callback holding a stranger's bytes.</summary>
        public static IPEndPoint ParsePayload(byte[] data, int length)
        {
            try
            {
                if (data == null) return null;
                if (length < Magic.Length + ConnectCode.TotalSymbols) return null;
                if (length > MaxPacketBytes || length > data.Length) return null;
                for (int i = 0; i < length; i++)
                    if (data[i] < 0x20 || data[i] > 0x7E) return null;   // ASCII printable only
                var text = Encoding.ASCII.GetString(data, 0, length);
                if (!text.StartsWith(Magic, StringComparison.Ordinal)) return null;

                var ep = ConnectCode.Decode(text.Substring(Magic.Length));
                if (ep == null || ep.Port <= 0 || ep.Port > 65535) return null;
                var b = ep.Address.GetAddressBytes();
                if (b.Length != 4) return null;
                if (b[0] == 0 || b[0] >= 224) return null;               // "this network", multicast, broadcast
                return ep;
            }
            catch
            {
                return null;   // a stranger's packet may not raise an exception into a UI frame
            }
        }

        /// <summary>The directed broadcast for an address on its own subnet — <c>ip | ~mask</c>.</summary>
        internal static IPAddress DirectedBroadcast(IPAddress ip, IPAddress mask)
        {
            if (ip == null) return null;
            var a = ip.GetAddressBytes();
            if (a.Length != 4) return null;
            // ponytail: no mask (an adapter that reports none) falls back to /24 rather than to the limited
            // broadcast. It reaches the same-/24 slice of a wider net, which is where a co-op peer sits;
            // 255.255.255.255 would be dropped by the very links this feature exists for.
            var m = mask != null && mask.GetAddressBytes().Length == 4
                ? mask.GetAddressBytes()
                : new byte[] { 255, 255, 255, 0 };
            var o = new byte[4];
            for (int i = 0; i < 4; i++) o[i] = (byte)(a[i] | (byte)~m[i]);
            // A degenerate mask (0.0.0.0) degrades into the LIMITED broadcast, which routed and bridged
            // links drop — the one address this must never send to. Skip the adapter instead.
            if (o[0] == 255 && o[1] == 255 && o[2] == 255 && o[3] == 255) return null;
            return new IPAddress(o);
        }

        // ─── Host side ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Called every frame with "am I hosting". Shouts every <see cref="SendIntervalMs"/> ms
        /// while true and disposes the socket the moment it goes false.</summary>
        public static void HostTick(bool hosting)
        {
            if (!hosting)
            {
                if (_sender != null) { try { _sender.Close(); } catch { } _sender = null; }
                return;
            }
            if (unchecked(Environment.TickCount - _lastSendTick) < SendIntervalMs) return;
            _lastSendTick = Environment.TickCount;

            try
            {
                if (_sender == null)
                {
                    _sender = new UdpClient(AddressFamily.InterNetwork);
                    _sender.EnableBroadcast = true;
                }
                foreach (var local in LanIpResolver.LocalIPv4Addresses())
                {
                    var payload = EncodePayload(new IPEndPoint(local.Ip, SmartJoinParser.DefaultDirectPort));
                    var target = DirectedBroadcast(local.Ip, local.Mask);
                    if (payload == null || target == null) continue;
                    try { _sender.Send(payload, payload.Length, new IPEndPoint(target, Port)); }
                    catch { /* one unreachable adapter must not silence the others */ }
                }
            }
            catch
            {
                // Socket creation refused (sandbox, exhausted handles). Discovery is an extra; the session
                // is still joinable by a pasted address, so this may never reach the frame it runs on.
            }
        }

        // ─── Client side ───────────────────────────────────────────────────────────────────────────

        /// <summary>Idempotent listener switch — call with true while the browser is open, false on close.</summary>
        public static void Listen(bool on)
        {
            if (on)
            {
                if (_listener != null) return;
                try
                {
                    // ReuseAddress BEFORE Bind, or a second instance on this box (the standard co-op test
                    // rig) fails to bind and sees nothing. UdpClient(AddressFamily) creates the socket
                    // WITHOUT binding it, which is what leaves room for the option.
                    var c = new UdpClient(AddressFamily.InterNetwork);
                    c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    c.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                    _listener = c;
                    _listenStartTick = Environment.TickCount;
                    _heardAnything = false;
                    _warnedSilent = false;
                    BeginReceive(c);
                }
                catch (Exception e)
                {
                    _listener = null;
                    MpLog.LogWarning("[Multiplayer][lan] discovery listener did not start (" + e.Message +
                                     "); LAN sessions will not appear by themselves — paste the host's address.");
                }
                return;
            }

            if (_listener == null) return;
            var dead = _listener;
            _listener = null;               // the callback sees this and stops re-arming
            try { dead.Close(); } catch { }
            lock (_lock) { if (_seen.Count > 0) { _seen.Clear(); _version++; } }
        }

        private static void BeginReceive(UdpClient c)
        {
            try { c.BeginReceive(OnReceive, c); }
            catch { /* closed under us — nothing to re-arm */ }
        }

        private static void OnReceive(IAsyncResult ar)
        {
            var c = ar.AsyncState as UdpClient;
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var data = c.EndReceive(ar, ref from);
                _heardAnything = true;

                var ep = ParsePayload(data, data == null ? 0 : data.Length);
                if (ep != null) Remember(ep);
            }
            catch
            {
                // Socket closed, or a datagram that failed to even arrive. Neither is worth a line, and
                // neither may escape: this runs on a thread-pool thread, where a throw kills the process.
            }
            if (!ReferenceEquals(c, _listener)) return;   // Listen(false) happened; do not re-arm
            BeginReceive(c);
        }

        private static void Remember(IPEndPoint ep)
        {
            var address = ep.Address + ":" + ep.Port;
            lock (_lock)
            {
                Entry e;
                if (_seen.TryGetValue(address, out e)) { e.SeenTick = Environment.TickCount; return; }
                if (_seen.Count >= MaxEntries) return;   // a stranger may not grow this table without bound
                _seen[address] = new Entry { Address = address, SeenTick = Environment.TickCount };
                _version++;
            }
        }

        /// <summary>Expire what has gone quiet and return the version of the visible set. A caller repaints
        /// when this changes — that is the whole reactivity contract of this file.</summary>
        public static int Poll()
        {
            lock (_lock)
            {
                List<string> gone = null;
                foreach (var kv in _seen)
                    if (unchecked(Environment.TickCount - kv.Value.SeenTick) > ExpireMs)
                        (gone ?? (gone = new List<string>())).Add(kv.Key);
                if (gone != null)
                {
                    foreach (var k in gone) _seen.Remove(k);
                    _version++;
                }
            }

            // THE FIREWALL SIGNAL. Nothing at all after five seconds with the browser open is almost always
            // Windows Firewall holding the UDP bind, and that state is otherwise indistinguishable from
            // "nobody is hosting" — which is what sends players hunting for the wrong problem.
            if (_listener != null && !_heardAnything && !_warnedSilent &&
                unchecked(Environment.TickCount - _listenStartTick) > SilenceWarnMs)
            {
                _warnedSilent = true;
                MpLog.Log("[Multiplayer][lan] no LAN beacon heard on UDP " + Port + " in 5s. If a host IS " +
                          "running on this network, allow Phoenix Point through Windows Firewall on the " +
                          "PRIVATE profile (both machines), or join by pasting the host's address.");
            }
            return _version;
        }

        /// <summary>The live rows, newest state. Never null.</summary>
        public static List<LanSession> Snapshot()
        {
            var list = new List<LanSession>();
            lock (_lock)
                foreach (var kv in _seen) list.Add(new LanSession { Address = kv.Value.Address });
            return list;
        }
    }
}
