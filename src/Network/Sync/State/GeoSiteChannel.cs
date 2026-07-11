using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #5 — GeoSite IDENTITY + ACTIVE-MISSION mirror (fixes stale client sites → wrong
    /// geoscape-event header/backdrop; unlocks LIVE→site-id mission briefs, P1 of the 2026-07-05 popup-mirror
    /// spec). The client is a PURE MIRROR — under the sim-freeze its geoscape <c>Timing</c> is pinned (Now
    /// constant, producers Max'd; Inc4 V2 <c>ClientSimFreezeV2Gate</c>) and it never runs the host site sim, so
    /// existing client GeoSites never update their Owner/Type/State/EncounterID/name on their own; an event modal
    /// then resolves a STALE <c>Context.Site</c> and the native art collection (derived from
    /// <c>Context.Site.Owner</c>/<c>Type</c>) renders wrong unless THIS channel refreshes them. Host
    /// subscribes the aggregate site events on <c>GeoMap</c> (identity + the per-faction explored-state family:
    /// Inspected/Visible/Visited — plus the mission family SiteMissionStarted/Ended/Cancelled for the
    /// ActiveMission mirror, plus the WA-2 haven family HavenPopulationChanged/HavenPopulationZoneAttrition/
    /// HavenInfestationStateChanged for the haven tail); each fire marks that site's id dirty and the channel dirty.
    /// <see cref="Snapshot"/> encodes the dirty sites' identity + mission record (then clears the dirty set);
    /// the client resolves each by <c>SiteId</c> in <c>GeoMap.AllSites</c>, writes the identity onto the
    /// FRESH site (via private backing fields — pure mirror, no cascade) and attaches/clears the mirrored
    /// <c>ActiveMission</c> (direct property write — never SetActiveMission, never UI: the 0x69 report rail is
    /// the sole display driver). The wire codec + <see cref="GeoSiteSnapshot"/>/<see cref="GeoMissionRecord"/>
    /// live in their own pure file for unit testability; <see cref="GeoSiteReflection"/> is the bridge.
    /// Mirrors <see cref="DiplomacyChannel"/>.
    ///
    /// CASE A ONLY: vanilla never CREATES sites in-play (all exist from world setup + ride the join
    /// snapshot), so a snapshot id absent on the client is logged + skipped — genuinely-new-site creation
    /// (Case B) is deferred and NOT built here.
    /// </summary>
    public sealed class GeoSiteChannel : IStateChannel
    {
        public byte ChannelId => SurfaceIds.GeoSiteChannel; // 5

        private object _token;   // opaque GeoMap site-event subscription token
        private object _map;     // bound GeoMap instance (rebind guard)

        // The host-attached live instance (set in AttachHost — which only runs on the host branch of
        // SyncEngine.Tick — cleared in DetachHost). Lets out-of-band dirty sources (the WA-2 mission-drift
        // Harmony hook) mark a site without threading the channel instance through the patch.
        private static GeoSiteChannel _live;

        // Site ids changed since the last flush. Guarded by its own lock: the site events fire on the host
        // sim and the Snapshot flush drains it; a swap-under-lock keeps the encode consistent.
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private readonly object _dirtyLock = new object();

        // Host poll baseline: FNV-1a of the last PER-SITE snapshot we broadcast (stamped in Snapshot). A site
        // absent here was never flushed by this channel (it rode the join blob), so PollHostDrift SEEDS it
        // silently instead of marking it — this channel has NO payload budget, so bulk-marking ~70 sites at
        // once could overflow EncodeStateSync's u16 length. Flush-thread only (poll + Snapshot both run in
        // SyncEngine.Tick), same as the codebase treats every other per-flush hash map. Cleared on Detach.
        private readonly Dictionary<int, ulong> _lastSentSiteSig = new Dictionary<int, ulong>();

        // Per-site drift signature = FNV-1a of that ONE site's encoded record (identity + explored + mission +
        // WA-2/facility tails) — exactly the bytes the channel ships for it, so any field the client mirrors
        // changes it and any field it doesn't isn't in the DTO anyway. Used identically in Snapshot + poll.
        private static ulong SiteSig(GeoSiteState s)
        {
            var one = new GeoSiteSnapshot();
            one.Sites.Add(s);
            return ResearchSnapshot.Fnv1a(GeoSiteSnapshot.Encode(one));
        }

        /// <summary>Out-of-band host dirty-mark (WA-2 mission drift): mark <paramref name="siteId"/> dirty on
        /// the live host-attached channel. No-op when not host-attached (client / no session) — the ONLY
        /// setter of <see cref="_live"/> is the host-branch AttachHost.</summary>
        public static void MarkSiteDirtyExternal(int siteId)
        {
            var ch = _live;
            if (ch == null || siteId < 0) return;
            lock (ch._dirtyLock) { ch._dirty.Add(siteId); }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.GeoSiteChannel);
        }

        public byte[] Snapshot(GeoRuntime rt)
        {
            int[] ids;
            lock (_dirtyLock)
            {
                if (_dirty.Count == 0) return null; // nothing changed → no payload (FlushChannel no-ops on null)
                ids = new int[_dirty.Count];
                _dirty.CopyTo(ids);
                _dirty.Clear();
            }
            var sites = GeoSiteReflection.SnapshotDirty(rt, ids);
            if (sites == null || sites.Count == 0) return null;
            var snap = new GeoSiteSnapshot();
            snap.Sites.AddRange(sites);
            foreach (var s in sites) _lastSentSiteSig[s.SiteId] = SiteSig(s);   // poll baseline = exactly what we send
            // DIAG (site channel): one line per flush (site-event driven — rare). The whole host→client site
            // path was previously silent end-to-end, which made the explored-state desync invisible in logs.
            Debug.Log("[Multiplayer] GeoSiteChannel flush sites=" + sites.Count + " [" + string.Join(",", snap.Sites) + "]");
            return GeoSiteSnapshot.Encode(snap);
        }

        /// <summary>
        /// Host poll backstop (throttled by <see cref="SyncEngine.Tick"/>): walk EVERY live site, re-derive its
        /// per-site signature and re-mark the ones that drifted from the last broadcast — catching site mutations
        /// that fire no GeoMap event (other mods, future game patches). Goes far beyond the Inc5 CRC probe, which
        /// only covers Owner+State; this hashes the full DTO (name / mission / tails). Marks only — the existing
        /// per-site flush stays the sole sender; a site never yet flushed is seeded silently (it rode the join
        /// blob). No-op off-geoscape. ponytail: full ~70-site tail walk each poll (hence the longer cadence) —
        /// reuses TryReadCrcIdentities purely for the id list (a touch wasteful; a dedicated AllSiteIds accessor
        /// would trim the double walk if it ever bites).
        /// </summary>
        public void PollHostDrift(GeoRuntime rt, SyncEngine eng)
        {
            if (eng == null) return;
            if (!GeoSiteReflection.TryReadCrcIdentities(rt, out var ids) || ids == null) return;  // not in geoscape
            var allIds = new List<int>(ids.Count);
            foreach (var t in ids) allIds.Add(t.siteId);
            var sites = GeoSiteReflection.SnapshotDirty(rt, allIds);
            if (sites == null) return;
            foreach (var s in sites)
            {
                ulong sig = SiteSig(s);
                if (!_lastSentSiteSig.TryGetValue(s.SiteId, out var prev)) { _lastSentSiteSig[s.SiteId] = sig; continue; } // seed-on-first-sight
                if (sig == prev) continue;                       // client already holds this site
                // Drifted off-event → re-emit this ONE site. Baseline NOT stamped here — only the flush (the
                // sole sender) stamps it (Snapshot ~above), so a dropped flush (site unresolved mid-load) keeps
                // the drift armed and the next poll re-marks. A redundant pre-flush re-mark is idempotent.
                MarkSiteDirtyExternal(s.SiteId);
            }
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = GeoSiteSnapshot.Decode(data);
            if (snap == null) return;
            // Bind the reflection bridge BEFORE the first resolve: on a fresh client the first payload otherwise
            // hits ResolveSiteById with _allSitesProp still null → every site "unresolved" → mis-routed to the
            // Case-B spawn path. GetMapPublic runs Ensure and is idempotent.
            GeoSiteReflection.GetMapPublic(rt);
            Debug.Log("[Multiplayer] GeoSiteChannel apply sites=" + snap.Sites.Count);
            bool facilityApplied = false;
            foreach (var dto in snap.Sites)
            {
                var site = GeoSiteReflection.ResolveSiteById(rt, dto.SiteId);
                // Case B (in-play site absent on this sim-frozen client): spawn an INERT mirror so a geoscape-
                // event card resolves a real site (correct backdrop/subtitle) instead of the StartingBase
                // default. The channel always carries a full identity DTO, so hasIdentity=true here. The same
                // tested ShouldSpawnMirror predicate the client raise handler uses drives the decision;
                // SpawnMirrorSite is idempotent (re-apply / a now-present site is a no-op) and applies the
                // identity itself. Case A (site present) keeps the existing ApplyIdentity refresh.
                if (EventReflection.ShouldSpawnMirror(hasIdentity: true, siteResolved: site != null))
                    site = GeoSiteReflection.SpawnMirrorSite(rt, dto);
                else
                    GeoSiteReflection.ApplyIdentity(rt, site, dto);
                // P1 ActiveMission mirror: attach/refresh/clear the site's mirrored mission (null record =
                // tombstone). PURE state write — NO UI is opened here (spec §4: state channels never open UI);
                // the mirrored LIVE→site-id brief arrives separately on the 0x69 report rail and binds off
                // this attached mission. Null site (Case-B spawn failed) is a guarded no-op inside.
                GeoSiteReflection.ApplyMission(rt, site, dto.Mission);
                // WA-2 optional tails: value-only display stamps (haven population — infestation rides the
                // Owner identity write; alien-base type + addons; excavation dig state). Null tail = not
                // carried — never a clear. Frozen-sim safe (backing-field / private-setter writes only, no
                // native cascades) + idempotent last-wins; each ends with the native RefreshVisuals kick.
                GeoSiteReflection.ApplyHavenTail(rt, site, dto.Haven);
                GeoSiteReflection.ApplyAlienBaseTail(rt, site, dto.AlienBase);
                GeoSiteReflection.ApplyExcavationTail(rt, site, dto.Excavation);
                // Pre-attack schedule tail (gap 6b): stamps the faction's SiteAttackSchedule value-only and
                // re-raises the native SiteAttackScheduled (SuppressEvents-guarded) so the vanilla warning
                // toast + status-bar countdown render natively; empty tail = clear (attack fired/cancelled).
                GeoSiteReflection.ApplyAttackTail(rt, site, dto.Attack);
                // gap 6f weather + expiring-timer tails: value-only display stamps. Called even when the tail
                // is NULL — for these two a null tail on a snapshotted site is a meaningful default (weather
                // Clear / timer Zero), so the client RESETS to it (converges a drifted value), never "no change".
                GeoSiteReflection.ApplyWeatherTail(rt, site, dto.Weather);
                GeoSiteReflection.ApplyExpiringTimerTail(rt, site, dto.ExpiringTimer);
                // W1 facility working-state tail: value-only stamp of each facility's {State, IsPowered} (a host
                // power recompute unpowers/repowers labs). Null tail = not carried (non-base site) → no-op.
                if (GeoSiteReflection.ApplyFacilityTail(rt, site, dto.Facility)) facilityApplied = true;
            }
            // A mirrored facility power/state change flips facility.IsWorking, which the persistent top resource
            // bar's lab/workshop tally (UIModuleInfoBar.UpdateResourceInfo counts facility.IsWorking) and the
            // base-layout facility power icons read — neither repaints from a reflective backing-field write. Ride
            // the EXISTING repaint rail once per apply: force the wallet bar (past its modal IsOpen gate, so it
            // lands even under an event modal) + re-Init the base-layout grid if open. No new rail.
            if (facilityApplied)
            {
                GeoUiRefresh.RefreshWalletBar(rt, force: true);
                GeoUiRefresh.Refresh(rt, GeoUiRefresh.Screen.BaseLayout);
            }
        }

        public void AttachHost(SyncEngine eng)
        {
            // NO hard "already bound" gate: resolve the LIVE GeoMap every frame and decide off the
            // current-vs-bound INSTANCE (the WalletWatcher lesson, WalletWatcher.cs:20-28). A geoscape reload
            // (tactical round-trip, co-op save-reload) builds a FRESH GeoMap with no mid-session Detach; the old
            // `if (_bound) return;` gate left the channel subscribed to the now-DEAD map forever → zero site
            // sync after the first mission (soak 2026-07-05: host POI explorations after the 654s reload never
            // reached the client). The instance check keeps the per-frame cost to two cached reflection reads.
            if (eng == null) return;
            var rt = GeoRuntime.Instance;
            var map = GeoSiteReflection.GetMapPublic(rt);
            if (map == null) return;             // not in geoscape yet / mid-load
            if (ReferenceEquals(map, _map)) return; // already bound to this GeoMap

            DetachHost();                        // drop any stale (dead-map) binding
            _map = map;
            byte id = ChannelId;
            // Each site event hands us the changed GeoSite (the WA-2 haven family hands the GeoHaven —
            // GetOwningSiteId unwraps it via GeoHaven.Site); mark its id dirty + the channel dirty.
            _token = GeoSiteReflection.Subscribe(rt, site =>
            {
                int siteId = GeoSiteReflection.GetOwningSiteId(site);
                if (siteId < 0) return;
                lock (_dirtyLock) { _dirty.Add(siteId); }
                NetworkEngine.Instance?.Sync?.MarkChannelDirty(id);
            });
            if (_token == null) { _map = null; return; } // no event bound → retry next frame
            _live = this;                                // host-attached → out-of-band dirty marks target us
            Debug.Log("[Multiplayer] GeoSiteChannel: subscribed site events on live GeoMap (rebind-safe)");
        }

        public void DetachHost()
        {
            if (_token != null) GeoSiteReflection.Unsubscribe(_token);
            _token = null;
            _map = null;
            if (_live == this) _live = null;
            lock (_dirtyLock) { _dirty.Clear(); }
            _lastSentSiteSig.Clear();   // re-seed the poll baseline on the next (re)bind
        }
    }
}
