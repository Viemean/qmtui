using System;
using QmTui.Services;
using Xunit;

namespace QmTui.Tests;

public class CacheManagerTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(500L * OneGb, 8L * OneGb)]
    [InlineData(301L * OneGb, 8L * OneGb)]
    [InlineData(300L * OneGb, 8L * OneGb)]
    [InlineData(299L * OneGb, 4L * OneGb)]
    [InlineData(150L * OneGb, 4L * OneGb)]
    [InlineData(149L * OneGb, 2L * OneGb)]
    [InlineData(50L * OneGb, 2L * OneGb)]
    [InlineData(49L * OneGb, 1L * OneGb)]
    [InlineData(10L * OneGb, 1L * OneGb)]
    [InlineData(9L * OneGb, 512L * 1024 * 1024)]
    [InlineData(3L * OneGb, 512L * 1024 * 1024)]
    [InlineData(1L * OneGb, 128L * 1024 * 1024)]
    [InlineData(500L * 1024 * 1024, 128L * 1024 * 1024)]
    [InlineData(0L, 128L * 1024 * 1024)]
    public void CalculateLimitByFreeBytes_ShouldReturnExpectedLimit(long freeBytes, long expectedLimit)
    {
        long actual = CacheManager.CalculateLimitByFreeBytes(freeBytes);
        Assert.Equal(expectedLimit, actual);
    }

    [Fact]
    public void MaxTotalSizeBytes_ShouldBeValidPositiveNumber()
    {
        Assert.True(CacheManager.MaxTotalSizeBytes >= 128L * 1024 * 1024);
    }
}
