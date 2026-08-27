using System.Text.Json;

namespace BiliBiliLocalCacheManager.Desktop.Host.Rpc;

internal sealed record RpcRequest(string Id, string Method, JsonElement Parameters);

internal sealed record RpcError(string Code, string Message, object? Details = null);

internal sealed class RpcException : Exception
{
    public RpcException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public object? Details { get; }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
