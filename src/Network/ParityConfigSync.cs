using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Multiplayer.Network.Parity;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Modding;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// Parity soft-gate — CLIENT-side auto-apply of the HOST's mod settings (host-authoritative).
    /// The host manifest arrives in ConnectionAccepted; for every mod enabled on BOTH sides, each
    /// scalar config field that differs is set IN MEMORY via the game's own ModConfigField accessors,
    /// then the mod's OnConfigChanged hook fires (ModManager.OnConfigChanged) so mods that support
    /// runtime changes pick it up. Nothing is written to disk by this path (ModConfig.json persists
    /// only via ModManager.SaveModConfig — game init + the mod-management UI), but the client's
    /// ORIGINAL values are snapshotted anyway and restored at session teardown, so a mid-session
    /// game-driven SaveModConfig can never permanently overwrite the user's own configs after the
    /// session ends. Unappliable (complex/array) values are left alone — they keep diffing, so the
    /// roster badge + ready lock persist and the diff names the mod/key.
    /// </summary>
    public static class ParityConfigSync
    {
        // Snapshot of the client's ORIGINAL values, one per (mod, field) — first write only. The
        // ModConfigField delegates capture the live Config instance, so they stay valid all session.
        private static readonly List<(ModEntry mod, ModConfigField field, object original)> _snapshot
            = new List<(ModEntry, ModConfigField, object)>();
        private static readonly HashSet<string> _snapshotKeys = new HashSet<string>();

        /// <summary>Apply the host's scalar settings onto this client's enabled mods. Returns true if
        /// anything actually changed (→ caller re-sends a fresh manifest for host re-compare).</summary>
        public static bool ApplyHostSettings(ParityManifest host)
        {
            if (host?.Settings == null || host.Settings.Count == 0) return false;
            bool changedAny = false;
            try
            {
                var mm = GameUtl.GameComponent<PhoenixGame>()?.ModManager;
                if (mm == null) return false;

                foreach (var hs in host.Settings)
                {
                    var mod = mm.Mods.FirstOrDefault(m => m != null && m.Enabled && m.ID == hs.ModId);
                    if (mod == null || !mod.HasConfig || mod.Instance?.Config == null) continue;

                    var hostVals = ParityAutoApply.ToMap(hs.Entries);
                    bool modChanged = false;
                    foreach (var f in mod.Instance.Config.GetConfigFields())
                    {
                        if (f == null || !f.CanRead || !f.CanWrite) continue;
                        if (!hostVals.TryGetValue(f.ID, out var hostVal)) continue;

                        string cur;
                        try { cur = ParityManifestCollector.SerializeValue(f.GetValue()); }
                        catch { continue; }
                        if (string.Equals(cur, hostVal, StringComparison.Ordinal)) continue;

                        if (!ParityAutoApply.TryParseValue(f.FieldType, hostVal, out var parsed))
                        {
                            // Never-silent: the key stays a manifest diff (badge + lock persist), and the
                            // log names why it could not converge automatically.
                            Debug.LogWarning($"[Multiplayer] parity auto-apply: {hs.ModId}.{f.ID} " +
                                             $"(type {f.FieldType?.Name}) is not auto-appliable — stays a mismatch.");
                            continue;
                        }

                        SnapshotOnce(mod, f);
                        try { f.SetValue(parsed); modChanged = true; }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[Multiplayer] parity auto-apply: set {hs.ModId}.{f.ID} failed: {e.Message}");
                        }
                    }

                    if (modChanged)
                    {
                        changedAny = true;
                        // Fire the game's own config-changed hook so the mod re-reads its settings.
                        try { mm.OnConfigChanged(mod); }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[Multiplayer] parity auto-apply: OnConfigChanged('{hs.ModId}') failed: {e.Message}");
                        }
                        Debug.Log($"[Multiplayer] parity auto-apply: host settings applied to '{hs.ModId}' (in-memory, session-only).");
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] parity auto-apply failed: " + e.Message); }
            return changedAny;
        }

        private static void SnapshotOnce(ModEntry mod, ModConfigField f)
        {
            var key = mod.ID + "\n" + f.ID;
            if (!_snapshotKeys.Add(key)) return;
            try { _snapshot.Add((mod, f, f.GetValue())); } catch { _snapshotKeys.Remove(key); }
        }

        /// <summary>
        /// Restore the client's original mod settings (session teardown chokepoint —
        /// NetworkEngine.Shutdown/TearDown). No-op when nothing was applied (host / clean client).
        /// </summary>
        public static void RestoreOriginals()
        {
            if (_snapshot.Count == 0) return;
            var changedMods = new HashSet<ModEntry>();
            foreach (var (mod, field, original) in _snapshot)
            {
                try { field.SetValue(original); changedMods.Add(mod); } catch { }
            }
            _snapshot.Clear();
            _snapshotKeys.Clear();
            try
            {
                var mm = GameUtl.GameComponent<PhoenixGame>()?.ModManager;
                if (mm != null)
                    foreach (var mod in changedMods)
                    {
                        try { mm.OnConfigChanged(mod); } catch { }
                    }
            }
            catch { }
            Debug.Log($"[Multiplayer] parity auto-apply: restored original settings of {changedMods.Count} mod(s) on session end.");
        }
    }
}
