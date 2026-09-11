using System;
using QmTui.Services;
using Xunit;

namespace QmTui.Tests;

public class CacheManagerTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(500L * OneGb, 4L * OneGb)]
    [InlineData(201L * OneGb, 4L * OneGb)]
    [InlineData(200L * OneGb, 4L * OneGb)]
    [InlineData(199L * OneGb, 2L * OneGb)]
    [InlineData(150L * OneGb, 2L * OneGb)]
    [InlineData(100L * OneGb, 2L * OneGb)]
    [InlineData(99L * OneGb, 1L * OneGb)]
    [InlineData(50L * OneGb, 1L * OneGb)]
    [InlineData(10L * OneGb, 1L * OneGb)]
    [InlineData(0L, 1L * OneGb)]
    public void CalculateLimitByFreeBytes_ShouldReturnExpectedLimit(long freeBytes, long expectedLimit)
    {
        long actual = CacheManager.CalculateLimitByFreeBytes(freeBytes);
        Assert.Equal(expectedLimit, actual);
    }

    [Fact]
    public void MaxTotalSizeBytes_ShouldBeValidPositiveNumber()
    {
        Assert.True(CacheManager.MaxTotalSizeBytes >= OneGb);
    }
}
