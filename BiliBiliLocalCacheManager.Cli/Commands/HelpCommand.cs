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

        var sub = args[0].ToLowerInvariant();

        switch (sub)
        {
            case "scan":
                CliPrinter.PrintScanUsage();
                return 0;

            case "show":
                CliPrinter.PrintShowUsage();
                return 0;
            case "play":
                CliPrinter.PrintPlayUsage();
                return 0;
            case "delete":
                CliPrinter.PrintDeleteUsage();
                return 0;
            case "search":
                CliPrinter.PrintSearchUsage();
                return 0;
            default:
                CliPrinter.PrintUsage();
                return 0;
        }
    }
}
