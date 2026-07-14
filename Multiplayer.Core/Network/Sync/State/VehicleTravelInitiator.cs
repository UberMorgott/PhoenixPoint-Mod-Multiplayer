using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// HOST-side map: a travel DESTINATION SiteId → the peer that ordered the trip there, so a mission brief that
    /// opens when the vehicle ARRIVES (SiteMissionBrief / ActiveMissionBrief families) is mirrored to the
    /// INITIATING peer only (player-initiated UI), never to the whole co-op session. Mirrors the tactical
    /// move-origin tag (d295956): the ORIGIN, not "who is selected", is the reliable discriminator.
    ///
    /// The tag — not the modal type — is what separates a player-initiated brief from a world/story event: a
    /// haven-attack / infestation brief opens for a site nobody traveled to, so it has NO entry and the caller
    /// falls back to broadcast-to-all. Recorded at the single travel chokepoints (a RELAYED client order in
    /// <c>SyncEngine.OnActionRequest</c> with its sender peer; the host player's OWN order in
    /// <c>MoveVehiclePatch</c> host branch, tagged <see cref="HostSelf"/>) and CONSUMED once when the brief opens
    /// (<c>ReportModalMirror.HostBroadcast</c>). PURE (Unity-free) so it unit-tests directly.
    /// </summary>
    public static class VehicleTravelInitiator
    {
        /// <summary>Sentinel peer for the host player's own travel order — the host shows the arrival brief
        /// natively, so the caller mirrors it to no one. No real transport peer id can collide with this.</summary>
        public const ulong HostSelf = ulong.MaxValue;

        private static readonly Dictionary<int, ulong> _bySite = new Dictionary<int, ulong>();

        /// <summary>Record that <paramref name="peer"/> ordered a vehicle to travel to <paramref name="destSiteId"/>
        /// (the trip's FINAL site — where an arrival brief would open). Latest order for a site wins. No-op for an
        /// invalid site id.</summary>
        public static void Record(int destSiteId, ulong peer)
        {
            if (destSiteId < 0) return;
            _bySite[destSiteId] = peer;
        }

        /// <summary>Consume the initiator of the travel to <paramref name="siteId"/> (one brief per arrival — the
        /// entry is removed). False when no travel targeted this site, i.e. a world/story brief the caller must
        /// broadcast to all.</summary>
        public static bool TryConsume(int siteId, out ulong peer)
        {
            if (siteId >= 0 && _bySite.TryGetValue(siteId, out peer)) { _bySite.Remove(siteId); return true; }
            peer = 0;
            return false;
        }

        /// <summary>Drop all tags (new session / reload boundary — the site ids belong to the dead geoscape).</summary>
        public static void Reset() => _bySite.Clear();
    }
}
