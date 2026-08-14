namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed record SensitiveDataRedactionContext
{
    public const int MaximumKnownSensitiveValueCount = 2_000;

    public SensitiveDataRedactionContext(
        string? CacheRoot = null,
        string? UserProfileDirectory = null,
        string? LocalApplicationDataDirectory = null,
        string? TemporaryDirectory = null,
        IReadOnlyCollection<string>? KnownSensitiveValues = null)
    {
        this.CacheRoot = CacheRoot;
        this.UserProfileDirectory = UserProfileDirectory;
        this.LocalApplicationDataDirectory = LocalApplicationDataDirectory;
        this.TemporaryDirectory = TemporaryDirectory;
        this.KnownSensitiveValues = PrepareKnownSensitiveValues(KnownSensitiveValues);
    }

    public string? CacheRoot { get; }

    public string? UserProfileDirectory { get; }

    public string? LocalApplicationDataDirectory { get; }

    public string? TemporaryDirectory { get; }

    /// <summary>
    /// A bounded, de-duplicated and longest-first snapshot prepared once per export.
    /// The redactor can reuse it for every JSON string without repeated sorting.
    /// </summary>
    public IReadOnlyList<string> KnownSensitiveValues { get; }

    private static IReadOnlyList<string> PrepareKnownSensitiveValues(
        IReadOnlyCollection<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Array.Empty<string>();
        }

        var prepared = new List<string>(Math.Min(
            values.Count,
            MaximumKnownSensitiveValueCount));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in values)
        {
            var normalized = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            prepared.Add(normalized);
            if (prepared.Count == MaximumKnownSensitiveValueCount)
            {
                break;
            }
        }

        prepared.Sort(static (left, right) =>
        {
            var lengthOrder = right.Length.CompareTo(left.Length);
            return lengthOrder != 0
                ? lengthOrder
                : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        });
        return prepared.ToArray();
    }
}
