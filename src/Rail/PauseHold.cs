using PhoenixPoint.Geoscape.View;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// WHAT IS LEFT OF THE PAUSE HOLD: NOTHING, ON PURPOSE. A blocking window is a ONE-SHOT PAUSE and
    /// THE GAME ITSELF ISSUES IT. <c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>:58-73 calls
    /// <c>_view.RequestGamePause()</c> for any dequeued request with <c>PauseGame</c> set, and that is
    /// <c>SetGamePauseState(paused: true)</c> one frame later (GeoscapeView.cs:1269 → RequestPauseCrt:1290-1295).
    /// <c>SetGamePauseState</c> is ALREADY a <see cref="TimeSync"/> capture seam, so the peer whose window
    /// opened pauses locally AND the pause reaches every other peer — with no seam, no op and no state of
    /// our own. Kept from the previous fix because both halves are load-bearing: the mirrored raisers push
    /// the game's own <c>PauseGame = true</c>, and a client's PAUSE runs LOCALLY instead of being blocked
    /// (an already-paused host re-writing <c>Timing.Paused</c> is swallowed by the change-gated setter,
    /// Timing.cs:112 — no event, no diff, no delta, so nothing would ever arrive to correct the client).
    ///
    /// THE HOLD SET AND ITS RESUME VETO ARE DELETED (live 3-instance run 2026-08-04). Holding the shared
    /// clock until EVERY peer had dismissed its window meant one peer's open popup — or one peer AFK inside
    /// a cutscene — froze the campaign for everybody: the play button did nothing and no aircraft could be
    /// flown. It broke the project's first-class rule: AT ANY MOMENT ANY PLAYER MUST BE ABLE TO PLAY
    /// EVERYTHING — with 49 of 50 players AFK the one active player still plays the whole game, and that
    /// includes the host being the AFK one. So PAUSE is a COURTESY EDGE (nothing runs unattended while
    /// people read) and RESUME IS UNCONDITIONAL, from any peer, at any time: whoever acts first wins, the
    /// rail's standing arrival-order rule, no arbiter and nobody held hostage.
    ///
    /// The class survives only for the queued-window question below — which is about REPAINTS, not about
    /// time at all. The name stays because <c>OpenUiRepaint</c> calls it by that name.
    /// </summary>
    internal static class PauseHold
    {
        /// <summary>Is <paramref name="state"/> the window the game's own queue is currently showing?
        /// Used by <see cref="OpenUiRepaint"/> to keep its Exit+Enter fallback off one-shot presentations
        /// (a cinematic re-entered is a cinematic replayed).</summary>
        internal static bool IsCurrentQueuedWindow(GeoscapeView view, object state)
        {
            if (view == null || state == null) return false;
            var q = WindowQueueSync.SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            if (q == null || WindowQueueSync.CurrentRequestField == null) return false; // GetValue(null) throws
            var req = WindowQueueSync.CurrentRequestField.GetValue(q) as GeoscapeViewStateSwitchRequest;
            return req != null && ReferenceEquals(req.State, state);
        }
    }
}
