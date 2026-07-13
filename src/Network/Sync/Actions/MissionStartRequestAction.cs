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
        /// <summary>Sanity cap for the squad tail (native MaxPlayerUnits is single-digit; a corrupt/hostile
        /// count above this reads as "no squad" → host falls back to its own native deployment window).</summary>
        private const int MaxSquadIds = 64;
        private static readonly long[] NoUnits = new long[0];

        private readonly byte _modalType; // native ModalType of the clicked brief (PhoenixPoint.Common.Utils.ModalType)
        private readonly int _siteId;     // GeoSite.SiteId of the brief's mission site; -1 = unreadable (modalType-match only)
        private readonly long[] _unitIds; // client-picked squad (GeoUnitIds, PersonnelReflection.ReadUnitId); empty = legacy/no pick

        public MissionStartRequestAction(byte modalType, int siteId)
            : this(modalType, siteId, null) { }

        public MissionStartRequestAction(byte modalType, int siteId, long[] unitIds)
        {
            _modalType = modalType;
            _siteId = siteId;
            _unitIds = unitIds ?? NoUnits;
        }

        public byte ModalType => _modalType;
        public int SiteId => _siteId;
        public long[] UnitIds => _unitIds;

        public ushort ActionId => SyncedActionIds.MissionStartRequest;
        public ActionCategory Category => ActionCategory.Dialogs;

        public void Write(BinaryWriter w)
        {
            w.Write(_modalType);
            w.Write(_siteId);
            // Squad tail (2026-07-13, client-side squad pick): count + GeoUnitIds. Old readers never see it
            // (they stop after siteId); old writers omit it (tolerant Read below).
            w.Write(_unitIds.Length);
            foreach (var id in _unitIds) w.Write(id);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            byte modalType = r.ReadByte();
            int siteId = r.ReadInt32();
            // Tolerant tail: each action is framed in its own MemoryStream (SyncEngine.ReadAction), so
            // "bytes remain" reliably means "new writer with a squad tail".
            long[] ids = NoUnits;
            if (r.BaseStream.Position < r.BaseStream.Length)
            {
                int n = r.ReadInt32();
                if (n > 0 && n <= MaxSquadIds)
                {
                    ids = new long[n];
                    for (int i = 0; i < n; i++) ids[i] = r.ReadInt64();
                }
            }
            return new MissionStartRequestAction(modalType, siteId, ids);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && ReportModalClassifier.IsMissionBrief(_modalType);

        public void Apply(GeoRuntime rt)
        {
            // Client-picked squad (2026-07-13): resolve the GeoUnitIds against the host roster and arm the
            // one-shot LaunchMission override — the native FinishDialog(Confirm) → LaunchMission call below
            // then launches with THIS squad directly and SKIPS the host's deployment window (the initiating
            // client already picked). Zero resolved ids → stay unarmed → native window on host (safe fallback).
            if (_unitIds.Length > 0)
            {
                var index = PersonnelReflection.BuildCharacterIndex(rt);
                var chars = new System.Collections.Generic.List<object>(_unitIds.Length);
                foreach (var id in _unitIds)
                    if (index.ById.TryGetValue(id, out var ch)) chars.Add(ch);
                if (chars.Count > 0)
                    Multiplayer.Harmony.Sync.MissionLaunchSquadOverride.Arm(chars);
                Debug.Log("[Multiplayer] HOST MissionStartRequest squad tail: " + chars.Count + "/" +
                          _unitIds.Length + " GeoUnitId(s) resolved" +
                          (chars.Count == 0 ? " — falling back to host deployment window" : ""));
            }

            // Resolves against the host's OWN open brief through the native click path; logs apply/reject
            // internally. No channel marks needed: on success the launch itself drives every follow-up rail
            // (ReportModalHide, tac.deploy, save transfer); on reject nothing changed.
            if (!GeoModalDisplay.TryHostConfirmBlocking(rt, _modalType, _siteId))
            {
                Multiplayer.Harmony.Sync.MissionLaunchSquadOverride.Disarm(); // reject → never leave a stale arm
                Debug.Log("[Multiplayer] HOST MissionStartRequest rejected modalType=" + _modalType +
                          " siteId=" + _siteId + " (stale/mismatched brief — logged no-op, client re-mirrors)");
            }
        }
    }
}
