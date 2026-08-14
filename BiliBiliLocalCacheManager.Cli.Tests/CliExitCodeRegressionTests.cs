using BiliBiliLocalCacheManager.Cli.Commands;

namespace BiliBiliLocalCacheManager.Cli.Tests;

public sealed class CliExitCodeRegressionTests
{
    [Fact]
    public void MissingTarget_ShouldReturnFailureForShowPlayAndDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_cli_exit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(1, new ShowCommand().Execute(["999", "--root", root]));
            Assert.Equal(1, new PlayCommand().Execute(["999", "--root", root]));
            Assert.Equal(1, new DeleteCommand().Execute(["999", "--root", root]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
