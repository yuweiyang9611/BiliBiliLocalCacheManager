namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed class HelpCommand : ICommand
{
    public int Execute(string[] args)
    {
        if (args.Length == 0)
        {
            CliPrinter.PrintUsage();
            return 0;
        }

        CommandCatalog.PrintUsage(args[0]);
        return 0;
    }
}
