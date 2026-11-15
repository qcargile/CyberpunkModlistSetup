using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConflictStudio.App;

public enum SourceEditorKind { NormalOpen, VisualStudioCode, NotepadPlusPlus }

public sealed record SourceEditorPreference(SourceEditorKind Kind, string? ExecutablePath = null);

public sealed class SourceEditorPreferenceStore
{
    private const int SchemaVersion = 1;
    private static readonly SourceEditorPreference NormalOpen = new(SourceEditorKind.NormalOpen);
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly string _path;

    public SourceEditorPreferenceStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _path = Path.Combine(root, "source-editor.json");
    }

    public SourceEditorPreference Load()
    {
        if (!File.Exists(_path)) return NormalOpen;
        try
        {
            SourceEditorPreferenceDocument? document = JsonSerializer.Deserialize<SourceEditorPreferenceDocument>(File.ReadAllText(_path), Options);
            return document?.SchemaVersion == SchemaVersion ? Normalize(document.Preference) ?? NormalOpen : NormalOpen;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return NormalOpen;
        }
    }

    public bool TrySave(SourceEditorPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        SourceEditorPreference? normalized = Normalize(preference);
        if (normalized is null) return false;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SourceEditorPreferenceDocument(SchemaVersion, normalized), Options));
            File.Move(temporary, _path, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static SourceEditorPreference? Normalize(SourceEditorPreference? preference)
    {
        if (preference is null || !Enum.IsDefined(preference.Kind)) return null;
        if (preference.Kind == SourceEditorKind.NormalOpen) return NormalOpen;
        if (string.IsNullOrWhiteSpace(preference.ExecutablePath)) return null;
        string executable = preference.ExecutablePath.Trim();
        try
        {
            if (!Path.IsPathFullyQualified(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return preference with { ExecutablePath = Path.GetFullPath(executable) };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed record SourceEditorPreferenceDocument(int SchemaVersion, SourceEditorPreference Preference);
}
