namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE (Unity-free) decisions behind the CLIENT-side view-lock of a mirrored BLOCKING modal (the mandatory
    /// ambush brief — <c>ReportModalClassifier.IsBlockingModal</c>). The client renders the SAME native fullscreen
    /// modal the host sees, but as a MIRROR of a host-pending decision it must be inert: the client can neither
    /// start the mission (host-authoritative — the host's confirm drives the tactical co-op deploy flow) nor
    /// close/skip the window (native semantics: the prompt blocks everything until resolved). The live Harmony
    /// glue (<c>BlockingModalClientLockPatches</c>) binds game types and is NOT unit-linked; these truth tables are.
    /// </summary>
    public static class BlockingModalLockDecision
    {
        /// <summary>
        /// Swallow a LOCAL close/confirm of the modal (<c>UIStateGeoModal.FinishDialog</c> — every button routes
        /// there via <c>UIModal.Confirm/Close/Cancel → _handler</c>; and <c>OnCancel</c> — the Esc/back path) iff:
        /// we are a CLIENT in an active co-op session, this is NOT an engine-driven replay (a mirror-driven close
        /// under <c>SyncApplyScope</c> must pass), and the modal is a blocking type. Host is NEVER touched
        /// (host transparency: its native confirm launches the mission). Fail-open on the glue side (an
        /// unreadable modal type → native runs).
        /// </summary>
        public static bool ShouldBlockLocalClose(bool isHost, bool isActiveSession, bool isApplying, bool isBlockingModal)
            => !isHost && isActiveSession && !isApplying && isBlockingModal;

        /// <summary>
        /// Grey the modal's buttons (CanvasGroup.interactable=false on the shown UIModal — the same one-toggle
        /// dim used by the event-dialog client lock) iff: client + active session + blocking type. No
        /// <c>isApplying</c> term: the mirrored state is QUEUED under the apply scope but the modal SHOWS later
        /// (UIStateGeoModal.EnterState → UIModuleModal.Show on a view-update frame, outside the scope).
        /// </summary>
        public static bool ShouldGreyButtons(bool isHost, bool isActiveSession, bool isBlockingModal)
            => !isHost && isActiveSession && isBlockingModal;
    }
}
