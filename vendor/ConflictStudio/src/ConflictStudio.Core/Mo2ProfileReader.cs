using System.Text;

namespace ConflictStudio.Core;

public sealed record Mo2ActiveProvider(string Name, int Priority);

public static class Mo2ProfileReader
{
    public static string[] ReadActiveProviders(string modlistPath)
        => ReadActiveProviderEntries(modlistPath).Select(value => value.Name).ToArray();

    public static Mo2ActiveProvider[] ReadActiveProviderEntries(string modlistPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modlistPath);
        if (!File.Exists(modlistPath)) throw new FileNotFoundException("The MO2 profile modlist does not exist.", modlistPath);
        return ReadActiveProviderEntries(File.ReadAllBytes(modlistPath));
    }

    public static Mo2ActiveProvider[] ReadActiveProviderEntries(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string[] entries = Encoding.UTF8.GetString(content).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim().TrimStart('\uFEFF')).Where(line => line.Length > 1 && line[0] is '+' or '-').ToArray();
        return entries.Select((line, index) => (line, index))
            .Where(value => value.line[0] == '+')
            .Select(value => new Mo2ActiveProvider(value.line[1..], entries.Length - 1 - value.index))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .ToArray();
    }
}
