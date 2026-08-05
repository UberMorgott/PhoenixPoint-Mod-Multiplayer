using System.Collections.Generic;
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

        /// <summary>Patch classes that did NOT bind this run, "&lt;class&gt;: &lt;reason&gt;". Empty is the
        /// only good answer — anything in here is a feature silently missing from the running game.</summary>
        public static readonly List<string> UnboundPatches = new List<string>();

        public override void OnModEnabled()
        {
            Instance = this;
            MultiplayerLog.Init(); // earliest: capture startup lines into the dedicated mod log.
            Logger.LogInfo("[Multiplayer] OnModEnabled");

            try
            {
                var harmony = (HarmonyLib.Harmony)HarmonyInstance;
                PatchEveryClass(harmony);

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
                Logger.LogError("[Multiplayer] mod init failed AFTER patching: " + e, e);
            }

            _ui = ModGO.AddComponent<MultiplayerUI>();
            Logger.LogInfo("[Multiplayer] UI initialized");

            // Parity auto-apply: wire the teardown restore hook (delegate field — NetworkEngine must not
            // reference ParityConfigSync's game types directly, same JIT-safety rule as SteamLobbyCleanup).
            NetworkEngine.ParityConfigRestore = ParityConfigSync.RestoreOriginals;
        }

        /// <summary>What <c>Harmony.PatchAll(assembly)</c> is, with a try inside the loop. Its whole body is
        /// <c>GetTypesFromAssembly(asm).Do(t =&gt; CreateClassProcessor(t).Patch())</c> — ONE unguarded loop, so
        /// the first class that throws abandons every class after it and the mod runs on half-patched. On
        /// 2026-08-05 that cost the entire mod: one tactical transpiler picked up a coroutine body Harmony
        /// cannot re-emit, and the main-menu button, both late binders and the whole rail went with it, under
        /// a single LogWarning that printed <c>e.Message</c> and dropped the InnerException holding the cause.
        /// Now a broken patch costs its own class, says so as an error with the full exception, and leaves a
        /// list of what is missing. Half a mod beats no mod — but only if it admits which half. (law L125)</summary>
        private void PatchEveryClass(HarmonyLib.Harmony harmony)
        {
            UnboundPatches.Clear();
            foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception e)
                {
                    UnboundPatches.Add(type.FullName + ": " + e.Message);
                    Logger.LogError("[Multiplayer] PATCH CLASS DID NOT BIND: " + type.FullName + " — " + e, e);
                }
            }
            if (UnboundPatches.Count == 0)
                Logger.LogInfo("[Multiplayer] PatchAll done — every patch class bound");
            else
                Logger.LogError("[Multiplayer] PatchAll done with " + UnboundPatches.Count + " patch class(es) " +
                                "NOT BOUND; those features are ABSENT this session: " +
                                string.Join(" | ", UnboundPatches.ToArray()));
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
