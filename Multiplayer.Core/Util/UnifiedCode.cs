using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Multiplayer.Util
{
    /// <summary>
    /// ONE steam-free invite code (v2). Carries an optional 32-bit Steam account id AND/OR an
    /// optional public IPv4 endpoint, so a single pasted code lets the client cascade Steam → STUN →
    /// Direct. Crockford base32 (I,L,O,U excluded), dash-grouped for display, 1 checksum symbol.
    ///
    /// Layout (big-endian): flags(1B) [+ accountId(4B) if bit0] [+ ip(4B)+port(2B) if bit1] + check.
    /// The three variants have UNIQUE symbol lengths so they never collide with the older formats:
    ///   steam-only   = 8 data + 1 check =  9 symbols
    ///   endpoint-only= 12 data + 1 check = 13 symbols
    ///   both         = 18 data + 1 check = 19 symbols
    /// (InviteCode is 8, ConnectCode is 10, a bare SteamID64 is 17 digits — all distinct.)
    ///
    /// Pure BCL, no Steam/Unity types. Rebuild the full SteamID64 via <see cref="InviteCode.ToSteamId64"/>.
    /// </summary>
    public static class UnifiedCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32 (no I,L,O,U)
        private const byte FlagSteam = 0x01;
        private const byte FlagEndpoint = 0x02;

        /// <summary>
        /// Build the code from whatever the host has. <paramref name="steamAccountId"/> = null off Steam;
        /// <paramref name="endpoint"/> = null when no public endpoint yet (UPnP/STUN still discovering).
        /// Returns null when NEITHER is available (caller shows a "discovering…" placeholder).
        /// </summary>
        public static string Encode(uint? steamAccountId, IPEndPoint endpoint)
        {
            bool hasSteam = steamAccountId.HasValue;
            bool hasEndpoint = endpoint != null
                && endpoint.Address.AddressFamily == AddressFamily.InterNetwork;
            if (!hasSteam && !hasEndpoint) return null;

            byte flags = 0;
            if (hasSteam) flags |= FlagSteam;
            if (hasEndpoint) flags |= FlagEndpoint;

            var data = new List<byte>(11) { flags };
            if (hasSteam)
            {
                uint id = steamAccountId.Value;
                data.Add((byte)(id >> 24)); data.Add((byte)(id >> 16));
                data.Add((byte)(id >> 8)); data.Add((byte)id);
            }
            if (hasEndpoint)
            {
                data.AddRange(endpoint.Address.GetAddressBytes()); // 4 bytes IPv4
                data.Add((byte)((endpoint.Port >> 8) & 0xFF));
                data.Add((byte)(endpoint.Port & 0xFF));
            }

            var symbols = Base32Encode(data.ToArray());
            var raw = symbols + Alphabet[Checksum(symbols)];
            return Group(raw);
        }

        /// <summary>
        /// Parse a candidate back to its fields. Returns false (all outs default) when the candidate is
        /// not a 9/13/19-symbol Crockford code, has an illegal symbol, fails the check symbol, sets an
        /// unknown flag bit, or the payload length disagrees with the flags. Never throws.
        /// </summary>
        public static bool TryDecode(string code, out uint steamAccountId, out bool hasSteam,
                                     out IPEndPoint endpoint, out bool hasEndpoint)
        {
            steamAccountId = 0; hasSteam = false; endpoint = null; hasEndpoint = false;
            if (string.IsNullOrWhiteSpace(code)) return false;

            var clean = Normalize(code);
            if (clean.Length != 9 && clean.Length != 13 && clean.Length != 19) return false;
            foreach (var ch in clean) if (Alphabet.IndexOf(ch) < 0) return false; // illegal symbol

            var dataSymbols = clean.Substring(0, clean.Length - 1);
            if (Alphabet[Checksum(dataSymbols)] != clean[clean.Length - 1]) return false; // bad check

            var bytes = Base32Decode(dataSymbols);
            if (bytes.Length < 1) return false;

            byte flags = bytes[0];
            if ((flags & ~(FlagSteam | FlagEndpoint)) != 0) return false; // unknown/version bits set
            hasSteam = (flags & FlagSteam) != 0;
            hasEndpoint = (flags & FlagEndpoint) != 0;
            if (!hasSteam && !hasEndpoint) return false;

            int expected = 1 + (hasSteam ? 4 : 0) + (hasEndpoint ? 6 : 0);
            if (bytes.Length != expected) return false;

            int pos = 1;
            if (hasSteam)
            {
                steamAccountId = (uint)((bytes[pos] << 24) | (bytes[pos + 1] << 16)
                                      | (bytes[pos + 2] << 8) | bytes[pos + 3]);
                pos += 4;
            }
            if (hasEndpoint)
            {
                var addr = new byte[4];
                Array.Copy(bytes, pos, addr, 0, 4); pos += 4;
                int port = (bytes[pos] << 8) | bytes[pos + 1];
                endpoint = new IPEndPoint(new IPAddress(addr), port);
            }
            return true;
        }

        // ─── base32 (Crockford), same bit-packing as ConnectCode ─────────────

        private static string Base32Encode(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
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
                sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]); // pad final partial symbol with 0s
            return sb.ToString();
        }

        private static byte[] Base32Decode(string symbols)
        {
            var outBytes = new List<byte>(symbols.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (var c in symbols)
            {
                int idx = Alphabet.IndexOf(c);
                if (idx < 0) return Array.Empty<byte>();
                buffer = (buffer << 5) | idx;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    outBytes.Add((byte)((buffer >> bits) & 0xFF));
                }
            }
            return outBytes.ToArray();
        }

        // Weighted checksum over the data symbols (indices), mod 32 — same scheme as InviteCode:
        // odd weights are invertible mod 32, so every single-symbol substitution changes the check.
        private static int Checksum(string symbols)
        {
            int sum = 0;
            for (int i = 0; i < symbols.Length; i++)
                sum += Alphabet.IndexOf(symbols[i]) * (2 * i + 1);
            return sum & 0x1F;
        }

        // Crockford normalization: trim, drop dashes/spaces, upper-case, map I/L→1 and O→0.
        private static string Normalize(string code)
        {
            var sb = new StringBuilder(code.Length);
            foreach (var ch in code.Trim())
            {
                if (ch == '-' || ch == ' ') continue;
                var c = char.ToUpperInvariant(ch);
                if (c == 'I' || c == 'L') c = '1';
                else if (c == 'O') c = '0';
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Dash-group in 4s for readability (dashes are stripped on decode).
        private static string Group(string raw)
        {
            var sb = new StringBuilder(raw.Length + raw.Length / 4);
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(raw[i]);
            }
            return sb.ToString();
        }
    }
}
