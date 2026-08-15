using System;

namespace Multiplayer.Network
{
    internal static class LoadBarrierAuthority
    {
        internal static bool AcceptLoadComplete(bool isHost, bool phaseActive, bool revealed,
            Guid currentBoundary, Guid claimedBoundary, bool senderHasSlot, byte senderSlot, byte claimedSlot)
        {
            return isHost && phaseActive && !revealed && currentBoundary != Guid.Empty &&
                   claimedBoundary == currentBoundary && senderHasSlot && senderSlot == claimedSlot;
        }

        internal static bool AcceptRevealAll(bool isHost, bool phaseActive, bool revealed,
            Guid currentBoundary, Guid claimedBoundary, ulong? hostPeerId, ulong senderPeerId)
        {
            return !isHost && phaseActive && !revealed && currentBoundary != Guid.Empty &&
                   claimedBoundary == currentBoundary && hostPeerId.HasValue &&
                   senderPeerId == hostPeerId.Value;
        }

        /// <summary>Who may say that a slot has rendered its own first post-load frame (law L554). The
        /// role split here is about TRANSPORT AUTHORITY, not about the barrier: on the host every peer
        /// speaks for itself and only for itself, and on a client the only trustworthy speaker is the
        /// relay — the host — because a client never hears another client directly. The barrier logic
        /// above it is identical on both roles, which is the point.
        ///
        /// Deliberately NOT gated on this peer having armed its own reveal: RevealAll and a relayed
        /// RevealReady are separate messages and nothing guarantees the arm lands first. Recording a
        /// readiness early is harmless — it is boundary-scoped, and the local arm is what decides when
        /// the set is consulted — while dropping one is a barrier that never opens.</summary>
        internal static bool AcceptRevealReady(bool isHost, bool revealed, Guid currentBoundary,
            Guid claimedBoundary, ulong? hostPeerId, ulong senderPeerId,
            bool senderHasSlot, byte senderSlot, byte claimedSlot)
        {
            if (revealed || currentBoundary == Guid.Empty || claimedBoundary != currentBoundary)
                return false;
            return isHost
                ? senderHasSlot && senderSlot == claimedSlot
                : hostPeerId.HasValue && senderPeerId == hostPeerId.Value;
        }
    }
}
