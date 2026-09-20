using BiliBiliLocalCacheManager.Cli.Commands;

namespace BiliBiliLocalCacheManager.Cli;

internal static class CommandCatalog
{
    private static readonly Dictionary<string, (Func<ICommand> Create, Action Usage)> Commands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["scan"] = (() => new ScanCommand(), CliPrinter.PrintScanUsage),
            ["show"] = (() => new ShowCommand(), CliPrinter.PrintShowUsage),
            ["play"] = (() => new PlayCommand(), CliPrinter.PrintPlayUsage),
            ["delete"] = (() => new DeleteCommand(), CliPrinter.PrintDeleteUsage),
            ["trash"] = (() => new TrashCommand(), CliPrinter.PrintTrashUsage),
            ["search"] = (() => new SearchCommand(), CliPrinter.PrintSearchUsage),
            ["help"] = (() => new HelpCommand(), CliPrinter.PrintUsage)
        };

    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(Commands.Keys.ToArray());

    public static ICommand Create(string name)
        => name is "--help" or "-h" ? new HelpCommand()
            : Commands.TryGetValue(name, out var command) ? command.Create()
            : new UnknownCommand(name);

    public static void PrintUsage(string name)
    {
        if (Commands.TryGetValue(name, out var command)) command.Usage();
        else CliPrinter.PrintUsage();
    }
}
