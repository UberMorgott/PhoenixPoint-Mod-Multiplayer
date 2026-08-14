using System;
using System.Collections.Generic;
using System.IO;
using Multiplayer.Network.Parity;

namespace RailCheck
{
    /// <summary>
    /// L474 — A MOD'S PARITY IDENTITY IS ITS BYTES, AND THE MISMATCH REACHES A VISIBLE SURFACE.
    ///
    /// The blind spot this law closes, in full: host and client ran DIFFERENT TFTV binaries that both
    /// reported version "1.1.4.5" — the host a local build (Mods\TFTV\TFTV.dll, 3202048 B), the client the
    /// Workshop one (workshop\content\839770\2872311902\TFTV.dll, 3165184 B). The manifest compared version
    /// STRINGS, found them equal, printed "mods=4/4 crc=F4FE78A1" and declared full parity — while a field
    /// present in one build and absent in the other silently killed a whole replicated surface. A whole
    /// session was spent misdiagnosing the symptom, because the one instrument that exists to answer "are
    /// these two installs the same" was answering from the installs' own self-report.
    ///
    /// So identity = reported version ⊕ crc32 of the LOADED assembly
    /// (<see cref="ParityManifest.ComposeVersion"/>), and it rides the existing ModRef.Version field — no
    /// new wire shape to keep in sync. And a difference that only a log line carries is a difference nobody
    /// acts on: the operator reads the roster row, so the row must SAY it in words.
    ///
    /// ARMS
    ///   (a) <c>same-version-different-bytes-passes</c> — two installs reporting the same version with
    ///       different assembly hashes MUST diff, and the line must name the mod. THE incident.
    ///   (b) <c>identical-install-diffed</c> — NEGATIVE CONTROL: same version, same hash ⇒ no diff.
    ///   (c) <c>unknown-hash-invents-a-mismatch</c> — NEGATIVE CONTROL: same version, one side stating no
    ///       hash (an older peer, or an unreadable file) ⇒ no diff. Unknown never rejects a live peer —
    ///       the same arm the GameVersion gate holds.
    ///   (d) <c>build-gap-worded-as-a-version-gap</c> — the same-version case must not be phrased as
    ///       "version differs": the fix is "copy the same file", not "update the mod", and the two hashes
    ///       must both be printed or nobody can tell which machine is wrong.
    ///   (e) <c>diff-only-reaches-a-log</c> — the lobby roster row must paint the mismatch in WORDS from
    ///       the row's own diff text, not only as a badge that has to be noticed and clicked.
    ///   (f) <c>parity-gate-waits-on-a-human</c> — the gate stays SOFT and computed: readiness follows
    ///       from the diff text alone, never from anyone pressing anything (second postulate, no quorums),
    ///       and a diff that really does break the wire still stops that peer.
    ///   (g) <c>build-gap-blocks-ready</c> — and the BUILD line itself is a BADGE: it paints, it does not
    ///       lock READY. Both peers run the release they think they run, and the shape that hits it most is
    ///       the owner's own hand-deployed Multiplayer.dll reaching one instance before another.
    ///
    /// Falsify: make ComposeVersion return the bare version → (a) red; compare only BaseVersion → (a) red;
    /// diff on an unknown hash → (c) red; drop the hashes from the message → (d) red; delete the status-cell
    /// wording from LobbyPanel → (e) red; ReadyAllowed back to `string.IsNullOrEmpty(parityDiffs)` → (g)
    /// red; ReadyAllowed → `true` → (f) red.
    /// </summary>
    internal static class L474_TheParityManifestKnowsWhichBuildItIsTalkingTo
    {
        private const string Mod = "phoenixrising.tftv";
        private const string Ver = "1.1.4.5";
        private const string HostCrc = "C67311FA";   // the local build
        private const string ClientCrc = "C561B54A"; // the Workshop build

        internal static IEnumerable<string> Check()
        {
            // VACUITY GUARD. Every arm below is phrased in composed version strings, so if composition or
            // its two accessors stop agreeing with each other the whole law silently compares nothing and
            // reports green. Assert the instrument before using it.
            var composed = ParityManifest.ComposeVersion(Ver, HostCrc);
            if (!string.Equals(ParityManifest.BaseVersion(composed), Ver, StringComparison.Ordinal) ||
                !string.Equals(ParityManifest.ContentHash(composed), HostCrc, StringComparison.Ordinal) ||
                !string.Equals(ParityManifest.ComposeVersion(Ver, ""), Ver, StringComparison.Ordinal))
            {
                yield return "L474 premise-changed: ParityManifest.ComposeVersion/BaseVersion/ContentHash no " +
                             "longer round-trip (composed \"" + composed + "\"). Those three ARE the content " +
                             "identity this law asserts — re-point the law at whatever carries a mod's assembly " +
                             "hash now; do not delete it, or the manifest goes back to trusting a mod's own " +
                             "version string, which is how two different TFTV builds both passed as v1.1.4.5.";
                yield break;
            }

            var host = Manifest(ParityManifest.ComposeVersion(Ver, HostCrc));
            var same = Manifest(ParityManifest.ComposeVersion(Ver, HostCrc));
            var other = Manifest(ParityManifest.ComposeVersion(Ver, ClientCrc));
            var legacy = Manifest(Ver); // a peer whose Multiplayer build predates content identity

            var differing = ParityComparer.Compare(host, other);
            var line = "";
            foreach (var d in differing)
                if (d.IndexOf(Mod, StringComparison.Ordinal) >= 0) line = d;

            if (line.Length == 0)
                yield return "L474 same-version-different-bytes-passes: two installs of " + Mod + " both " +
                             "reporting v" + Ver + " but built from different bytes (" + HostCrc + " vs " +
                             ClientCrc + ") produced no diff naming that mod. That is the exact state the " +
                             "host and client were in when the manifest reported mods=4/4 full parity and a " +
                             "field missing from one build killed a replicated surface unannounced. A mod's " +
                             "identity is the assembly the game loaded, never the version string it claims.";

            if (ParityComparer.Compare(host, same).Count != 0)
                yield return "L474 identical-install-diffed: two byte-identical installs diffed. Every join " +
                             "would then warn, the warning would mean nothing the first time it is wrong, and " +
                             "READY would be locked for peers who have nothing to fix.";

            if (ParityComparer.Compare(host, legacy).Count != 0)
                yield return "L474 unknown-hash-invents-a-mismatch: a peer stating the same version but NO " +
                             "assembly hash (an older Multiplayer build, or a file we failed to read) was " +
                             "diffed. An unknown is not a difference — it degrades to the old version-only " +
                             "comparison, exactly as the GameVersion gate does, or we lock out peers over a " +
                             "value we failed to read.";

            if (line.Length > 0 &&
                (line.IndexOf(HostCrc, StringComparison.Ordinal) < 0 ||
                 line.IndexOf(ClientCrc, StringComparison.Ordinal) < 0 ||
                 line.IndexOf("BUILD", StringComparison.Ordinal) < 0))
                yield return "L474 build-gap-worded-as-a-version-gap: the same-version/different-bytes line " +
                             "(\"" + line + "\") does not say BUILD and print BOTH hashes. \"Mod version " +
                             "differs: host v1.1.4.5 != client v1.1.4.5\" is the sentence that reads as a " +
                             "display bug and gets ignored; the fix here is copying one file to both " +
                             "machines, and the two hashes are what tell the players which file won.";

            var panel = Path.Combine(Program.RepoRoot(), "src", "Lobby", "LobbyPanel.cs");
            var panelSrc = File.Exists(panel) ? File.ReadAllText(panel) : "";
            if (panelSrc.IndexOf("INSTALL DIFFERS", StringComparison.Ordinal) < 0 ||
                panelSrc.IndexOf("row.ParityDiffs = p.ParityDiffs", StringComparison.Ordinal) < 0)
                yield return "L474 diff-only-reaches-a-log: the lobby roster row no longer paints the parity " +
                             "mismatch in words from the row's own diff text (src/Lobby/LobbyPanel.cs). The " +
                             "badge alone is a small button that has to be noticed AND clicked before it says " +
                             "anything — the operator reads the row, sees no READY, assumes \"not ready yet\" " +
                             "and starts the session. The status cell is already a sentence about that peer; " +
                             "the mismatch belongs in it.";

            if (!ParityComparer.ReadyAllowed("") ||
                ParityComparer.ReadyAllowed("Mod missing on client: some.other.mod v2.0"))
                yield return "L474 parity-gate-waits-on-a-human: readiness no longer follows from the diff " +
                             "text alone. The gate is SOFT and computed — it must never become a peer " +
                             "confirming, acknowledging or voting on anything (second postulate: one player " +
                             "drives the whole game while everyone else is AFK) — and a diff that really " +
                             "does break the wire must still stop that peer.";

            // (g) THE BUILD LINE IS A BADGE, NOT A BLOCK. Content identity is the right instrument, but it
            // rides the same text the READY gate reads, so shipping it turned every same-version byte gap
            // into a hard stop overnight: a Workshop copy against a local build of one release, and — during
            // development, constantly — a hand-deployed Multiplayer.dll that reached one instance and not
            // another. Those peers could ready the day before and have nothing to fix at the moment they are
            // stopped. The words stay; the lock goes.
            if (line.Length > 0 && !ParityComparer.ReadyAllowed(line))
                yield return "L474 build-gap-blocks-ready: a same-version BUILD difference locks READY. It is " +
                             "a warning worth painting, not a wire break: both peers run the release they " +
                             "think they run, and the shape that hits this most is the owner's own " +
                             "hand-deployed Multiplayer.dll landing on one instance before another. A gate " +
                             "that fires on the normal case is a gate players learn to route around.";
        }

        private static ParityManifest Manifest(string modVersion) =>
            ParityManifest.Build(
                new List<string>(),
                new List<(string, string)> { (Mod, modVersion) },
                new List<(string, IEnumerable<(string, string)>)>());
    }
}
