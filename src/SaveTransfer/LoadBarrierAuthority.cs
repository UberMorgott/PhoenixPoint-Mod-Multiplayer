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
    }
}
