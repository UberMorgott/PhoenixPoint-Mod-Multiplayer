using System.Net;
using Multiplayer.Util;
using Xunit;
using TransportType = Multiplayer.Transport.TransportType;

namespace Multiplayer.Tests
{
    public class JoinPlanTests
    {
        [Fact]
        public void Unified_SteamAndEndpoint_SteamAlive_Cascades_Steam_Stun_Direct()
        {
            var t = JoinTarget.Unified(76561197960265728UL, "1.2.3.4", 14242);
            var plan = JoinPlan.Build(t, steamAlive: true);

            Assert.Equal(3, plan.Count);
            Assert.Equal(TransportType.SteamP2P, plan[0].Transport);
            Assert.Equal("76561197960265728", plan[0].Address);
            Assert.Equal(TransportType.StunUDP, plan[1].Transport);
            Assert.Equal("1.2.3.4:14242", plan[1].Address);
            Assert.Equal(TransportType.DirectIP, plan[2].Transport);
            Assert.Equal("1.2.3.4", plan[2].Address);
            Assert.Equal(14242, plan[2].Port);
        }

        [Fact]
        public void Unified_SteamAndEndpoint_SteamOff_SkipsSteam()
        {
            var t = JoinTarget.Unified(76561197960265728UL, "1.2.3.4", 14242);
            var plan = JoinPlan.Build(t, steamAlive: false);

            Assert.Equal(2, plan.Count);
            Assert.Equal(TransportType.StunUDP, plan[0].Transport);
            Assert.Equal(TransportType.DirectIP, plan[1].Transport);
        }

        [Fact]
        public void Unified_EndpointOnly_IgnoresSteamAlive()
        {
            var t = JoinTarget.Unified(0UL, "5.6.7.8", 9000);
            var plan = JoinPlan.Build(t, steamAlive: true);

            Assert.Equal(2, plan.Count);
            Assert.Equal(TransportType.StunUDP, plan[0].Transport);
            Assert.Equal("5.6.7.8:9000", plan[0].Address);
            Assert.Equal(TransportType.DirectIP, plan[1].Transport);
        }

        [Fact]
        public void Unified_SteamOnly_SteamAlive_SingleSteamAttempt()
        {
            var t = JoinTarget.Unified(76561197960265728UL, null, 0);
            var plan = JoinPlan.Build(t, steamAlive: true);

            Assert.Single(plan);
            Assert.Equal(TransportType.SteamP2P, plan[0].Transport);
        }

        [Fact]
        public void Unified_SteamOnly_SteamOff_EmptyPlan()
        {
            var t = JoinTarget.Unified(76561197960265728UL, null, 0);
            var plan = JoinPlan.Build(t, steamAlive: false);
            Assert.Empty(plan);
        }

        [Fact]
        public void Legacy_DirectIp_SingleDirectAttempt()
        {
            var plan = JoinPlan.Build(JoinTarget.Direct("10.0.0.2", 7777), steamAlive: false);
            Assert.Single(plan);
            Assert.Equal(TransportType.DirectIP, plan[0].Transport);
            Assert.Equal("10.0.0.2", plan[0].Address);
            Assert.Equal(7777, plan[0].Port);
        }

        [Fact]
        public void Legacy_Stun_SingleStunAttempt()
        {
            var plan = JoinPlan.Build(JoinTarget.Stun(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 5555)), steamAlive: true);
            Assert.Single(plan);
            Assert.Equal(TransportType.StunUDP, plan[0].Transport);
            Assert.Equal("1.2.3.4:5555", plan[0].Address);
        }

        [Fact]
        public void Legacy_Steam_SingleSteamAttempt()
        {
            var plan = JoinPlan.Build(JoinTarget.Steam(76561197960265728UL), steamAlive: true);
            Assert.Single(plan);
            Assert.Equal(TransportType.SteamP2P, plan[0].Transport);
            Assert.Equal("76561197960265728", plan[0].Address);
        }

        [Fact]
        public void Invalid_EmptyPlan()
        {
            Assert.Empty(JoinPlan.Build(JoinTarget.Invalid(), steamAlive: true));
        }
    }
}
