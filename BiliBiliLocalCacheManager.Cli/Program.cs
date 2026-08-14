using BiliBiliLocalCacheManager.Cli.Commands;
using Spectre.Console;

namespace BiliBiliLocalCacheManager.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            CliPrinter.PrintUsage();
            return 0;
        }

        var commandName = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            ICommand command = commandName switch
            {
                "scan" => new ScanCommand(),
                "show" => new ShowCommand(),
                "play" => new PlayCommand(),
                "delete" => new DeleteCommand(),
                "trash" => new TrashCommand(),
                "search" => new SearchCommand(),
                "help" or "--help" or "-h" => new HelpCommand(),
                _ => new UnknownCommand(commandName)
            };

            return command.Execute(rest);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]错误:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
