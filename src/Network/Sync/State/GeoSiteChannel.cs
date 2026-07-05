using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #5 — GeoSite IDENTITY + ACTIVE-MISSION mirror (fixes stale client sites → wrong
    /// geoscape-event header/backdrop; unlocks LIVE→site-id mission briefs, P1 of the 2026-07-05 popup-mirror
    /// spec). The client geoscape sim is frozen, so existing client GeoSites never update their
    /// Owner/Type/State/EncounterID/name; an event modal then resolves a STALE <c>Context.Site</c> and the
    /// native art collection (derived from <c>Context.Site.Owner</c>/<c>Type</c>) renders wrong. Host
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
            // DIAG (site channel): one line per flush (site-event driven — rare). The whole host→client site
            // path was previously silent end-to-end, which made the explored-state desync invisible in logs.
            Debug.Log("[Multiplayer] GeoSiteChannel flush sites=" + sites.Count + " [" + string.Join(",", snap.Sites) + "]");
            return GeoSiteSnapshot.Encode(snap);
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
        }
    }
}
