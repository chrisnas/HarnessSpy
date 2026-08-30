using HarnessSpy.Core.Hooks;

namespace HarnessSpy.Tests;

public sealed class SpawningProcessResolverTests
{
    [Fact]
    public void ResolveReturnsParentOfCurrentProcessOnWindows()
    {
        SpawningProcessInfo info = new SpawningProcessResolver().Resolve();

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(SpawningProcessInfo.Unknown, info);
            return;
        }

        Assert.NotNull(info.ProcessId);
    }
}
