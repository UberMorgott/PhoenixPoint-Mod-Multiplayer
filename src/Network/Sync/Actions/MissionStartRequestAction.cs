using System;
using System.IO;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Client-edit intent: the client clicked "begin mission" (ModalResult.Confirm) on a MIRRORED blocking
    /// site-mission brief (<see cref="ReportModalClassifier.IsMissionBrief"/> — ambush/scavenge/haven-attack/
    /// base-defense/ancient/... families). The mirror's DialogCallback is null (and the mandatory subset's
    /// close paths are swallowed), so natively the click was a dead end — this action relays the CONFIRM to
    /// the host instead. Wire: <c>u8 modalType, i32 siteId</c> — the brief's native ModalType + the mission
    /// site's stable <c>GeoSite.SiteId</c> (the same identity the 0x69 report mirror / tac.deploy already
    /// ship), read off the client's rebuilt <c>UIStateGeoModal.ModalData</c> mission.
    ///
    /// Host apply (<see cref="GeoModalDisplay.TryHostConfirmBlocking"/>) VALIDATES against the host's OWN
    /// currently-open brief (same ModalType + same mission site) and then invokes the exact native click path
    /// <c>UIStateGeoModal.FinishDialog(ModalResult.Confirm)</c> → <c>GeoscapeView.ModalResultCallback</c> →
    /// <c>LaunchMission</c> — so the mandatory-save toggle, the AncientSiteDefence handler branch, the
    /// HostBlockingPromptGate release + ReportModalHide broadcast (BlockingModalReleasePatch) and the geo→tac
    /// co-op deploy flow (LaunchTacticalGameGatePatch host branch + tac.deploy + save transfer) all run
    /// exactly as if the HOST had clicked. A stale/mismatched request (host already resolved, different site)
    /// is a logged no-op — the client's window closes via the normal ReportModalHide mirror anyway.
    ///
    /// <see cref="IHostOnlyApply"/>: clients never launch locally (LaunchTacticalGameGatePatch keeps gating
    /// spontaneous client launches; the deploy-driven one is host-initiated). <see cref="IResolvesOutsideScope"/>:
    /// the apply drives live native UI whose host-side broadcast patches (ReportModalHide, event raise/dismiss,
    /// deploy rail) early-return under <c>SyncApplyScope.IsApplying</c> — running inside the scope would
    /// suppress the very broadcasts the launch must emit (AnswerEventAction precedent). Category Dialogs:
    /// it answers a host-pending dialog — un-gated like the event-answer relay (everyone may click; the host's
    /// current-modal validation is the arbiter). The HostBlockingPromptGate intent reject EXEMPTS this id
    /// (id-aware ShouldRejectIntent overload) — it is the one intent that RESOLVES the armed prompt.
    /// </summary>
    public sealed class MissionStartRequestAction : ISyncedAction, IHostOnlyApply, IResolvesOutsideScope
    {
        private readonly byte _modalType; // native ModalType of the clicked brief (PhoenixPoint.Common.Utils.ModalType)
        private readonly int _siteId;     // GeoSite.SiteId of the brief's mission site; -1 = unreadable (modalType-match only)

        public MissionStartRequestAction(byte modalType, int siteId)
        {
            _modalType = modalType;
            _siteId = siteId;
        }

        public byte ModalType => _modalType;
        public int SiteId => _siteId;

        public ushort ActionId => SyncedActionIds.MissionStartRequest;
        public ActionCategory Category => ActionCategory.Dialogs;

        public void Write(BinaryWriter w)
        {
            w.Write(_modalType);
            w.Write(_siteId);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new MissionStartRequestAction(r.ReadByte(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && ReportModalClassifier.IsMissionBrief(_modalType);

        public void Apply(GeoRuntime rt)
        {
            // Resolves against the host's OWN open brief through the native click path; logs apply/reject
            // internally. No channel marks needed: on success the launch itself drives every follow-up rail
            // (ReportModalHide, tac.deploy, save transfer); on reject nothing changed.
            if (!GeoModalDisplay.TryHostConfirmBlocking(rt, _modalType, _siteId))
                Debug.Log("[Multiplayer] HOST MissionStartRequest rejected modalType=" + _modalType +
                          " siteId=" + _siteId + " (stale/mismatched brief — logged no-op, client re-mirrors)");
        }
    }
}
