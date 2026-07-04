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

                // TFTV is enabled AFTER Multiplayer, so its logger types are not loaded yet and the
                // [HarmonyPatch] TFTV/PRM log-redirect classes get silently skipped by PatchAll. Arm a
                // deferred installer that patches them the instant their assembly loads (which precedes
                // TFTV's synchronous *Logger.Initialize), giving the secondary instance its own log file.
                Multiplayer.Harmony.TftvLogDeferredInstaller.Install(harmony);
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Multiplayer] PatchAll failed: " + e.Message);
            }

            _ui = ModGO.AddComponent<MultiplayerUI>();
            Logger.LogInfo("[Multiplayer] UI initialized");

            // Tactical deploy-sync (Increment 1): arm the SurfaceRouter tactical fast-path so a client can
            // receive host tac.deploy snapshots over the 0x67 envelope rail. Null-guarded + inert until a
            // tactical mission deploys; the deploy/suppress Harmony patches auto-register via PatchAll above.
            Multiplayer.Sync.Tactical.TacticalDeploySync.ArmInboundHook();
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

        private void Update()
        {
            NetworkEngine.Instance?.Update();
        }
    }
}
