using System;
using System.IO;
using QmTui.Utils;
using Xunit;

namespace QmTui.Tests;

public class AppPathHelperTests
{
    [Fact]
    public void AppPathHelper_Directories_ShouldPointToQmtui()
    {
        Assert.EndsWith("qmtui", AppPathHelper.ConfigDir);
        Assert.EndsWith("qmtui", AppPathHelper.CacheDir);
        Assert.EndsWith("qmtui", AppPathHelper.DataDir);

        Assert.True(Directory.Exists(AppPathHelper.ConfigDir));
        Assert.True(Directory.Exists(AppPathHelper.CacheDir));
        Assert.True(Directory.Exists(AppPathHelper.DataDir));
    }
}
