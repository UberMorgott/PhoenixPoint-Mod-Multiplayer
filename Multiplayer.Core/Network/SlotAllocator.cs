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
        private byte _next = 1; // 0 reserved for host

        public SlotAllocator(Guid hostIdentity)
        {
            _slots[hostIdentity] = 0;
        }

        /// <summary>Assign (or reuse) the slot for an identity; returns its slotIndex.</summary>
        public byte Assign(Guid identity)
        {
            if (_slots.TryGetValue(identity, out var existing)) return existing;
            var slot = _next++;
            _slots[identity] = slot;
            return slot;
        }

        public byte SlotFor(Guid identity) => _slots[identity];

        public bool TryGetSlot(Guid identity, out byte slot) => _slots.TryGetValue(identity, out slot);
    }
}
