using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed class Mo2ProfileWorkspaceViewModel
{
    public IReadOnlyList<Mo2Profile> Profiles { get; private set; } = [];
    public Mo2Profile? SelectedProfile { get; private set; }

    public void Discover(string mo2Root)
    {
        Profiles = Mo2ProfileDiscovery.Discover(mo2Root);
        string? selected = Mo2InstancePathResolver.Resolve(mo2Root).SelectedProfile;
        SelectedProfile = Profiles.FirstOrDefault(value => string.Equals(value.Name, selected, StringComparison.OrdinalIgnoreCase)) ?? (Profiles.Count > 0 ? Profiles[0] : null);
    }
}
