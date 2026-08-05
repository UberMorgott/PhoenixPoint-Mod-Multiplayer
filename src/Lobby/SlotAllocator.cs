using System;
using System.Collections.Generic;

namespace Multiplayer.Network
{
    /// <summary>
    /// Host-side stable slotIndex allocation. Host is always slot 0. Clients are assigned
    /// the next free slot in arrival order; a reconnecting identity (matched by PlayerGuid)
    /// reuses its original slot. Pure logic — no UnityEngine / transport dependency.
    /// </summary>
    public sealed class SlotAllocator
    {
        private readonly Dictionary<Guid, byte> _slots = new Dictionary<Guid, byte>();
        private readonly SortedSet<byte> _inUse = new SortedSet<byte>();

        public SlotAllocator(Guid hostIdentity)
        {
            _slots[hostIdentity] = 0;
            _inUse.Add(0);
        }

        /// <summary>
        /// Assign (or reuse) the slot for an identity; returns its slotIndex.
        ///
        /// SLOT 0 IS THE HOST AND MUST NEVER BE HANDED OUT AGAIN. This used to be a bare <c>_next++</c>
        /// with no recycling and no in-use check, so the 256th identity a session ever saw wrapped a
        /// <c>byte</c> straight onto the host's own slot — two players sharing an identity, silently,
        /// in the one place nothing checks. Allocation now walks the LOWEST FREE slot instead of a
        /// monotonic counter, and <see cref="Release"/> puts a departed player's slot back, so the space
        /// is bounded by CONCURRENT players (50, mandate) rather than by how many people ever passed
        /// through. Exhaustion is now a loud refusal instead of a collision.
        ///
        /// Deliberately still a <c>byte</c>: 50 concurrent players fit 254 free slots with room to spare,
        /// and widening it would change the slot field in PEER_LIST, LoadComplete and RosterProgress, plus
        /// the load overlay's Dictionary&lt;byte,Row&gt; — a real wire change bought for a ceiling nothing
        /// is near. Recycling is what the bug actually needed.
        /// </summary>
        public byte Assign(Guid identity)
        {
            if (_slots.TryGetValue(identity, out var existing)) return existing;
            byte slot = 0;
            for (int i = 1; i <= byte.MaxValue; i++)
                if (!_inUse.Contains((byte)i)) { slot = (byte)i; break; }
            if (slot == 0) throw new InvalidOperationException(
                "SlotAllocator: all 255 player slots are occupied — refusing to reuse the host's slot 0.");
            _slots[identity] = slot;
            _inUse.Add(slot);
            return slot;
        }

        /// <summary>A player LEFT (voluntarily): give its slot back to the pool. Not called for a peer that
        /// merely lost its connection — that one is paused and keeps its seat, which is the whole point.</summary>
        public void Release(Guid identity)
        {
            if (!_slots.TryGetValue(identity, out var slot) || slot == 0) return;
            _slots.Remove(identity);
            _inUse.Remove(slot);
        }

        public byte SlotFor(Guid identity) => _slots[identity];

        public bool TryGetSlot(Guid identity, out byte slot) => _slots.TryGetValue(identity, out slot);
    }
}
