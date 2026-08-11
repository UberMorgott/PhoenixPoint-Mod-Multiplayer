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
    ///
    /// A CHECK SYMBOL RIDES ALONG, exactly as <see cref="InviteCode"/> and <c>UnifiedCode</c> already do.
    /// Without it every 10-symbol string over the alphabet decoded to SOME IPv4:port, so a single mistyped
    /// character silently became a different, arbitrary endpoint and the player got a connect timeout with
    /// nothing naming the typo. Codes are session-ephemeral (minted per host session, pasted immediately),
    /// and two builds that disagree about the format cannot play together anyway — the parity/version gate
    /// refuses the join — so nothing durable is invalidated by the extra symbol.
    /// </summary>
    public static class ConnectCode
    {
        private const string Alphabet = Crockford.Alphabet;

        /// <summary>10 data symbols (48 bits of endpoint, 2 bits of padding) + 1 check symbol.</summary>
        public const int DataSymbols = 10;
        public const int TotalSymbols = DataSymbols + 1;

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

            var sym = new int[TotalSymbols];
            int pos = 0, buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sym[pos++] = (buffer >> bits) & 0x1F;
                }
            }
            if (bits > 0)
                sym[pos++] = (buffer << (5 - bits)) & 0x1F; // pad final partial symbol
            sym[DataSymbols] = Crockford.Checksum(sym, DataSymbols);

            var sb = new StringBuilder(TotalSymbols);
            for (int i = 0; i < TotalSymbols; i++) sb.Append(Alphabet[sym[i]]);

            // Group as 4-3-4 → "7F3B-21K-9M4" style (11 symbols → groups of 4,3,4).
            var raw = sb.ToString();
            return raw.Substring(0, 4) + "-" + raw.Substring(4, 3) + "-" + raw.Substring(7, 4);
        }

        public static IPEndPoint Decode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            // The SHARED normalizer, aliases included. This used to trim/strip/upper-case by hand and skip
            // the Crockford I/L→1, O→0 mapping every other codec applied, so typing the letter O for a zero
            // was "invalid code" here and a working join through InviteCode — one typo, two verdicts.
            // Widening only: every string this accepted before normalizes to the same 11 symbols it did.
            var clean = Crockford.Normalize(code);
            if (clean.Length != TotalSymbols) return null;

            var sym = new int[TotalSymbols];
            for (int i = 0; i < TotalSymbols; i++)
            {
                var s = Alphabet.IndexOf(clean[i]);
                if (s < 0) return null; // illegal symbol
                sym[i] = s;
            }
            if (sym[DataSymbols] != Crockford.Checksum(sym, DataSymbols)) return null; // a typo, not an endpoint

            int buffer = 0, bits = 0;
            var outBytes = new byte[6];
            int outPos = 0;
            for (int i = 0; i < DataSymbols; i++)
            {
                var idx = sym[i];
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
