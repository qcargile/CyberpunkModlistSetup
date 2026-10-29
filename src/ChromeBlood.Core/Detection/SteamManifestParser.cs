namespace ChromeBlood.Core.Detection;

public sealed record SteamManifest(
    string BuildId,
    string TargetBuildId,
    string BetaKey,
    ulong StateFlags,
    bool Redmod,
    bool StateFlagsPresent,
    bool StateFlagsValid,
    string AutoUpdateBehavior,
    bool AutoUpdateBehaviorPresent);

public sealed class SteamManifestParser
{
    public static string GetValue(string text, string key) => ReadFirstValue(ReadQuotedTokens(text).ToArray(), key);

    public static SteamManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] tokens = ReadQuotedTokens(text).ToArray();
        bool stateFlagsPresent = TryReadFirstValue(tokens, "StateFlags", out string stateFlagsText);
        (ulong stateFlags, bool stateFlagsValid) = ParseLegacyUnsigned(stateFlagsText);
        bool autoUpdateBehaviorPresent = TryReadFirstValue(tokens, "AutoUpdateBehavior", out string autoUpdateBehavior);
        return new SteamManifest(
            ReadFirstValue(tokens, "buildid"),
            ReadFirstValue(tokens, "TargetBuildID"),
            ReadFirstValue(tokens, "BetaKey"),
            stateFlags,
            tokens.Contains("2060310", StringComparer.Ordinal),
            stateFlagsPresent,
            stateFlagsPresent && stateFlagsValid,
            autoUpdateBehavior,
            autoUpdateBehaviorPresent);
    }

    public static IReadOnlyList<string> ReadPaths(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> paths = [];
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line)
        {
            int start = FirstNonWhitespace(line);
            if (start < 0 || !line.AsSpan(start).StartsWith("\"path\"", StringComparison.Ordinal))
            {
                continue;
            }

            int openingQuote = line.IndexOf('"', start + 6);
            int closingQuote = openingQuote < 0 ? -1 : line.IndexOf('"', openingQuote + 1);
            if (closingQuote > openingQuote)
            {
                paths.Add(UnescapePath(line[(openingQuote + 1)..closingQuote]));
            }
        }

        return paths;
    }

    private static string ReadFirstValue(string[] tokens, string key)
        => TryReadFirstValue(tokens, key, out string value) ? value : string.Empty;

    private static bool TryReadFirstValue(string[] tokens, string key, out string value)
    {
        for (int index = 0; index + 1 < tokens.Length; index++)
        {
            if (string.Equals(tokens[index], key, StringComparison.Ordinal))
            {
                value = tokens[index + 1];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> ReadQuotedTokens(string text)
    {
        int offset = 0;
        while (offset < text.Length)
        {
            int openingQuote = text.IndexOf('"', offset);
            if (openingQuote < 0)
            {
                yield break;
            }

            int closingQuote = text.IndexOf('"', openingQuote + 1);
            if (closingQuote < 0)
            {
                yield break;
            }

            yield return text[(openingQuote + 1)..closingQuote];
            offset = closingQuote + 1;
        }
    }

    private static string UnescapePath(string value)
    {
        if (!value.Contains("\\\\", StringComparison.Ordinal))
        {
            return value;
        }

        return value.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static int FirstNonWhitespace(string line)
    {
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] is not (' ' or '\t'))
            {
                return index;
            }
        }

        return -1;
    }

    private static (ulong Value, bool Valid) ParseLegacyUnsigned(string value)
    {
        int offset = 0;
        while (offset < value.Length && value[offset] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
        {
            offset++;
        }

        bool negative = offset < value.Length && value[offset] == '-';
        if (offset < value.Length && value[offset] is '+' or '-')
        {
            offset++;
        }

        ulong parsed = 0;
        bool foundDigit = false;
        bool overflow = false;
        while (offset < value.Length && value[offset] is >= '0' and <= '9')
        {
            foundDigit = true;
            uint digit = (uint)(value[offset] - '0');
            if (parsed > (ulong.MaxValue - digit) / 10)
            {
                overflow = true;
            }
            else if (!overflow)
            {
                parsed = (parsed * 10) + digit;
            }

            offset++;
        }

        if (!foundDigit)
        {
            return (0, false);
        }

        if (overflow)
        {
            return (ulong.MaxValue, true);
        }

        return (negative ? unchecked(0UL - parsed) : parsed, true);
    }
}

