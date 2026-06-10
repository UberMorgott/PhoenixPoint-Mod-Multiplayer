using System.Net;
using System.Net.Sockets;
using Multipleer.Util;
using Xunit;

namespace Multipleer.Tests
{
    public class LanIpResolverTests
    {
        [Fact]
        public void TryResolve_DoesNotThrow_AndShapesResult()
        {
            // Environment-light: must never throw; returns true with an IPv4 OR false with null.
            var ok = LanIpResolver.TryResolveLocalIPv4(out var ip);
            if (ok)
            {
                Assert.NotNull(ip);
                Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily);
            }
            else
            {
                Assert.Null(ip);
            }
        }

        [Fact]
        public void Resolve_NeverReturnsLoopback_WhenItSucceeds()
        {
            if (LanIpResolver.TryResolveLocalIPv4(out var ip) && ip != null)
                Assert.False(IPAddress.IsLoopback(ip));
        }
    }
}
