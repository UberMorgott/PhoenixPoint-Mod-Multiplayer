using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Network.Parity
{
    /// <summary>
    /// FIX-4 — pure host-authoritative parity comparison. <see cref="Compare"/> returns the list of
    /// human-readable diffs between the host manifest (reference) and a client manifest; an empty list
    /// means full parity. SOFT GATE: a mismatched client still JOINS the lobby and gets a roster warning
    /// badge; whether it can also READY is <see cref="ReadyAllowed"/> (enforced host-side too), which
    /// blocks on every diff EXCEPT the same-version build-hash line — see its remarks. Diff rules:
    ///   • DLC: only "missing on client" diffs — the host save uses it and the client lacks it, so the
    ///     transferred save fails to deserialize. Extra DLC on the client is harmless (it just owns more).
    ///   • Mods: missing / extra / version-differs all diff — a def-patching mod divergence desyncs.
    ///   • Settings: for a mod present on BOTH sides, any per-key value difference diffs (mods read
    ///     config at load time, so different settings = different behaviour/defs). Host settings are
    ///     AUTO-APPLIED on the client at join (ParityConfigSync); only unappliable keys keep diffing.
    /// </summary>
    public static class ParityComparer
    {
        public static List<string> Compare(ParityManifest host, ParityManifest client)
        {
            var diffs = new List<string>();
            if (host == null || client == null)
            {
                diffs.Add("Parity manifest missing (the other player did not send one — incompatible Multiplayer mod version?).");
                return diffs;
            }

            // ── THIS MOD's OWN VERSION, first and by name. It already rode the generic mod loop below as
            // one anonymous "Mod version differs" line among DLC, settings and every other mod — true, but
            // unreadable, and it is the ONE diff that explains all the others (two Multiplayer builds do not
            // agree on the wire, so every later symptom is downstream of it). Named here, first, so the
            // roster badge and the client's join notice both lead with the thing to actually fix.
            if (MultiplayerVersionMismatch(host, client))
                diffs.Add($"Multiplayer mod version differs: host v{ReportedVersion(host)} != " +
                          $"client v{ReportedVersion(client)}. Both players must run the SAME " +
                          "Multiplayer mod version — update the older install.");

            // ── GAME BUILD: the identity the mod list cannot carry. Both peers may run the same mods on
            // different Phoenix Point builds; join then rides the native save loader (mandate L6) across a
            // build boundary the game itself keys saves on (SavegameMetaData.BuildRevisionNumber), fields
            // and defs move under the diff rail, and the campaign diverges mid-session with nothing said.
            // Compared ONLY when both sides state a version: an unknown means the other peer's Multiplayer
            // build predates this field, which its MOD VERSION already diffs below — so a blank never
            // rejects a peer that is actually fine.
            if (!string.IsNullOrEmpty(host.GameVersion) && !string.IsNullOrEmpty(client.GameVersion) &&
                !string.Equals(host.GameVersion, client.GameVersion, StringComparison.Ordinal))
                diffs.Add($"Phoenix Point build differs: host {host.GameVersion} != client {client.GameVersion}. " +
                          "Co-op needs the same game build on both machines (check for a pending Steam update).");

            // ── DLC: block only when the host has a DLC the client does not. ──
            var clientDlc = new HashSet<string>(client.Dlc ?? new List<string>(), StringComparer.Ordinal);
            foreach (var d in host.Dlc ?? new List<string>())
                if (!clientDlc.Contains(d))
                    diffs.Add($"DLC missing on client: {d}");

            // ── Mods: missing / extra / version mismatch all block. ──
            var hostMods = ToModMap(host.Mods);
            var clientMods = ToModMap(client.Mods);
            foreach (var kv in hostMods)
            {
                if (!clientMods.TryGetValue(kv.Key, out var cv))
                    diffs.Add($"Mod missing on client: {kv.Key} v{kv.Value}");
                // Our own VERSION gap already has its own named line above — say it ONCE. A same-version
                // BUILD gap does NOT (MultiplayerVersionMismatch compares the reported versions only), so
                // it still falls through here and comes out as the badge-only "Mod BUILD differs" line.
                // That is the hand-deployed-Multiplayer.dll case, which must warn and must not block.
                else if (!string.Equals(kv.Value, cv, StringComparison.Ordinal) &&
                         !(string.Equals(kv.Key, ParityManifest.MultiplayerModId, StringComparison.Ordinal) &&
                           MultiplayerVersionMismatch(host, client)))
                {
                    var line = ModIdentityDiff(kv.Key, kv.Value, cv);
                    if (line.Length > 0) diffs.Add(line); // "" = same version, one side's bytes unreadable
                }
            }
            foreach (var kv in clientMods)
                if (!hostMods.ContainsKey(kv.Key))
                    diffs.Add($"Extra mod on client: {kv.Key} v{kv.Value}");

            // ── Settings: only for mods present on BOTH sides (mod-presence handled above). ──
            var clientSettings = (client.Settings ?? new List<ModSettings>())
                .GroupBy(s => s.ModId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var hs in host.Settings ?? new List<ModSettings>())
            {
                if (!clientSettings.TryGetValue(hs.ModId, out var cs)) continue;
                if (hs.Hash == cs.Hash) continue; // fast equal
                foreach (var line in DiffEntries(hs.ModId, hs.Entries, cs.Entries))
                    diffs.Add(line);
            }

            return diffs;
        }

        /// <summary>ONE mod, two identities that disagree — said in the words that match WHICH half moved.
        /// The version-only sentence was the whole blind spot: host and client ran different TFTV binaries
        /// that BOTH reported "1.1.4.5", so with versions alone there was no sentence to print at all. Now
        /// the identity carries a crc32 of the assembly (<see cref="ParityManifest.ComposeVersion"/>), and
        /// the two cases read differently because the fix differs: a version gap means "update the mod", a
        /// same-version BUILD gap means "you two are running different files of the same release" — the
        /// local-build-vs-Workshop case, which no version string can ever show.
        /// "" (NO DIFF) for one case only: the reported versions agree and one side stated no hash — an
        /// older Multiplayer build, or an assembly we failed to read. Same arm as the GameVersion gate: an
        /// unknown must never invent a mismatch, it only degrades to the old version-only comparison.</summary>
        public static string ModIdentityDiff(string modId, string hostVersion, string clientVersion)
        {
            var hb = ParityManifest.BaseVersion(hostVersion);
            var cb = ParityManifest.BaseVersion(clientVersion);
            var hh = ParityManifest.ContentHash(hostVersion);
            var ch = ParityManifest.ContentHash(clientVersion);
            if (string.Equals(hb, cb, StringComparison.Ordinal) && (hh.Length == 0 || ch.Length == 0))
                return "";
            if (string.Equals(hb, cb, StringComparison.Ordinal))
                return $"{BuildDiffPrefix}{modId} v{hb} — same version, DIFFERENT file " +
                       $"(host {hh} != client {ch}). One of you runs a local build and the other the " +
                       "Workshop one; copy the same file to both machines.";
            return $"Mod version differs: {modId} host v{hostVersion} != client v{clientVersion}";
        }

        /// <summary>This peer's OWN Multiplayer-mod version, pulled out of the manifest's ordinary mod list
        /// (<see cref="ParityManifest.MultiplayerModId"/>). "" = UNKNOWN, which happens when the collector
        /// could not read the mod list at all, or the peer's manifest predates a shape we can read. Unknown
        /// is never a mismatch — see <see cref="MultiplayerVersionMismatch"/>.</summary>
        public static string MultiplayerVersion(ParityManifest m)
        {
            if (m?.Mods == null) return "";
            foreach (var mod in m.Mods)
                if (mod != null && string.Equals(mod.Id, ParityManifest.MultiplayerModId, StringComparison.Ordinal))
                    return mod.Version ?? "";
            return "";
        }

        /// <summary>THE version gate, one decision, read by the host diff AND by the client's join notice.
        /// A mismatch needs BOTH sides to actually state a version: an unknown means we failed to read a
        /// version, not that the other install is wrong, and refusing on it would lock out a peer who is
        /// fine. (A peer whose Multiplayer really is absent is a different, generically-diffed sentence.)</summary>
        /// <remarks>THE REPORTED VERSION, NOT THE BYTES (L474). Since content identity rides this same
        /// field (<c>ComposeVersion</c>), comparing the composed string made every hand-deployed build of
        /// our OWN mod a "version mismatch": the door prompt told the player to update to the version he was
        /// already running, and READY locked. A byte gap at the same version is real and still reported —
        /// as the badge-only BUILD line the generic mod loop emits — but it is not a version mismatch and it
        /// does not block.</remarks>
        public static bool MultiplayerVersionMismatch(ParityManifest host, ParityManifest client)
        {
            var h = ReportedVersion(host);
            var c = ReportedVersion(client);
            return h.Length > 0 && c.Length > 0 && !string.Equals(h, c, StringComparison.Ordinal);
        }

        /// <summary>This peer's Multiplayer version with any content tag stripped — what a human reads and
        /// what "update your mod" is about.</summary>
        public static string ReportedVersion(ParityManifest m) => ParityManifest.BaseVersion(MultiplayerVersion(m));

        /// <summary>The CLIENT-facing join notice ("" when the versions match or either is unknown): shown
        /// natively the moment the host's manifest lands on the accept — before the lobby ever opens, so a
        /// mismatched player learns it at the door instead of after picking a seat. Names BOTH versions and
        /// the action; second-person because only the client is ever shown this string (the host reads the
        /// neutral roster diff from <see cref="Compare"/>). The peer stays CONNECTED — nobody is thrown out
        /// (L84) — it simply cannot READY until the versions match.</summary>
        public static string VersionNoticeForClient(ParityManifest host, ParityManifest client)
            => !MultiplayerVersionMismatch(host, client) ? ""
             : "MULTIPLAYER MOD VERSION MISMATCH\n\n" +
               $"The host is running Multiplayer v{ReportedVersion(host)}.\n" +
               $"You are running Multiplayer v{ReportedVersion(client)}.\n\n" +
               $"Update your Multiplayer mod to v{ReportedVersion(host)} (or ask the host to match yours). " +
               "You stay connected and can chat, but READY stays locked until both versions are the same.";

        /// <summary>Join the diffs into a single message-box-ready block ("" when there are none).</summary>
        public static string Format(List<string> diffs)
            => diffs == null || diffs.Count == 0 ? "" : string.Join("\n", diffs);

        /// <summary>The one marker that separates a BADGE from a BLOCK. Every diff line built by
        /// <see cref="ModIdentityDiff"/>'s same-version arm carries it, and <see cref="ReadyAllowed"/> is the
        /// only reader.</summary>
        internal const string BuildDiffPrefix = "Mod BUILD differs: ";

        /// <summary>
        /// Parity soft-gate READY decision (shared by the host's authoritative gate and the client's
        /// button lock): a peer may ready up unless it holds a diff that is worth stopping for.
        ///
        /// THE BUILD-HASH LINE IS A BADGE, NOT A BLOCK (2026-08-15). L474 gave a mod's identity a crc32 of
        /// the assembly the game loaded, which is the right instrument — but it rides the same text the gate
        /// reads, so every same-version byte gap became a hard stop overnight. Two shapes that are NORMAL
        /// hit it: a Workshop copy against a local build of the same release, and — constantly, during
        /// development — a hand-deployed Multiplayer.dll that reached one instance and not another. Those
        /// peers could ready two days ago and have nothing to "fix" at the moment they are stopped.
        /// So the line still reaches the roster and still says which file won; it just stops locking READY.
        ///
        /// Everything that blocked BEFORE the crc landed still blocks: a Multiplayer VERSION gap, a Phoenix
        /// Point build gap, a missing/extra mod, a missing DLC, a settings divergence. Per-peer and computed
        /// from that peer's own text — never a vote, an acknowledgement, or anything another player must do.
        /// </summary>
        public static bool ReadyAllowed(string parityDiffs)
        {
            if (string.IsNullOrEmpty(parityDiffs)) return true;
            foreach (var line in parityDiffs.Split('\n'))
                if (line.Trim().Length > 0 && !line.StartsWith(BuildDiffPrefix, StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static Dictionary<string, string> ToModMap(List<ModRef> mods)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mods != null)
                foreach (var m in mods)
                    if (m != null && !string.IsNullOrEmpty(m.Id))
                        d[m.Id] = m.Version ?? "";
            return d;
        }

        private static IEnumerable<string> DiffEntries(string modId, List<string> hostEntries, List<string> clientEntries)
        {
            var h = ToKvMap(hostEntries);
            var c = ToKvMap(clientEntries);
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in h.Keys) keys.Add(k);
            foreach (var k in c.Keys) keys.Add(k);
            foreach (var k in keys)
            {
                h.TryGetValue(k, out var hv);
                c.TryGetValue(k, out var cv);
                if (!string.Equals(hv, cv, StringComparison.Ordinal))
                    yield return $"Setting {modId}.{k}: host={hv ?? "(absent)"} client={cv ?? "(absent)"}";
            }
        }

        private static Dictionary<string, string> ToKvMap(List<string> entries)
            => ParityAutoApply.ToMap(entries); // one splitter, shared with the auto-apply
    }
}
