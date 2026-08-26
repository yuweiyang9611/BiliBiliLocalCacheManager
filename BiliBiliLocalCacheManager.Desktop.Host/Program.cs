using System.Text;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host;

public static class Program
{
    public static async Task<int> Main()
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var application = new DesktopHostApplication();
        using var input = new StreamReader(
            Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        using var output = new StreamWriter(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: false)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var server = new JsonLineRpcServer(application, input, output);
        await server.RunAsync();
        return 0;
    }
}
