using System.Collections.Generic;
using Multiplayer.Network;
using Xunit;

public class AllReadyGateTests
{
    [Fact]
    public void HostAlone_NoClients_IsFalse()
    {
        // No non-host peers at all → never all-ready (host-alone never starts).
        Assert.False(LobbyController.AllClientsReady(new List<bool>()));
        Assert.False(LobbyController.AllClientsReady(null));
    }

    [Fact]
    public void OneClient_Ready_IsTrue()
    {
        Assert.True(LobbyController.AllClientsReady(new List<bool> { true }));
    }

    [Fact]
    public void OneClient_NotReady_IsFalse()
    {
        Assert.False(LobbyController.AllClientsReady(new List<bool> { false }));
    }

    [Fact]
    public void MultipleClients_AllReady_IsTrue()
    {
        Assert.True(LobbyController.AllClientsReady(new List<bool> { true, true, true }));
    }

    [Fact]
    public void MultipleClients_OneNotReady_IsFalse()
    {
        Assert.False(LobbyController.AllClientsReady(new List<bool> { true, false, true }));
    }
}
