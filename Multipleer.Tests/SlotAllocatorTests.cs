using System;
using Multipleer.Network;
using Xunit;

namespace Multipleer.Tests
{
    public class SlotAllocatorTests
    {
        private static readonly Guid Host = Guid.NewGuid();
        private static readonly Guid A = Guid.NewGuid();
        private static readonly Guid B = Guid.NewGuid();

        [Fact]
        public void Host_Is_Slot_Zero()
        {
            var alloc = new SlotAllocator(Host);
            Assert.Equal((byte)0, alloc.SlotFor(Host));
        }

        [Fact]
        public void Clients_Get_Arrival_Order_Slots()
        {
            var alloc = new SlotAllocator(Host);
            Assert.Equal((byte)1, alloc.Assign(A));
            Assert.Equal((byte)2, alloc.Assign(B));
        }

        [Fact]
        public void Reconnect_Reuses_Same_Slot()
        {
            var alloc = new SlotAllocator(Host);
            var first = alloc.Assign(A);
            alloc.Assign(B);
            var again = alloc.Assign(A); // A reconnects
            Assert.Equal(first, again);
        }

        [Fact]
        public void SlotFor_Unknown_Throws_Or_Assigns_Via_Assign_Only()
        {
            var alloc = new SlotAllocator(Host);
            Assert.False(alloc.TryGetSlot(A, out _));
            alloc.Assign(A);
            Assert.True(alloc.TryGetSlot(A, out var s));
            Assert.Equal((byte)1, s);
        }
    }
}
