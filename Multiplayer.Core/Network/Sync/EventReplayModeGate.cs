namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Rollout gate for the co-op geoscape event-window "replay mode" (mirrors <see cref="EventMirrorFixGate"/>
    /// / <see cref="ReportMirrorGate"/>). Supersedes the legacy "forced in-place transition for everyone" model
    /// with ONE per-peer decoupled rule for EVERY window kind (multi-choice, single-OK info, close-only —
    /// written once in <c>EventCorrelator</c>):
    ///   • decided &amp;&amp; NOT locally answered/won &amp;&amp; window OPEN (or queued → opens later, armed) → ARM,
    ///     never force: the window stays live and the LOCAL click consumes the buffered terminal (authoritative
    ///     result page in place, or local close for a close-only terminal) at the reader's own pace.
    ///   • Locally answered (single-choice modal-hold) / locally won (picked == decided index) → the existing
    ///     auto in-place transition (winner/answering-peer path, unchanged).
    ///   • Window never opened on this peer (buffered dismiss before raise / 1-window) → legacy jump/close.
    /// Choice count affects ONLY the arm visuals (<c>EventReplayReflection.ApplyReplayButtons</c>): N≥2 →
    /// losers greyed + winner highlighted (native selected state); N≤1 → the lone OK stays live; close-only →
    /// no visual change. Stacked host fast-clicks never skip or force-pop windows: queued raises stay queued
    /// and open ARMED in occId (host emission) order. The host's OWN live window is armed the same way when a
    /// client wins (kills the nonsense result page: text from the host's click, reward from the winner's).
    ///
    /// While <see cref="Enabled"/> is false the correlator's <c>replayMode</c> paths are never taken and the
    /// legacy behavior (forced ShowResultInPlace/CloseDialog/ShowResultPage; host force-transition via
    /// TryHostNativeResolve) is byte-for-byte unchanged.
    /// </summary>
    public static class EventReplayModeGate
    {
        /// <summary>Master switch for co-op event-window replay mode. ON for 2-instance in-game validation 2026-07-08.</summary>
        public static bool Enabled = true;
    }
}
