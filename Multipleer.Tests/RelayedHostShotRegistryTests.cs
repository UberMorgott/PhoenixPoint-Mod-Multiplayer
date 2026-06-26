using Multipleer.Sync.Tactical;
using Xunit;

namespace Multipleer.Tests
{
    /// <summary>
    /// PURE identity-keyed registry behind the relayed-shot cosmetic-delay strips (FIX B). B1 reads
    /// <see cref="RelayedHostShotRegistry.IsAbilityActive"/> (reroute enqueue→inline); B2 reads
    /// <see cref="RelayedHostShotRegistry.IsActorActive"/> (skip aim-up). Host's OWN shots are never
    /// registered, so both predicates return false for them → full native cinematic preserved. The Harmony /
    /// coroutine integration (which native points are patched) is verified in-game, not here.
    /// </summary>
    public class RelayedHostShotRegistryTests
    {
        [Fact]
        public void NoEntry_NeitherAbilityNorActorActive()
        {
            var r = new RelayedHostShotRegistry();
            Assert.False(r.IsAbilityActive(new object()));   // B1: unknown → native enqueue defer
            Assert.False(r.IsActorActive(new object()));      // B2: unknown → native aim-up wait
            Assert.Equal(0, r.Count);
        }

        [Fact]
        public void Begin_MarksBothAbilityAndShooterActive()
        {
            var r = new RelayedHostShotRegistry();
            var ability = new object();
            var shooter = new object();
            r.Begin(ability, shooter);
            Assert.True(r.IsAbilityActive(ability));   // B1: reroute this shoot to inline PlayAction
            Assert.True(r.IsActorActive(shooter));      // B2: skip this shooter's aim-up wait
            Assert.Equal(1, r.Count);
        }

        [Fact]
        public void End_RemovesEntry_BothFalseAgain()
        {
            var r = new RelayedHostShotRegistry();
            var ability = new object();
            var shooter = new object();
            r.Begin(ability, shooter);
            r.End(ability);
            Assert.False(r.IsAbilityActive(ability));
            Assert.False(r.IsActorActive(shooter));
            Assert.Equal(0, r.Count);
        }

        [Fact]
        public void DistinctAbilitiesAndActors_AreIndependent()
        {
            var r = new RelayedHostShotRegistry();
            var aRelayed = new object();
            var actorRelayed = new object();
            var aOther = new object();
            var actorHostOwn = new object();
            r.Begin(aRelayed, actorRelayed);

            Assert.True(r.IsAbilityActive(aRelayed));
            Assert.False(r.IsAbilityActive(aOther));        // a different ability is NOT rerouted
            Assert.True(r.IsActorActive(actorRelayed));
            Assert.False(r.IsActorActive(actorHostOwn));    // host's OWN soldier keeps the full aim cinematic
        }

        [Fact]
        public void Begin_NullArgs_NoOp()
        {
            var r = new RelayedHostShotRegistry();
            r.Begin(null, new object());
            r.Begin(new object(), null);
            Assert.Equal(0, r.Count);
            Assert.False(r.IsAbilityActive(null));
            Assert.False(r.IsActorActive(null));
        }

        [Fact]
        public void Begin_SameAbilityTwice_OverwritesToSingleEntry_SelfHeal()
        {
            var r = new RelayedHostShotRegistry();
            var ability = new object();
            var actorOld = new object();
            var actorNew = new object();
            r.Begin(ability, actorOld);
            r.Begin(ability, actorNew);   // leaked prior clear → re-register overwrites
            Assert.Equal(1, r.Count);
            Assert.True(r.IsActorActive(actorNew));
            Assert.False(r.IsActorActive(actorOld));
        }

        [Fact]
        public void Reset_ClearsAllPending()
        {
            var r = new RelayedHostShotRegistry();
            r.Begin(new object(), new object());
            r.Begin(new object(), new object());
            r.Reset();
            Assert.Equal(0, r.Count);
        }
    }
}
