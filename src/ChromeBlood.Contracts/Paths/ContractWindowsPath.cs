namespace ChromeBlood.Contracts.Paths;

public static class ContractWindowsPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CLOCK$",
        "CONIN$",
        "CONOUT$"
    };

    public static string NormalizeAbsolute(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Windows paths must not be blank.", nameof(value));
        }

        string normalized = value.Replace('/', '\\');
        if (HasNamespacePrefix(normalized))
        {
            throw new ArgumentException("Windows namespace paths are not supported.", nameof(value));
        }

        if (IsDrivePrefix(normalized))
        {
            if (normalized.Length < 3 || normalized[2] != '\\')
            {
                throw new ArgumentException("Drive paths must be absolute.", nameof(value));
            }

            string[] segments = ParseSegments(normalized[3..], true, value);
            string root = $"{char.ToUpperInvariant(normalized[0])}:\\";
            return segments.Length == 0 ? root : root + string.Join('\\', segments);
        }

        if (normalized.StartsWith("\\\\", StringComparison.Ordinal))
        {
            string[] segments = ParseSegments(normalized[2..], true, value);
            if (segments.Length < 2)
            {
                throw new ArgumentException("UNC paths require a server and share.", nameof(value));
            }

            return "\\\\" + string.Join('\\', segments);
        }

        throw new ArgumentException("Windows paths must be absolute drive or UNC paths.", nameof(value));
    }

    public static bool TryNormalizeAbsolute(string? value, out string? normalized)
    {
        if (value is null)
        {
            normalized = null;
            return false;
        }

        try
        {
            normalized = NormalizeAbsolute(value);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = null;
            return false;
        }
    }

    public static bool LooksRootedOrNamespace(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string normalized = value.Replace('/', '\\');
        return IsDrivePrefix(normalized) || normalized.StartsWith('\\');
    }

    public static void ValidateSegment(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value is "." or ".."
            || value.EndsWith('.')
            || value.EndsWith(' ')
            || value.Any(IsInvalidCharacter)
            || IsReservedName(value))
        {
            throw new ArgumentException("The value is not a valid Win32 path segment.", nameof(value));
        }
    }

    public static bool IsSameOrChild(string path, string parent)
    {
        string normalizedPath = NormalizeAbsolute(path);
        string normalizedParent = NormalizeAbsolute(parent);
        if (string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = normalizedParent.EndsWith('\\') ? normalizedParent : normalizedParent + '\\';
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string path)
    {
        string normalized = NormalizeAbsolute(path);
        return normalized.Length == 3 && normalized[1] == ':' && normalized[2] == '\\';
    }

    private static string[] ParseSegments(string remainder, bool allowEmptyRoot, string originalValue)
    {
        if (remainder.Length == 0)
        {
            return allowEmptyRoot ? [] : throw new ArgumentException("The path requires a segment.", nameof(originalValue));
        }

        string[] segments = remainder.Split('\\', StringSplitOptions.None);
        if (segments[^1].Length == 0)
        {
            segments = segments[..^1];
        }

        if (segments.Length == 0 && allowEmptyRoot)
        {
            return [];
        }

        foreach (string segment in segments)
        {
            try
            {
                ValidateSegment(segment);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException("The path contains an invalid Win32 segment.", nameof(originalValue), exception);
            }
        }

        return segments;
    }

    private static bool HasNamespacePrefix(string value)
        => value.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || value.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || value.StartsWith("\\??\\", StringComparison.Ordinal)
            || value.StartsWith("\\\\??\\", StringComparison.Ordinal);

    private static bool IsDrivePrefix(string value)
        => value.Length >= 2
            && value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            && value[1] == ':';

    private static bool IsInvalidCharacter(char value)
        => char.IsControl(value) || value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static bool IsReservedName(string value)
    {
        string stem = value.Split('.', 2)[0];
        if (ReservedNames.Contains(stem))
        {
            return true;
        }

        if (stem.Length != 4)
        {
            return false;
        }

        bool numberedDevice = stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase);
        return numberedDevice && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
