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
            return CommandCatalog.Create(commandName).Execute(rest);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]错误:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
