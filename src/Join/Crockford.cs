using System.Text;

namespace Multiplayer.Util
{
    /// <summary>
    /// The one Crockford base32 vocabulary this mod's join codes share: the alphabet, the typo-tolerant
    /// normalization, and the weighted check symbol. <see cref="InviteCode"/>, <see cref="ConnectCode"/>,
    /// <c>UnifiedCode</c> and <c>SmartJoinParser</c> all speak it; only the LAYOUT (how many data symbols
    /// carry what, and how they are grouped for display) belongs to each codec.
    ///
    /// It lives here because the copies had already drifted: <see cref="ConnectCode"/>.<c>Decode</c>
    /// trimmed, stripped dashes and upper-cased but skipped the I/L→1, O→0 aliasing every other codec did,
    /// so a player who typed the letter O for zero got "invalid code" from an 11-symbol STUN code and a
    /// working join from an 8-symbol invite code — the same typo, two answers.
    /// </summary>
    internal static class Crockford
    {
        /// <summary>Crockford base32 — I, L, O and U are absent, so 0/O and 1/I/L cannot be confused and
        /// no four-letter word can be spelled by accident.</summary>
        internal const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>Trim, drop dashes/spaces, upper-case, map I/L→1 and O→0. Does NOT validate: an illegal
        /// symbol survives normalization and is rejected by the caller's own alphabet lookup.</summary>
        internal static string Normalize(string code)
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

        /// <summary>Weighted checksum over the first <paramref name="count"/> data symbols, mod 32 — so the
        /// check symbol is itself an in-alphabet character. Odd weights are all invertible mod 32, so EVERY
        /// single-symbol substitution changes it; the weighting also catches most adjacent transpositions.
        /// </summary>
        internal static int Checksum(int[] sym, int count)
        {
            int sum = 0;
            for (int i = 0; i < count; i++) sum += sym[i] * (2 * i + 1);
            return sum & 0x1F;
        }

        /// <summary>The same scheme over already-encoded symbols. Byte-for-byte the array form's result for
        /// the same sequence — a codec may not disagree with another about what a code's check symbol is.
        /// </summary>
        internal static int Checksum(string symbols)
        {
            int sum = 0;
            for (int i = 0; i < symbols.Length; i++) sum += Alphabet.IndexOf(symbols[i]) * (2 * i + 1);
            return sum & 0x1F;
        }
    }
}
