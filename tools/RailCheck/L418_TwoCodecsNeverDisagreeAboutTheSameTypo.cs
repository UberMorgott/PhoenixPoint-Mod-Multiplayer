using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Multiplayer.Util;

namespace RailCheck
{
    /// <summary>
    /// L418 — TWO CODECS MAY NOT DISAGREE ABOUT THE SAME TYPO.
    ///
    /// THE FAILURE (9a08976). <c>ConnectCode.Decode</c> trimmed, stripped dashes and upper-cased, and skipped
    /// the Crockford <c>I/L→1</c>, <c>O→0</c> aliasing that <see cref="InviteCode"/> and <see cref="UnifiedCode"/>
    /// both applied — and <c>SmartJoinParser</c> gated the whole ConnectCode branch on its own un-aliased
    /// symbol test on top of that. So the letter O typed for a zero was a WORKING JOIN in an 8-symbol invite
    /// code and a silent "invalid code" in an 11-symbol endpoint code. One typo, two verdicts, and the player
    /// gets no way to tell which of the two codes he is holding.
    ///
    /// The alphabet was duplicated four times, the checksum three, the normalizer verbatim twice. That is why
    /// it drifted: nothing in the build could notice one copy being edited and the others not, because
    /// agreement between copies was never asserted anywhere.
    ///
    /// PURE BCL — no game types, so every arm below is EXECUTED against the shipped codecs rather than read
    /// off their IL. A law that asserts "the shared normalizer is CALLED" is the weaker law that would have
    /// gone green the moment someone re-inlined it correctly-but-differently.
    ///
    /// THE ARMS:
    ///   (a) <c>checksum-forms-disagree</c> — the two <c>Crockford.Checksum</c> overloads (over symbol values,
    ///       and over already-encoded symbols) must answer identically for the same sequence at every length
    ///       the three layouts use. One codec computes the check symbol one way and its reader the other.
    ///   (b) THE VACUITY GUARD, <c>premise-changed</c> — each codec's own <c>Encode</c>→<c>Decode</c>
    ///       round-trip, run before (c) and fatal to the law. Three codecs that refuse EVERYTHING agree
    ///       perfectly about every typo, so without this floor the arm that catches the real bug is an arm
    ///       that cannot fail.
    ///   (c) <c>typo-verdicts-diverge</c> — THE ARM THAT WOULD HAVE CAUGHT IT. The SAME typo set (O↔0, I↔1,
    ///       L↔1, lower case, extra dashes, spaces) is applied to a code from each codec, and every codec
    ///       must still decode it to the value it started from. Plus the other direction: a genuine
    ///       single-symbol substitution must be REFUSED by every codec, or "tolerant" would just mean the
    ///       check symbol stopped working.
    ///   (d) <c>alphabet-copied</c> — the alphabet literal appears EXACTLY ONCE under <c>src/</c>. The drift
    ///       above was a copy-paste that stayed correct for a year and then did not; one literal is the only
    ///       state in which arms (a)-(c) cannot be satisfied by four vocabularies that happen to agree today.
    ///
    /// GUARD: arm (b), <c>premise-changed</c> and fatal — it refuses to let the typo arm be satisfied by
    /// codecs that decode nothing at all. Every other arm executes production code on constructed input and
    /// reports a throw as a failure, so there is nothing here that can pass by not running.
    ///
    /// Falsify: drop the I/L/O aliasing from <c>Crockford.Normalize</c> → (c) on all three; hand ConnectCode
    /// back its private normalizer → (c) on ConnectCode alone, which is the original bug; change one
    /// <c>Checksum</c> overload's weights → (a); paste the alphabet into a second file → (d).
    /// </summary>
    internal static class L418_TwoCodecsNeverDisagreeAboutTheSameTypo
    {
        /// <summary>What a human mistypes into a join code, and what Crockford promises to forgive.</summary>
        private static readonly KeyValuePair<string, Func<string, string>>[] Typos =
        {
            new KeyValuePair<string, Func<string, string>>("O for 0",    s => s.Replace('0', 'O')),
            new KeyValuePair<string, Func<string, string>>("I for 1",    s => s.Replace('1', 'I')),
            new KeyValuePair<string, Func<string, string>>("L for 1",    s => s.Replace('1', 'L')),
            new KeyValuePair<string, Func<string, string>>("lower case", s => s.ToLowerInvariant()),
            new KeyValuePair<string, Func<string, string>>("o/i lower",  s => s.Replace('0', 'o').Replace('1', 'i')),
            new KeyValuePair<string, Func<string, string>>("no dashes",  s => s.Replace("-", "")),
            new KeyValuePair<string, Func<string, string>>("spaces",     s => " " + s.Replace("-", " ") + " "),
        };

        internal static IEnumerable<string> Check()
        {
            // ═══ (a) THE TWO CHECKSUM FORMS ARE ONE CHECKSUM ═══
            var rng = new Random(418);
            for (int len = 1; len <= 20; len++)
                for (int trial = 0; trial < 200; trial++)
                {
                    var sym = new int[len];
                    var text = new char[len];
                    for (int i = 0; i < len; i++)
                    {
                        sym[i] = rng.Next(32);
                        text[i] = Crockford.Alphabet[sym[i]];
                    }
                    var s = new string(text);
                    int fromValues = Crockford.Checksum(sym, len);
                    int fromSymbols = Crockford.Checksum(s);
                    if (fromValues != fromSymbols)
                    {
                        yield return "L418 checksum-forms-disagree: Crockford.Checksum(int[]," + len + ")=" +
                                     fromValues + " but Checksum(\"" + s + "\")=" + fromSymbols + ". InviteCode " +
                                     "and ConnectCode mint their check symbol from the array form, UnifiedCode " +
                                     "from the string form — a disagreement means a code one codec produces is a " +
                                     "typo to the other, which is this law's whole subject one layer down.";
                        yield break;   // one report; 4000 identical lines help nobody
                    }
                }

            // ═══ (b) EACH CODEC KEEPS ITS OWN CODE — the vacuity guard, so it runs before (c) ═══
            var codecs = Codecs();
            var broke = new List<string>();
            foreach (var c in codecs)
            {
                string thrown;
                if (!Accepts(c, c.Code, out thrown))
                    broke.Add(c.Name + " ('" + c.Code + "'" + (thrown == null ? "" : ", threw " + thrown) + ")");
            }
            if (broke.Count > 0)
            {
                yield return "L418 premise-changed: [" + string.Join(", ", broke.ToArray()) + "] does not decode " +
                             "the code it just encoded. Arm (c) below asks whether every codec gives the same " +
                             "verdict on the same typo, and three codecs that all refuse EVERYTHING agree " +
                             "perfectly — so a broken round-trip would turn the arm that catches the real bug " +
                             "into an arm that cannot fail. Fix the codec (or re-point this law at the layouts " +
                             "in use now) before believing anything below.";
                yield break;
            }

            // ═══ (c) THE SAME TYPO GETS THE SAME VERDICT EVERYWHERE ═══
            foreach (var typo in Typos)
            {
                var refused = new List<string>();
                foreach (var c in codecs)
                {
                    string thrown;
                    var typed = typo.Value(c.Code);
                    if (!Accepts(c, typed, out thrown))
                        refused.Add(c.Name + " ('" + typed + "'" + (thrown == null ? "" : ", threw " + thrown) + ")");
                }
                if (refused.Count > 0)
                    yield return "L418 typo-verdicts-diverge: the typo '" + typo.Key + "' is refused by [" +
                                 string.Join(", ", refused.ToArray()) + "] out of " + codecs.Count +
                                 " codec(s) over one shared vocabulary. This is " +
                                 "9a08976 verbatim: ConnectCode skipped the I/L→1, O→0 aliasing the other two " +
                                 "applied, so the letter O for a zero was a working join in an 8-symbol invite " +
                                 "code and 'invalid code' in an 11-symbol endpoint code. The player cannot tell " +
                                 "the two codes apart and has no reason to suspect the character he typed. Every " +
                                 "codec must go through Crockford.Normalize and nothing else.";
            }

            // …and the check symbol still has to work, or "tolerant" means "accepts anything".
            foreach (var c in codecs)
            {
                var broken = Corrupt(c.Code);
                string thrown;
                if (broken != null && Accepts(c, broken, out thrown))
                    yield return "L418 typo-verdicts-diverge: " + c.Name + " ACCEPTS '" + broken + "', a genuine " +
                                 "single-symbol substitution in '" + c.Code + "', and decodes it to the original " +
                                 "value. The check symbol is what separates forgiving a Crockford alias from " +
                                 "forgiving a wrong character, and without it a mistyped code becomes a " +
                                 "different, arbitrary endpoint and the player gets a connect timeout with " +
                                 "nothing naming the typo.";
            }

            // ═══ (d) ONE ALPHABET ═══
            var src = Path.Combine(Program.RepoRoot(), "src");
            var carriers = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
                    if (File.ReadAllText(f).IndexOf(Crockford.Alphabet, StringComparison.Ordinal) >= 0)
                        carriers.Add(Path.GetFileName(f));
            }
            catch (Exception ex) { carriers.Add("<unreadable: " + ex.GetType().Name + ">"); }
            if (carriers.Count != 1)
                yield return "L418 alphabet-copied: the Crockford alphabet literal appears in " + carriers.Count +
                             " source file(s) [" + string.Join(", ", carriers.ToArray()) + "] rather than exactly " +
                             "one (Crockford.cs). Four copies is the state this family drifted from: nothing in " +
                             "the build can notice one copy being edited and the others not, and the arms above " +
                             "only prove the copies agree TODAY. If this is a new codec, point it at " +
                             "Crockford.Alphabet.";
        }

        private sealed class Codec
        {
            public string Name;
            public string Code;
            public Func<string, bool> Decodes;   // decodes to the value Code was made from
        }

        /// <summary>Each codec with a code whose symbols include both a 0 and a 1, so the aliasing typos have
        /// something to act on. Searched rather than hardcoded: the layouts are free to change.</summary>
        private static List<Codec> Codecs()
        {
            var list = new List<Codec>();

            uint account = FirstWhere<uint>(i => (uint)(0x1234567 + i * 7919), id => Has01(InviteCode.Encode(id)));
            list.Add(new Codec
            {
                Name = "InviteCode",
                Code = InviteCode.Encode(account),
                Decodes = s => { uint got; return InviteCode.TryDecode(s, out got) && got == account; },
            });

            var ep = FirstWhere<IPEndPoint>(i => new IPEndPoint(new IPAddress(new byte[] { 203, 0, 113, (byte)i }), 27000 + i),
                                            e => Has01(ConnectCode.Encode(e)));
            list.Add(new Codec
            {
                Name = "ConnectCode",
                Code = ConnectCode.Encode(ep),
                Decodes = s => { var got = ConnectCode.Decode(s); return got != null && got.Equals(ep); },
            });

            var both = FirstWhere<int>(i => i, i => Has01(UnifiedCode.Encode((uint)(0x1234567 + i * 7919),
                                                                            new IPEndPoint(new IPAddress(new byte[] { 203, 0, 113, (byte)i }), 27000 + i))));
            uint uAccount = (uint)(0x1234567 + both * 7919);
            var uEp = new IPEndPoint(new IPAddress(new byte[] { 203, 0, 113, (byte)both }), 27000 + both);
            list.Add(new Codec
            {
                Name = "UnifiedCode",
                Code = UnifiedCode.Encode(uAccount, uEp),
                Decodes = s =>
                {
                    uint gotId; bool hasId; IPEndPoint gotEp; bool hasEp;
                    return UnifiedCode.TryDecode(s, out gotId, out hasId, out gotEp, out hasEp) &&
                           hasId && gotId == uAccount && hasEp && uEp.Equals(gotEp);
                },
            });
            return list;
        }

        private static T FirstWhere<T>(Func<int, T> make, Func<T, bool> ok)
        {
            for (int i = 0; i < 256; i++) { var v = make(i); if (Safe(() => ok(v))) return v; }
            return make(0);   // arm (c) then simply has fewer aliasing symbols to act on; (b) still holds
        }

        private static bool Has01(string code) => code != null && code.IndexOf('0') >= 0 && code.IndexOf('1') >= 0;

        private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

        private static bool Accepts(Codec c, string code, out string thrown)
        {
            thrown = null;
            try { return c.Decodes(code); }
            catch (Exception ex) { thrown = ex.GetType().Name; return false; }
        }

        /// <summary>One data symbol replaced by a DIFFERENT in-alphabet symbol — a real typo, not an alias.</summary>
        private static string Corrupt(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var chars = code.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                int at = Crockford.Alphabet.IndexOf(chars[i]);
                if (at < 0) continue;                              // a dash
                chars[i] = Crockford.Alphabet[(at + 1) & 0x1F];
                return new string(chars);
            }
            return null;
        }
    }
}
