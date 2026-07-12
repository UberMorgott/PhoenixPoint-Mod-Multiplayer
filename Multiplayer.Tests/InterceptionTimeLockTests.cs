using Multiplayer.Network.Sync;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>
    /// The host-side interception time-lock window (open at brief-show, close on disengage / outcome-close,
    /// reset at geoscape boundary). Pure flag — verifies the open/close/reset transitions the Harmony patches
    /// drive. Runs serially (static state) — reset at the top of each test to avoid cross-test bleed.
    /// </summary>
    public class InterceptionTimeLockTests
    {
        [Fact]
        public void Default_IsInactive()
        {
            InterceptionTimeLock.Reset();
            Assert.False(InterceptionTimeLock.Active);
        }

        [Fact]
        public void Open_Activates_Close_Deactivates()
        {
            InterceptionTimeLock.Reset();
            InterceptionTimeLock.Open();
            Assert.True(InterceptionTimeLock.Active);   // brief 32 shown → time locked
            InterceptionTimeLock.Close();
            Assert.False(InterceptionTimeLock.Active);  // disengage / outcome closed → unlocked
        }

        [Fact]
        public void Open_IsIdempotent()
        {
            InterceptionTimeLock.Reset();
            InterceptionTimeLock.Open();
            InterceptionTimeLock.Open();
            Assert.True(InterceptionTimeLock.Active);
            InterceptionTimeLock.Close();
            Assert.False(InterceptionTimeLock.Active);
        }

        [Fact]
        public void Reset_ClearsAStuckLock()
        {
            InterceptionTimeLock.Reset();
            InterceptionTimeLock.Open();
            Assert.True(InterceptionTimeLock.Active);
            InterceptionTimeLock.Reset();               // geoscape/save-transfer boundary belt
            Assert.False(InterceptionTimeLock.Active);
        }
    }
}
