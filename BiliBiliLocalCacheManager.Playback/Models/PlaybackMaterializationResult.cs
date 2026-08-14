namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed class PlaybackMaterializationResult
{
    private PlaybackMaterializationResult(
        bool succeeded,
        string message,
        string? outputPath,
        bool isTemporary,
        string? materializerName)
    {
        Succeeded = succeeded;
        Message = message;
        OutputPath = outputPath;
        IsTemporary = isTemporary;
        MaterializerName = materializerName;
    }

    public bool Succeeded { get; }

    public string Message { get; }

    public string? OutputPath { get; }

    public bool IsTemporary { get; }

    public string? MaterializerName { get; }

    public static PlaybackMaterializationResult Success(
        string outputPath,
        bool isTemporary,
        string message,
        string? materializerName)
    {
        return new PlaybackMaterializationResult(true, message, outputPath, isTemporary, materializerName);
    }

    public static PlaybackMaterializationResult Failure(string message, string? materializerName = null)
    {
        return new PlaybackMaterializationResult(false, message, null, false, materializerName);
    }
}
