using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #5 — GeoSite IDENTITY mirror (fixes stale client sites → wrong geoscape-event
    /// header/backdrop). The client geoscape sim is frozen, so existing client GeoSites never update their
    /// Owner/Type/State/EncounterID/name; an event modal then resolves a STALE <c>Context.Site</c> and the
    /// native art collection (derived from <c>Context.Site.Owner</c>/<c>Type</c>) renders wrong. Host
    /// subscribes the 6 aggregate site events on <c>GeoMap</c>; each fire marks that site's id dirty and the
    /// channel dirty. <see cref="Snapshot"/> encodes the dirty sites' identity (then clears the dirty set);
    /// the client resolves each by <c>SiteId</c> in <c>GeoMap.AllSites</c> and writes the identity onto the
    /// FRESH site (via private backing fields — pure mirror, no cascade). The wire codec +
    /// <see cref="GeoSiteSnapshot"/> live in their own pure file for unit testability;
    /// <see cref="GeoSiteReflection"/> is the bridge. Mirrors <see cref="DiplomacyChannel"/>.
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
        private bool _bound;

        // Site ids changed since the last flush. Guarded by its own lock: the site events fire on the host
        // sim and the Snapshot flush drains it; a swap-under-lock keeps the encode consistent.
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private readonly object _dirtyLock = new object();

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
            return GeoSiteSnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = GeoSiteSnapshot.Decode(data);
            if (snap == null) return;
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
                {
                    GeoSiteReflection.SpawnMirrorSite(rt, dto);
                    continue;
                }
                GeoSiteReflection.ApplyIdentity(rt, site, dto);
            }
        }

        public void AttachHost(SyncEngine eng)
        {
            if (_bound) return;                  // bound; skip the per-frame reflection
            if (eng == null) return;
            var rt = GeoRuntime.Instance;
            var map = GeoSiteReflection.GetMapPublic(rt);
            if (map == null) return;             // not in geoscape yet / mid-load
            if (ReferenceEquals(map, _map)) return; // already bound to this GeoMap

            DetachHost();                        // drop any stale binding
            _map = map;
            byte id = ChannelId;
            // Each site event hands us the changed GeoSite; mark its id dirty + the channel dirty.
            _token = GeoSiteReflection.Subscribe(rt, site =>
            {
                int siteId = GeoSiteReflection.GetSiteId(site);
                if (siteId < 0) return;
                lock (_dirtyLock) { _dirty.Add(siteId); }
                NetworkEngine.Instance?.Sync?.MarkChannelDirty(id);
            });
            if (_token == null) { _map = null; return; } // no event bound → retry next frame
            _bound = true;
        }

        public void DetachHost()
        {
            if (_token != null) GeoSiteReflection.Unsubscribe(_token);
            _token = null;
            _map = null;
            _bound = false;
            lock (_dirtyLock) { _dirty.Clear(); }
        }
    }
}
