using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>
    /// Symptom B: a CLIENT clicking LEAVE in the lobby closes its only peer (the host). That
    /// transport drop is indistinguishable from a genuine host crash, so the F3 HostLeaveHandler
    /// used to fire "Host ended the session" + a forced HomeScreen reload on a VOLUNTARY self-leave.
    /// The suppression signal (NetworkEngine._intentionalDisconnect, set at the top of every
    /// intentional teardown) was unwired. These tests pin the EXTRACTED pure decision: suppress the
    /// host-left notification when the local teardown was intentional, notify otherwise.
    /// </summary>
    public class HostLeaveSuppressionTests
    {
        [Fact]
        public void ShouldNotifyHostLeft_IntentionalLocalLeave_Suppressed()
        {
            // Voluntary client self-leave: the local teardown set _intentionalDisconnect = true before
            // the peer drop propagates → no toast, no HandleHostLeft.
            Assert.False(SessionLifecycle.ShouldNotifyHostLeft(localDisconnectIntentional: true));
        }

        [Fact]
        public void ShouldNotifyHostLeft_UnexpectedHostDrop_Notifies()
        {
            // Genuine host crash / link loss: the client did NOT initiate the teardown, so the flag is
            // clear → the session-fatal notification must still fire.
            Assert.True(SessionLifecycle.ShouldNotifyHostLeft(localDisconnectIntentional: false));
        }
    }
}
