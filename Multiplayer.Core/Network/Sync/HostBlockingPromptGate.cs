namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// HOST-side authoritative intent gate for a BLOCKING native prompt (the mandatory ambush brief,
    /// ModalType.GeoAmbushBrief — see <c>ReportModalClassifier.IsBlockingModal</c>). Native single-player
    /// semantics: while that fullscreen prompt is up NOTHING else can happen (no vehicle orders, no time flow)
    /// until the mission starts. In co-op the client's mirrored modal is view-locked too, but a client intent
    /// may already be IN FLIGHT when the prompt raises (UI lock alone is raceable) — so the host, the single
    /// authority, REJECTS every client <c>ActionRequest</c> while the prompt is pending
    /// (<c>SyncEngine.OnActionRequest</c> → <see cref="ShouldRejectIntent"/> → <c>ActionReject</c>).
    ///
    /// Lifecycle (one prompt at a time — the native geoscape is fully modal under it):
    ///   • ARM   — host opens a blocking modal (<c>ReportModalMirror.HostBroadcast</c>, independent of the
    ///             mirror gate: even a mirror-degraded client must not act while the host is modal-locked).
    ///   • RELEASE — the host's modal resolves through the native <c>GeoscapeView.ModalResultCallback</c>
    ///             (Confirm → LaunchMission, or any other result; every close path funnels there —
    ///             <c>UIStateGeoModal.FinishDialog</c>/<c>ExitState</c> both invoke the opener's handler)
    ///             → <c>BlockingModalReleasePatch</c>. Normal relay flow resumes.
    ///   • RESET — session/geoscape boundary belt (fresh session must never inherit a stale arm).
    /// <see cref="ShouldRejectIntent"/> additionally requires host + active session at READ time, so a stale
    /// arm can never block outside a live hosted session. PURE (Unity-free) — unit-tested directly.
    /// </summary>
    public static class HostBlockingPromptGate
    {
        private const int None = -1;
        private static int _armedModalType = None;

        /// <summary>True iff a blocking prompt is currently pending on the host.</summary>
        public static bool IsArmed => _armedModalType != None;

        /// <summary>The armed native ModalType value, or -1 when idle (diagnostics/tests).</summary>
        public static int ArmedModalType => _armedModalType;

        /// <summary>Arm for <paramref name="modalType"/> (idempotent re-arm of the same prompt is a no-op).</summary>
        public static void Arm(int modalType)
        {
            if (modalType < 0) return;   // ModalType.None can never arm
            _armedModalType = modalType;
        }

        /// <summary>Release ONLY the matching arm — a stray release for a different modal never unblocks.</summary>
        public static void Release(int modalType)
        {
            if (_armedModalType == modalType) _armedModalType = None;
        }

        /// <summary>Boundary belt: drop any arm unconditionally (session teardown / geoscape reload).</summary>
        public static void Reset() => _armedModalType = None;

        /// <summary>
        /// The intent-apply decision: reject an incoming client action iff a blocking prompt is armed AND we
        /// are the host of an active session. Off-host / no-session reads are never blocked (stale-arm safe).
        /// </summary>
        public static bool ShouldRejectIntent(bool isHost, bool isActiveSession)
            => IsArmed && isHost && isActiveSession;

        /// <summary>
        /// Id-aware overload: same decision, EXCEPT the <see cref="SyncedActionIds.MissionStartRequest"/>
        /// intent is exempt — it is the ONE client action that RESOLVES the armed blocking prompt (client
        /// "begin mission" click → host FinishDialog(Confirm) → LaunchMission), so rejecting it would make
        /// the mirrored brief's confirm button permanently dead. Every other intent stays rejected while the
        /// prompt is pending (the gate's whole point: nothing else may happen under a blocking modal).
        /// </summary>
        public static bool ShouldRejectIntent(bool isHost, bool isActiveSession, ushort actionId)
            => actionId != SyncedActionIds.MissionStartRequest && ShouldRejectIntent(isHost, isActiveSession);
    }
}
