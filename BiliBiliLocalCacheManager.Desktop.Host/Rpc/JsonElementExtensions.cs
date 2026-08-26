using System.Globalization;
using System.Text.Json;

namespace BiliBiliLocalCacheManager.Desktop.Host.Rpc;

internal static class JsonElementExtensions
{
    public static string RequireString(this JsonElement value, string propertyName)
    {
        if (!value.TryGetPropertyIgnoreCase(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw InvalidProperty(propertyName, "a non-empty string");
        }

        return property.GetString()!.Trim();
    }

    public static string? OptionalString(this JsonElement value, string propertyName)
    {
        if (!value.TryGetPropertyIgnoreCase(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidProperty(propertyName, "a string or null");
        }

        var text = property.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static bool? OptionalBoolean(this JsonElement value, string propertyName)
    {
        if (!value.TryGetPropertyIgnoreCase(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidProperty(propertyName, "a boolean")
        };
    }

    public static int? OptionalInt32(this JsonElement value, string propertyName)
    {
        if (!value.TryGetPropertyIgnoreCase(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result))
        {
            throw InvalidProperty(propertyName, "a 32-bit integer");
        }

        return result;
    }

    public static long? OptionalInt64(this JsonElement value, string propertyName)
    {
        if (!value.TryGetPropertyIgnoreCase(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result))
        {
            throw InvalidProperty(propertyName, "a 64-bit integer");
        }

        return result;
    }

    public static long RequireInt64(this JsonElement value, string propertyName)
    {
        return value.OptionalInt64(propertyName) ??
               throw InvalidProperty(propertyName, "a 64-bit integer");
    }

    public static IReadOnlyList<JsonElement> OptionalArray(this JsonElement value, string propertyName)
    {
        if (!value.TryGetPropertyIgnoreCase(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<JsonElement>();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw InvalidProperty(propertyName, "an array");
        }

        return property.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public static bool TryGetPropertyIgnoreCase(
        this JsonElement value,
        string propertyName,
        out JsonElement property)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            property = default;
            return false;
        }

        if (value.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        foreach (var candidate in value.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    public static T ParseEnum<T>(string? rawValue, T defaultValue, string propertyName)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultValue;
        }

        if (Enum.TryParse<T>(rawValue, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new RpcException(
            "invalid_params",
            $"Property '{propertyName}' has an unsupported value.",
            new
            {
                property = propertyName,
                value = rawValue,
                allowed = Enum.GetNames<T>()
            });
    }

    private static RpcException InvalidProperty(string propertyName, string expectation)
    {
        return new RpcException(
            "invalid_params",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Property '{propertyName}' must be {expectation}."),
            new { property = propertyName });
    }
}
