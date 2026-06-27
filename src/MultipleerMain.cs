using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.UI;
using Multipleer.Util;
using PhoenixPoint.Modding;
using UnityEngine;

namespace Multipleer
{
    public class MultipleerMain : ModMain
    {
        public static new MultipleerMain Instance { get; private set; }
        public override bool CanSafelyDisable => false;

        private MultiplayerUI _ui;

        public override void OnModEnabled()
        {
            Instance = this;
            MultipleerLog.Init(); // earliest: capture startup lines into the dedicated mod log.
            Logger.LogInfo("[Multipleer] OnModEnabled");

            try
            {
                var harmony = (HarmonyLib.Harmony)HarmonyInstance;
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Logger.LogInfo("[Multipleer] PatchAll done");

                // TFTV is enabled AFTER Multipleer, so its logger types are not loaded yet and the
                // [HarmonyPatch] TFTV/PRM log-redirect classes get silently skipped by PatchAll. Arm a
                // deferred installer that patches them the instant their assembly loads (which precedes
                // TFTV's synchronous *Logger.Initialize), giving the secondary instance its own log file.
                Multipleer.Harmony.TftvLogDeferredInstaller.Install(harmony);
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Multipleer] PatchAll failed: " + e.Message);
            }

            _ui = ModGO.AddComponent<MultiplayerUI>();
            Logger.LogInfo("[Multipleer] UI initialized");

            // Tactical deploy-sync (Increment 1): arm the SurfaceRouter tactical fast-path so a client can
            // receive host tac.deploy snapshots over the 0x67 envelope rail. Null-guarded + inert until a
            // tactical mission deploys; the deploy/suppress Harmony patches auto-register via PatchAll above.
            Multipleer.Sync.Tactical.TacticalDeploySync.ArmInboundHook();
        }

        public override void OnModDisabled()
        {
            Logger.LogInfo("[Multipleer] OnModDisabled");

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
            MultipleerLog.Shutdown();
        }

        private void Update()
        {
            NetworkEngine.Instance?.Update();
        }
    }
}
