using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Applies a geoscape event choice over the research-style action relay. Wire payload:
    /// <c>ushort occId, string eventId, i32 choiceIndex</c>.
    /// <list type="bullet">
    ///   <item><c>occId</c> = the host-synthesized per-occurrence id (<see cref="Multipleer.Harmony.Sync.EventOccurrenceIds"/>)
    ///   carried on the EventRaised wire — disambiguates two occurrences that share a reusable def-name and lets
    ///   the host resolve the LIVE event instance (NOT a throwaway reconstruction).</item>
    ///   <item><c>eventId</c> = <c>GeoscapeEvent.EventID</c> (def-name; validity/logging).</item>
    ///   <item><c>choiceIndex</c> = index into <c>EventData.Choices</c> (-1 = the null "decline" choice).</item>
    /// </list>
    /// Host-side first-click-wins arbitration is enforced at the native CompleteEvent chokepoint
    /// (<c>CompleteEventPatch.Prefix</c> → <c>SyncEngine.Arbiter.Claim(occId)</c>), which both this relayed answer
    /// (resolved via <c>EventReflection.TryHostNativeResolve</c>/<c>CompleteEventByOccurrence</c>) and a host-local
    /// click pass through; the winner's native completion advances the host modal to the result page. This action
    /// stays transport-agnostic.
    /// </summary>
    public sealed class AnswerEventAction : ISyncedAction, IHostOnlyApply, IResolvesOutsideScope
    {
        private readonly ushort _occId;
        private readonly string _eventId;
        private readonly int _choiceIndex;

        public AnswerEventAction(ushort occId, string eventId, int choiceIndex)
        {
            _occId = occId;
            _eventId = eventId;
            _choiceIndex = choiceIndex;
        }

        /// <summary>Host-synthesized per-occurrence id (the choice-arbiter key); 0 = none.</summary>
        public ushort OccurrenceId => _occId;
        /// <summary>Index into <c>EventData.Choices</c> (-1 = null/decline).</summary>
        public int ChoiceIndex => _choiceIndex;
        /// <summary><c>GeoscapeEvent.EventID</c> def-name (logging/validity).</summary>
        public string EventId => _eventId;

        public ushort ActionId => SyncedActionIds.AnswerEvent;
        public ActionCategory Category => ActionCategory.Dialogs;

        public void Write(BinaryWriter w)
        {
            w.Write(_occId);
            w.Write(_eventId ?? "");
            w.Write(_choiceIndex);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new AnswerEventAction(r.ReadUInt16(), r.ReadString(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_eventId) && rt != null && rt.IsGeoscapeActive;

        // Implements IHostOnlyApply: the host applies the event outcome EXACTLY ONCE (here). The CLIENT replay is
        // suppressed by SyncEngine.OnActionApply (this action is IHostOnlyApply) — reconstructing the event and
        // completing it locally would double-apply non-currency outcomes and diverge from the host. Synced
        // consequences reconverge on their own: currency via the wallet echo, items via InventoryChannel, research
        // via ResearchChannel; the dialog is closed by the host's EventDismiss broadcast.
        //
        // Implements IResolvesOutsideScope: the host runs this OUTSIDE SyncApplyScope (OnActionRequest honors the
        // marker) so the native CompleteEvent's CompleteEventDismissPatch.Postfix — which early-returns under
        // SyncApplyScope.IsApplying — still broadcasts the EventDismiss the clients render.
        //
        // Resolves the LIVE event by occurrence id (EventOccurrenceIds.TryGetEvent inside CompleteEventByOccurrence)
        // — NOT a throwaway rebuild — so the real ChoiceReward / RNG roll is applied and the dismiss carries the
        // authentic outcome.
        // TODO(multipleer): non-channelled event outcomes (site reveal / mission spawn / faction-diplomacy
        // flag / direct research unlock) are NOT yet synced to the client — known gap, see SyncEngine log.
        public void Apply(GeoRuntime rt) => EventReflection.CompleteEventByOccurrence(rt, _occId, _choiceIndex);
    }
}
