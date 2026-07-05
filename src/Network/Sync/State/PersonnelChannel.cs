using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #9 — Phoenix roster COMPOSITION mirror (PS1 of the 2026-07-05 personnel-sync spec).
    /// The client geoscape sim is frozen, so after join its roster CONTAINMENT never changes: a host
    /// base↔craft assignment, hire, dismissal or transfer leaves the client's <c>_tacUnits</c> stale
    /// forever (audit §2). The host re-snapshots the FULL per-site membership (ordered <c>GeoUnitId</c>s
    /// of every Phoenix site's <c>_tacUnits</c>) whenever any membership seam fires; the client
    /// reconciles each mirrored container VALUE-ONLY via <see cref="RosterReconcile"/> — direct list
    /// mutation, never native <c>AddCharacter</c>/<c>RemoveCharacter</c> (their cascade is sim on the
    /// frozen mirror). Vehicle CREW rides the #6 crew tail (one writer per field: #9 = site membership
    /// only). Codec + reconcile core live in <see cref="PersonnelSnapshot"/> (pure, unit-tested);
    /// <see cref="PersonnelReflection"/> is the bridge. Mirrors <see cref="GeoSiteChannel"/>.
    ///
    /// Dirty triggers: the PS1 Harmony seams (container <c>AddCharacter</c>/<c>RemoveCharacter</c> on
    /// GeoVehicle + GeoSite — taxonomy §8; hire/gift/manufacture/transfer all funnel through them) call
    /// <see cref="MarkSoldierDirtyExternal"/> on the live host-attached channel. Marks coalesce per
    /// SyncEngine tick; the flush ships the WHOLE Phoenix site-membership set (small — ids only), so a
    /// multi-container transfer lands atomically in ONE snapshot.
    /// </summary>
    public sealed class PersonnelChannel : IStateChannel
    {
        public byte ChannelId => SurfaceIds.PersonnelChannel; // 9

        // The host-attached live instance (set in AttachHost — host branch of SyncEngine.Tick only —
        // cleared in DetachHost), so the static Harmony membership hooks can mark dirty without
        // threading the channel instance through the patches (the GeoSiteChannel._live pattern).
        private static PersonnelChannel _live;

        // GeoUnitIds changed since the last flush (0 = seed/unresolved sentinel — the flush is full-set
        // regardless; the set is the coalesced dirty TRIGGER + diagnostic, and PS2 will drain it for
        // per-soldier blob emission). Own lock: hooks fire on the host sim, Snapshot drains (swap-then-clear).
        private readonly HashSet<long> _dirty = new HashSet<long>();
        private readonly object _dirtyLock = new object();

        private object _faction;   // bound GeoPhoenixFaction instance (rebind-by-instance guard)

        /// <summary>Out-of-band host dirty-mark (PS1 membership Harmony seams): mark
        /// <paramref name="geoUnitId"/> dirty on the live host-attached channel. No-op when not
        /// host-attached (client / no session) — the ONLY setter of <see cref="_live"/> is AttachHost.</summary>
        public static void MarkSoldierDirtyExternal(long geoUnitId)
        {
            var ch = _live;
            if (ch == null) return;
            lock (ch._dirtyLock) { ch._dirty.Add(geoUnitId); }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.PersonnelChannel);
        }

        public byte[] Snapshot(GeoRuntime rt)
        {
            int dirtyCount;
            lock (_dirtyLock)
            {
                if (_dirty.Count == 0) return null;   // nothing changed → no payload (FlushChannel no-ops on null)
                dirtyCount = _dirty.Count;
                _dirty.Clear();
            }
            var rosters = PersonnelReflection.SnapshotSiteRosters(rt);
            if (rosters == null || rosters.Count == 0) return null;   // mid-load / no faction → next mark re-arms
            var snap = new PersonnelSnapshot();
            snap.Sites.AddRange(rosters);
            Debug.Log("[Multiplayer] PersonnelChannel flush sites=" + rosters.Count + " dirtyUnits=" + dirtyCount);
            return PersonnelSnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = PersonnelSnapshot.Decode(data);
            if (snap == null || snap.Sites.Count == 0) return;
            Debug.Log("[Multiplayer] PersonnelChannel apply sites=" + snap.Sites.Count);
            // ONE index for the whole payload: every Phoenix soldier resolved across vehicles + sites.
            // RosterReconcile Contains-guards entries that a preceding record's move made stale, and the
            // host emits each soldier in at most one container per snapshot (single-writer truth).
            var index = PersonnelReflection.BuildCharacterIndex(rt);
            foreach (var rec in snap.Sites)
                PersonnelReflection.ApplySiteRoster(rt, rec, index);
        }

        public void AttachHost(SyncEngine eng)
        {
            // Rebind when the LIVE faction instance changes (geoscape reload after a tactical round-trip /
            // co-op save-reload builds a fresh GeoPhoenixFaction with no mid-session Detach) — the
            // WalletWatcher lesson. No event subscription here: the dirty sources are the static Harmony
            // membership seams routed through _live, so rebinding = retargeting _live + re-seeding.
            if (eng == null) return;
            var fac = GeoRuntime.Instance.PhoenixFaction();
            if (fac == null) return;                       // not in geoscape yet / mid-load
            if (ReferenceEquals(fac, _faction)) return;    // already bound to this instance

            _faction = fac;
            _live = this;
            // Seed a baseline flush on (re)bind: the join blob / reloaded save already matches, so the
            // client reconcile is an idempotent no-op — but a reload boundary that raced a membership
            // change re-converges here. Sentinel 0 arms the local dirty gate for the engine flush.
            lock (_dirtyLock) { _dirty.Add(0); }
            eng.MarkChannelDirty(ChannelId);
        }

        public void DetachHost()
        {
            _faction = null;
            if (_live == this) _live = null;
            lock (_dirtyLock) { _dirty.Clear(); }
        }
    }
}
