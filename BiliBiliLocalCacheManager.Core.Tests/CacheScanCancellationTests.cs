using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class CacheScanCancellationTests
{
    [Fact]
    public void BuildIndexWithReport_ShouldHonorPreCancelledToken()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_cancel_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                new FileSystemCacheIndexBuilder().BuildIndexWithReport(
                    root,
                    new CacheIndexBuildOptions(),
                    cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
