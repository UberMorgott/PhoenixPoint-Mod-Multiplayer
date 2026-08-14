using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Network.Parity
{
    /// <summary>
    /// FIX-4 — pure, Unity-free co-op parity snapshot exchanged at join: the set of enabled/owned DLC,
    /// the enabled mods (id + version), and a per-mod SETTINGS fingerprint (sorted key=value lines + a
    /// crc32 hash of them). The game-facing collector (src/Network/ParityManifestCollector) feeds plain
    /// primitives into <see cref="Build"/>; comparison/formatting lives in <see cref="ParityComparer"/>.
    /// Everything is deterministically sorted so host and client that are genuinely identical produce
    /// byte-identical manifests (and equal hashes).
    /// </summary>
    public sealed class ParityManifest
    {
        /// <summary>The GAME's own build identity — <c>Base.Build.RuntimeBuildInfo.BuildVersion</c>
        /// ("1.30.2.&lt;revision&gt;"). THE gate the mod set alone cannot be: peers can run identical mods on
        /// different Phoenix Point builds, and join rides the native save loader (mandate L6) whose own
        /// compatibility key is that same revision (<c>SavegameMetaData.BuildRevisionNumber</c>,
        /// SavegameMetaData.cs:34). A build difference moves fields and defs under the diff rail and the
        /// result is a silent mid-campaign divergence, which is the one failure this manifest exists to
        /// make loud. "" = the peer's build could not be read, or its Multiplayer mod predates this field —
        /// compared only when BOTH sides state one, so an unknown never rejects a legitimate peer (the mod
        /// version itself already diffs in that case).</summary>
        public string GameVersion = "";

        /// <summary>THIS mod's own id, exactly as <c>meta.json</c> declares it ("Morgott.Multiplayer") and
        /// therefore exactly as <c>ModEntry.ID</c> reports it to the collector. Our own version needs NO new
        /// wire field: it already rides <see cref="Mods"/> like every other mod, and this constant is the
        /// key that pulls it back out (<see cref="ParityComparer.MultiplayerVersion"/>). One version scheme,
        /// the one the mod actually ships.</summary>
        public const string MultiplayerModId = "Morgott.Multiplayer";

        /// <summary>THE ONE SETTING OF OUR OWN THAT IS PART OF PARITY. Everything else in
        /// <c>MultiplayerConfig</c> is a local preference and is deliberately kept out of the manifest (a
        /// keybind, a convenience toggle) — left in, the host's ping key would silently overwrite the
        /// client's. The deployment cap is different in kind: it is enforced TWICE, in the deployment UI on
        /// whichever peer opens the screen and in the host's launch validator (<c>MissionSync.Validate</c>),
        /// so two peers holding different numbers means every client launch is refused. It rides the
        /// ordinary settings rail, which means the ordinary auto-apply (<c>ParityConfigSync</c>) converges it
        /// to the HOST's value at join and restores the client's own at teardown. Field NAME, exactly as
        /// <c>ModConfigField.ID</c> reports it (<c>ModConfigField.cs</c>:38 <c>ID = info.Name</c>).</summary>
        public const string DeployCapSettingId = "MaxDeployUnits";

        /// <summary>Does THIS mod's own config field belong in the parity manifest? Everything not named
        /// here is a local preference and is skipped by <c>ParityManifestCollector</c>.</summary>
        public static bool IsSyncedOwnSetting(string fieldId) =>
            string.Equals(fieldId, DeployCapSettingId, StringComparison.Ordinal);

        /// <summary>Sorted DLC def-names the peer owns/has enabled.</summary>
        public List<string> Dlc = new List<string>();

        /// <summary>Enabled mods, sorted by Id.</summary>
        public List<ModRef> Mods = new List<ModRef>();

        /// <summary>Per-enabled-mod settings fingerprint, sorted by ModId.</summary>
        public List<ModSettings> Settings = new List<ModSettings>();

        public static ParityManifest Build(
            IEnumerable<string> dlc,
            IEnumerable<(string id, string version)> mods,
            IEnumerable<(string modId, IEnumerable<(string key, string value)> entries)> settings,
            string gameVersion = "")
        {
            var m = new ParityManifest { GameVersion = gameVersion ?? "" };

            if (dlc != null)
                m.Dlc = dlc.Where(d => !string.IsNullOrEmpty(d))
                           .Distinct(StringComparer.Ordinal)
                           .OrderBy(d => d, StringComparer.Ordinal)
                           .ToList();

            if (mods != null)
                m.Mods = mods.Where(x => !string.IsNullOrEmpty(x.id))
                             .GroupBy(x => x.id, StringComparer.Ordinal)
                             .Select(g => g.First())
                             .OrderBy(x => x.id, StringComparer.Ordinal)
                             .Select(x => new ModRef { Id = x.id, Version = x.version ?? "" })
                             .ToList();

            if (settings != null)
                foreach (var s in settings.Where(x => !string.IsNullOrEmpty(x.modId))
                                          .OrderBy(x => x.modId, StringComparer.Ordinal))
                {
                    var entries = (s.entries ?? Enumerable.Empty<(string key, string value)>())
                        .Where(e => e.key != null)
                        .OrderBy(e => e.key, StringComparer.Ordinal)
                        .Select(e => e.key + "=" + (e.value ?? ""))
                        .ToList();
                    m.Settings.Add(new ModSettings
                    {
                        ModId = s.modId,
                        Entries = entries,
                        Hash = HashEntries(entries)
                    });
                }

            return m;
        }

        /// <summary>crc32 over the canonical "key=value\n…" form (stable, deterministic).</summary>
        public static uint HashEntries(IEnumerable<string> sortedEntries)
        {
            var canonical = string.Join("\n", sortedEntries ?? Enumerable.Empty<string>());
            return Multiplayer.Util.Crc32.Compute(Encoding.UTF8.GetBytes(canonical));
        }

        // ── CONTENT IDENTITY ─────────────────────────────────────────────────────────────────────────
        // A mod's SELF-REPORTED version is not its identity. Two TFTV builds both said "1.1.4.5" — a
        // host-local build (Mods\TFTV\TFTV.dll, 3202048 B) and the Workshop one (2872311902, 3165184 B) —
        // and the manifest, which compared version strings only, reported full parity while a field present
        // in one build and absent in the other silently killed a whole replicated surface. So the version
        // that travels on the wire is the version string WITH a crc32 of the loaded assembly appended
        // ("1.1.4.5+C67311FA"): same version + different bytes is now a diff, which is the whole point.
        // Rides the EXISTING ModRef.Version field on purpose — no new wire field, no codec change, and a
        // peer on an older Multiplayer build simply sends the bare version (its own Multiplayer version
        // already diffs by name). The PATH SOURCE (Workshop vs local Mods) is deliberately NOT part of the
        // compared string: identical bytes in a different folder behave identically, so diffing on it would
        // be a pure false positive. It is logged locally by the collector instead.

        /// <summary>Separator between the reported version and the assembly crc32. '+' is semver's own
        /// build-metadata marker and appears in no Phoenix Point mod version string.</summary>
        public const char ContentTagSeparator = '+';

        /// <summary>version ⊕ content hash. An empty hash (unreadable assembly) yields the bare version —
        /// an unreadable file must never invent a mismatch.</summary>
        public static string ComposeVersion(string version, string contentHash)
            => string.IsNullOrEmpty(contentHash) ? (version ?? "")
             : (version ?? "") + ContentTagSeparator + contentHash;

        /// <summary>The self-reported half of a composed version ("1.1.4.5+C67311FA" → "1.1.4.5").</summary>
        public static string BaseVersion(string composed)
        {
            if (string.IsNullOrEmpty(composed)) return "";
            var i = composed.IndexOf(ContentTagSeparator);
            return i < 0 ? composed : composed.Substring(0, i);
        }

        /// <summary>The content half ("" when the peer stated none).</summary>
        public static string ContentHash(string composed)
        {
            if (string.IsNullOrEmpty(composed)) return "";
            var i = composed.IndexOf(ContentTagSeparator);
            return i < 0 ? "" : composed.Substring(i + 1);
        }
    }

    public sealed class ModRef
    {
        public string Id;
        public string Version;
    }

    public sealed class ModSettings
    {
        public string ModId;
        /// <summary>Sorted "key=value" lines (value is a deterministic string form of the config value).</summary>
        public List<string> Entries = new List<string>();
        /// <summary>crc32 of the joined entries — fast settings equality check before diffing.</summary>
        public uint Hash;
    }
}
