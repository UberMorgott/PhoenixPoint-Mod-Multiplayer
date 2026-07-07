using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE placement decision for a HIRED (brand-new) soldier the client has never seen (personnel-sync
    /// PS1/PS2, hire gap). The #9 membership reconcile (<see cref="RosterReconcile"/>) and the PS2
    /// live-state apply both operate on soldiers that ALREADY EXIST in a client container — a hire creates
    /// a fresh <c>GeoCharacter</c>, so its <c>GeoUnitId</c> resolves to nothing and BOTH blocks skip it
    /// ("not live on this client"). The soldier's whole-<c>GeoCharacter</c> blob DOES ride the same
    /// snapshot's PS2 state block, so the client can MATERIALIZE it — this core decides WHERE: the site
    /// whose mirrored roster lists that id.
    ///
    /// A soldier the host emits appears in at most one site roster per snapshot (single-writer truth), so
    /// FIRST-wins is exact. Ids already live on the client (in <paramref name="liveIds"/>) are never
    /// newcomers. Unity-free → directly unit-testable.
    /// </summary>
    public static class PersonnelNewcomerPlan
    {
        /// <summary>Map each membership-listed <c>GeoUnitId</c> that is NOT yet live on the client to the
        /// SiteId of the first roster that lists it — the placement target for materializing that hire.
        /// Empty when every listed id is already live (the common no-hire case).</summary>
        public static Dictionary<long, int> ResolvePlacements(IEnumerable<PersonnelSiteRoster> sites, ICollection<long> liveIds)
        {
            var placements = new Dictionary<long, int>();
            if (sites == null) return placements;
            foreach (var rec in sites)
            {
                if (rec == null || rec.UnitIds == null) continue;
                foreach (var id in rec.UnitIds)
                {
                    if (id == 0) continue;                                   // None sentinel — never a real soldier
                    if (liveIds != null && liveIds.Contains(id)) continue;  // already present → not a newcomer
                    if (!placements.ContainsKey(id)) placements[id] = rec.SiteId;   // first roster wins
                }
            }
            return placements;
        }
    }
}
