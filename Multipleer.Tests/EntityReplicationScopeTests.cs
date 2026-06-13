using Multipleer.Network.CommandSync;
using Xunit;

public class EntityReplicationScopeTests
{
    [Fact]
    public void IsApplying_DefaultsFalse()
    {
        Assert.False(EntityReplicationScope.IsApplying);
    }

    [Fact]
    public void IsApplying_TrueInsideScope_RestoredAfter()
    {
        Assert.False(EntityReplicationScope.IsApplying);
        using (EntityReplicationScope.Enter())
        {
            Assert.True(EntityReplicationScope.IsApplying);
        }
        Assert.False(EntityReplicationScope.IsApplying);
    }

    [Fact]
    public void NestedScopes_RestoreOuterState()
    {
        using (EntityReplicationScope.Enter())
        {
            Assert.True(EntityReplicationScope.IsApplying);
            using (EntityReplicationScope.Enter())
            {
                Assert.True(EntityReplicationScope.IsApplying);
            }
            Assert.True(EntityReplicationScope.IsApplying); // inner dispose must not clear outer
        }
        Assert.False(EntityReplicationScope.IsApplying);
    }
}
