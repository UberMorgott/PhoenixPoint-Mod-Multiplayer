namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) decision boundary for the CAMPAIGN-END sync (feat-campaign-end): the geoscape
    /// campaign conclusion — victory outro after the final mission chain, defeat by Phoenix collapse
    /// (all bases lost / world population under threshold), and TFTV custom endings (they ride the same
    /// native machinery: win-event outcomes set <c>GeoEventChoiceOutcome.GameOverVictoryFaction</c> →
    /// <c>GeoFactionReward.Apply</c> → <c>TriggerGameOver</c>).
    ///
    /// SINGLE NATIVE CHOKEPOINT (decompile-verified 2026-07-07): every campaign ending funnels through
    /// <c>GeoLevelController.TriggerGameOver(GeoFaction victoriousFaction)</c> (GeoLevelController.cs:1068
    /// — one-shot on <c>_gameOverTriggered</c>; callers: GameOverCheck :1064 bases-lost,
    /// ChangeWorldPopulation :1109 population collapse, GeoFactionReward.cs:121 event-driven win/lose).
    /// It branches ONLY on <c>victoriousFaction == PhoenixFaction</c>: statistics
    /// (<c>PhoenixStatisticsManager.OnGeoscapeGameOver</c>) + <c>View.ToGameOverState(palaceEnd)</c>,
    /// whose cinematic def is resolved LOCALLY off the view's own serialized fields
    /// (AlienVictoryCinematicDef / BasesLostCinematicDef, GeoscapeView.cs:662-670) — so the wire carries
    /// only {victory|defeat, victor faction guid}, never assets (behemoth local-template precedent).
    ///
    /// The notice rides the EXISTING 0x69 report rail as a NEW classifier variant
    /// (<see cref="ReportModalVariant.CampaignEnd"/> — the WA-3 InterceptionNotice precedent: no new
    /// packet family). Because campaign end is NOT a native <c>ModalType</c> (it never flows through the
    /// OpenModal chokepoint), the payload's ModalType byte carries the SYNTHETIC
    /// <see cref="SentinelModalType"/> — deliberately OUTSIDE the native enum range (0..40) and
    /// deliberately NOT whitelisted in <see cref="ReportModalClassifier.IsReportModal"/> (the OpenModal
    /// rail must never claim, suppress, gate or hide it).
    /// </summary>
    public static class CampaignEndFlow
    {
        /// <summary>Synthetic ModalType byte for the campaign-end notice. NOT a native ModalType value
        /// (the enum spans None=-1, 0..40, _CustomMission=9999) — it exists only inside the 0x69 payload
        /// so the wire shape stays untouched. Must NEVER enter the IsReportModal whitelist.</summary>
        public const byte SentinelModalType = 255;

        /// <summary>ShareLevel wire encoding of the ending kind (the field rides every variant).</summary>
        public const int ShareLevelDefeat = 0;
        public const int ShareLevelVictory = 1;

        /// <summary>Build the campaign-end 0x69 payload: {victory|defeat} in ShareLevel, the victorious
        /// faction's Def.Guid (the "ending id" — informational: the client outro branches only on the
        /// victory flag, matching native TriggerGameOver semantics) in DefId. Priority int.MaxValue
        /// mirrors the native endgame surfaces (post-tac rail / cancel paths open at int.MaxValue).</summary>
        public static ReportModalPayload BuildPayload(bool victory, string victorFactionGuid)
            => new ReportModalPayload(SentinelModalType, ReportModalVariant.CampaignEnd,
                                      -1, int.MaxValue,
                                      victory ? ShareLevelVictory : ShareLevelDefeat,
                                      victorFactionGuid, null);

        /// <summary>Decode the ending kind off the wire ShareLevel.</summary>
        public static bool IsVictory(int shareLevel) => shareLevel == ShareLevelVictory;

        /// <summary>
        /// HOST gate for the one-shot broadcast at the TriggerGameOver Postfix. Broadcast iff: we are the
        /// authoritative host of an active session, the mirror rail is on (<c>ReportMirrorGate</c> — the
        /// rail's single existing kill-switch; no new flag), THIS call actually flipped the native
        /// <c>_gameOverTriggered</c> latch false→true (a re-entrant call is a native no-op and must not
        /// re-broadcast), and it is not an engine replay.
        /// </summary>
        public static bool HostShouldBroadcast(bool isHost, bool isActiveSession, bool mirrorEnabled,
                                               bool wasAlreadyTriggered, bool isApplying)
            => isHost && isActiveSession && mirrorEnabled && !wasAlreadyTriggered && !isApplying;

        /// <summary>
        /// CLIENT suppress decision for the native <c>TriggerGameOver</c> Prefix: a pure-mirror client
        /// must never end the campaign off its own mirrored state (e.g. the bases-lost GameOverCheck
        /// re-firing on mirrored site writes) — only the host's 0x69 notice (replayed under
        /// <c>SyncApplyScope</c>, which passes here) drives the client's ending. Host / gate-off /
        /// engine replay → native runs.
        /// </summary>
        public static bool ClientShouldSuppressNativeTrigger(bool isHost, bool isActiveSession,
                                                             bool mirrorEnabled, bool isApplying)
            => !isHost && isActiveSession && mirrorEnabled && !isApplying;

        /// <summary>The ordered client reaction to a received campaign-end notice (executed by
        /// <c>SyncEngine.ShowCampaignEnd</c>; the ORDER is the contract — see <see cref="ClientSteps"/>).</summary>
        public enum ClientStep : byte
        {
            /// <summary>Client still in tactical (geo view not live) → queue-don't-drop; drained on the
            /// geoscape tick (Batch-2 outcome-queue precedent). The latch is NOT pre-consumed here — a
            /// host teardown while we are still in tactical must keep the F3 host-left menu return.</summary>
            QueueUntilGeoscape = 0,
            /// <summary>Pre-consume the F3 host-leave latch: the session is ending by CAMPAIGN CONCLUSION,
            /// so the host's later transport teardown (after ITS outro) must not fire the "Host ended the
            /// session" prompt / forced menu-return over the client's own native outro. MUST run before
            /// any step that could yield to the transport (notice-before-teardown ordering).</summary>
            SuppressHostLeaveNotice = 1,
            /// <summary>Release client view-locks (mirror-origin tags + the current blocking mirror, if
            /// any): a defeat ending can land while a mirrored blocking brief is up — the locked window
            /// would swallow the outro's view-switch. Mirrors the ResetEventMirror boundary belts.</summary>
            ReleaseViewLocks = 2,
            /// <summary>Replay the SAME native outro locally: <c>TriggerGameOver(victory ? PhoenixFaction
            /// : AlienFaction)</c> under SyncApplyScope — cinematic def resolved locally by the view,
            /// then the native GameOver screen → its own "back to main menu" teardown.</summary>
            ReplayNativeOutro = 3,
            /// <summary>Degrade-to-notify (ReportMirrorGate precedent): the native replay failed →
            /// plain text prompt with the ending kind.</summary>
            ShowEndNotice = 4,
            /// <summary>Degrade teardown: return to the main menu via the SAME native quit-to-menu
            /// chokepoint the F3 host-leave handler uses (<c>FinishLevelAndGoToLobby</c>; the existing
            /// TearDown postfix closes the session). Only ever AFTER <see cref="ShowEndNotice"/>.</summary>
            ReturnToMainMenu = 5,
        }

        /// <summary>
        /// The pinned client step plan (teardown-ordering contract):
        ///   • geo view not live → queue only (the drain re-plans once live);
        ///   • outro replayable  → suppress-latch, release locks, replay the native outro;
        ///   • replay failed     → suppress-latch, release locks, notify, THEN menu-return —
        ///     the notice always precedes the teardown, and the latch is always consumed first.
        /// </summary>
        public static ClientStep[] ClientSteps(bool geoViewLive, bool outroReplayable)
        {
            if (!geoViewLive) return new[] { ClientStep.QueueUntilGeoscape };
            if (outroReplayable)
                return new[] { ClientStep.SuppressHostLeaveNotice, ClientStep.ReleaseViewLocks,
                               ClientStep.ReplayNativeOutro };
            return new[] { ClientStep.SuppressHostLeaveNotice, ClientStep.ReleaseViewLocks,
                           ClientStep.ShowEndNotice, ClientStep.ReturnToMainMenu };
        }
    }
}
