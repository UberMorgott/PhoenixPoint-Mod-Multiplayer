using System;
using System.Reflection;
using Base.Core;
using Base.Serialization;
using Base.UI;
using Base.UI.MessageBox;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.UI;
using PhoenixPoint.Common.Saves;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Home.View;
using PhoenixPoint.Home.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// Co-op "Choose save" intercept. Instead of hand-building a save-row list (which kept
    /// rendering empty), the host opens the game's OWN native Load screen and we INTERCEPT the
    /// load-on-click so it does NOT load the campaign — it captures the chosen SavegameMetaData
    /// and hands it back to the lobby as the co-op base save.
    ///
    /// NATIVE LOAD FLOW (decompiled, file:line):
    ///   - Open: UIStateMainMenu.OnLoadGameButtonClicked() → SwitchToState(new UIStateHomeLoadGame(),
    ///     PushOnTop)  (UIStateMainMenu.cs:134-137). That handler is subscribed to the menu's
    ///     LoadGameButton.PointerClicked (UIStateMainMenu.cs:46-47), so firing that event opens the
    ///     screen exactly like the player clicking "LOAD GAME".
    ///   - Build: UIStateHomeLoadGame.EnterState activates LoadGameModule (a UIModuleSaveGame),
    ///     PushState → InitLoadMode(SwitchToPreviousState, showConfirmationForLoad:false)
    ///     (UIStateHomeLoadGame.cs:12-28) → InitializeSaveGames builds rows via
    ///     UIModuleSaveGameSlot.InitUsedSaveSlot(..., onLoad: OnLoadGamePressed, ...)
    ///     (UIModuleSaveGame.cs:276,283,294).
    ///   - LOAD-ON-CLICK (what we avoid): slot click → UIModuleSaveGameSlot.OnLoadGamePressedCrt →
    ///     OnLoadPressed.Invoke(_data) (UIModuleSaveGameSlot.cs:273) → wired to
    ///     UIModuleSaveGame.OnLoadGamePressed(PPSavegameMetaData) (UIModuleSaveGame.cs:166-171) →
    ///     SaveManager.LoadGame(meta).  >>> We Prefix UIModuleSaveGame.OnLoadGamePressed and, while
    ///     armed, capture the meta + close the screen + return false (skip the real load). <<<
    ///   - Close (cancel OR after pick): UIStateHomeLoadGame.ExitState (UIStateHomeLoadGame.cs:30-34)
    ///     runs in BOTH cases, so a Postfix on it re-shows the lobby and (if a meta was captured)
    ///     hands it to the lobby's chosen-save handler.
    ///
    /// FEASIBILITY: pushing UIStateHomeLoadGame is a normal home-screen state push; it does NOT
    /// touch the network session (engine is independent of the UI) and does NOT destroy the mod's
    /// own ModGO canvases. The only conflict is visual: the lobby's Show() disables every native
    /// root canvas (NativeWidgetFactory.HideMenuChrome), so we MUST restore chrome before opening
    /// the native screen and re-hide it (via lobby Show) afterwards — done by MultiplayerUI.
    /// </summary>
    [HarmonyPatch]
    public static class SaveLoadInterceptPatch
    {
        // Co-op pick mode. While armed, the native load-on-click is intercepted instead of loading.
        private static bool _armed;
        private static Action<SavegameMetaData> _onPicked;
        private static SavegameMetaData _pickedMeta;
        // F2: deliver the picked meta to the callback DIRECTLY inside the OnLoadGamePressed prefix,
        // instead of waiting for the UIStateHomeLoadGame.ExitState postfix. That postfix is HOME-screen
        // only and never fires from the IN-GAME pause-menu Load submenu (UIModulePauseScreen.
        // LoadGameModule), so a mid-session host load would otherwise capture the meta but never run.
        // The lobby/home flow leaves this FALSE → delivery stays on the proven ExitState path (which
        // also re-shows the lobby); the in-game F2 path arms with this TRUE for prefix delivery.
        private static bool _deliverInPrefix;

        public static bool IsArmed => _armed;

        /// <summary>Arm co-op pick mode: the next native save load is captured, not executed.
        /// Delivery runs on the home-screen UIStateHomeLoadGame.ExitState postfix (lobby flow).</summary>
        public static void Arm(Action<SavegameMetaData> onPicked)
        {
            _armed = true;
            _pickedMeta = null;
            _onPicked = onPicked;
            _deliverInPrefix = false;
        }

        /// <summary>
        /// F2: arm co-op pick mode for the IN-GAME pause-menu Load submenu. Identical capture, but the
        /// picked meta is delivered to <paramref name="onPicked"/> straight from the OnLoadGamePressed
        /// prefix (the home-only ExitState postfix never fires in-game). Self-disarms on delivery.
        /// </summary>
        public static void ArmInPrefix(Action<SavegameMetaData> onPicked)
        {
            _armed = true;
            _pickedMeta = null;
            _onPicked = onPicked;
            _deliverInPrefix = true;
        }

        /// <summary>Disarm and forget any captured meta + callback.</summary>
        public static void Disarm()
        {
            _armed = false;
            _pickedMeta = null;
            _onPicked = null;
            _deliverInPrefix = false;
        }

        /// <summary>
        /// Open the game's native Load screen by driving the home-screen state stack straight to
        /// UIStateHomeLoadGame — exactly what UIStateMainMenu.OnLoadGameButtonClicked does
        /// (SwitchToState(new UIStateHomeLoadGame(), PushOnTop), UIStateMainMenu.cs:134-137).
        ///
        /// WHY NOT the LoadGameButton.PointerClicked event: that handler is (un)subscribed per
        /// UIStateMainMenu Enter/Exit (UIStateMainMenu.cs:47/72). Pushing the load state on top runs
        /// MainMenu.ExitState (StateStack.SwitchToState:79), which REMOVES the subscription; by the
        /// 2nd Choose-Save click the cached button's PointerClicked is stale/null, so firing it was
        /// silently a no-op (and the lobby then fell to the wrong custom window). Pushing the state
        /// directly is subscription-independent and works on EVERY click. The pick is still captured
        /// by our Prefix on UIModuleSaveGame.OnLoadGamePressed regardless of how the screen opened.
        ///
        /// Returns false if the live HomeScreenView / its state stack could not be reached (caller
        /// surfaces a warning — there is NO custom-window fallback).
        /// </summary>
        public static bool OpenNativeLoadScreen() => PushHomeScreenState(new UIStateHomeLoadGame());

        /// <summary>
        /// Shared home-screen state push (also used by NewCampaignInterceptPatch to open the native
        /// new-game settings screen): find the LIVE HomeScreenView's private state stack and push the
        /// given state on top — exactly what the native menu button handlers do. Returns false if the
        /// live view / stack could not be reached (caller surfaces a warning; no custom fallback).
        /// </summary>
        internal static bool PushHomeScreenState(object state)
        {
            try
            {
                // _statesStack is private (StateStack<HomeScreenViewContext>); reach it by reflection.
                var stackField = AccessTools.Field(typeof(HomeScreenView), "_statesStack");
                if (stackField == null)
                {
                    Debug.LogError("[Multiplayer] PushHomeScreenState: HomeScreenView._statesStack field not found.");
                    return false;
                }

                // Find the LIVE home-screen view — the one whose state stack is already initialized
                // (FindObjectsOfTypeAll can also surface uninitialized template instances; pick the
                // one with a non-null _statesStack).
                object stack = null;
                foreach (var v in Resources.FindObjectsOfTypeAll<HomeScreenView>())
                {
                    if (v == null) continue;
                    var s = stackField.GetValue(v);
                    if (s != null) { stack = s; break; }
                }
                if (stack == null)
                {
                    Debug.LogError("[Multiplayer] PushHomeScreenState: no live HomeScreenView with an initialized state stack.");
                    return false;
                }

                // Push the state on top exactly like the native menu handlers (e.g. UIStateMainMenu.
                // OnLoadGameButtonClicked / OnNewGeoscape, UIStateMainMenu.cs:113-116,134-137).
                var switchToState = AccessTools.Method(stack.GetType(), "SwitchToState");
                if (switchToState == null)
                {
                    Debug.LogError("[Multiplayer] PushHomeScreenState: StateStack.SwitchToState not found.");
                    return false;
                }

                switchToState.Invoke(stack, new object[] { state, StateStackAction.PushOnTop });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] PushHomeScreenState failed: " + e.Message);
                return false;
            }
        }

        // ── Prefix: intercept the actual load on a save click ──────────────────────────────────
        // UIModuleSaveGame.OnLoadGamePressed(PPSavegameMetaData) is the method that calls
        // SaveManager.LoadGame (UIModuleSaveGame.cs:166-171). While armed we capture the meta,
        // close the native screen, and skip the load.
        [HarmonyPatch(typeof(UIModuleSaveGame), "OnLoadGamePressed")]
        [HarmonyPrefix]
        public static bool OnLoadGamePressed_Prefix(UIModuleSaveGame __instance, PPSavegameMetaData ppSaveGameChosen)
        {
            // DURABLE lobby gate (independent of the fragile static _armed flag). A host with an ACTIVE
            // co-op lobby whose session has NOT started must NEVER trigger a native campaign load: the
            // host can reach a native Load screen un-armed (the lobby re-makes native menu buttons
            // clickable), and gating only on _armed then let the real load run. Re-gate on DURABLE
            // session state instead: capture the pick as the lobby's chosen save + return to the lobby,
            // skipping the load. The mid-session F2 path (sessionStarted==true) is EXCLUDED by the
            // predicate and still loads immediately; single-player (no active session) is also excluded.
            try
            {
                var engine = NetworkEngine.Instance;
                bool lobbyActive = engine?.IsActiveSession ?? false;
                bool sessionStarted = engine?.SaveTransfer?.SessionStarted ?? false;
                if (SessionLifecycle.ShouldCaptureAsLobbyPick(engine?.IsHost ?? false, lobbyActive, sessionStarted))
                {
                    var picked = ppSaveGameChosen as SavegameMetaData;

                    // Close the native Load screen the same way its own close button does (CloseModule is
                    // private → reflection), so the host is NOT left stuck on the load screen.
                    var closeModule = AccessTools.Method(typeof(UIModuleSaveGame), "CloseModule");
                    closeModule?.Invoke(__instance, null);

                    // Disarm FIRST so the home-screen ExitState postfix (which early-returns when !_armed)
                    // can NOT also deliver — this durable path is the single delivery, armed or not.
                    Disarm();

                    // Self-contained route to the chosen-save path (works even when _armed==false /
                    // _onPicked==null): records + broadcasts the lobby save (label only, NO load) and
                    // re-shows the lobby. The campaign load happens ONLY via host PLAY → CommitStart.
                    if (picked != null) MultiplayerUI.Instance?.OnLobbyLoadPickCaptured(picked);

                    return false; // skip SaveManager.LoadGame
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] lobby-pick gate failed: " + e.Message);
            }

            if (!_armed) return true; // normal single-player load

            try
            {
                _pickedMeta = ppSaveGameChosen;

                // Close the native Load screen the same way its own close button does
                // (UIModuleSaveGame.CloseModule:145 → Close == SwitchToPreviousState). On the home flow
                // that triggers UIStateHomeLoadGame.ExitState, where our Postfix re-shows the lobby +
                // delivers the captured meta. In-game it invokes the pause screen's own OnExitEvent
                // (hides the Load submenu, re-shows the pause panel). Use reflection: CloseModule is private.
                var close = AccessTools.Method(typeof(UIModuleSaveGame), "CloseModule");
                close?.Invoke(__instance, null);

                // F2 (in-game): the home-only ExitState postfix will NOT fire here, so deliver the
                // captured meta to the armed callback NOW and self-disarm. The lobby/home flow leaves
                // _deliverInPrefix=false → delivery stays on the ExitState postfix (unchanged).
                if (_deliverInPrefix)
                {
                    var meta = _pickedMeta;
                    var cb = _onPicked;
                    Disarm();
                    if (meta != null) cb?.Invoke(meta);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] OnLoadGamePressed intercept failed: " + e.Message);
            }

            return false; // skip SaveManager.LoadGame — we only wanted the metadata
        }

        // ── Postfix: UIModuleSaveGame closed → cancel-disarm for the IN-GAME (prefix) arm ───────
        // F2 fix: if the host opens the pause-menu Load submenu and CANCELS without picking,
        // OnLoadGamePressed never fires, so an in-game (prefix-delivery) arm would otherwise leak and
        // intercept the NEXT unrelated save-slot click. CloseModule runs on every close — including
        // cancel — so disarm here when armed-in-prefix AND no meta was captured. On the PICK path the
        // prefix sets _pickedMeta BEFORE it calls CloseModule, so _pickedMeta != null here → we do NOT
        // disarm (the prefix's own delivery block disarms after invoking the callback). The home/lobby
        // arm (_deliverInPrefix == false) is untouched — its delivery + disarm stay on ExitState.
        [HarmonyPatch(typeof(UIModuleSaveGame), "CloseModule")]
        [HarmonyPostfix]
        public static void CloseModule_Postfix()
        {
            if (_armed && _deliverInPrefix && _pickedMeta == null)
            {
                Disarm();
                Debug.Log("[Multiplayer] F2: in-game load submenu cancelled — intercept disarmed.");
            }
        }

        // ── Postfix: native Load screen closed (pick OR cancel) → back to the lobby ─────────────
        // UIStateHomeLoadGame.ExitState runs on every close (UIStateHomeLoadGame.cs:30). If armed,
        // re-show the lobby; if a meta was captured, deliver it to the lobby's chosen-save handler.
        [HarmonyPatch(typeof(UIStateHomeLoadGame), "ExitState")]
        [HarmonyPostfix]
        public static void LoadScreenExitState_Postfix()
        {
            if (!_armed) return;

            var meta = _pickedMeta;
            var cb = _onPicked;
            Disarm();

            try
            {
                // Re-show the lobby (re-hides the native chrome we restored on open) and deliver the
                // chosen save (null on cancel → just re-open the lobby with no selection change).
                MultiplayerUI.Instance?.OnNativeSaveScreenClosed(meta, cb);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] LoadScreen exit handling failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// F2 — host loads ANY save MID-SESSION (tactical OR geoscape) → all clients reload into it.
    ///
    /// The host opens the native in-game pause menu and clicks LOAD themselves. That routes through
    /// <c>UIModulePauseScreen.OnLoadPressed()</c> → <c>LoadGameModule.InitLoadMode(...)</c>
    /// (UIModulePauseScreen.cs:158-164). We postfix OnLoadPressed and — only when the live engine is
    /// the host in an active, already-started session with ≥1 client and no transfer in flight — ARM
    /// the existing save-pick intercept in PREFIX-delivery mode. The host's next save-slot click is
    /// then captured by <see cref="SaveLoadInterceptPatch.OnLoadGamePressed_Prefix"/> (skips the local
    /// SaveManager.LoadGame) and the picked meta is routed to <see cref="MultiplayerUI.OnInGameLoadPicked"/>,
    /// which re-runs the proven <c>SaveTransferCoordinator.HostStartSession(meta)</c> chunked transfer +
    /// 2-phase barrier so every client reloads into the chosen save.
    ///
    /// We do NOT call OpenNativeLoadScreen (home-only) — the host opens the pause Load screen natively;
    /// we only intercept the pick. If the guard fails (not host, lobby, alone, or a transfer is already
    /// running) we DISARM, so the host's load behaves as a normal single-player load (no interception).
    /// </summary>
    [HarmonyPatch(typeof(UIModulePauseScreen), "OnLoadPressed")]
    public static class InGameLoadArmPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (MultiplayerUI.Instance != null && MultiplayerUI.Instance.TryArmInGameHostLoad())
                    return;

                // Guard closed (not host / lobby / alone / transfer running): make sure no stale arm
                // leaks into this single-player-style load.
                SaveLoadInterceptPatch.Disarm();
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] InGameLoadArm postfix failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// CONTINUE / Quickload intercept hole — the durable backstop.
    ///
    /// The UI save-pick intercept (<see cref="SaveLoadInterceptPatch.OnLoadGamePressed_Prefix"/>) only
    /// covers the native Load SCREEN. Two solo-load doors bypass it entirely and converge instead at
    /// <c>PhoenixSaveManager.LoadGame(PPSavegameMetaData)</c> (decompiled PhoenixSaveManager.cs:609-621 →
    /// <c>_game.FinishLevelAndLoadGame</c>):
    ///   • main-menu CONTINUE — <c>UIModuleMainMenuButtons.OnContinueGameButtonClicked</c> → TryLoadSave →
    ///     <c>OnLoadSaveConfirmed</c> → <c>SaveManager.LoadGame(saveData)</c> (UIModuleMainMenuButtons.cs:236-252).
    ///   • Quickload — menu <c>OnQuickloadButtonClicked</c> → OnLoadSaveConfirmed → LoadGame; AND
    ///     <c>PhoenixSaveManager.QuickLoad</c> → <c>LoadGame(QuickSavegame)</c> (PhoenixSaveManager.cs:586-607),
    ///     reached in-geoscape via <c>GeoscapeView.QuickLoadGame</c> → QuickLoad (GeoscapeView.cs:1159-1176).
    /// All of these end at the SAME <c>LoadGame</c>, so a single Prefix there closes both holes.
    ///
    /// WHY THIS DOES NOT DOUBLE-GATE THE LEGIT CO-OP ENTRY: the co-op session entry does NOT call
    /// <c>LoadGame</c> at all — <see cref="Multiplayer.Network.SaveTransferCoordinator"/> reimplements the
    /// load in-memory (ApplyPrepareLoadGameState) and enters the level by calling
    /// <c>PhoenixGame.FinishLevel(_pendingResult)</c> DIRECTLY (EnterLevel). The barrier-gated FinishLevel
    /// seam (<see cref="FinishLevelBarrierPatch"/>) still solely owns that entry; this prefix only ever
    /// catches a VANILLA solo-load door. The UI prefix, when it captures, returns false so LoadGame is
    /// never reached on that path → no double handling.
    ///
    /// GATE (reuses the same <see cref="Multiplayer.Network.SessionLifecycle"/> predicates as the UI path):
    ///   • host + active lobby + !started → treat exactly like a lobby save pick: capture as the lobby's
    ///     chosen save (label only, NO load) and re-show the lobby; skip the solo load.
    ///   • host + session STARTED (mid-session) → reroute to the host-authoritative in-session reload
    ///     (the F2 / HostStartSessionInGame transfer) via <see cref="MultiplayerUI.OnInGameLoadPicked"/>
    ///     when <see cref="Multiplayer.Network.SessionLifecycle.HostLoadGuard"/> permits; otherwise BLOCK
    ///     the solo load and log (a host loading a different save solo would desync the clients). Either
    ///     way the original solo <c>LoadGame</c> is skipped.
    ///   • not host / no active session → pass through (vanilla single-player CONTINUE/Quickload unchanged).
    /// </summary>
    [HarmonyPatch(typeof(PhoenixSaveManager), "LoadGame")]
    public static class LoadGameConvergenceGatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PPSavegameMetaData metaData)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null) return true; // no co-op engine → vanilla load

                bool isHost = engine.IsHost;
                bool lobbyActive = engine.IsActiveSession;
                var coord = engine.SaveTransfer;
                bool sessionStarted = coord?.SessionStarted ?? false;
                var picked = metaData as SavegameMetaData; // PPSavegameMetaData : SavegameMetaData

                // Case A — host + active lobby + not started: a CONTINUE/Quickload here is the old
                // immediate-solo-load regression via an unpatched door. Treat it like a lobby save pick.
                if (SessionLifecycle.ShouldCaptureAsLobbyPick(isHost, lobbyActive, sessionStarted))
                {
                    if (picked != null) MultiplayerUI.Instance?.OnLobbyLoadPickCaptured(picked);
                    Debug.Log("[Multiplayer] CONTINUE/Quickload at lobby captured as lobby save pick (no solo load).");
                    return false; // skip SaveManager.LoadGame
                }

                // Case B — host mid-session: a solo load here would silently desync the clients.
                if (SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost, lobbyActive, sessionStarted))
                {
                    int connectedClients = engine.Session?.ClientCount ?? 0;

                    // Clientless host: every peer has left, so there is nothing to desync. Let the vanilla
                    // solo load proceed (CONTINUE/Quickload) — the in-game co-op reload reroute below is
                    // itself >=1-client gated, so a lone host would otherwise be locked out of loading.
                    if (SessionLifecycle.HostInSessionHasNoClients(isHost, lobbyActive, sessionStarted, connectedClients))
                    {
                        Debug.Log("[Multiplayer] Host mid-session load with no connected clients — allowing vanilla solo load.");
                        return true; // run SaveManager.LoadGame (no peers to desync)
                    }

                    bool canReroute = SessionLifecycle.HostLoadGuard(
                        isHost: isHost,
                        isActiveSession: lobbyActive,
                        sessionStarted: sessionStarted,
                        connectedClientCount: connectedClients,
                        transferActive: coord?.TransferActive ?? false);

                    if (canReroute && picked != null)
                    {
                        // Reroute into the proven F2 host-authoritative reload (chunked transfer + barrier):
                        // every client reloads into the chosen save. OnInGameLoadPicked re-validates the guard.
                        Debug.Log("[Multiplayer] Host mid-session CONTINUE/Quickload rerouted to co-op in-session reload.");
                        MultiplayerUI.Instance?.OnInGameLoadPicked(picked);
                    }
                    else
                    {
                        // Clean reroute not available (>=1 client connected but a transfer is already
                        // running, or null meta): block the solo load rather than desync the clients. The
                        // host should use the in-game co-op reload (pause-menu LOAD) instead. (The zero-
                        // client case was already handled above by allowing the vanilla solo load.)
                        Debug.LogWarning("[Multiplayer] Host mid-session CONTINUE/Quickload BLOCKED — would desync clients. " +
                            "Use the in-game co-op reload (pause-menu LOAD) instead.");
                    }
                    return false; // skip SaveManager.LoadGame either way (never solo-load mid-session)
                }

                // Case C — NON-HOST in an active session: a CONTINUE / pause-menu LOAD / Quickload here
                // would solo-load a save on the client while it stays wired into the live co-op session →
                // desync. Host-authoritative: ONLY the host may load. Block the solo load and tell the
                // user to ask the host. (Single-player on a non-host machine — no active session — is not
                // gated and passes through below.)
                if (SessionLifecycle.ShouldBlockClientLoad(isHost, lobbyActive))
                {
                    Debug.LogWarning("[Multiplayer] Client CONTINUE/LOAD/Quickload BLOCKED — only the host can load in co-op.");
                    GameUtl.GetMessageBox()?.ShowSimplePrompt(
                        "Only the host can load the game in co-op.",
                        MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
                    return false; // skip SaveManager.LoadGame — client must never solo-load mid-session
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] LoadGame convergence gate failed: " + e.Message);
            }

            return true; // not host / no active session → vanilla single-player load
        }
    }
}
