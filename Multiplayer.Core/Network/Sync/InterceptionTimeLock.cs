namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// HOST-side authoritative "an air-combat interception is in progress" window. While it is open the shared
    /// geoscape clock is HARD-LOCKED for everyone (host + every client, any permission): the host is off
    /// resolving the interception minigame (<c>GeoLevelController.StartInterceptionCrt</c>, driven on the
    /// geoscape Timing) and the shared clock must not advance under anyone's control. This is DISTINCT from
    /// <see cref="HostBlockingPromptGate"/> — that gate rejects ALL geoscape intents while a mandatory brief is
    /// modal; interception deliberately does NOT freeze intents (clients keep a fully usable geoscape and relay
    /// research/manufacturing/roster edits throughout), it locks ONLY time control.
    ///
    /// Lifecycle (host-only; the flag never toggles on a client — clients learn the lock from the authoritative
    /// TimeState anchor's <c>Locked</c> bit and grey their time widget):
    ///   • OPEN   — the interception brief (ModalType.InterceptionBrief 32) opens
    ///              (<c>ReportModalMirror.HostBroadcast</c>).
    ///   • CLOSE  — the interception is fully resolved. Two exit paths:
    ///              (a) DISENGAGE: the brief resolves to a non-Confirm result (no minigame launched)
    ///                  → <c>BlockingModalReleasePatch</c> (native ModalResultCallback).
    ///              (b) INTERCEPT / AUTO-RESOLVE: the minigame runs and its OUTCOME modal
    ///                  (ModalType.InterceptionOutcome 33) closes → <c>BlockingModalHideReleasePatch</c>
    ///                  (native UIModuleModal.Hide). Co-op is always non-tutorial, and the non-tutorial
    ///                  CompleteCurrentInterceptionCrt ALWAYS shows the outcome, so this is reliable.
    ///   • RESET  — session/geoscape boundary belt (<c>SyncEngine.ResetEventMirror</c>): a save-transfer/reload
    ///              must never inherit a stale lock (its interception is gone with the old geoscape).
    /// PURE (Unity-free) — set from the game-facing Harmony patches (like <see cref="HostBlockingPromptGate"/>),
    /// read by the host time-request gate + anchor stamp; unit-tested directly.
    /// </summary>
    public static class InterceptionTimeLock
    {
        private static bool _active;

        /// <summary>True iff an interception window is open on the host (time control is locked for everyone).</summary>
        public static bool Active => _active;

        /// <summary>Open the window (interception brief appeared). Idempotent.</summary>
        public static void Open() => _active = true;

        /// <summary>Close the window (interception fully resolved). Idempotent.</summary>
        public static void Close() => _active = false;

        /// <summary>Boundary belt: drop the lock unconditionally (session teardown / geoscape reload).</summary>
        public static void Reset() => _active = false;
    }
}
