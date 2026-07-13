namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE (Unity-free) decisions behind the CLIENT-side handling of a mirrored blocking mission brief.
    /// A MANDATORY brief's mirror (<c>ReportModalClassifier.IsMandatoryBrief</c> — ambush 15 / base defense 11 /
    /// ancient defence 28) stays UNCLOSEABLE (native semantics: unskippable until resolved) but its buttons are
    /// LIVE since 2026-07-13: a client CONFIRM ("begin mission") RELAYS the intent to the host
    /// (<c>MissionStartRequestAction</c> — the host drives its own open brief through the native
    /// FinishDialog(Confirm) → LaunchMission), while every other local close/confirm result stays swallowed.
    /// (The pre-relay CanvasGroup button-grey — ShouldGreyButtons — is deleted: it made the confirm click
    /// physically impossible.) OPTIONAL mirrored briefs (scavenge 4 etc.) are NOT locked (2026-07-05 soak:
    /// dead CLOSE on the client) — their native CLOSE stays live as a pure local dismiss (null mirror
    /// DialogCallback) and their CONFIRM also relays; the HOST intent gate (armed for ALL blocking briefs)
    /// keeps every OTHER client intent from racing the host's pending decision. The live Harmony glue
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
        /// RELAY a client's "begin mission" CONFIRM to the host (<c>MissionStartRequestAction</c>) iff: we are
        /// a CLIENT in an active co-op session, NOT an engine-driven replay, the clicked result is CONFIRM
        /// (<c>ModalResult.Confirm</c> — every other result stays a swallow/local-dismiss), the modal is a
        /// mirrored MISSION BRIEF (<paramref name="isMissionBrief"/> — <c>ReportModalClassifier.IsMissionBrief</c>;
        /// the interception brief and plain report modals never relay), AND it was shown BY THE MIRROR
        /// (<paramref name="isMirrorShown"/> — same origin contract as <see cref="ShouldBlockLocalClose"/>: a
        /// client-NATIVE window must never relay a phantom start). Host/single-player are untouched (their
        /// native confirm launches directly).
        /// </summary>
        public static bool ShouldRelayBeginMission(bool isHost, bool isActiveSession, bool isApplying,
                                                   bool isConfirmResult, bool isMissionBrief, bool isMirrorShown)
            => !isHost && isActiveSession && !isApplying && isConfirmResult && isMissionBrief && isMirrorShown;
    }
}
