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
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Multipleer] PatchAll failed: " + e.Message);
            }

            _ui = ModGO.AddComponent<MultiplayerUI>();
            Logger.LogInfo("[Multipleer] UI initialized");
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
