namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Default-OFF rollout gate for the co-op geoscape event-window "replay mode" (mirrors
    /// <see cref="EventMirrorFixGate"/> / <see cref="ReportMirrorGate"/>). Supersedes the legacy
    /// "forced in-place transition for everyone" model with a per-peer decoupled UI:
    ///   • A peer whose OPEN choice window did NOT win the occurrence is NOT force-transitioned to the
    ///     result page when the decided signal arrives. Instead its non-winning buttons grey INSTANTLY and
    ///     the winning button is highlighted (native selected state) — the only clickable one. Clicking it
    ///     shows the authoritative result page in place, at the reader's own pace.
    ///   • The winner (its locally-picked choice == the decided winning choice) keeps the current auto
    ///     in-place transition.
    ///   • The host's OWN live window is armed the same way when a client wins (kills the nonsense result
    ///     page: text from the host's click, reward from the winner's).
    ///
    /// While <see cref="Enabled"/> is false the correlator's <c>replayMode</c> paths are never taken and the
    /// legacy behavior (ShowResultInPlace for every open dialog; host force-transition via
    /// TryHostNativeResolve) is byte-for-byte unchanged. Flip ON after the 2-instance in-game gate.
    /// </summary>
    public static class EventReplayModeGate
    {
        /// <summary>Master switch for co-op event-window replay mode. Default OFF (ship ON after in-game validation).</summary>
        public static bool Enabled = false;
    }
}
