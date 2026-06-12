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

        [Fact]
        public void RosterProgress_Roundtrips_Rows()
        {
            var rows = new List<ProgressRow>
            {
                new ProgressRow { SlotIndex = 0, Phase = 1, Percent = 100 },
                new ProgressRow { SlotIndex = 1, Phase = 0, Percent = 42 },
                new ProgressRow { SlotIndex = 2, Phase = 1, Percent = 7 },
            };
            var back = MessageSerializer.DeserializeRosterProgress(
                          MessageSerializer.SerializeRosterProgress(rows));
            Assert.Equal(3, back.Count);
            Assert.Equal((byte)1, back[0].Phase);
            Assert.Equal((byte)42, back[1].Percent);
            Assert.Equal((byte)2, back[2].SlotIndex);
        }

        [Fact]
        public void LoadComplete_Roundtrips()
        {
            var id = Guid.NewGuid();
            var (slot, transferId) = MessageSerializer.DeserializeLoadComplete(
                          MessageSerializer.SerializeLoadComplete(5, id));
            Assert.Equal((byte)5, slot);
            Assert.Equal(id, transferId);
        }
    }
}
