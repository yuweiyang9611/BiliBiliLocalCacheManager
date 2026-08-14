using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed class DeleteCommand : ICommand
{
    private readonly ICacheTrashService _trashService;

    public DeleteCommand()
        : this(new FileSystemCacheTrashService())
    {
    }

    internal DeleteCommand(ICacheTrashService trashService)
    {
        _trashService = trashService;
    }

    public int Execute(string[] args)
    {
        // 定义本命令允许的参数，未知参数会直接报错，避免“无效参数被忽略”的误解。
        var specs = new Dictionary<string, OptionParser.OptionSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["--root"] = OptionParser.ValueOption("root"),
            ["-r"] = OptionParser.ValueOption("root"),
            ["--dry-run"] = OptionParser.FlagOption("dry-run"),
            ["--permanent"] = OptionParser.FlagOption("permanent"),
            ["--yes"] = OptionParser.FlagOption("yes"),
            ["-y"] = OptionParser.FlagOption("yes"),
            ["--help"] = OptionParser.FlagOption("help"),
            ["-h"] = OptionParser.FlagOption("help")
        };

        OptionParser.ParsedArguments parsed;
        try
        {
            parsed = OptionParser.Parse(args, specs);
        }
        catch (ArgumentException ex)
        {
            CliPrinter.WriteError(ex.Message);
            CliPrinter.PrintDeleteUsage();
            return 1;
        }

        // 帮助参数优先
        if (parsed.Has("help"))
        {
            CliPrinter.PrintDeleteUsage();
            return 0;
        }

        // delete 只允许一个位置参数：avid
        if (parsed.Positionals.Count == 0)
        {
            CliPrinter.WriteError("缺少 avid 参数。");
            CliPrinter.PrintDeleteUsage();
            return 1;
        }

        if (parsed.Positionals.Count > 1)
        {
            CliPrinter.WriteError("avid 参数只能有一个，请检查是否多写了参数。");
            CliPrinter.PrintDeleteUsage();
            return 1;
        }

        if (!AvidParser.TryParse(parsed.Positionals[0], out var avid))
        {
            CliPrinter.WriteError("无效的 avid，请输入整数（也接受 av 前缀，例如 av170001）。");
            CliPrinter.PrintDeleteUsage();
            return 1;
        }

        var dryRun = parsed.Has("dry-run");
        var permanent = parsed.Has("permanent");
        var assumeYes = parsed.Has("yes");

        var (root, _) = OptionParser.ParseCommonOptions(parsed);
        if (string.IsNullOrWhiteSpace(root))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            CliPrinter.PrintDeleteUsage();
            return 1;
        }

        return permanent
            ? DeletePermanently(root, avid, dryRun, assumeYes)
            : MoveToTrash(root, avid, dryRun, assumeYes);
    }

    /// <summary>
    /// 默认路径：移入应用回收站，与图形界面行为一致，可以再还原回来。
    /// </summary>
    private int MoveToTrash(string root, long avid, bool dryRun, bool assumeYes)
    {
        var targetPath = Path.Combine(root, avid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!Directory.Exists(targetPath))
        {
            CliPrinter.WriteError($"没有在根目录中找到 avid = {avid} 对应的缓存目录。");
            return 1;
        }

        if (dryRun)
        {
            CliPrinter.WriteLine("【试运行】将把以下目录移入应用回收站（未实际移动）：");
            CliPrinter.WriteLine(targetPath);
            return 0;
        }

        if (!assumeYes &&
            !CliPrinter.Confirm($"确定把 avid = {avid} 的缓存移入应用回收站吗？（之后可用 trash restore 还原）"))
        {
            CliPrinter.WriteLine("已取消。");
            return 0;
        }

        var result = _trashService.MoveToTrash(root, avid);
        if (!result.Found)
        {
            CliPrinter.WriteError($"没有在根目录中找到 avid = {avid} 对应的缓存目录。");
            return 1;
        }

        if (!result.Succeeded)
        {
            CliPrinter.WriteError($"移入应用回收站失败：{result.ErrorMessage ?? "未知错误"}");
            return 1;
        }

        CliPrinter.WriteSuccess($"已把 avid = {avid} 移入应用回收站：");
        CliPrinter.WriteLine(result.TrashPath ?? "(未知路径)");
        CliPrinter.WriteLine("如需还原：BiliBiliLocalCacheManager.Cli trash restore " +
            $"{avid} --root {root}");
        return 0;
    }

    /// <summary>
    /// 显式要求的永久删除路径，不可撤销，因此默认必须交互确认。
    /// </summary>
    private static int DeletePermanently(string root, long avid, bool dryRun, bool assumeYes)
    {
        var manager = new CacheManager();

        if (dryRun)
        {
            var preview = manager.DeleteByAvid(root, avid, dryRun: true);
            if (!preview.Found)
            {
                CliPrinter.WriteError($"没有在根目录中找到 avid = {avid} 对应的缓存目录。");
                return 1;
            }

            CliPrinter.WriteLine("【试运行】将永久删除以下目录（未实际删除）：");
            CliPrinter.WriteLine(preview.TargetPath ?? "(未知路径)");
            return 0;
        }

        var probe = manager.DeleteByAvid(root, avid, dryRun: true);
        if (!probe.Found)
        {
            CliPrinter.WriteError($"没有在根目录中找到 avid = {avid} 对应的缓存目录。");
            return 1;
        }

        if (!assumeYes &&
            !CliPrinter.Confirm(
                $"确定永久删除 avid = {avid} 的缓存吗？此操作不可撤销，也不会进入应用回收站。",
                defaultValue: false))
        {
            CliPrinter.WriteLine("已取消。");
            return 0;
        }

        var result = manager.DeleteByAvid(root, avid, dryRun: false);
        if (!result.Found)
        {
            CliPrinter.WriteError($"没有在根目录中找到 avid = {avid} 对应的缓存目录。");
            return 1;
        }

        if (result.Deleted)
        {
            CliPrinter.WriteSuccess($"已永久删除 avid = {avid} 的缓存目录：");
            CliPrinter.WriteLine(result.TargetPath ?? "(未知路径)");
            return 0;
        }

        CliPrinter.WriteError($"删除 avid = {avid} 失败：{result.ErrorMessage ?? "未知错误"}");
        if (!string.IsNullOrEmpty(result.TargetPath))
        {
            CliPrinter.WriteLine($"目标目录：{result.TargetPath}");
        }

        return 1;
    }
}
