using System;
using System.IO;
using Multipleer.Network.Sync.State;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Client-initiated cancel of a queued research element. The client does NOT simulate, so a local
    /// cancel must reach the host (the channel echo then reconciles every peer). Wire payload:
    /// <c>string researchId</c> (the <c>ResearchElement.ResearchID</c>).
    /// Host <c>Apply</c> → <c>Research.Cancel(resolve(id))</c>.
    ///
    /// IHostOnlyApply: same single-source-of-truth contract as <see cref="StartResearchAction"/>. The cancel
    /// is the client-&gt;host REQUEST trigger; the host applies it in <c>OnActionRequest</c> (marker not checked
    /// there) and the resulting queue change reaches every peer through the <c>ResearchChannel</c> echo. Marking
    /// this IHostOnlyApply stops the client double-applying the cancel via BOTH the action replay AND ch2 (which
    /// would drive two research rebuilds). Host-local cancel already routes ch2-only; this keeps client-initiated
    /// cancel consistent — the channel is the sole client truth for cancel too.
    /// </summary>
    public sealed class CancelResearchAction : ISyncedAction, IHostOnlyApply
    {
        private readonly string _researchId;

        public CancelResearchAction(string researchId) { _researchId = researchId; }

        public ushort ActionId => SyncedActionIds.CancelResearch;
        public ActionCategory Category => ActionCategory.Research;

        public void Write(BinaryWriter w) => w.Write(_researchId ?? "");
        public static ISyncedAction Read(BinaryReader r) => new CancelResearchAction(r.ReadString());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_researchId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt) => ResearchStateReflection.Cancel(rt, _researchId);
    }
}
