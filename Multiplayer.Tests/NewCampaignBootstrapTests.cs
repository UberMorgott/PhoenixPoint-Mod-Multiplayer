using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>
    /// P0 new-campaign co-op bootstrap: the host starts a FRESH campaign from the lobby via the
    /// NATIVE new-game flow, the mod autosaves at the first playable geoscape frame and feeds that
    /// autosave into the EXISTING chunked transfer + 2-phase barrier so every client loads the
    /// byte-identical campaign start.
    ///
    /// These pin the pure, Unity-free decision surfaces that back the live wiring:
    ///   - SessionLifecycle.NewCampaignArmGuard   (arm allowed only host + lobby + !started + no transfer)
    ///   - NewCampaignBootstrap                   (single-shot latch: arm at confirm, fire once at geoscape)
    ///   - SessionLifecycle.FreshAutosaveCaptured (autosave-meta handoff: never ship a stale blob)
    /// The engine glue (SaveTransferCoordinator.ArmNewCampaignBootstrap / OnNewCampaignPlayableFrame /
    /// NewCampaignAutosaveAndTransferCrt, NewCampaignInterceptPatch) binds NetworkEngine + game types
    /// and is verified in-game, mirroring the rest of the save-transfer barrier.
    /// </summary>
    public class NewCampaignBootstrapTests
    {
        // ─── ARM gate truth table: host + lobby (active, not started) + no transfer ───────────────

        [Fact]
        public void ArmGuard_Opens_For_Host_In_Lobby_NoTransfer()
        {
            Assert.True(SessionLifecycle.NewCampaignArmGuard(
                isHost: true, isActiveSession: true, sessionStarted: false, transferActive: false));
        }

        [Fact]
        public void ArmGuard_Blocks_NonHost()
        {
            Assert.False(SessionLifecycle.NewCampaignArmGuard(
                isHost: false, isActiveSession: true, sessionStarted: false, transferActive: false));
        }

        [Fact]
        public void ArmGuard_Blocks_Without_Active_Session()
        {
            // Single-player new game (no co-op lobby) must never be captured as a bootstrap.
            Assert.False(SessionLifecycle.NewCampaignArmGuard(
                isHost: true, isActiveSession: false, sessionStarted: false, transferActive: false));
        }

        [Fact]
        public void ArmGuard_Blocks_After_Session_Start()
        {
            // Mid-session fresh campaign is NOT this gate's case — it is an F2 host reload with a
            // to-be-created save, owned by the EXISTING HostLoadGuard (one rule, no copy).
            Assert.False(SessionLifecycle.NewCampaignArmGuard(
                isHost: true, isActiveSession: true, sessionStarted: true, transferActive: false));

            Assert.True(SessionLifecycle.HostLoadGuard(
                isHost: true, isActiveSession: true, sessionStarted: true,
                connectedClientCount: 1, transferActive: false));
        }

        [Fact]
        public void ArmGuard_Blocks_While_Transfer_In_Flight()
        {
            // Barrier-reuse pin: the bootstrap rides the ONE existing transfer machinery, so it can
            // never arm while that machinery is busy (no parallel barrier, ever).
            Assert.False(SessionLifecycle.NewCampaignArmGuard(
                isHost: true, isActiveSession: true, sessionStarted: false, transferActive: true));
        }

        [Fact]
        public void ArmGuard_Does_Not_Require_A_Client()
        {
            // A lone host may bootstrap a fresh campaign; later peers onboard via the P1
            // mid-session join (the guard deliberately has no client-count dimension).
            Assert.True(SessionLifecycle.NewCampaignArmGuard(
                isHost: true, isActiveSession: true, sessionStarted: false, transferActive: false));
        }

        // ─── Latch: arm at confirm → fire EXACTLY ONCE at the first playable geoscape frame ───────

        [Fact]
        public void Latch_Fires_Once_On_First_Playable_Geoscape_Frame()
        {
            var latch = new NewCampaignBootstrap();
            latch.Arm();
            Assert.True(latch.Armed);

            Assert.True(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));
            Assert.False(latch.Armed);

            // A later playable frame (e.g. the barrier re-entry's own Playing) must never re-fire.
            Assert.False(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));
        }

        [Fact]
        public void Latch_Stays_Armed_Through_NonGeoscape_Playable_Frames()
        {
            // Belt+braces for the tutorial/intro case: a non-geoscape level reaching Playing keeps
            // the bootstrap pending until the geoscape itself is reached.
            var latch = new NewCampaignBootstrap();
            latch.Arm();

            Assert.False(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: false, transferActive: false));
            Assert.True(latch.Armed);

            Assert.True(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));
        }

        [Fact]
        public void Latch_Consumes_Without_Firing_When_Fire_Guard_Closed()
        {
            // Geoscape reached but the guard closed (e.g. a transfer already in flight): the latch is
            // CONSUMED (stale arm can never fire on a later unrelated load) and reports no-fire.
            var latch = new NewCampaignBootstrap();
            latch.Arm();

            Assert.False(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: true));
            Assert.False(latch.Armed);

            Assert.False(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));
        }

        [Fact]
        public void Latch_Never_Fires_Unarmed_Or_After_Disarm()
        {
            var latch = new NewCampaignBootstrap();
            Assert.False(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));

            latch.Arm();
            latch.Disarm(); // host backed out of the native new-game settings
            Assert.False(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));
        }

        [Fact]
        public void Latch_Rearm_Supports_Second_Fresh_Campaign_Same_Session()
        {
            // Starting a SECOND fresh campaign in the same session re-runs the same path: re-arm →
            // re-fire (the coordinator's OpenBarrier is already idempotent per fresh barrier).
            var latch = new NewCampaignBootstrap();
            latch.Arm();
            Assert.True(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));

            latch.Arm();
            Assert.True(latch.TryFire(isHost: true, isActiveSession: true,
                geoscapeActive: true, transferActive: false));
        }

        // ─── Autosave-meta handoff: only a FRESH capture may be shipped ────────────────────────────

        [Fact]
        public void FreshAutosave_True_For_New_Meta_Instance()
        {
            var oldMeta = new object();
            var newMeta = new object();
            Assert.True(SessionLifecycle.FreshAutosaveCaptured(oldMeta, newMeta));
        }

        [Fact]
        public void FreshAutosave_True_For_First_Ever_Autosave()
        {
            // No prior autosave (fresh campaign): any non-null capture is fresh.
            Assert.True(SessionLifecycle.FreshAutosaveCaptured(null, new object()));
        }

        [Fact]
        public void FreshAutosave_False_When_Meta_Did_Not_Advance()
        {
            // Ironman substitution / write failure leaves the OLD instance in place → stale, abort.
            var same = new object();
            Assert.False(SessionLifecycle.FreshAutosaveCaptured(same, same));
        }

        [Fact]
        public void FreshAutosave_False_When_Capture_Produced_Nothing()
        {
            Assert.False(SessionLifecycle.FreshAutosaveCaptured(new object(), null));
            Assert.False(SessionLifecycle.FreshAutosaveCaptured(null, null));
        }
    }
}
