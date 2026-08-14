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

        /// <summary>The in-game mod settings (see <see cref="MultiplayerConfig"/>). The game builds it and
        /// assigns it before <see cref="OnModEnabled"/> (<c>ModEntry.cs:264</c>); null only if the type
        /// ever fails to instantiate, so callers null-check.</summary>
        public new MultiplayerConfig Config => base.Config as MultiplayerConfig;

        private MultiplayerUI _ui;
        private PingMarkers _pings;
        private LoadOverlayController _loadOverlay;

        /// <summary>Patch classes that did NOT bind this run, "&lt;class&gt;: &lt;reason&gt;". Empty is the
        /// only good answer — anything in here is a feature silently missing from the running game.</summary>
        public static readonly List<string> UnboundPatches = new List<string>();

        public override void OnModEnabled()
        {
            Instance = this;
            MultiplayerLog.Init(); // earliest: capture startup lines into the dedicated mod log.
            MpLog.Log("[Multiplayer] OnModEnabled");

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
                MpLog.LogError("[Multiplayer] mod init failed AFTER patching: " + e, e);
            }

            _ui = ModGO.AddComponent<MultiplayerUI>();
            MpLog.Log("[Multiplayer] UI initialized");

            // Ping markers: its own pump (polls the hotkey, expires markers, draws the off-screen arrow)
            // and the arrival seam NetworkEngine relays into. Presentation only — see PingMarkers.
            _pings = ModGO.AddComponent<PingMarkers>();
            NetworkEngine.PingMarkerShow = PingMarkers.Show;

            // Co-op load overlay: one named bar per OTHER peer while anyone is still loading. It is a
            // MonoBehaviour whose OWN Update drives visibility (LoadOverlayVisibility.ShouldShow) and
            // repaint, so creating it is the whole wiring — there is no show call site by design. It
            // never existed at runtime before: the only construction site was a MultiplayerUI helper
            // whose caller was dropped in the v2 port, so the component sat dead in the tree while the
            // host looked at a frozen native bar with no idea who it was waiting for.
            _loadOverlay = ModGO.AddComponent<LoadOverlayController>();

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
                    MpLog.LogError("[Multiplayer] PATCH CLASS DID NOT BIND: " + type.FullName + " — " + e, e);
                }
            }
            if (UnboundPatches.Count == 0)
                MpLog.Log("[Multiplayer] PatchAll done — every patch class bound");
            else
                MpLog.LogError("[Multiplayer] PatchAll done with " + UnboundPatches.Count + " patch class(es) " +
                                "NOT BOUND; those features are ABSENT this session: " +
                                string.Join(" | ", UnboundPatches.ToArray()));
        }

        public override void OnModDisabled()
        {
            MpLog.Log("[Multiplayer] OnModDisabled");

            if (NetworkEngine.Instance != null)
            {
                NetworkEngine.Instance.Shutdown();
            }

            if (_ui != null)
            {
                Object.Destroy(_ui);
                _ui = null;
            }

            NetworkEngine.PingMarkerShow = null;
            if (_pings != null)
            {
                Object.Destroy(_pings);
                _pings = null;
            }

            if (_loadOverlay != null)
            {
                Object.Destroy(_loadOverlay);
                _loadOverlay = null;
            }

            Instance = null;
            MultiplayerLog.Shutdown();
        }

        // No Update() here: ModMain is a plain abstract class (not a MonoBehaviour), so Unity never
        // pumps it. The engine is pumped by MultiplayerUI.Update (the component added above).
    }
}
