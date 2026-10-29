namespace ChromeBlood.Contracts.Paths;

public static class ContractRelativePath
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0'))
        {
            throw new ArgumentException("Contract paths must not be empty or contain NUL characters.", nameof(value));
        }

        string normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || IsDrivePath(normalized))
        {
            throw new ArgumentException("Contract paths must be relative.", nameof(value));
        }

        string[] segments = normalized.Split('/', StringSplitOptions.None);
        for (int index = 0; index < segments.Length; index++)
        {
            try
            {
                ContractWindowsPath.ValidateSegment(segments[index]);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException("Contract paths must contain valid Win32 segments.", nameof(value), exception);
            }

            segments[index] = segments[index].ToLowerInvariant();
        }

        return string.Join('/', segments);
    }

    private static bool IsDrivePath(string value)
    {
        return value.Length >= 2
            && ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z'))
            && value[1] == ':';
    }
}
