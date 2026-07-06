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
        /// </summary>
        public static bool ShouldDriveHostAdvance(bool isHost, bool modalShowingThisOccurrence, bool isCompleted, int choiceCount, bool paging, bool alreadyAdvanced)
            => isHost
               && modalShowingThisOccurrence
               && isCompleted
               && choiceCount == 1
               && !paging
               && !alreadyAdvanced;
    }
}
