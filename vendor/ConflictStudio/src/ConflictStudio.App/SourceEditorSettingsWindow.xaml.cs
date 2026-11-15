using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ConflictStudio.App;

public partial class SourceEditorSettingsWindow : Window
{
    private readonly SourceEditorPreferenceStore _store;
    public SourceEditorPreference Preference { get; private set; }

    public SourceEditorSettingsWindow(Window owner, SourceEditorPreferenceStore store) : this(store)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
    }

    internal SourceEditorSettingsWindow(SourceEditorPreferenceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        Preference = store.Load();
        InitializeComponent();
        EditorKindComboBox.Items.Add(new ComboBoxItem { Content = "Open with the normal Windows editor", Tag = SourceEditorKind.NormalOpen });
        EditorKindComboBox.Items.Add(new ComboBoxItem { Content = "Visual Studio Code", Tag = SourceEditorKind.VisualStudioCode });
        EditorKindComboBox.Items.Add(new ComboBoxItem { Content = "Notepad++", Tag = SourceEditorKind.NotepadPlusPlus });
        EditorKindComboBox.SelectedItem = EditorKindComboBox.Items.Cast<ComboBoxItem>().Single(value => Equals(value.Tag, Preference.Kind));
        EditorExecutableTextBox.Text = Preference.ExecutablePath ?? string.Empty;
        UpdateEditorFields();
    }

    private void EditorKindChanged(object sender, SelectionChangedEventArgs e) => UpdateEditorFields();

    private void UpdateEditorFields()
    {
        bool custom = SelectedKind() != SourceEditorKind.NormalOpen;
        EditorExecutableTextBox.IsEnabled = custom;
        BrowseEditorButton.IsEnabled = custom;
        EditorSettingsStatusTextBlock.Text = custom
            ? "Choose the editor's .exe file. Source files will open at the displayed line."
            : "Normal open uses the editor registered with Windows and cannot select a recorded line.";
    }

    private void BrowseEditorClicked(object sender, RoutedEventArgs e)
    {
        OpenFileDialog picker = new() { Title = "Choose the source editor executable", Filter = "Applications|*.exe", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) == true) EditorExecutableTextBox.Text = picker.FileName;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        SourceEditorKind kind = SelectedKind();
        string? executable = kind == SourceEditorKind.NormalOpen ? null : EditorExecutableTextBox.Text.Trim();
        if (kind != SourceEditorKind.NormalOpen && (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable)))
        {
            EditorSettingsStatusTextBlock.Text = "Choose an existing editor executable.";
            return;
        }
        SourceEditorPreference preference = new(kind, executable);
        if (!_store.TrySave(preference))
        {
            EditorSettingsStatusTextBlock.Text = "The editor preference could not be saved.";
            return;
        }
        Preference = preference;
        DialogResult = true;
    }

    private SourceEditorKind SelectedKind()
        => EditorKindComboBox.SelectedItem is ComboBoxItem { Tag: SourceEditorKind kind } ? kind : SourceEditorKind.NormalOpen;

    private void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
}
