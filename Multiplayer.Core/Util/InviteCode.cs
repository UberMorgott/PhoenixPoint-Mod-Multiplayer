using System.Text;

namespace Multiplayer.Util
{
    /// <summary>
    /// Short, human-typeable invite code for friend-free Steam-P2P co-op join. Encodes a 32-bit
    /// Steam account id (the low 32 bits of a SteamID64) as 7 Crockford base32 data symbols + 1
    /// checksum symbol, displayed grouped as XXXX-XXXX. Decode is case-insensitive and tolerant of
    /// dashes/spaces and the Crockford I/L→1, O→0 aliases, and rejects a wrong check symbol.
    ///
    /// Pure BCL — NO Steam/Facepunch types (Multiplayer.Core has no Facepunch dependency). The
    /// caller supplies the account id (host) and rebuilds the full SteamID64 from a decoded id
    /// (client) via <see cref="ToSteamId64"/>.
    ///
    /// SteamID64 = 0x0110000100000000 | accountId — the well-known 76561197960265728 base for the
    /// public universe / individual account-type / desktop instance. A real individual SteamID64's
    /// upper 32 bits are exactly 0x01100001, so OR-ing the base with the low-32 account id is an
    /// exact reconstruction.
    /// </summary>
    public static class InviteCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32 (no I,L,O,U)
        public const int DataSymbols = 7;   // 7*5 = 35 bits ≥ 32-bit accountId (top 3 bits always 0)
        public const int TotalSymbols = 8;  // + 1 checksum symbol
        public const ulong SteamId64Base = 0x0110000100000000UL;

        /// <summary>accountId → "XXXX-XXXX" Crockford invite code (7 data symbols + 1 check symbol).</summary>
        public static string Encode(uint accountId)
        {
            var sym = new int[TotalSymbols];
            for (int i = 0; i < DataSymbols; i++)
                sym[i] = (int)((accountId >> (5 * (DataSymbols - 1 - i))) & 0x1F);
            sym[DataSymbols] = Checksum(sym);

            var sb = new StringBuilder(TotalSymbols);
            for (int i = 0; i < TotalSymbols; i++) sb.Append(Alphabet[sym[i]]);
            var raw = sb.ToString();
            return raw.Substring(0, 4) + "-" + raw.Substring(4, 4); // XXXX-XXXX
        }

        /// <summary>
        /// Parse a candidate invite code back to the account id. Returns false (accountId=0) when the
        /// candidate is not exactly 8 Crockford symbols after normalization, contains an illegal
        /// symbol, or fails the check symbol. Never throws.
        /// </summary>
        public static bool TryDecode(string code, out uint accountId)
        {
            accountId = 0;
            if (string.IsNullOrWhiteSpace(code)) return false;
            var clean = Normalize(code);
            if (clean.Length != TotalSymbols) return false;

            var sym = new int[TotalSymbols];
            for (int i = 0; i < TotalSymbols; i++)
            {
                var idx = Alphabet.IndexOf(clean[i]);
                if (idx < 0) return false; // illegal symbol
                sym[i] = idx;
            }
            if (sym[DataSymbols] != Checksum(sym)) return false; // wrong check symbol

            ulong val = 0;
            for (int i = 0; i < DataSymbols; i++) val = (val << 5) | (uint)sym[i];
            if (val > uint.MaxValue) return false; // top 3 bits set → not a real 32-bit account id
            accountId = (uint)val;
            return true;
        }

        /// <summary>Reconstruct the full SteamID64 from a decoded account id.</summary>
        public static ulong ToSteamId64(uint accountId) => SteamId64Base | accountId;

        // Weighted checksum over the 7 data symbols, mod 32 (so the check symbol is itself an
        // in-alphabet Crockford char). Odd weights are all invertible mod 32, so EVERY single-symbol
        // substitution changes the checksum; the weighting also catches most adjacent transpositions.
        private static int Checksum(int[] sym)
        {
            int sum = 0;
            for (int i = 0; i < DataSymbols; i++) sum += sym[i] * (2 * i + 1);
            return sum & 0x1F; // mod 32
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
    }
}
