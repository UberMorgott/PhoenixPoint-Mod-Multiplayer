using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Host-driven research completion. Hourly progression runs on the host (authority); when the host
    /// completes a research it broadcasts this so the client converges to the same state. Wire payload:
    /// <c>string researchId</c>.
    ///
    /// IHostOnlyApply: the host already ran the REAL <c>Research.CompleteResearch</c> (its progression hit
    /// the patched original directly — the host never routes its own completion through this <c>Apply</c>);
    /// this broadcast exists only for the client to mirror. A client replay of <c>Apply</c> would call the
    /// reward-bearing <c>CompleteResearch</c> AGAIN (State setter → OnStateChanged → <c>Complete()</c> →
    /// ApplyRewards / Wallet.Give / OnCompleted) and DOUBLE-APPLY the rewards, because the client's synced
    /// consequences already arrive through dedicated echoes: the completed STATE via <c>ResearchChannel</c>
    /// (reward-free <c>CompleteEchoOnly</c> — a direct <c>_state</c> backing-field write that bypasses the
    /// reward cascade) and the reward RESOURCES via the wallet echo. So the client suppresses the replay
    /// entirely and the channel + wallet echo fully converge it — same fix shape as <see cref="AnswerEventAction"/>
    /// and the same double-reward class as the ResearchStateReflection CRIT.
    /// </summary>
    public sealed class ResearchCompletedAction : ISyncedAction, IHostOnlyApply
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
