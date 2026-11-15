using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed record ProfileColumnState(string Key, double Width, bool Visible, string? SortDirection = null, int SortPriority = 0);

public sealed record ProfileViewState(ModManagerKind ManagerKind, string InstallationId, string ProfileName)
{
    public string CodeSearch { get; init; } = string.Empty;
    public string CodeView { get; init; } = "Actionable";
    public string CodeSurface { get; init; } = "All";
    public string CodeProvider { get; init; } = "All mods";
    public string CodeOtherProvider { get; init; } = "All mods";
    public string ArchiveModFilter { get; init; } = string.Empty;
    public string ArchiveFileFilter { get; init; } = string.Empty;
    public bool ShowNonConflictingFiles { get; init; }
    public bool OnlyConflictingArchives { get; init; }
    public ProfileColumnState[] Columns { get; init; } = [];
    public double CodeDetailFraction { get; init; } = 0.6;
    public bool SummaryExpanded { get; init; } = true;
    public int SelectedTab { get; init; }
    public string HistoryReference { get; init; } = "previous";
    public string HistoryFilter { get; init; } = "Changes";
}

public sealed class ProfileViewStateStore
{
    public static readonly string[] CodeColumnKeys = ["classification", "proof", "surface", "target", "providers", "files"];

    private const int SchemaVersion = 1;
    private const int MaximumTextLength = 512;
    private const double MinimumColumnWidth = 80;
    private const double MaximumColumnWidth = 1600;
    private const double MinimumDetailFraction = 0.15;
    private const double MaximumDetailFraction = 0.85;
    private const int TabCount = 4;
    private static readonly string[] CodeViews = ["Actionable", "Proven", "NeedsDecision", "Reviewed", "Compatible", "All"];
    private static readonly string[] CodeSurfaces = ["All", "VirtualFile", "ScriptAndTweak", "SharedState", "ArchiveXl", "Diagnostic"];
    private static readonly string[] SortDirections = ["Ascending", "Descending"];
    private static readonly string[] HistoryReferences = ["previous", "baseline"];
    private static readonly string[] HistoryFilters = ["Changes", "All", "New", "Changed", "NoLongerDetected", "Unchanged"];
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly string _viewsDirectory;

    public ProfileViewStateStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _viewsDirectory = Path.Combine(root, "views");
    }

    public ProfileViewState Load(ModManagerKind managerKind, string installationId, string profileName)
    {
        ProfileViewState fallback = new(managerKind, installationId, profileName);
        if (!IsValidIdentity(fallback)) return fallback;
        string path = PathFor(fallback);
        if (!File.Exists(path)) return fallback;
        try
        {
            ProfileViewStateDocument? document = JsonSerializer.Deserialize<ProfileViewStateDocument>(File.ReadAllText(path), Options);
            if (document?.SchemaVersion != SchemaVersion || document.State is null || !SameIdentity(document.State, fallback)) return fallback;
            return Normalize(document.State);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return fallback;
        }
    }

    public bool TrySave(ProfileViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValidIdentity(state)) return false;
        string path = PathFor(state);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_viewsDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ProfileViewStateDocument(SchemaVersion, Normalize(state)), Options));
            File.Move(temporary, path, true);
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

    public bool TryDelete(ModManagerKind managerKind, string installationId, string profileName)
    {
        ProfileViewState identity = new(managerKind, installationId, profileName);
        if (!IsValidIdentity(identity)) return false;
        try
        {
            string path = PathFor(identity);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private string PathFor(ProfileViewState identity)
    {
        string material = ((int)identity.ManagerKind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + identity.InstallationId + "\0" + identity.ProfileName;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return Path.Combine(_viewsDirectory, hash + ".json");
    }

    private static ProfileViewState Normalize(ProfileViewState state)
    {
        return state with
        {
            CodeSearch = BoundedOrDefault(state.CodeSearch, string.Empty),
            CodeView = AllowedOrDefault(state.CodeView, CodeViews, "Actionable"),
            CodeSurface = AllowedOrDefault(state.CodeSurface, CodeSurfaces, "All"),
            CodeProvider = BoundedOrDefault(state.CodeProvider, "All mods"),
            CodeOtherProvider = BoundedOrDefault(state.CodeOtherProvider, "All mods"),
            ArchiveModFilter = BoundedOrDefault(state.ArchiveModFilter, string.Empty),
            ArchiveFileFilter = BoundedOrDefault(state.ArchiveFileFilter, string.Empty),
            Columns = NormalizeColumns(state.Columns),
            CodeDetailFraction = double.IsFinite(state.CodeDetailFraction) ? Math.Clamp(state.CodeDetailFraction, MinimumDetailFraction, MaximumDetailFraction) : 0.6,
            SelectedTab = state.SelectedTab is >= 0 and < TabCount ? state.SelectedTab : 0,
            HistoryReference = AllowedOrDefault(state.HistoryReference, HistoryReferences, "previous"),
            HistoryFilter = AllowedOrDefault(state.HistoryFilter, HistoryFilters, "Changes")
        };
    }

    private static ProfileColumnState[] NormalizeColumns(ProfileColumnState[]? columns)
    {
        if (columns is null) return [];
        List<ProfileColumnState> result = [];
        foreach (ProfileColumnState column in columns)
        {
            if (column is not null && CodeColumnKeys.Contains(column.Key, StringComparer.Ordinal) && !result.Any(value => value.Key == column.Key))
            {
                double width = double.IsFinite(column.Width) ? Math.Clamp(column.Width, MinimumColumnWidth, MaximumColumnWidth) : MinimumColumnWidth;
                string? sortDirection = column.SortDirection is not null && SortDirections.Contains(column.SortDirection, StringComparer.Ordinal) ? column.SortDirection : null;
                result.Add(new ProfileColumnState(column.Key, width, column.Visible, sortDirection, Math.Clamp(column.SortPriority, 0, 5)));
            }
        }
        return [.. result];
    }

    private static bool IsValidIdentity(ProfileViewState state) => Enum.IsDefined(state.ManagerKind) && IsBounded(state.InstallationId) && IsBounded(state.ProfileName) && !state.InstallationId.Contains('\0') && !state.ProfileName.Contains('\0');

    private static bool SameIdentity(ProfileViewState candidate, ProfileViewState expected) => candidate.ManagerKind == expected.ManagerKind && candidate.InstallationId == expected.InstallationId && candidate.ProfileName == expected.ProfileName;

    private static string BoundedOrDefault(string? value, string fallback) => value is { Length: <= MaximumTextLength } && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string AllowedOrDefault(string? value, string[] allowed, string fallback) => value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : fallback;

    private static bool IsBounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumTextLength;

    private sealed record ProfileViewStateDocument(int SchemaVersion, ProfileViewState? State);
}
