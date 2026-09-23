namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed record ExportTranscodeRequirement(string ApprovalToken, string ProcessingKind, string Reason, string Impact);

public sealed class ExportTranscodeRequiredException(ExportTranscodeRequirement requirement)
    : Exception(requirement.Reason)
{
    public ExportTranscodeRequirement Requirement { get; } = requirement;
}
