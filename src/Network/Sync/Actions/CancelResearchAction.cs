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
    /// </summary>
    public sealed class CancelResearchAction : ISyncedAction
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
