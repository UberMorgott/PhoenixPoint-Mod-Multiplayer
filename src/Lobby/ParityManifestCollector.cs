using System;
using System.Collections.Generic;
using System.Globalization;
using Base.Core;
using Base.Platforms;
using Multiplayer.Network.Parity;
using PhoenixPoint.Common.Game;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// FIX-4 — build the co-op parity manifest from the LIVE game: the DLC the user is entitled to, the
    /// ENABLED mods (id + version), and each enabled mod's SETTINGS. The pure comparison/hash logic lives
    /// in Multiplayer.Core (<see cref="ParityManifest"/> / <see cref="ParityComparer"/>); this collector
    /// only reads the game and feeds plain primitives in, so the Core stays Unity-free + unit-testable.
    ///
    /// LIMITATION (documented in docs/WAN-COOP.md): mod settings are read generically via ModConfig's
    /// public config fields and stringified without a JSON dependency, so scalar settings
    /// (bool/int/float/enum/string) diff exactly, but complex/array config values are compared by their
    /// stable type-string only (equal values still hash equal; their contents are not shown in a diff).
    /// Settings are checked, NOT auto-synced — mods read config at load time, so distribution is a later
    /// increment.
    /// </summary>
    public static class ParityManifestCollector
    {
        public static ParityManifest Collect()
        {
            var dlc = new List<string>();
            var mods = new List<(string id, string version)>();
            var settings = new List<(string modId, IEnumerable<(string key, string value)> entries)>();
            // The GAME's own build string, the same one the main menu shows and saves are keyed on. Read
            // outside the big try so a failure here cannot cost the DLC/mod halves; "" then means "unknown"
            // and ParityComparer skips the arm rather than blocking a peer over a value we failed to read.
            var gameVersion = "";
            int seen = 0;   // mods the ModManager knows about, enabled or not — see the audit line below
            try { gameVersion = Base.Build.RuntimeBuildInfo.BuildVersion ?? ""; }
            catch (Exception e) { Debug.LogWarning("[Multiplayer] parity: game build version unreadable: " + e.Message); }

            try
            {
                var game = GameUtl.GameComponent<PhoenixGame>();

                // Owned DLC (independent of any loaded save): enumerate all EntitlementDefs and keep those
                // the platform says the user is entitled to. The stable def name is the wire identifier.
                try
                {
                    var ent = GameUtl.GameComponent<PlatformComponent>()?.Platform?.GetPlatformEntitlement();
                    if (ent != null)
                        foreach (var e in ent.GetAllEntitlements())
                            if (e != null && ent.IsUserEntitledFor(e))
                                dlc.Add(e.name);
                }
                catch (Exception e) { Debug.LogWarning("[Multiplayer] parity: DLC enumeration failed: " + e.Message); }

                // Enabled mods + their settings.
                var mm = game?.ModManager;
                if (mm != null)
                {
                    foreach (var mod in mm.Mods)
                    {
                        if (mod != null) seen++;
                        if (mod == null || !mod.Enabled) continue;
                        var version = mod.MetaData?.Version?.ToString() ?? "0.0";
                        mods.Add((mod.ID, version));

                        // THIS mod's own settings are PER FIELD, not all-or-nothing. Parity exists to catch
                        // CONTENT divergence between peers, and most of what we ship is a local preference —
                        // a keybind (the ping hotkey), a convenience toggle. Left in, they would diff on
                        // every peer that changed one, and ParityConfigSync would then AUTO-APPLY the host's
                        // key over the client's, silently, because KeyCode is an enum and
                        // ParityAutoApply.IsScalar says yes. The deployment cap is the exception and is
                        // named by ParityManifest.IsSyncedOwnSetting: it is enforced in the deployment UI
                        // AND in the host's launch validator, so peers holding different numbers means every
                        // client launch is refused — that one is meant to converge on the host's value.
                        // Our VERSION still rides Mods (L114 compares it) regardless.
                        if (mod.HasConfig && mod.Instance?.Config != null)
                        {
                            bool ours = string.Equals(mod.ID, ParityManifest.MultiplayerModId,
                                                      StringComparison.Ordinal);
                            var kv = new List<(string key, string value)>();
                            try
                            {
                                foreach (var f in mod.Instance.Config.GetConfigFields())
                                {
                                    if (f == null || !f.CanRead) continue;
                                    if (ours && !ParityManifest.IsSyncedOwnSetting(f.ID)) continue;
                                    string val;
                                    try { val = SerializeValue(f.GetValue()); }
                                    catch { val = "<unreadable>"; }
                                    kv.Add((f.ID, val));
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogWarning($"[Multiplayer] parity: settings of '{mod.ID}' failed: {e.Message}");
                            }
                            settings.Add((mod.ID, kv));
                        }
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] parity manifest collect failed: " + e.Message); }

            // THE AUDIT LINE, on both peers. What it settles, and what nothing else could: a client machine
            // reported 41 installed mods against a host's 10 and produced ZERO mod diffs, which is either
            // "38 of them are disabled" or "mod.Enabled under-reports at collect time" — and no existing log
            // told the two apart, because the collected manifest was never printed at all. enabled/seen
            // answers it directly; the crc makes two peers' lines comparable at a glance without pasting
            // 41 ids. Sorted so the hash does not depend on ModManager's enumeration order.
            var ids = mods.ConvertAll(m => m.id);
            ids.Sort(StringComparer.Ordinal);
            Debug.Log("[Multiplayer] parity manifest: mods=" + mods.Count + "/" + seen + " enabled dlc=" + dlc.Count +
                      " game='" + gameVersion + "' crc=" + ParityManifest.HashEntries(ids).ToString("X8", CultureInfo.InvariantCulture));

            return ParityManifest.Build(dlc, mods, settings, gameVersion);
        }

        // Deterministic, dependency-free value → string (no Newtonsoft ref in the mod). Scalars stringify
        // identically on host + client; floats use round-trip invariant formatting so 0.30 == 0.3. Complex
        // values fall through to Convert.ToString (typically the type name — stable across peers).
        // Internal: ParityConfigSync uses the SAME stringify to decide "already equals host value".
        internal static string SerializeValue(object v)
        {
            if (v == null) return "null";
            if (v is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? v.GetType().FullName;
        }
    }
}
