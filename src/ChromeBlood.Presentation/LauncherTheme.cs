using System.Windows;
using System.Windows.Media;

namespace ChromeBlood.Presentation;

public static class LauncherTheme
{
    public static void Apply(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        SetBrush(resources, "AccentBrush", Color.FromRgb(0xFF, 0x2E, 0x44));
        SetBrush(resources, "CrimsonBrush", Color.FromRgb(0xFF, 0x2E, 0x44));
        SetBrush(resources, "AccentTextBrush", Color.FromRgb(0x0A, 0x0B, 0x0D));
        SetBrush(resources, "ChromeBrush", Color.FromRgb(0xC9, 0xD7, 0xF0));
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is not SolidColorBrush)
            throw new InvalidOperationException($"Theme resource '{key}' is missing.");
        resources[key] = new SolidColorBrush(color);
    }
}
