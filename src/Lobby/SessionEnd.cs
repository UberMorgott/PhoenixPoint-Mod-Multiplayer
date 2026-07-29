using System;
using Base.Core;
using Base.Levels;
using Base.UI.MessageBox;
using Base.View;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Game;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// THE ordered end-of-session seam. Every path that ends a co-op session routes through
    /// <see cref="Begin"/>; nothing hand-rolls the sequence any more (the same four-step block written
    /// out twice in <see cref="HostLeaveHandler"/> was the tell that this seam was missing).
    ///
    /// WHY IT EXISTS (RCA 2026-07-18 — client hangs at the end of loading after the host quits):
    /// <c>PhoenixGame.FinishLevel</c> is ASYNCHRONOUS — it only sets <c>_levelResult</c> and
    /// <c>Pulse()</c> (PhoenixGame.cs:262-266) and returns, so the level actually unloads LATER on the
    /// Timing scheduler, long after <c>TearDown()</c> nulled <c>NetworkEngine.Instance</c>. Every gate
    /// keyed on <c>IsActiveSession</c> is therefore already OFF for the whole teardown window. And the
    /// level switch runs <c>GeoscapeView.CleanupView</c>, which nulls the viewer-faction wiring
    /// (GeoscapeView.cs:1096) BEFORE clearing the view-state stack (:1097) — so the top screen's
    /// <c>ExitState()</c> runs against a half-unwired view. For <c>UIStateEditSoldier</c> that is a
    /// commit-on-exit (:225 <c>UpdateStorage</c> / :226 <c>UpdateSoldierEquipment</c>), which threw an
    /// NRE and killed the coroutine chain <c>SetCurrentCrt → LoadAndSetCurrentCrt → RunGameLevel →
    /// MenuCrt</c>; with <c>MenuCrt</c> dead the main menu never completes and loading freezes forever.
    ///
    /// Vanilla never reaches it: a native quit-to-menu always has the PAUSE state on top, and
    /// <c>StateStack.Clear()</c> calls <c>Exit()</c> on the TOP state only (StateStack.cs:107-121). A
    /// host-leave is the one thing that yanks a level switch from whatever screen the player had open.
    ///
    /// THE FIX IS ORDERING, NOT A GUARD: quiesce the open UI while the level is still ALIVE (step b), so
    /// the later <c>CleanupView</c> clear finds an empty stack and no <c>ExitState</c> ever runs against
    /// an unwired view. Generic over every view state — and every view — so it is not a per-screen fix.
    ///
    /// <see cref="InProgress"/> is NOT a second session predicate. <c>NetworkEngine.IsActiveSession</c>
    /// stays the authority; this is the PHASE that predicate structurally cannot express, because by
    /// then the engine it reads is already gone.
    /// </summary>
    public static class SessionEnd
    {
        // The level the session ended on. Doubles as the phase flag — see InProgress for why this is a
        // Level and not a bool.
        private static Level _endedOn;

        /// <summary>
        /// True from <see cref="Begin"/> until the NEXT level is current. Gates that must stay closed
        /// across the asynchronous level teardown read this, because the engine is null by then and
        /// <c>IsActiveSession</c> cannot serve.
        ///
        /// Level-scoped rather than a plain bool ON PURPOSE: a bool would stay true until the next
        /// <c>NetworkEngine.Initialize()</c>, and a player who drops to the menu and then starts a SOLO
        /// campaign never calls that — the stale flag would keep <c>EquipStorageGate</c> shut forever and
        /// break single-player inventory. <c>Game.LoadAndSetCurrentCrt</c> nulls <c>CurrentLevel</c>
        /// (Game.cs:185) for the entire teardown+load window and only re-assigns it once the next level
        /// is loaded (:205), so "a DIFFERENT level is current" is exactly the end of the phase — and the
        /// flag clears itself with no hook to wire and nothing to leak.
        /// </summary>
        public static bool InProgress
        {
            get
            {
                if (_endedOn == null) return false; // never begun, or the level was destroyed
                var current = GameUtl.CurrentLevel();
                if (current != null && !ReferenceEquals(current, _endedOn)) _endedOn = null;
                return _endedOn != null;
            }
        }

        /// <summary>Re-arm for a fresh session (<c>NetworkEngine.Initialize</c>). Belt only:
        /// <see cref="InProgress"/> already self-clears on the next level; this covers just the
        /// pathological end-and-re-host without a level switch in between.</summary>
        public static void Reset() => _endedOn = null;

        /// <summary>
        /// End the session, in order. <paramref name="notice"/> is the modal text to show first, or null
        /// when the caller already informed the player (the campaign-end flow shows its own outro notice
        /// before it degrades to a menu return).
        /// </summary>
        public static void Begin(string notice)
        {
            _endedOn = GameUtl.CurrentLevel();
            if (MpDiag.On)
                Debug.Log("[MP][sessionend] begin — " + (notice ?? "caller already notified") +
                          " (level=" + (_endedOn == null ? "none" : _endedOn.name) + ")");

            QuiesceOpenUi();
            ShowNotice(notice);
            FinishLevel();
            TearDownSession();

            // DELIBERATELY NOT reset here: HostLeaveHandler's HostLeaveLatch. It is re-armed per session
            // in AttachTo, and SuppressForCampaignEnd pre-consumes it ACROSS the session boundary by
            // design, so the host's post-outro teardown cannot interrupt this client's ending.
            //
            // The rail statics are not reset here either: TearDown → Sync.DetachAllChannels is the ONE
            // existing aggregate for that (DiffEngine / EquipSync / ResearchSync / ManufactureSync /
            // GenericApplier / OpenUiRepaint / SyncApplyScope). A second reset list here would be exactly
            // the duplication this seam exists to delete.
        }

        // ─── (b) quiesce the open UI, while the level is still alive ────────
        //
        // UNIVERSAL — no level-type branch. Geoscape, tactical and home-screen views all declare the SAME
        // private field (GeoscapeView.cs:108, TacticalView.cs:93, HomeScreenView.cs:30) and all derive
        // from GameView, whose [RequireComponent(typeof(Level))] (GameView.cs:14-15) puts the component
        // on the current Level's own GameObject — so one lookup covers every level type, including any
        // future one. The generic argument differs per view (StateStack<GeoscapeViewContext> vs
        // StateStack<TacticalViewContext>), so there is no shared static type to cast to and Clear() is
        // invoked reflectively.
        //
        // Clearing runs the top screen's Exit()/Pop() exactly as closing it normally would — the level,
        // the view wiring and the context are all still intact at this point, which is the entire fix.
        private static void QuiesceOpenUi()
        {
            try
            {
                var level = GameUtl.CurrentLevel();
                var view = level == null ? null : level.GetComponent<GameView>();
                if (view == null)
                {
                    if (MpDiag.On) Debug.Log("[MP][sessionend] quiesce: no GameView on the current level — nothing open");
                    return;
                }

                var stack = AccessTools.Field(view.GetType(), "_statesStack")?.GetValue(view);
                if (stack == null)
                {
                    if (MpDiag.On) Debug.Log("[MP][sessionend] quiesce: " + view.GetType().Name + " has no live state stack");
                    return;
                }

                var clear = AccessTools.Method(stack.GetType(), "Clear");
                if (clear == null)
                {
                    Debug.LogWarning("[MP][sessionend] quiesce: StateStack.Clear unresolved on " + stack.GetType().Name);
                    return;
                }

                // law 8: the native Exit/Pop this fires must not echo an intent back to the departing host.
                using (SyncApplyScope.Enter())
                    clear.Invoke(stack, null);
                if (MpDiag.On) Debug.Log("[MP][sessionend] quiesced " + view.GetType().Name + " state stack");
            }
            catch (Exception e) { Debug.LogError("[MP][sessionend] quiesce failed: " + e); }
        }

        // ─── (c) notice ────────────────────────────────────────────────────

        // Session-fatal, so a modal prompt is acceptable here (works on tactical, geoscape and home).
        private static void ShowNotice(string notice)
        {
            if (notice == null) return;
            try
            {
                GameUtl.GetMessageBox()?.ShowSimplePrompt(
                    notice, MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
            }
            catch (Exception e) { Debug.LogError("[MP][sessionend] notice failed: " + e.Message); }
        }

        // ─── (d) native quit-to-menu chokepoint ────────────────────────────

        // ASYNC (see the class doc): everything after this call runs while the old level is still
        // unloading, which is precisely the window InProgress covers.
        private static void FinishLevel()
        {
            try { GameUtl.GameComponent<PhoenixGame>()?.FinishLevelAndGoToLobby(); }
            catch (Exception e) { Debug.LogError("[MP][sessionend] return-to-menu failed: " + e.Message); }
        }

        // ─── (e) teardown ──────────────────────────────────────────────────

        private static void TearDownSession()
        {
            // FinishLevelAndGoToLobbyTearDownPatch's postfix already ran TearDown inside step (d); this is
            // the belt for a Begin() outside a level, where that patch never fires. TearDown is idempotent
            // and is also what drives the one rail-static reset (Sync.DetachAllChannels).
            try { NetworkEngine.Instance?.TearDown(); }
            catch (Exception e) { Debug.LogError("[MP][sessionend] TearDown failed: " + e.Message); }

            // The network TearDown does NOT reset the UI lobby FSM or clear the chosen save; without this
            // the next host/join inherits a stale ClientLobby state and a phantom _pendingChosenSave.
            try { Multiplayer.UI.MultiplayerUI.Instance?.TeardownLobbyOnSessionEnd(); }
            catch (Exception e) { Debug.LogError("[MP][sessionend] UI teardown failed: " + e.Message); }
        }
    }
}
