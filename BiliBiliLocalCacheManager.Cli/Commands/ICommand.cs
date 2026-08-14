namespace BiliBiliLocalCacheManager.Cli.Commands;

public interface ICommand
{
    int Execute(string[] args);
}