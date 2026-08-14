namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed class UnknownCommand(string commandName) : ICommand
{
    public static readonly string[] KnownCommands =
        ["scan", "show", "play", "delete", "trash", "search", "help"];

    public int Execute(string[] args)
    {
        CliPrinter.WriteWarning($"未知命令: {commandName}");

        var suggestion = CommandSuggestion.FindClosest(commandName, KnownCommands);
        if (suggestion is not null)
        {
            CliPrinter.WriteWarning($"你是不是想输入 {suggestion}？");
        }

        CliPrinter.PrintUsage();
        return 1;
    }
}
