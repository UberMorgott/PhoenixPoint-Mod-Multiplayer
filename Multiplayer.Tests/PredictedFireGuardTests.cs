using Multiplayer.Sync.Tactical;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>
    /// Pure de-dup decision for CLIENT-PREDICTED fire animations (combat concurrency fix). The originating client
    /// plays a predicted local fire anim on its own press AND records the shooter here; the host echoes
    /// tac.fire.start back to ALL peers. The originator must SKIP its OWN echo (already animated) while a
    /// non-originating viewer / host-origin shot must REPLAY. Entries self-expire on a TTL so a never-arriving echo
    /// never leaks an entry or blocks a later legit replay.
    /// </summary>
    public class PredictedFireGuardTests
    {
        [Fact]
        public void NoPredictedEntry_Replays()
        {
            var g = new PredictedFireGuard(ttlSeconds: 8f);
            // No record → this is another viewer's / host-origin shot → REPLAY (false).
            Assert.False(g.ConsumeIfPredicted(shooterNetId: 5, now: 1f));
        }

        [Fact]
        public void RecordedShooter_FirstEchoSkips_SecondEchoReplays()
        {
            var g = new PredictedFireGuard(ttlSeconds: 8f);
            g.RecordPredicted(shooterNetId: 5, now: 0f);

            // The host's echo for our own predicted shot → SKIP (consume the single entry).
            Assert.True(g.ConsumeIfPredicted(shooterNetId: 5, now: 1f));
            // A subsequent host-origin shot of the same shooter (no predicted entry) → REPLAY.
            Assert.False(g.ConsumeIfPredicted(shooterNetId: 5, now: 2f));
        }

        [Fact]
        public void ExpiredEntry_IsPurgedAndReplays()
        {
            var g = new PredictedFireGuard(ttlSeconds: 8f);
            g.RecordPredicted(shooterNetId: 5, now: 0f);     // deadline = 8

            // Echo arrives AFTER the TTL window (host rejected/redirected the shot → no echo until too late):
            // the stale entry is purged and the (unrelated) incoming fire-start REPLAYS rather than being eaten.
            Assert.False(g.ConsumeIfPredicted(shooterNetId: 5, now: 9f));
            Assert.Equal(0, g.PendingCount);
        }

        [Fact]
        public void DifferentShooters_AreIndependent()
        {
            var g = new PredictedFireGuard(ttlSeconds: 8f);
            g.RecordPredicted(shooterNetId: 5, now: 0f);

            // An echo for a DIFFERENT shooter we never predicted → REPLAY; our own entry is untouched.
            Assert.False(g.ConsumeIfPredicted(shooterNetId: 7, now: 1f));
            Assert.True(g.ConsumeIfPredicted(shooterNetId: 5, now: 1f));
        }

        [Fact]
        public void RapidReFire_DeDupsOneToOne_Fifo()
        {
            var g = new PredictedFireGuard(ttlSeconds: 8f);
            g.RecordPredicted(shooterNetId: 5, now: 0f);
            g.RecordPredicted(shooterNetId: 5, now: 1f);

            Assert.Equal(2, g.PendingCount);
            Assert.True(g.ConsumeIfPredicted(shooterNetId: 5, now: 2f));   // 1st echo skips
            Assert.True(g.ConsumeIfPredicted(shooterNetId: 5, now: 3f));   // 2nd echo skips
            Assert.False(g.ConsumeIfPredicted(shooterNetId: 5, now: 4f));  // no more → replay
        }

        [Fact]
        public void NegativeNetId_IsIgnored()
        {
            var g = new PredictedFireGuard(ttlSeconds: 8f);
            g.RecordPredicted(shooterNetId: -1, now: 0f);
            Assert.Equal(0, g.PendingCount);
            Assert.False(g.ConsumeIfPredicted(shooterNetId: -1, now: 1f));
        }

        [Fact]
        public void Reset_ClearsAllPending()
        {
            var g = new PredictedFireGuard(ttlSeconds: 8f);
            g.RecordPredicted(shooterNetId: 5, now: 0f);
            g.RecordPredicted(shooterNetId: 7, now: 0f);
            g.Reset();
            Assert.Equal(0, g.PendingCount);
            Assert.False(g.ConsumeIfPredicted(shooterNetId: 5, now: 1f));
        }
    }
}
