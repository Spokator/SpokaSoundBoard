using SpokaSoundBoard.Core;
using Xunit;

namespace SpokaSoundBoard.Tests;

public sealed class AppEnvironmentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RealDataFolderHasNoInstanceSuffix(string? dir) => Assert.Equal("", AppEnvironment.InstanceSuffixFor(dir));

    [Fact]
    public void CustomDataFolderGetsStableSuffix()
    {
        var a = AppEnvironment.InstanceSuffixFor(@"C:\Temp\spoka-test");
        Assert.StartsWith(".", a);
        Assert.Equal(13, a.Length);
        // Same folder written differently: same instance.
        Assert.Equal(a, AppEnvironment.InstanceSuffixFor(@"c:\temp\SPOKA-TEST\"));
        Assert.NotEqual(a, AppEnvironment.InstanceSuffixFor(@"C:\Temp\spoka-other"));
    }
}
