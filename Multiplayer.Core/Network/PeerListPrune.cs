using System.Collections.Generic;

namespace Multiplayer.Network
{
    /// <summary>
    /// Pure, Unity-free decision for pruning the client-side <c>_clients</c> map against a freshly
    /// received PEER_LIST roster.
    ///
    /// On a crash/timeout peer drop the host re-broadcasts ONLY the authoritative PEER_LIST (no
    /// ClientLeave packet → no client-side RemoveClient), so a remaining client must drop every
    /// <c>_clients</c> entry that is absent from the new roster. Otherwise the dropped peer lingers
    /// forever and ClientCount / GetConnectedClients over-count (status-bar drift).
    ///
    /// Extracted from <see cref="SessionManager.HandlePeerList"/> so the logic is unit-testable
    /// without dragging the Unity-coupled NetworkEngine graph, mirroring the project's gate-helper
    /// pattern. The caller passes the NON-HOST roster ids as the keep-set (the host is never a
    /// <c>_clients</c> key — IsHost rows are skipped before insertion), so the host is preserved by
    /// construction.
    /// </summary>
    public static class PeerListPrune
    {
        /// <summary>
        /// Returns every key in <paramref name="currentClientKeys"/> that is NOT present in
        /// <paramref name="newClientKeys"/> (the non-host ids in the new roster) — i.e. the
        /// <c>_clients</c> entries to remove.
        /// </summary>
        public static IEnumerable<ulong> PruneKeys(
            IEnumerable<ulong> currentClientKeys, IEnumerable<ulong> newClientKeys)
        {
            var keep = new HashSet<ulong>(newClientKeys);
            var remove = new List<ulong>();
            foreach (var key in currentClientKeys)
                if (!keep.Contains(key))
                    remove.Add(key);
            return remove;
        }
    }
}
