using System.Text;

namespace ChromeBlood.Core.Preparation;

public sealed class SteamManifestFormatException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record SteamManifestEdit(byte[] OriginalBytes, byte[] UpdatedBytes, string OriginalValue);

public static class SteamManifestEditor
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("AutoUpdateBehavior");
    private static readonly byte[] AppIdKey = Encoding.ASCII.GetBytes("appid");
    private static readonly byte[] CyberpunkAppId = Encoding.ASCII.GetBytes("1091500");

    public static SteamManifestEdit SetLaunchOnly(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaximumBytes || bytes.Contains((byte)0))
        {
            throw new SteamManifestFormatException("The Steam manifest is empty, oversized, or contains NUL bytes.");
        }

        List<(int Start, int Length)> matches = [];
        int appIdCount = 0;
        int index = 0;
        while (index < bytes.Length)
        {
            if (bytes[index] != (byte)'"')
            {
                index++;
                continue;
            }

            (int contentStart, int contentLength, int next) = ReadQuoted(bytes, index);
            if (bytes.AsSpan(contentStart, contentLength).SequenceEqual(Key))
            {
                int valueQuote = next;
                while (valueQuote < bytes.Length && IsWhitespace(bytes[valueQuote])) valueQuote++;
                if (valueQuote >= bytes.Length || bytes[valueQuote] != (byte)'"')
                {
                    throw new SteamManifestFormatException("AutoUpdateBehavior does not have one quoted value.");
                }

                (int valueStart, int valueLength, int valueNext) = ReadQuoted(bytes, valueQuote);
                if (valueLength != 1 || bytes[valueStart] is not ((byte)'0') and not ((byte)'1') and not ((byte)'2'))
                {
                    throw new SteamManifestFormatException("AutoUpdateBehavior has an unsupported value.");
                }

                matches.Add((valueStart, valueLength));
                index = valueNext;
                continue;
            }

            if (bytes.AsSpan(contentStart, contentLength).SequenceEqual(AppIdKey))
            {
                int valueQuote = next;
                while (valueQuote < bytes.Length && IsWhitespace(bytes[valueQuote])) valueQuote++;
                if (valueQuote >= bytes.Length || bytes[valueQuote] != (byte)'"')
                {
                    throw new SteamManifestFormatException("appid does not have one quoted value.");
                }

                (int valueStart, int valueLength, int valueNext) = ReadQuoted(bytes, valueQuote);
                if (!bytes.AsSpan(valueStart, valueLength).SequenceEqual(CyberpunkAppId))
                {
                    throw new SteamManifestFormatException("The Steam manifest is not app ID 1091500.");
                }
                appIdCount++;
                index = valueNext;
                continue;
            }

            index = next;
        }

        if (appIdCount != 1 || matches.Count != 1)
        {
            throw new SteamManifestFormatException("The Steam manifest must contain exactly one appid 1091500 and AutoUpdateBehavior key.");
        }

        byte[] updated = bytes.ToArray();
        (int start, _) = matches[0];
        string original = ((char)bytes[start]).ToString(System.Globalization.CultureInfo.InvariantCulture);
        updated[start] = (byte)'1';
        return new SteamManifestEdit(bytes.ToArray(), updated, original);
    }

    public static string ReadValue(byte[] bytes) => SetLaunchOnly(bytes).OriginalValue;

    private static (int ContentStart, int ContentLength, int Next) ReadQuoted(byte[] bytes, int quote)
    {
        int current = quote + 1;
        while (current < bytes.Length)
        {
            if (bytes[current] == (byte)'\\')
            {
                current += 2;
                if (current > bytes.Length) break;
                continue;
            }

            if (bytes[current] == (byte)'"')
            {
                return (quote + 1, current - quote - 1, current + 1);
            }

            if (bytes[current] is (byte)'\r' or (byte)'\n')
            {
                throw new SteamManifestFormatException("The Steam manifest contains an unterminated quoted token.");
            }

            current++;
        }

        throw new SteamManifestFormatException("The Steam manifest contains an unterminated quoted token.");
    }

    private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}

