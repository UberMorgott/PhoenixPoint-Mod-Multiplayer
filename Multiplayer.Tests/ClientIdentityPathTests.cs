using Multiplayer.Util;
using Xunit;

namespace Multiplayer.Tests
{
    // Pins the PER-INSTANCE identity FILE selection used by ClientIdentity: instance 1 =>
    // identity.json (shared/primary), instance N>1 => identity-N.json — so a 2nd same-machine
    // instance (which shares persistentDataPath) gets its OWN persistent playerGUID WITHOUT the
    // manual MULTIPLAYER_IDENTITY override, closing the guid-collision that collapses the joining
    // client into the host's slot 0. ClientIdentity reuses the ONE canonical "-N before extension"
    // suffixer (TftvLogRedirect) that the per-instance LOG file uses, so identity and log share
    // identical instance-suffix semantics; these tests lock that contract for the identity path.
    // (ClientIdentity itself binds Application.persistentDataPath and is in-game verified; the pure
    // path decision under test lives in Multiplayer.Core and is exercised here directly.)
    public class ClientIdentityPathTests
    {
        private const string Base =
            @"C:\Users\me\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Multiplayer\identity.json";

        [Fact]
        public void Instance1_UsesSharedIdentityJson()
        {
            // Primary same-machine instance (and every real cross-machine peer) keeps identity.json.
            Assert.Equal(Base, TftvLogRedirect.ResolveRedirectedPath(Base, isSecondary: false, instanceIndex: 1));
        }

        [Fact]
        public void Instance2_UsesIdentityDash2Json()
        {
            var r = TftvLogRedirect.ResolveRedirectedPath(Base, isSecondary: true, instanceIndex: 2);
            Assert.Equal(
                @"C:\Users\me\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Multiplayer\identity-2.json", r);
        }

        [Fact]
        public void Instance3_UsesIdentityDash3Json()
        {
            var r = TftvLogRedirect.ResolveRedirectedPath(Base, isSecondary: true, instanceIndex: 3);
            Assert.EndsWith(@"\identity-3.json", r);
        }
    }
}
