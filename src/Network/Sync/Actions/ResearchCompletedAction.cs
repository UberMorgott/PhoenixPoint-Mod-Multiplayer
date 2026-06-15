using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Host-driven research completion. Hourly progression runs on the host (authority); when the host
    /// completes a research it broadcasts this so clients complete the same element (their own
    /// self-completion is suppressed by the interceptor). Wire payload: <c>string researchId</c>.
    /// </summary>
    public sealed class ResearchCompletedAction : ISyncedAction
    {
        private readonly string _researchId;

        public ResearchCompletedAction(string researchId) { _researchId = researchId; }

        public ushort ActionId => SyncedActionIds.ResearchCompleted;
        public ActionCategory Category => ActionCategory.Research;

        public void Write(BinaryWriter w) => w.Write(_researchId ?? "");
        public static ISyncedAction Read(BinaryReader r) => new ResearchCompletedAction(r.ReadString());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_researchId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt) => ResearchReflection.Complete(rt, _researchId);
    }
}
