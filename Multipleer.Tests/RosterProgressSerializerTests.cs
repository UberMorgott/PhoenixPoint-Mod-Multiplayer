using System;
using System.Collections.Generic;
using Multipleer.Network.MessageLayer;
using Xunit;

namespace Multipleer.Tests
{
    public class RosterProgressSerializerTests
    {
        [Fact]
        public void PeerList_Roundtrips_SlotIndex()
        {
            var peers = new List<PeerListEntry>
            {
                new PeerListEntry { SteamId = 0, PlayerGuid = Guid.NewGuid(), Nickname = "Host",
                                    Permissions = 0, Ready = true, IsHost = true, SlotIndex = 0 },
                new PeerListEntry { SteamId = 77, PlayerGuid = Guid.NewGuid(), Nickname = "Bob",
                                    Permissions = 3, Ready = false, IsHost = false, SlotIndex = 1 },
            };
            var back = MessageSerializer.DeserializePeerList(MessageSerializer.SerializePeerList(peers));
            Assert.Equal((byte)0, back[0].SlotIndex);
            Assert.Equal((byte)1, back[1].SlotIndex);
            Assert.Equal("Bob", back[1].Nickname);
            Assert.True(back[0].IsHost);
        }
    }
}
