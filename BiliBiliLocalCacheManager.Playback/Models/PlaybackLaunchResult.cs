namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed class PlaybackLaunchResult
{
    private PlaybackLaunchResult(
        bool succeeded,
        string message,
        string? playerName,
        string? managedArtifactPath)
    {
        Succeeded = succeeded;
        Message = message;
        PlayerName = playerName;
        ManagedArtifactPath = managedArtifactPath;
    }

    public bool Succeeded { get; }

    public string Message { get; }

    public string? PlayerName { get; }

    /// <summary>
    /// Gets the managed transcode artifact used by a successful playback launch, when applicable.
    /// </summary>
    public string? ManagedArtifactPath { get; }

    public static PlaybackLaunchResult Success(string message, string playerName)
    {
        return new PlaybackLaunchResult(true, message, playerName, null);
    }

    public static PlaybackLaunchResult Success(
        string message,
        string playerName,
        string managedArtifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedArtifactPath);
        return new PlaybackLaunchResult(true, message, playerName, managedArtifactPath);
    }

    public static PlaybackLaunchResult Failure(string message, string? playerName = null)
    {
        return new PlaybackLaunchResult(false, message, playerName, null);
    }

    internal PlaybackLaunchResult WithManagedArtifact(string managedArtifactPath)
    {
        if (!Succeeded)
        {
            throw new InvalidOperationException("A failed playback launch cannot reference a managed artifact.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(managedArtifactPath);
        return new PlaybackLaunchResult(true, Message, PlayerName, managedArtifactPath);
    }
}
