using System.IO;
using System.Text.Json;
using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed record WorkspacePreference(string Mo2Root, string ProfileName, ModManagerKind ManagerKind = ModManagerKind.Mo2, string? ContextPath = null);

public sealed class WorkspacePreferenceStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _path;

    public WorkspacePreferenceStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _path = Path.Combine(root, "workspace.json");
    }

    public WorkspacePreference? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            WorkspacePreference? preference = JsonSerializer.Deserialize<WorkspacePreference>(File.ReadAllText(_path), Options);
            return preference is not null && !string.IsNullOrWhiteSpace(preference.Mo2Root)
                && !string.IsNullOrWhiteSpace(preference.ProfileName) && Enum.IsDefined(preference.ManagerKind)
                ? preference : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Save(WorkspacePreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preference, Options));
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public bool TrySave(WorkspacePreference preference)
    {
        try
        {
            Save(preference);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
}
