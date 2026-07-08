namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure, Unity-free decision gates behind the CLIENT→HOST single-choice PROMPT advance relay
    /// (<c>PacketType.EventAdvanceRequest</c> 0x6B, first-wins, host-authoritative).
    ///
    /// Bug fixed (in-game confirmed 2026-07-02): a CLIENT OK click on a single-choice prompt mirror only closed
    /// the client's own modal (<c>EncounterChoiceClientPatch</c> localClose branch) — the HOST's prompt stayed
    /// open until the host clicked. Single-choice events auto-complete on the host AT TRIGGER
    /// (GeoscapeEventSystem.cs:651-655), so the plain <c>AnswerEventAction</c> relay cannot advance the host UI:
    /// <c>EventReflection.TryHostNativeResolve</c> early-returns true on <c>IsCompleted</c> WITHOUT driving the
    /// modal, and re-completing would throw (GeoscapeEvent.cs:88-91). This dedicated advance-request instead
    /// drives the host's open native modal through <c>OnChoiceSelected</c> exactly as a local host click would —
    /// SelectChoice sees IsCompleted (no CompleteEvent) → SetClosingEncounter shows the result page →
    /// <c>SingleChoiceAdvancePatch</c> broadcasts <c>EventAdvanceResult</c> to everyone.
    ///
    /// Consulted by the game-bound glue on both sides:
    ///   • CLIENT (<c>EncounterChoiceClientPatch</c> single-choice branch): <see cref="ShouldRelayClientAdvance"/> —
    ///     relay only a real host event's prompt (non-empty EventID, exactly 1 choice, known occurrence) while the
    ///     mirror gate is ON (off-gate the client never enters the prompt-mirror state).
    ///   • HOST (<c>EventReflection.TryHostNativeAdvanceSingleChoice</c> via <c>SyncEngine.OnEventAdvanceRequest</c>):
    ///     <see cref="ShouldDriveHostAdvance"/> — drive at most ONCE per occurrence. The module keeps the SAME
    ///     <c>_geoEvent</c> on its result page (SetClosingEncounter never reassigns it, decompile
    ///     UIModuleSiteEncounters.cs:324-359), so only the advanced-occurrence mark
    ///     (<c>EventOccurrenceIds.MarkAdvanced</c>, set by SingleChoiceAdvancePatch on ANY SetClosingEncounter)
    ///     can tell prompt from result — it makes the handler idempotent under both the host-click-vs-client-click
    ///     race and the transport's deliberate double-send.
    /// </summary>
    public static class SingleChoiceAdvanceGate
    {
        /// <summary>
        /// CLIENT: should this OK click on a single-choice modal ALSO relay an advance-request to the host
        /// (in addition to the unchanged local close)? True only for a real host event's prompt mirror:
        /// non-empty <paramref name="eventId"/> (empty = the client-owned synthetic result page, never relayed),
        /// exactly one choice (multi-choice rides the AnswerEventAction relay), a known open occurrence
        /// (<paramref name="occurrenceId"/> != 0) and the event-mirror gate ON.
        /// </summary>
        public static bool ShouldRelayClientAdvance(bool isClient, bool gateEnabled, string eventId, int choiceCount, ushort occurrenceId)
            => isClient
               && gateEnabled
               && !string.IsNullOrEmpty(eventId)
               && choiceCount == 1
               && occurrenceId != 0;

        /// <summary>
        /// HOST: should a received advance-request drive the native prompt→result advance (invoke
        /// <c>OnChoiceSelected</c> on the open module)? Requires: this peer is the host; the host modal is
        /// showing THIS occurrence's event; the event already auto-completed at trigger (a NOT-completed event
        /// belongs to the AnswerEventAction relay — never complete anything here); exactly one choice
        /// (unreadable count fails safe to no-op); not paging description text (a click would only page); and
        /// the occurrence not already advanced (first wins — host click or an earlier request marked it).
        /// LEGACY path only: under <c>EventReplayModeGate</c> the MODEL-ONLY advance
        /// (<see cref="ShouldModelAdvance"/>) runs first and this native drive remains the degrade fallback.
        /// </summary>
        public static bool ShouldDriveHostAdvance(bool isHost, bool modalShowingThisOccurrence, bool isCompleted, int choiceCount, bool paging, bool alreadyAdvanced)
            => isHost
               && modalShowingThisOccurrence
               && isCompleted
               && choiceCount == 1
               && !paging
               && !alreadyAdvanced;

        /// <summary>
        /// HOST, replay mode: should a received client advance-request be applied MODEL-ONLY — mark the
        /// occurrence advanced + broadcast the authoritative <c>EventAdvanceResult</c> straight off the LIVE
        /// (already-completed-at-trigger) event, WITHOUT driving the host's own window? The host's window is then
        /// just another peer to the unified replay rule: it stays wherever the host player is reading (prompt /
        /// paging / not yet shown) and the host's own later click natively consumes (SetClosingEncounter renders
        /// the same window-2 in place; native IsCompleted guards make it apply-nothing). Requires: host, replay
        /// gate ON, the live event resolvable by occurrence id (the model is the source — no UI needed, so this
        /// works for an open, queued, or not-yet-shown host window alike), auto-completed at trigger (the outcome
        /// already exists; NOTHING is ever completed here), exactly one choice, and not already advanced (first
        /// wins). Any false → the caller degrades to the legacy native drive / buffer path.
        /// </summary>
        public static bool ShouldModelAdvance(bool isHost, bool replayEnabled, bool liveEventFound, bool isCompleted, int choiceCount, bool alreadyAdvanced)
            => isHost
               && replayEnabled
               && liveEventFound
               && isCompleted
               && choiceCount == 1
               && !alreadyAdvanced;
    }
}
