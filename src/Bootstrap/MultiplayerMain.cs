using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.UI;
using Multiplayer.Util;
using PhoenixPoint.Modding;
using UnityEngine;

namespace Multiplayer
{
    public class MultiplayerMain : ModMain
    {
        public static new MultiplayerMain Instance { get; private set; }
        public override bool CanSafelyDisable => false;

        // Exposes the base ModMain logger (otherwise protected) so the co-op host/join gate can report
        // the reflection version-guard failure through the same log the startup self-check uses. Null
        // before OnModEnabled / after OnModDisabled — callers null-check.
        public static ModLogger Log => Instance?.Logger;

        private MultiplayerUI _ui;

        public override void OnModEnabled()
        {
            Instance = this;
            MultiplayerLog.Init(); // earliest: capture startup lines into the dedicated mod log.
            Logger.LogInfo("[Multiplayer] OnModEnabled");

            try
            {
                var harmony = (HarmonyLib.Harmony)HarmonyInstance;
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Logger.LogInfo("[Multiplayer] PatchAll done");

                // Same deferral for the TFTV UI/aircraft/tactical-script GUARD patches: they too gate on a
                // TFTV type in Prepare(), so PatchAll silently skipped them (TFTV loads after us) and every
                // TFTV guard was dead in prod (126x geoscape-teardown NRE storm). Bind them when TFTV loads.
                // A3b: neutralise foreign ref-DamageResult patches during a mirror apply. Installed HERE for
                // mods already loaded, and AGAIN from TftvLateBinder once TFTV lands (it is idempotent).
                Multiplayer.Tactical.MirrorApplyGuard.Install(harmony);
                Multiplayer.Harmony.TftvLateBinder.Install(harmony);
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Multiplayer] PatchAll failed: " + e.Message);
            }

            _ui = ModGO.AddComponent<MultiplayerUI>();
            Logger.LogInfo("[Multiplayer] UI initialized");

            // Parity auto-apply: wire the teardown restore hook (delegate field — NetworkEngine must not
            // reference ParityConfigSync's game types directly, same JIT-safety rule as SteamLobbyCleanup).
            NetworkEngine.ParityConfigRestore = ParityConfigSync.RestoreOriginals;
        }

        public override void OnModDisabled()
        {
            Logger.LogInfo("[Multiplayer] OnModDisabled");

            if (NetworkEngine.Instance != null)
            {
                NetworkEngine.Instance.Shutdown();
            }

            if (_ui != null)
            {
                Object.Destroy(_ui);
                _ui = null;
            }

            Instance = null;
            MultiplayerLog.Shutdown();
        }

        // No Update() here: ModMain is a plain abstract class (not a MonoBehaviour), so Unity never
        // pumps it. The engine is pumped by MultiplayerUI.Update (the component added above).
    }
}
