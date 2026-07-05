namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE (Unity-free) decisions behind the CLIENT-side view-lock of a mirrored MANDATORY blocking modal
    /// (<c>ReportModalClassifier.IsMandatoryBrief</c> — ambush 15 / base defense 11 / ancient defence 28). The
    /// client renders the SAME native fullscreen modal the host sees, but as a MIRROR of a host-pending decision
    /// it must be inert: the client can neither start the mission (host-authoritative — the host's confirm drives
    /// the tactical co-op deploy flow) nor close/skip the window (native semantics: a MANDATORY prompt blocks
    /// everything until resolved). OPTIONAL mirrored briefs (scavenge 4 etc.) are deliberately NOT locked
    /// (2026-07-05 soak: dead CLOSE on the client) — their native CLOSE stays live and performs a pure local
    /// dismiss (null mirror DialogCallback), while the HOST intent gate (armed for ALL blocking briefs) keeps
    /// client intents from racing the host's pending decision. The live Harmony glue
    /// (<c>BlockingModalClientLockPatches</c>) binds game types and is NOT unit-linked; these truth tables are.
    /// </summary>
    public static class BlockingModalLockDecision
    {
        /// <summary>
        /// Swallow a LOCAL close/confirm of the modal (<c>UIStateGeoModal.FinishDialog</c> — every button routes
        /// there via <c>UIModal.Confirm/Close/Cancel → _handler</c>; and <c>OnCancel</c> — the Esc/back path) iff:
        /// we are a CLIENT in an active co-op session, this is NOT an engine-driven replay (a mirror-driven close
        /// under <c>SyncApplyScope</c> must pass), the modal is a MANDATORY brief
        /// (<paramref name="isMandatoryBrief"/> — <c>ReportModalClassifier.IsMandatoryBrief</c>; an optional
        /// mirrored brief keeps its native CLOSE, soak 2026-07-05), AND it was shown BY THE MIRROR
        /// (<paramref name="isMirrorShown"/> — <c>BlockingModalMirrorRegistry</c>): a client-NATIVE window of the
        /// same type keeps its native buttons (no host hide will ever release it — locking it bricked the client,
        /// soak 2026-07-05), and a mirror window whose hide already landed (hide-before-show race) enters
        /// unlocked. Host is NEVER touched (host transparency: its native confirm launches the mission).
        /// Fail-open on the glue side (an unreadable modal type → native runs).
        /// </summary>
        public static bool ShouldBlockLocalClose(bool isHost, bool isActiveSession, bool isApplying, bool isMandatoryBrief, bool isMirrorShown)
            => !isHost && isActiveSession && !isApplying && isMandatoryBrief && isMirrorShown;

        /// <summary>
        /// Grey the modal's buttons (CanvasGroup.interactable=false on the shown UIModal — the same one-toggle
        /// dim used by the event-dialog client lock) iff: client + active session + MANDATORY brief + shown BY
        /// THE MIRROR (same origin contract as <see cref="ShouldBlockLocalClose"/>). No <c>isApplying</c> term:
        /// the mirrored state is QUEUED under the apply scope but the modal SHOWS later
        /// (UIStateGeoModal.EnterState → UIModuleModal.Show on a view-update frame, outside the scope).
        /// </summary>
        public static bool ShouldGreyButtons(bool isHost, bool isActiveSession, bool isMandatoryBrief, bool isMirrorShown)
            => !isHost && isActiveSession && isMandatoryBrief && isMirrorShown;
    }
}
