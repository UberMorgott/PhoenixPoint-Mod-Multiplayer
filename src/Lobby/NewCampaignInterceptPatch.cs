using System;
using System.Linq;
using Base.Core;
using Base.UI.MessageBox;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.UI;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Home.View.ViewStates;
using PhoenixPoint.Modding;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// P0 — new-campaign co-op bootstrap. Co-op could only start FROM AN EXISTING SAVE (both
    /// <c>HostStartSession</c> variants require a picked <c>SavegameMetaData</c>); this closes the
    /// gap by letting the HOST run the game's OWN new-campaign flow and turning its result into the
    /// EXISTING chunked save transfer — zero new transfer mechanisms.
    ///
    /// NATIVE NEW-GAME FLOW (decompiled, file:line):
    ///   - main menu "NEW GAME" button (one per GameModeDef, UIModuleMainMenuButtons.cs:127-141) →
    ///     OnNewGeoscape → SwitchToState(new UIStateNewGeoscapeGameSettings(gameModeDef), PushOnTop)
    ///     (UIStateMainMenu.cs:113-116);
    ///   - CONFIRM chokepoint: UIStateNewGeoscapeGameSettings.GameSettings_OnConfirm builds
    ///     GeoscapeGameParams (difficulty/tutorial/DLC — host-only choices) and calls
    ///     PhoenixGame.FinishLevel(new PlayNewGameResult{...}) (UIStateNewGeoscapeGameSettings.cs:103-152);
    ///   - BACK: OnSettingsBackClicked → SwitchToPreviousState (UIStateNewGeoscapeGameSettings.cs:154-157).
    ///
    /// BOOTSTRAP: the confirm prefix below ARMS <see cref="SaveTransferCoordinator.ArmNewCampaignBootstrap"/>
    /// (durable gate — fires whether the host reached the screen via the lobby's NEW CAMPAIGN button or
    /// any other native route) and lets the NATIVE campaign creation run. When the new campaign's geoscape
    /// reports itself READY (<see cref="GeoscapeReadyPatch"/> below), the coordinator autosaves
    /// (AutosaveGame — the established P1 join state-capture path) and feeds that autosave into the
    /// EXISTING chunked transfer + 2-phase barrier, so every peer — host included — loads the
    /// byte-identical campaign start. Clients meanwhile wait in the lobby (a system-chat notice tells
    /// them the host is creating the campaign) until the chunks arrive and the proven transfer overlay
    /// takes over. TFTV compatibility: TFTV's extended new-game menu re-invokes the SAME
    /// GameSettings_OnConfirm (TFTVNewGameMenu reflection call), so the prefix fires there too;
    /// re-arming is idempotent.
    /// </summary>
    [HarmonyPatch]
    public static class NewCampaignInterceptPatch
    {
        // ── Prefix: the native new-game CONFIRM — the ONE convergence point ─────────────────────
        // Mirrors the case structure of LoadGameConvergenceGatePatch (same SessionLifecycle predicates):
        //   A) host + active lobby + !started → ARM the bootstrap, let the native creation run;
        //   B) host + session STARTED → a fresh campaign mid-session is exactly an F2 host reload with
        //      a to-be-created save: ARM when the EXISTING HostLoadGuard permits, vanilla when the host
        //      is clientless (nothing to desync), BLOCK when a transfer is in flight;
        //   C) NON-HOST in an active session → BLOCK + notice (only the host may start a campaign).
        //   No engine / no active session → vanilla single-player new game, untouched.
        [HarmonyPatch(typeof(UIStateNewGeoscapeGameSettings), "GameSettings_OnConfirm")]
        [HarmonyPrefix]
        public static bool OnConfirm_Prefix()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null) return true; // no co-op engine → vanilla new game

                bool isHost = engine.IsHost;
                bool active = engine.IsActiveSession;
                var coord = engine.SaveTransfer;

                // Case C — a client starting a campaign would solo-desync it from the live session.
                if (SessionLifecycle.ShouldBlockClientLoad(isHost, active))
                {
                    Debug.LogWarning("[Multiplayer] Client NEW GAME BLOCKED — only the host can start a new campaign in co-op.");
                    GameUtl.GetMessageBox()?.ShowSimplePrompt(
                        "Only the host can start a new campaign in co-op.",
                        MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
                    return false; // skip the native confirm — no campaign is created
                }

                if (!isHost || !active || coord == null) return true; // single-player → vanilla

                // Mirror the native platform early-return (UIStateNewGeoscapeGameSettings.cs:105-108)
                // so the latch is never armed for a confirm the native code itself refuses.
                if (!GameUtl.GameComponent<PhoenixGame>().Platform.CanStartGameOnCurrentPlatform())
                    return true;

                bool sessionStarted = coord.SessionStarted;
                int clients = engine.Session?.ClientCount ?? 0;

                // Clientless mid-session host: nothing to desync → vanilla solo new game (the same
                // allowance as the clientless CONTINUE/Quickload solo load).
                if (SessionLifecycle.HostInSessionHasNoClients(isHost, active, sessionStarted, clients))
                    return true;

                bool armAllowed =
                    // Case A — the lobby bootstrap gate (host + lobby + !started + no transfer).
                    SessionLifecycle.NewCampaignArmGuard(isHost, active, sessionStarted, coord.TransferActive)
                    // Case B — mid-session second fresh campaign = F2 host reload (existing rule).
                    || SessionLifecycle.HostLoadGuard(isHost, active, sessionStarted, clients, coord.TransferActive);
                if (armAllowed)
                {
                    coord.ArmNewCampaignBootstrap();
                    return true; // native confirm runs → FinishLevel(PlayNewGameResult) → campaign creation
                }

                // Host in an active session but neither gate open (a transfer is already in flight):
                // never overlap the one barrier — block rather than desync.
                Debug.LogWarning("[Multiplayer] NEW GAME blocked: a co-op save transfer is already in flight.");
                GameUtl.GetMessageBox()?.ShowSimplePrompt(
                    "A co-op load is already in progress — try again when it finishes.",
                    MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] new-campaign confirm gate failed: " + e.Message);
            }
            return true;
        }

        // ── Prefix: the co-op campaign-creation params ──────────────────────────────────────────
        // CreateSceneBinding(GeoscapeGameParams) builds the tutorial multi-level binding when
        // TutorialEnabled (UIStateNewGeoscapeGameSettings.cs:159-186) — a co-op campaign must reach
        // the GEOSCAPE (the bootstrap fires there, and the tutorial is a solo tactical mission), so
        // flip the flagless NATIVE option on the params instead of hand-rolling any skip. Runs after
        // OnConfirm_Prefix armed the latch (the native confirm body calls CreateSceneBinding).
        [HarmonyPatch(typeof(UIStateNewGeoscapeGameSettings), "CreateSceneBinding")]
        [HarmonyPrefix]
        public static void CreateSceneBinding_Prefix(GeoscapeGameParams gameParams)
        {
            try
            {
                var coord = NetworkEngine.Instance?.SaveTransfer;
                if (coord == null || !coord.NewCampaignPending || gameParams == null) return;
                bool introWasOn = gameParams.PlayIntroCinematic;
                ApplyCoopCampaignParams(gameParams);
                coord.NoteIntroCinematicOwed(introWasOn);
                Debug.Log("[Multiplayer] New-campaign bootstrap: tutorial forced OFF (co-op starts on the " +
                          "geoscape); intro cinematic suppressed at creation and " +
                          (introWasOn ? "re-issued to EVERY peer after the reveal." : "not owed (the game mode does not play one)."));
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] new-campaign params prefix failed: " + e.Message);
            }
        }

        /// <summary>
        /// The two native options a co-op bootstrap campaign must be created with. PURE and public so
        /// RailCheck EXECUTES it rather than describing it (L124).
        ///
        /// TutorialEnabled: the bootstrap fires at the first playable GEOSCAPE frame and the tutorial is a
        /// solo tactical mission ahead of it.
        ///
        /// PlayIntroCinematic: the host creates the campaign while the clients are still in the lobby, so
        /// the host would be the ONLY peer to see the intro — and no client can ever catch up, because the
        /// native condition (GeoLevelController.cs:741-743) requires <c>instanceData == null</c> and every
        /// client loads the campaign FROM the transferred autosave blob. <c>UIStateGeoCutscene</c> is not
        /// <c>IGeoscapeRestorableViewState</c>, so it does not ride the save either. Suppressing it here and
        /// re-issuing it after the barrier reveal (<c>CutsceneMirror.ReplayCampaignIntro</c>) is what makes
        /// it a SHARED moment: one <c>ToCutsceneState</c> call on the host, fanned to every peer by the
        /// 0xBA mirror that already postfixes that exact funnel.
        /// </summary>
        public static void ApplyCoopCampaignParams(GeoscapeGameParams gameParams)
        {
            if (gameParams == null) return;
            gameParams.TutorialEnabled = false;
            gameParams.PlayIntroCinematic = false;
        }

        // ── Postfix: BACK from the new-game settings → back to the lobby ────────────────────────
        // OnSettingsBackClicked is the dedicated back-out path (UIStateNewGeoscapeGameSettings.cs:154).
        // Drop any pending arm (stale-arm safety: a suppressed/refused confirm — e.g. TFTV's warning
        // flow cancelled — must never fire on a later unrelated load) and re-show the lobby overlay
        // the NEW CAMPAIGN button hid. Mid-session (no lobby on screen) only the disarm applies.
        [HarmonyPatch(typeof(UIStateNewGeoscapeGameSettings), "OnSettingsBackClicked")]
        [HarmonyPostfix]
        public static void SettingsBack_Postfix()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                var coord = engine?.SaveTransfer;
                if (engine == null || coord == null || !engine.IsHost || !engine.IsActiveSession) return;
                coord.DisarmNewCampaignBootstrap();
                if (!coord.SessionStarted)
                    MultiplayerUI.Instance?.ShowLobby();
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] new-campaign back postfix failed: " + e.Message);
            }
        }

        /// <summary>
        /// Open the game's OWN native new-game settings screen (lobby NEW CAMPAIGN button): resolve
        /// the default geoscape game mode from the game def (PhoenixGame.Def.GameModeDefs — the same
        /// array the main-menu buttons are built from, UIModuleMainMenuButtons.cs:127) and push
        /// UIStateNewGeoscapeGameSettings exactly like UIStateMainMenu.OnNewGeoscape. Difficulty /
        /// DLC / (forced-off) tutorial stay host-only choices on the native screen. Returns false if
        /// the mode or the live home-screen stack could not be reached (caller re-shows the lobby).
        /// </summary>
        public static bool OpenNativeNewGameScreen()
        {
            try
            {
                var game = GameUtl.GameComponent<PhoenixGame>();
                var mode = game?.Def?.GameModeDefs?.OfType<GeoscapeGameModeDef>().FirstOrDefault();
                if (mode == null)
                {
                    Debug.LogError("[Multiplayer] OpenNativeNewGameScreen: no GeoscapeGameModeDef on PhoenixGame.Def.");
                    return false;
                }
                return SaveLoadInterceptPatch.PushHomeScreenState(new UIStateNewGeoscapeGameSettings(mode));
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] OpenNativeNewGameScreen failed: " + e.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// THE GEOSCAPE-READY SEAM. The bootstrap used to fire at the curtain's Loaded→Playing edge, and that
    /// edge is not readiness — it is the moment GeoLevelController.OnLevelStart merely STARTS LevelCrt
    /// (GeoLevelController.cs:377-379 → :464). At that instant the campaign has no faction sub-managers,
    /// so the game's own AutosaveGame() dies in GeoAlienFaction.RecordExtendedInstanceData (AlienRaidManager
    /// is built later, at :651-653 → GeoFaction.cs:418 → GeoAlienFaction.cs:246-251) and the save that was
    /// supposed to become the co-op start never exists.
    ///
    /// <c>ModManager.OnGeoscapeStart</c> (GeoLevelController.cs:757) is the game's OWN answer to the same
    /// question, and it is the LAST line of LevelCrt's init block before the level parks in its
    /// GameOverCheck loop (:762) — every structure a savegame write walks is already built. It is the
    /// modding API's geoscape-start callback, so it is also the one point that stays correct when another
    /// mod makes that init slower or longer: no frame counts, no polled field, no guessing. Postfix (not
    /// prefix) so any mod's own OnGeoscapeStart work is included in what gets captured.
    ///
    /// Fires on EVERY geoscape start, load and new campaign alike; the coordinator's latch makes it a
    /// no-op unless the host armed a bootstrap.
    /// </summary>
    [HarmonyPatch(typeof(ModManager), "OnGeoscapeStart")]
    public static class GeoscapeReadyPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try { NetworkEngine.Instance?.SaveTransfer?.OnGeoscapeReady(); }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] geoscape-ready seam failed: " + e.Message);
            }
        }
    }
}
