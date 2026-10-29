using System.Windows.Controls;
using System.Windows.Media;

namespace ChromeBlood.Presentation;

public static class LauncherTextTracking
{
    public static void Apply(TextBlock label, double spacing)
    {
        ArgumentNullException.ThrowIfNull(label);
        TextEffectCollection effects = new();
        for (int position = 1; position < label.Text.Length; position++)
            effects.Add(new TextEffect(new TranslateTransform(position * spacing, 0), null, null, position, 1));
        label.TextEffects = effects;
    }
}
