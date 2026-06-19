using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapAllowanceSnapshotPolicyTests
{
    [Theory]
    [InlineData(0UL, 0UL, false)]
    [InlineData(0xABCUL, 0UL, false)]
    [InlineData(0xABCUL, 0xDEFUL, false)]
    [InlineData(0xABCUL, 0xABCUL, true)]
    public void ShouldWriteOnlyForMatchingNonZeroActiveContentId(
        ulong activeContentId,
        ulong snapshotContentId,
        bool expected)
    {
        var actual = MapAllowanceSnapshotPolicy.ShouldWrite(activeContentId, snapshotContentId);

        Assert.Equal(expected, actual);
    }
}
