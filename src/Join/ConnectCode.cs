using System;
using System.Net;
using System.Text;

namespace Multiplayer.Util
{
    /// <summary>
    /// Deterministic codec turning an IPv4 endpoint (4 octets + 16-bit port = 6 bytes) into a
    /// short, human-typeable code (Crockford base32, dash-grouped) and back. Shared by the host
    /// rail display and the client smart-Join parser so both speak the SAME format.
    /// Crockford alphabet excludes I, L, O, U to avoid 0/O and 1/I/L confusion; decode is
    /// case-insensitive and tolerant of dashes / surrounding whitespace.
    /// </summary>
    public static class ConnectCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32 (32 chars)

        public static string Encode(IPEndPoint pub)
        {
            if (pub == null) return null;
            var addr = pub.Address.GetAddressBytes();
            if (addr.Length != 4) return null; // IPv4 only

            // 6 bytes = 48 bits → 10 base32 symbols (48/5 = 9.6, pad to 10).
            var data = new byte[6];
            Array.Copy(addr, 0, data, 0, 4);
            data[4] = (byte)((pub.Port >> 8) & 0xFF);
            data[5] = (byte)(pub.Port & 0xFF);

            var sb = new StringBuilder(10);
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sb.Append(Alphabet[(buffer >> bits) & 0x1F]);
                }
            }
            if (bits > 0)
                sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]); // pad final partial symbol

            // Group as 4-2-4 → "7F3B-21-K9" style (10 symbols → groups of 4,2,4).
            var raw = sb.ToString();
            return raw.Substring(0, 4) + "-" + raw.Substring(4, 2) + "-" + raw.Substring(6, 4);
        }

        public static IPEndPoint Decode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var clean = code.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (clean.Length != 10) return null;

            int buffer = 0, bits = 0;
            var outBytes = new byte[6];
            int outPos = 0;
            foreach (var c in clean)
            {
                var idx = Alphabet.IndexOf(c);
                if (idx < 0) return null; // illegal symbol
                buffer = (buffer << 5) | idx;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    if (outPos >= 6) return null;
                    outBytes[outPos++] = (byte)((buffer >> bits) & 0xFF);
                }
            }
            if (outPos != 6) return null;

            var addr = new byte[4];
            Array.Copy(outBytes, 0, addr, 0, 4);
            var port = (outBytes[4] << 8) | outBytes[5];
            return new IPEndPoint(new IPAddress(addr), port);
        }
    }
}
