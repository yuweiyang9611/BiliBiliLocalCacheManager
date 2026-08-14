namespace BiliBiliLocalCacheManager.Cli;

/// <summary>
/// 输错命令时给出「你是不是想输入…」的建议，避免用户只能对着 usage 自己找。
/// </summary>
public static class CommandSuggestion
{
    private const int MaxDistance = 2;

    public static string? FindClosest(string input, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(input) || candidates.Count == 0)
        {
            return null;
        }

        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = Distance(input, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= MaxDistance ? best : null;
    }

    private static int Distance(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
