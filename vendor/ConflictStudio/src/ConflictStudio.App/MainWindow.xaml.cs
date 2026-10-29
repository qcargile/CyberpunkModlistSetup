using ConflictStudio.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ConflictStudio.App;

public partial class MainWindow : Window, IDisposable
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private readonly ArchiveOrderWorkspaceViewModel _workspace = new();
    private readonly Mo2ProfileWorkspaceViewModel _profiles = new();
    private readonly DiagnosticLog _diagnostics;
    private readonly WorkspacePreferenceStore _preferences;
    private readonly string _defaultVortexContextPath;
    private readonly string _decisionDirectory;
    private readonly ArchiveConflictTreeViewModel _archiveTree = new();
    private readonly List<DiagnosticAction> _sessionActions = [];
    private ModManagerKind _managerKind = ModManagerKind.Mo2;
    private string? _vortexContextPath;
    private ProfileScanReceipt? _receipt;
    private ConflictWorkItem[] _workItems = [];
    private SourceFileLocations? _sourceFileLocations;
    private CodeComparisonWindow? _codeComparisonWindow;
    private EvidenceDecision[] _decisions = [];
    private CancellationTokenSource? _scanCancellation;
    private bool _scanLocked;
    private bool _archiveOperationLocked;
    private Point _archiveDragStart;
    private string[] _draggedArchives = [];
    private string? _archivePressedForDrag;
    private string[] _archiveSelectionAtPress = [];
    private bool _archivePressedWasSelected;
    private bool _archiveSelectionModifier;
    private ArchiveConflictNode? _selectedArchiveNode;
    private ArchiveOrderProblemLane _orderProblemLane;
    private bool _syncingArchiveSelection;
    private bool _restoringWorkspace;
    private readonly DispatcherTimer _archiveFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private int _archiveFilterRevision;
    private ResourceProvider[] _archiveRelationshipResources = [];
    private ResourceProvider[] _archivePreviewResources = [];
    private ArchiveRailItem[] _archiveRailItems = [];
    private string[] _selectedArchiveNames = [];
    private ScrollViewer? _loadOrderScrollViewer;
    private ScrollViewer? _conflictScrollViewer;
    private bool _syncingArchiveScrollbars;
    private bool _syncingManagerMode;
    private readonly DispatcherTimer _archiveDragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private int _archiveDragScrollLines;
    private int _archiveDragWheelRemainder;
    private bool _archiveDragActive;
    private bool _previewingArchiveOrder;
    private bool _archivePreviewUnavailable;
    private bool _allowClose;
    private bool _applyingPendingOrderForClose;
    private string DefaultVortexContextPath => _defaultVortexContextPath;

    public MainWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cyberpunk Conflict Studio")) { }

    internal MainWindow(string applicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationData);
        _defaultVortexContextPath = Path.Combine(applicationData, "vortex", "context.json");
        _preferences = new WorkspacePreferenceStore(applicationData);
        _diagnostics = new DiagnosticLog(applicationData);
        _decisionDirectory = Path.Combine(applicationData, "decisions");
        InitializeComponent();
        DataContext = _workspace;
        InitializeInvestigation(applicationData);
        ArchiveOrderListBox.AllowDrop = true;
        ArchiveOrderListBox.PreviewMouseLeftButtonDown += ArchiveOrderMouseDown;
        ArchiveOrderListBox.MouseMove += ArchiveOrderMouseMove;
        ArchiveOrderListBox.DragOver += ArchiveOrderDragOver;
        ArchiveOrderListBox.DragLeave += ArchiveOrderDragLeave;
        ArchiveOrderListBox.Drop += ArchiveOrderDrop;
        ArchiveOrderListBox.PreviewMouseWheel += ArchiveOrderMouseWheel;
        ArchiveConflictTreeView.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(ConflictTreeExtentChanged));
        ArchiveConflictTreeView.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(ConflictTreeExtentChanged));
        Loaded += ConnectArchiveScrollbars;
        Loaded += (_, _) => Dispatcher.BeginInvoke(ConnectStyledScrollbars, DispatcherPriority.Loaded);
        MainTabControl.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ConnectStyledScrollbars, DispatcherPriority.Loaded);
        TechnicalEvidenceExpander.Expanded += (_, _) => Dispatcher.BeginInvoke(ConnectStyledScrollbars, DispatcherPriority.Loaded);
        _archiveFilterTimer.Tick += (_, _) =>
        {
            _archiveFilterTimer.Stop();
            ApplyArchiveTreeFilter();
        };
        _archiveDragScrollTimer.Tick += (_, _) => ScrollArchiveOrderDuringDrag();
        OpenSelectedArchiveButton.IsEnabled = false;
        HistoricalDiagnosticsTextBox.Text = _diagnostics.ReadRecent();
        Loaded += RestoreWorkspacePreference;
    }

    private async void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (RejectArchiveMutationWhileBusy("archive-apply")) return;
        SetArchiveOperationLocked(true);
        try
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            RecordAction("archive-apply", "started", "Applying the previewed order");
            await Task.Run(_workspace.ApplyOrder);
            RecordAction("archive-apply", "completed", "Order written, verified, and backed up");
            await ScanSelectedProfileAsync(true, true);
        }
        catch (Exception exception) { ShowError("archive-apply", exception); }
        finally { SetArchiveOperationLocked(false); }
    }

    private async void DiscoverProfilesClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            if (_managerKind == ModManagerKind.Vortex)
            {
                await ScanSelectedProfileAsync();
                return;
            }
            if (_managerKind == ModManagerKind.Manual)
            {
                RecordAction("profile-discovery", "started", "Opening the Cyberpunk installation folder picker");
                OpenFolderDialog gamePicker = new() { Title = "Choose the Cyberpunk 2077 installation folder", Multiselect = false };
                if (Directory.Exists(Mo2RootTextBox.Text)) gamePicker.InitialDirectory = Mo2RootTextBox.Text;
                if (gamePicker.ShowDialog(this) != true)
                {
                    RecordAction("profile-discovery", "cancelled", "Folder selection cancelled");
                    return;
                }
                string gameRoot = Path.GetFullPath(gamePicker.FolderName);
                if (!File.Exists(Path.Combine(gameRoot, "bin", "x64", "Cyberpunk2077.exe")) && !Directory.Exists(Path.Combine(gameRoot, "archive", "pc", "content"))) throw new DirectoryNotFoundException("Choose the Cyberpunk 2077 installation folder.");
                Mo2RootTextBox.Text = gameRoot;
                ManualProfileOption option = new("Deployed game", gameRoot);
                ProfileComboBox.ItemsSource = new[] { option };
                ProfileComboBox.SelectedItem = option;
                WorkspaceStatusTextBlock.Text = "Deployed Cyberpunk files selected.";
                RecordAction("profile-discovery", "completed", gameRoot);
                return;
            }
            RecordAction("profile-discovery", "started", "Opening the MO2 folder picker");
            OpenFolderDialog picker = new() { Title = "Choose the Mod Organizer 2 installation folder", Multiselect = false };
            if (Directory.Exists(Mo2RootTextBox.Text)) picker.InitialDirectory = Mo2RootTextBox.Text;
            if (picker.ShowDialog(this) != true)
            {
                RecordAction("profile-discovery", "cancelled", "Folder selection cancelled");
                return;
            }
            Mo2RootTextBox.Text = picker.FolderName;
            _profiles.Discover(Mo2RootTextBox.Text);
            ProfileComboBox.ItemsSource = _profiles.Profiles;
            WorkspaceStatusTextBlock.Text = $"Found {_profiles.Profiles.Count} profile{(_profiles.Profiles.Count == 1 ? string.Empty : "s")}.";
            ProfileComboBox.SelectedItem = _profiles.SelectedProfile;
            RecordAction("profile-discovery", "completed", $"Found {_profiles.Profiles.Count} profiles");
        }
        catch (Exception exception) { ShowError("profile-discovery", exception); }
    }

    private async void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is VortexProfileOption vortex)
        {
            InvalidateReceipt();
            _managerKind = ModManagerKind.Vortex;
            _vortexContextPath = vortex.ContextPath;
            ProfileBadgeTextBlock.Text = vortex.Name.ToUpperInvariant();
            WorkspaceStatusTextBlock.Text = "Vortex profile selected. Preparing the conflict scan…";
            if (!_restoringWorkspace) await ScanSelectedProfileAsync();
            return;
        }
        if (ProfileComboBox.SelectedItem is ManualProfileOption manual)
        {
            InvalidateReceipt();
            _managerKind = ModManagerKind.Manual;
            ProfileBadgeTextBlock.Text = manual.Name.ToUpperInvariant();
            WorkspaceStatusTextBlock.Text = "Deployed game files selected. Preparing the conflict scan…";
            _preferences.TrySave(new WorkspacePreference(manual.GameRoot, manual.Name, ModManagerKind.Manual));
            if (!_restoringWorkspace) await ScanSelectedProfileAsync();
            return;
        }
        if (ProfileComboBox.SelectedItem is not Mo2Profile profile) return;
        InvalidateReceipt();
        ProfileBadgeTextBlock.Text = profile.Name.ToUpperInvariant();
        WorkspaceStatusTextBlock.Text = "Profile selected. Preparing the conflict scan…";
        if (Directory.Exists(Mo2RootTextBox.Text) && !_preferences.TrySave(new WorkspacePreference(Mo2RootTextBox.Text, profile.Name))) FooterStatusTextBlock.Text = "Profile selected, but this choice could not be saved for next time.";
        if (!_restoringWorkspace && Directory.Exists(Mo2RootTextBox.Text)) await ScanSelectedProfileAsync();
    }

    private void Mo2RootChanged(object sender, TextChangedEventArgs e)
    {
        if (!_scanLocked) InvalidateReceipt();
    }

    private async void ManagerModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingManagerMode) return;
        if (ManagerModeComboBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string value || !Enum.TryParse(value, true, out ModManagerKind kind)) return;
        _managerKind = kind;
        if (ManagerLocationLabel is null || ProfileComboBox is null) return;
        ManagerLocationLabel.Text = kind switch { ModManagerKind.Vortex => "VORTEX PROFILE INFORMATION", ModManagerKind.Manual => "CYBERPUNK INSTALLATION", _ => "MO2 INSTALLATION" };
        InvalidateReceipt();
        ProfileComboBox.ItemsSource = null;
        if (kind == ModManagerKind.Vortex)
        {
            WorkspacePreference? preference = _preferences.Load();
            _vortexContextPath = preference?.ManagerKind == ModManagerKind.Vortex && !string.IsNullOrWhiteSpace(preference.ContextPath)
                ? preference.ContextPath : _vortexContextPath ?? DefaultVortexContextPath;
            Mo2RootTextBox.Text = _vortexContextPath;
            FindProfilesButton.Content = "Connect";
            await ScanSelectedProfileAsync();
        }
        else if (kind == ModManagerKind.Manual && !string.IsNullOrWhiteSpace(Mo2RootTextBox.Text) && (Directory.Exists(Path.Combine(Mo2RootTextBox.Text, "archive", "pc", "content")) || File.Exists(Path.Combine(Mo2RootTextBox.Text, "bin", "x64", "Cyberpunk2077.exe"))))
        {
            FindProfilesButton.Content = "Browse";
            ManualProfileOption option = new("Deployed game", Path.GetFullPath(Mo2RootTextBox.Text));
            ProfileComboBox.ItemsSource = new[] { option };
            ProfileComboBox.SelectedItem = option;
        }
        else
        {
            FindProfilesButton.Content = "Browse";
            WorkspaceStatusTextBlock.Text = kind == ModManagerKind.Manual ? "Choose the Cyberpunk installation folder." : "Choose or launch from an MO2 installation.";
        }
    }

    private async void RestoreWorkspacePreference(object sender, RoutedEventArgs e)
        => await RestoreWorkspaceAsync();

    private async Task RestoreWorkspaceAsync(ApplicationLaunchOptions? launchOptions = null)
    {
        try
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            WorkspacePreference? preference = _preferences.Load();
            ApplicationLaunchOptions options = launchOptions ?? ApplicationLaunchOptions.Parse(Environment.GetCommandLineArgs());
            if (string.Equals(options.Manager, "vortex", StringComparison.OrdinalIgnoreCase)
                || options.Manager is null && options.VortexContext is not null
                || options.Manager is null && options.Root is null && options.Profile is null && preference?.ManagerKind == ModManagerKind.Vortex)
            {
                _restoringWorkspace = true;
                SelectManagerMode(ModManagerKind.Vortex);
                _vortexContextPath = options.VortexContext ?? preference?.ContextPath ?? DefaultVortexContextPath;
                Mo2RootTextBox.Text = _vortexContextPath;
                FindProfilesButton.Content = "Connect";
                _restoringWorkspace = false;
                await ScanSelectedProfileAsync();
                return;
            }
            ManagerStartupSelection? selection = string.Equals(options.Manager, "manual", StringComparison.OrdinalIgnoreCase)
                    ? ManagerStartupResolver.ResolveManual(options, preference)
                    : ManagerStartupResolver.ResolveMo2(options, Environment.CurrentDirectory, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ConflictStudio.exe"), preference)
                        ?? ManagerStartupResolver.ResolveManual(options, preference);
            if (selection is null && options.Manager is null && File.Exists(DefaultVortexContextPath))
            {
                SelectManagerMode(ModManagerKind.Vortex);
                Mo2RootTextBox.Text = DefaultVortexContextPath;
                FindProfilesButton.Content = "Connect";
                await ScanSelectedProfileAsync();
                return;
            }
            if (selection is null) return;
            _restoringWorkspace = true;
            if (selection.ManagerKind == ModManagerKind.Manual)
            {
                SelectManagerMode(ModManagerKind.Manual);
                Mo2RootTextBox.Text = selection.Root;
                ManualProfileOption option = new(selection.ProfileName, selection.Root);
                ProfileComboBox.ItemsSource = new[] { option };
                ProfileComboBox.SelectedItem = option;
                _restoringWorkspace = false;
                await ScanSelectedProfileAsync();
                return;
            }
            SelectManagerMode(ModManagerKind.Mo2);
            Mo2RootTextBox.Text = selection.Root;
            _profiles.Discover(selection.Root);
            ProfileComboBox.ItemsSource = _profiles.Profiles;
            ProfileComboBox.SelectedItem = _profiles.Profiles.FirstOrDefault(value => value.Name == selection.ProfileName) ?? (_profiles.Profiles.Count > 0 ? _profiles.Profiles[0] : null);
            _restoringWorkspace = false;
            if (ProfileComboBox.SelectedItem is Mo2Profile) await ScanSelectedProfileAsync();
        }
        catch (Exception exception) { ShowError("workspace-restore", exception); }
        finally { _restoringWorkspace = false; }
    }

    private async void UnifiedScanClicked(object sender, RoutedEventArgs e) => await ScanSelectedProfileAsync();

    private async Task ScanSelectedProfileAsync(bool preserveArchiveUndo = false, bool throwOnFailure = false)
    {
        if (_scanLocked || _archiveOperationLocked && !throwOnFailure)
        {
            RecordAction("profile-scan", "blocked", "Another workspace operation is running");
            WorkspaceStatusTextBlock.Text = "Wait for the current operation to finish.";
            return;
        }
        CancellationTokenSource cancellation = new();
        _scanCancellation = cancellation;
        SetScanLocked(true);
        try
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            string profileName = _managerKind == ModManagerKind.Vortex ? "Active Vortex profile" : SelectedProfileName();
            RecordAction("profile-scan", "started", profileName);
            ExportButton.IsEnabled = false;
            WorkspaceStatusTextBlock.Text = "Scanning the current profile…";
            Progress<ScanProgress> progress = new(UpdateProgress);
            ProfileScanReceipt receipt;
            if (_managerKind == ModManagerKind.Vortex)
            {
                string contextPath = Path.GetFullPath(string.IsNullOrWhiteSpace(Mo2RootTextBox.Text) ? _vortexContextPath ?? DefaultVortexContextPath : Mo2RootTextBox.Text);
                WorkspaceStatusTextBlock.Text = "Requesting the active Vortex profile…";
                await RefreshVortexContextAsync(contextPath, cancellation.Token);
                _vortexContextPath = contextPath;
                bool wasRestoring = _restoringWorkspace;
                _restoringWorkspace = true;
                try { LoadVortexContext(contextPath); }
                finally { _restoringWorkspace = wasRestoring; }
                WorkspaceStatusTextBlock.Text = "Scanning the current profile…";
                receipt = await Task.Run(() => ProfileScanCoordinator.ScanVortex(contextPath, DateTimeOffset.UtcNow, progress, cancellation.Token));
                LoadReceipt(receipt, contextPath, ProfileComboBox.SelectedItem ?? throw new InvalidOperationException("Select a Vortex profile first."), preserveArchiveUndo);
            }
            else if (_managerKind == ModManagerKind.Manual)
            {
                ManualProfileOption manual = ProfileComboBox.SelectedItem as ManualProfileOption ?? throw new InvalidOperationException("Choose the Cyberpunk installation folder first.");
                receipt = await Task.Run(() => ProfileScanCoordinator.ScanManual(manual.GameRoot, DateTimeOffset.UtcNow, progress, cancellation.Token));
                LoadReceipt(receipt, manual.GameRoot, manual, preserveArchiveUndo);
            }
            else
            {
                Mo2Profile profile = SelectedProfile();
                string mo2Root = Mo2RootTextBox.Text;
                receipt = await Task.Run(() => ProfileScanCoordinator.Scan(mo2Root, profile, DateTimeOffset.UtcNow, progress, cancellation.Token));
                LoadReceipt(receipt, mo2Root, profile, preserveArchiveUndo);
            }
            RecordAction("profile-scan", "completed", $"{receipt.ResourceConflicts.Length:N0} archive conflicts; {CodeCaseWorkspace.Counts(CodeWorkItems()).NeedsDecision:N0} code decisions");
            CompleteScanPresentation();
        }
        catch (OperationCanceledException)
        {
            WorkspaceStatusTextBlock.Text = "Scan cancelled. No incomplete scan results were saved.";
            FooterStatusTextBlock.Text = "Cancelled";
            RecordAction("profile-scan", "cancelled", "No partial receipt was saved");
        }
        catch (Exception exception)
        {
            if (throwOnFailure) throw;
            InvalidateMixedManagerReceiptAndShowScanError(exception);
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation)) _scanCancellation = null;
            cancellation.Dispose();
            SetScanLocked(false);
            ExportButton.IsEnabled = _receipt is not null && !_historyBusy && _noteSaveTask.IsCompleted;
        }
    }

    private void CancelScanClicked(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is null)
        {
            RecordAction("profile-scan-cancel", "blocked", "No scan is running");
            return;
        }
        RecordAction("profile-scan-cancel", "requested", "Cancellation requested");
        _scanCancellation.Cancel();
    }

    private void QueueFilterChanged(object sender, EventArgs e)
    {
        if (_restoringView) return;
        ApplyQueueFilter();
        ScheduleViewSave();
    }

    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFindingNavigation();
        Dispatcher.BeginInvoke(ConnectStyledScrollbars, DispatcherPriority.Loaded);
        ConflictWorkItem[] selected = WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().ToArray();
        ViewCodeButton.IsEnabled = selected.Length == 1 && selected[0].Comparisons.Length > 0;
        ViewCodeButton.ToolTip = selected.Length != 1 ? "Select one finding to view its supporting code." : ViewCodeButton.IsEnabled
            ? "Compare the exact operations supporting this finding."
            : "No exact supporting comparison was recorded for this finding. Its files remain available below.";
        SelectedFileComboBox.ItemsSource = _sourceFileLocations?.ForItems(selected) ?? [];
        SelectedFileComboBox.SelectedIndex = SelectedFileComboBox.Items.Count > 0 ? 0 : -1;
        SelectedFilesPanel.Visibility = SelectedFileComboBox.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected.Length == 0)
        {
            PresentReviewContext(selected);
            SelectedClassificationTextBlock.Text = "Select a case";
            SelectedTargetTextBlock.Text = "Select a row to see details";
            SelectedMeaningTextBlock.Text = string.Empty;
            SelectedSummaryTextBlock.Text = string.Empty;
            SelectedProofTextBlock.Text = string.Empty;
            SelectedBoundaryTextBlock.Text = string.Empty;
            SelectedProvidersTextBlock.Text = string.Empty;
            SelectedActionTextBlock.Text = string.Empty;
            SelectedHashTextBlock.Text = string.Empty;
            ExpertEvidenceTextBox.Text = string.Empty;
            TechnicalEvidenceExpander.IsExpanded = false;
            CopyEvidenceButton.IsEnabled = false;
            SelectedProviderComboBox.ItemsSource = null;
            ReviewOutcomeComboBox.SelectedIndex = -1;
            ReviewRationaleTextBox.Text = string.Empty;
            SaveReviewButton.Content = "Save review";
            ReopenButton.Content = "Reopen";
            SaveReviewButton.IsEnabled = false;
            ReopenButton.IsEnabled = false;
            return;
        }
        SaveReviewButton.IsEnabled = !_historyBusy && _noteSaveTask.IsCompleted && selected.All(value => value.Classification != EvidenceClassification.Unresolved);
        int reopenableCount = selected.Count(IsReviewed);
        ReopenButton.IsEnabled = reopenableCount > 0;
        if (selected.Length > 1)
        {
            PresentReviewContext(selected);
            string[] providers = selected.SelectMany(value => value.Providers).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            SelectedClassificationTextBlock.Text = "MULTIPLE CASES";
            SelectedTargetTextBlock.Text = $"{selected.Length:N0} cases selected";
            SelectedMeaningTextBlock.Text = "The selected items may describe different kinds of overlap and need different decisions.";
            SelectedSummaryTextBlock.Text = string.Join(" · ", selected.GroupBy(value => value.ClassificationLabel).OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase).Select(value => $"{value.Count():N0} {value.Key}"));
            SelectedProofTextBlock.Text = "Saving applies the same review choice to every selected item.";
            SelectedBoundaryTextBlock.Text = "Do not give different problems the same answer. Check each item separately if you are unsure.";
            SelectedProvidersTextBlock.Text = string.Join("  ↔  ", providers);
            SelectedActionTextBlock.Text = "Save one review choice only if it fits every selected item. Otherwise, select fewer items first.";
            SelectedHashTextBlock.Text = $"{selected.Length:N0} case IDs selected";
            ReviewRationaleTextBox.Text = string.Empty;
            ReviewOutcomeComboBox.SelectedIndex = -1;
            SelectedProviderComboBox.ItemsSource = providers;
            SelectedProviderComboBox.SelectedIndex = providers.Length > 0 ? 0 : -1;
            ExpertEvidenceTextBox.Text = string.Join(Environment.NewLine + Environment.NewLine, selected.Select(value => value.Target + Environment.NewLine + ExactEvidence(value)));
            TechnicalEvidenceExpander.IsExpanded = false;
            CopyEvidenceButton.IsEnabled = true;
            SaveReviewButton.Content = $"Save review ({selected.Length:N0})";
            ReopenButton.Content = reopenableCount == 0 ? "Reopen" : $"Reopen ({reopenableCount:N0})";
            return;
        }
        ConflictWorkItem item = selected[0];
        SelectedClassificationTextBlock.Text = string.IsNullOrWhiteSpace(item.AnalysisLabel) ? item.ClassificationLabel : item.AnalysisLabel;
        SelectedTargetTextBlock.Text = item.Target;
        SelectedMeaningTextBlock.Text = item.MeaningLabel;
        SelectedSummaryTextBlock.Text = item.Summary;
        SelectedProofTextBlock.Text = item.ProofLabel;
        SelectedBoundaryTextBlock.Text = item.BoundaryLabel;
        SelectedProvidersTextBlock.Text = string.Join("  ↔  ", item.Providers);
        SelectedActionTextBlock.Text = item.NextAction;
        SelectedHashTextBlock.Text = "Case ID: " + item.EvidenceSha256;
        ReviewRationaleTextBox.Text = string.Empty;
        ReviewOutcomeComboBox.SelectedIndex = -1;
        if (item.ReviewRationale is not null)
        {
            ComboBoxItem? outcome = ReviewOutcomeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(value => item.ReviewRationale.StartsWith(value.Tag?.ToString() ?? string.Empty, StringComparison.Ordinal));
            ReviewOutcomeComboBox.SelectedItem = outcome;
            if (outcome?.Tag?.ToString() is string outcomeName) ReviewRationaleTextBox.Text = CodeCaseWorkspace.ReviewNotes(item.ReviewRationale, outcomeName);
        }
        SelectedProviderComboBox.ItemsSource = item.Providers;
        SelectedProviderComboBox.SelectedIndex = item.Providers.Length > 0 ? 0 : -1;
        ExpertEvidenceTextBox.Text = ExactEvidence(item);
        CopyEvidenceButton.IsEnabled = true;
        SaveReviewButton.Content = "Save review";
        ReopenButton.Content = "Reopen";
        PresentReviewContext(selected);
    }

    private void ReviewClicked(object sender, RoutedEventArgs e)
    {
        if (_historyBusy || !_noteSaveTask.IsCompleted) return;
        string? outcome = (ReviewOutcomeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(outcome))
        {
            ErrorTextBlock.Text = "Choose a review outcome first.";
            ErrorTextBlock.Visibility = Visibility.Visible;
            RecordAction("review-finding", "blocked", "No review outcome was selected");
            ReviewOutcomeComboBox.Focus();
            return;
        }
        Execute("review-finding", () =>
        {
            SaveSelectedReviewsAndReload(outcome, ReviewRationaleTextBox.Text);
        });
    }

    private void ReopenClicked(object sender, RoutedEventArgs e)
    {
        Execute("reopen-finding", () =>
        {
            ReopenSelectedReviewsAndReload();
        });
    }

    private int SaveSelectedReviewsAndReload(string outcome, string notes)
    {
        ConflictWorkItem[] selected = WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().ToArray();
        EvidenceDecisionBatchResult result = SaveSelectedReviews(outcome, notes);
        ReloadQueue(selected[0]);
        WorkspaceStatusTextBlock.Text = result.ChangedCount == 1 ? $"Saved review for {selected[0].Target}" : $"Saved review for {result.ChangedCount:N0} cases";
        return result.ChangedCount;
    }

    private int ReopenSelectedReviewsAndReload()
    {
        ConflictWorkItem[] reopenable = WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().Where(IsReviewed).ToArray();
        if (reopenable.Length == 0) throw new InvalidOperationException("None of the selected cases has a saved review.");
        EvidenceDecisionBatchResult result = ReopenSelectedReviews(reopenable);
        ReloadQueue(reopenable[0]);
        WorkspaceStatusTextBlock.Text = result.ChangedCount == 1 ? $"Reopened {reopenable[0].Target}" : $"Reopened {result.ChangedCount:N0} cases";
        return result.ChangedCount;
    }

    private EvidenceDecisionBatchResult SaveSelectedReviews(string outcome, string notes)
    {
        ConflictWorkItem[] selected = WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().ToArray();
        if (_receipt is null || selected.Length == 0) throw new InvalidOperationException("Select at least one finding first.");
        if (_receipt.InstallationId is null) throw new InvalidOperationException("The scan has no installation identity.");
        string rationale = CodeCaseWorkspace.ReviewRationale(outcome, notes);
        EvidenceDecisionBatchResult result = new EvidenceDecisionStore(_decisionDirectory).ReviewMany(_receipt.InstallationId, _receipt.ProfileName, selected, rationale, DateTimeOffset.UtcNow);
        _decisions = result.Decisions;
        return result;
    }

    private EvidenceDecisionBatchResult ReopenSelectedReviews(ConflictWorkItem[] selected)
    {
        if (_receipt is null || selected.Length == 0) throw new InvalidOperationException("Select at least one reviewed finding first.");
        if (_receipt.InstallationId is null) throw new InvalidOperationException("The scan has no installation identity.");
        EvidenceDecisionBatchResult result = new EvidenceDecisionStore(_decisionDirectory).ReopenMany(_receipt.InstallationId, _receipt.ProfileName, selected);
        _decisions = result.Decisions;
        return result;
    }

    private static bool IsReviewed(ConflictWorkItem item) => item.ReviewRationale is not null || item.Classification == EvidenceClassification.Intentional;

    private async void UndoArchiveOrderClicked(object sender, RoutedEventArgs e)
    {
        if (RejectArchiveMutationWhileBusy("archive-undo")) return;
        SetArchiveOperationLocked(true);
        try
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            RecordAction("archive-undo", "started", "Restoring the last archive-order backup");
            await Task.Run(_workspace.UndoLastApply);
            RecordAction("archive-undo", "completed", "Backup restored and verified");
            await ScanSelectedProfileAsync(false, true);
        }
        catch (Exception exception) { ShowError("archive-undo", exception); }
        finally { SetArchiveOperationLocked(false); }
    }

    private void ResetArchiveOrderClicked(object sender, RoutedEventArgs e)
    {
        Execute("archive-order-reset", () =>
        {
            string[] selected = _selectedArchiveNames;
            _workspace.ResetProposedOrder();
            _previewingArchiveOrder = false;
            _archivePreviewUnavailable = false;
            if (_receipt is not null)
            {
                if (_workspace.CanApply) RefreshArchiveConflictPreview();
                else
                {
                    _archiveTree.Load(_receipt.ArchiveSummaries ?? []);
                    PresentArchiveOrderEvidence(_receipt.ArchiveOrderEvidence ?? new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, null, null, "Archive order evidence is unavailable."));
                }
            }
            RefreshArchiveRail();
            ApplyArchiveTreeFilter();
            SelectArchivesInLoadOrder(selected);
            SetOverviewSelection(selected);
            WorkspaceStatusTextBlock.Text = "Restored the scanned archive order in the preview.";
        });
    }

    private void FocusArchiveLoadOrderClicked(object sender, RoutedEventArgs e)
    {
        Execute("archive-order-guidance", () =>
        {
            ProfileScanReceipt? receipt = _receipt;
            if (receipt?.ArchiveOrderEvidence is not { } evidence) return;
            if (ArchiveOrderGuidance.OpensPreparedOrder(evidence))
            {
                _workspace.PreviewOrder();
                WorkspaceStatusTextBlock.Text = _workspace.PreviewStatus;
                if (_workspace.CanApply)
                {
                    ApplyArchiveOrderButton.BringIntoView();
                    ApplyArchiveOrderButton.Focus();
                }
                return;
            }
            if (evidence.IncompleteArchiveLane != ArchiveOrderProblemLane.None)
            {
                SupportTab.IsSelected = true;
                UpdateSupportSurface();
                Dispatcher.BeginInvoke(DiagnosticsTextBox.Focus, DispatcherPriority.Input);
                return;
            }
            string instruction = ArchiveOrderGuidance.Instruction(evidence, receipt.ManagerKind);
            ArchiveOrderEvidenceTextBlock.Text = instruction;
            ArchiveOrderEvidenceTextBlock.Visibility = Visibility.Visible;
            WorkspaceStatusTextBlock.Text = instruction;
            if (_orderProblemLane == ArchiveOrderProblemLane.Legacy)
            {
                ArchiveOrderListBox.Focus();
                if (ArchiveOrderListBox.Items.Count > 0 && ArchiveOrderListBox.SelectedIndex < 0) ArchiveOrderListBox.SelectedIndex = 0;
            }
        });
    }

    private void ArchiveSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = ArchiveSearchTextBox.Text.Trim();
        if (query.Length == 0) return;
        ArchiveRailItem? match = ArchiveOrderListBox.Items.Cast<ArchiveRailItem>().FirstOrDefault(value => value.ArchiveName.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;
        ArchiveOrderListBox.SelectedItem = match;
        ArchiveOrderListBox.ScrollIntoView(match);
    }

    private void ArchiveTreeFilterChanged(object sender, EventArgs e)
    {
        if (_restoringView) return;
        _archiveFilterTimer.Stop();
        _archiveFilterTimer.Start();
        ScheduleViewSave();
    }

    private void ClearArchiveFiltersClicked(object sender, RoutedEventArgs e)
    {
        ArchiveModFilterTextBox.Text = string.Empty;
        ArchiveFileFilterTextBox.Text = string.Empty;
        ShowNonConflictingFilesCheckBox.IsChecked = false;
        OnlyConflictingArchivesCheckBox.IsChecked = false;
        _archiveFilterTimer.Stop();
        ApplyArchiveTreeFilter();
    }

    private void ArchiveConflictTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch (e.NewValue)
        {
            case ArchiveConflictNode archive:
                _selectedArchiveNode = archive;
                OpenSelectedArchiveButton.IsEnabled = true;
                if (!_syncingArchiveSelection)
                {
                    SelectArchiveInLoadOrder(archive.ArchiveName);
                    SetOverviewSelection(archive.ArchiveName);
                }
                ArchiveSelectedTitleTextBlock.Text = $"{archive.ArchiveName}  ·  {archive.Provider}";
                ArchiveSelectedMeaningTextBlock.Text = archive.CountSummary + ". Expand a result group to inspect exact files inline.";
                ArchiveSelectedTechnicalTextBlock.Text = $"Load position: {archive.OrderPosition}\nPhysical archive: {archive.PhysicalPath ?? "unavailable"}";
                break;
            case ArchiveConflictGroupNode group:
                ArchiveSelectedTitleTextBlock.Text = group.Header;
                ArchiveSelectedMeaningTextBlock.Text = group.Tone switch { ArchiveTreeTone.Winning => "The current order selects this archive's copies of these files.", ArchiveTreeTone.Losing => "A higher archive supplies these files instead.", ArchiveTreeTone.Same => "These files have exactly the same content as the winning copies.", ArchiveTreeTone.Unknown => "Resolve the named archive problem before deciding which copy should take priority.", _ => "Only this archive contains these files." };
                ArchiveSelectedTechnicalTextBlock.Text = string.Empty;
                break;
            case ArchiveResourceNode resource:
                ArchiveSelectedTitleTextBlock.Text = resource.Path;
                ArchiveSelectedMeaningTextBlock.Text = resource.PlainMeaning + " " + resource.ProviderContext;
                ArchiveSelectedTechnicalTextBlock.Text = resource.TechnicalEvidence;
                break;
        }
    }

    private void ArchiveOrderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingArchiveSelection) return;
        string[] selected = ArchiveOrderListBox.SelectedItems.Cast<ArchiveRailItem>().Select(value => value.ArchiveName).ToArray();
        SetOverviewSelection(selected);
        if (selected.Length == 1) SelectArchiveInConflictTree(selected[0]);
    }

    private void SelectArchiveInLoadOrder(string archiveName)
        => SelectArchivesInLoadOrder([archiveName]);

    private void SelectArchivesInLoadOrder(string[] archiveNames)
    {
        bool previous = _syncingArchiveSelection;
        _syncingArchiveSelection = true;
        try
        {
            ArchiveOrderListBox.SelectedItems.Clear();
            foreach (string archiveName in archiveNames)
            {
                ArchiveRailItem? item = ArchiveOrderListBox.Items.Cast<ArchiveRailItem>().FirstOrDefault(value => string.Equals(value.ArchiveName, archiveName, StringComparison.OrdinalIgnoreCase));
                if (item is not null) ArchiveOrderListBox.SelectedItems.Add(item);
            }
            if (archiveNames.Length > 0)
            {
                ArchiveRailItem? first = ArchiveOrderListBox.Items.Cast<ArchiveRailItem>().FirstOrDefault(value => string.Equals(value.ArchiveName, archiveNames[0], StringComparison.OrdinalIgnoreCase));
                if (first is not null) ArchiveOrderListBox.ScrollIntoView(first);
            }
        }
        finally { _syncingArchiveSelection = previous; }
    }

    private void SelectArchiveInConflictTree(string archiveName)
    {
        _syncingArchiveSelection = true;
        try
        {
            ArchiveConflictNode? archive = _archiveTree.Find(archiveName);
            if (archive is null)
            {
                ArchiveModFilterTextBox.Text = string.Empty;
                ArchiveFileFilterTextBox.Text = string.Empty;
                ApplyArchiveTreeFilter();
                archive = _archiveTree.Find(archiveName);
            }
            if (archive is null) return;
            SelectVisibleArchive(archive, true);
            SelectArchiveInLoadOrder(archiveName);
            SetOverviewSelection(archiveName);
        }
        finally { _syncingArchiveSelection = false; }
    }

    private void SetOverviewSelection(string? archiveName) => SetOverviewSelection(archiveName is null ? [] : [archiveName]);

    private void SetOverviewSelection(IReadOnlyList<string> archiveNames)
    {
        string[] selected = archiveNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _selectedArchiveNames = selected;
        LoadOrderOverviewBar.SelectedArchives = selected;
        ConflictOverviewBar.SelectedArchives = selected;
        RefreshRelationshipOverviews(selected);
    }

    private void SelectVisibleArchive(ArchiveConflictNode archive, bool focus)
    {
        ArchiveConflictTreeView.UpdateLayout();
        if (ArchiveConflictTreeView.ItemContainerGenerator.ContainerFromItem(archive) is not TreeViewItem item) return;
        item.IsSelected = true;
        item.BringIntoView();
        if (focus) item.Focus();
    }

    private void ArchiveConflictTreeDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (ArchiveConflictTreeView.SelectedItem is ArchiveResourceNode resource)
        {
            Execute("copy-resource-path", () =>
            {
                Clipboard.SetText(resource.Path);
                FooterStatusTextBlock.Text = "Copied resource path";
            });
        }
    }

    private void OpenSelectedArchiveClicked(object sender, RoutedEventArgs e)
    {
        Execute("open-archive", () =>
        {
            if (_selectedArchiveNode?.PhysicalPath is not string path || !File.Exists(path)) throw new FileNotFoundException("The selected archive is not available in the current deployment view.");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        });
    }

    private void CopyActionClicked(object sender, RoutedEventArgs e)
    {
        Execute("copy-next-step", () =>
        {
            ConflictWorkItem[] selected = WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("Select at least one code case first.");
            Clipboard.SetText(SelectedActionTextBlock.Text);
            FooterStatusTextBlock.Text = selected.Length == 1 ? "Copied the next step" : $"Copied the review instructions for {selected.Length:N0} items";
        });
    }

    private void SelectedFileChanged(object sender, SelectionChangedEventArgs e)
    {
        SourceFileLocation? file = SelectedFileComboBox.SelectedItem as SourceFileLocation;
        SelectedFilePathTextBox.Text = file?.PhysicalPath ?? "Scan the profile again to locate this file.";
        OpenSourceFileButton.IsEnabled = file?.PhysicalPath is not null;
        ShowSourceFileButton.IsEnabled = file?.PhysicalPath is not null;
        CopySourcePathButton.IsEnabled = file?.PhysicalPath is not null;
    }

    private void OpenSourceFileClicked(object sender, RoutedEventArgs e)
    {
        Execute("open-source-file", () =>
        {
            SourceFileLocation file = SelectedFileComboBox.SelectedItem as SourceFileLocation ?? throw new InvalidOperationException("Select a file first.");
            SourceFileActions.OpenEditor(file);
        });
    }

    private void ShowSourceFileClicked(object sender, RoutedEventArgs e)
    {
        Execute("show-source-file", () =>
        {
            SourceFileLocation file = SelectedFileComboBox.SelectedItem as SourceFileLocation ?? throw new InvalidOperationException("Select a file first.");
            SourceFileActions.ShowInFolder(file);
        });
    }

    private void CopySourcePathClicked(object sender, RoutedEventArgs e)
    {
        Execute("copy-source-path", () =>
        {
            if (SelectedFileComboBox.SelectedItem is not SourceFileLocation { PhysicalPath: string path }) throw new InvalidOperationException("Select a file with a recorded location first.");
            Clipboard.SetText(path);
            FooterStatusTextBlock.Text = "Copied file location";
        });
    }

    private void ViewCodeClicked(object sender, RoutedEventArgs e)
    {
        Execute("view-code", () =>
        {
            if (WorkQueueDataGrid.SelectedItems.Count != 1 || WorkQueueDataGrid.SelectedItem is not ConflictWorkItem item) throw new InvalidOperationException("Select one finding to compare its source code.");
            CodeFindingWitness[] comparisons = item.Comparisons;
            if (comparisons.Length == 0) throw new InvalidOperationException("The exact supporting operations are unavailable for this finding. Run a fresh scan or open its files.");
            _codeComparisonWindow?.Close();
            CodeComparisonWindow window = new(this, item, comparisons, _receipt?.CodeEvidence, _receipt?.TweakReferences, new SourceEditorPreferenceStore(_applicationDataDirectory));
            _codeComparisonWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_codeComparisonWindow, window)) _codeComparisonWindow = null; };
            window.Show();
        });
    }

    private void OpenCodeProviderClicked(object sender, RoutedEventArgs e)
    {
        Execute("open-code-provider", () =>
        {
            if (SelectedProviderComboBox.SelectedItem is not string provider) throw new InvalidOperationException("Select a mod first.");
            string directory;
            if (_managerKind == ModManagerKind.Vortex)
            {
                string contextPath = _vortexContextPath ?? throw new InvalidOperationException("The Vortex bridge context is unavailable.");
                VortexManagerContext context = VortexManagerContextStore.Read(contextPath);
                directory = string.Equals(provider, "Game directory", StringComparison.OrdinalIgnoreCase) ? context.GameRoot : context.Providers.FirstOrDefault(value => string.Equals(value.Name, provider, StringComparison.OrdinalIgnoreCase))?.RootPath ?? throw new DirectoryNotFoundException($"The active Vortex provider folder could not be found: {provider}");
            }
            else if (_managerKind == ModManagerKind.Manual)
            {
                directory = Path.GetFullPath(Mo2RootTextBox.Text);
            }
            else
            {
                Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(Mo2RootTextBox.Text);
                directory = provider == "Overwrite" ? paths.OverwriteRoot : Path.Combine(paths.ModsRoot, provider);
            }
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"The active provider folder could not be found: {provider}");
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        });
    }

    private void ShowTechnicalEvidenceClicked(object sender, RoutedEventArgs e)
    {
        Execute("show-technical-evidence", () =>
        {
            if (WorkQueueDataGrid.SelectedItem is not ConflictWorkItem) throw new InvalidOperationException("Select a code case first.");
            ShowTechnicalEvidence();
        });
    }

    private void ShowTechnicalEvidence()
    {
        TechnicalEvidenceExpander.IsExpanded = true;
        TechnicalEvidenceExpander.UpdateLayout();
        TechnicalEvidenceExpander.BringIntoView();
    }

    private void CopyEvidenceClicked(object sender, RoutedEventArgs e)
    {
        Execute("copy-technical-evidence", () =>
        {
            if (string.IsNullOrWhiteSpace(ExpertEvidenceTextBox.Text)) throw new InvalidOperationException("Select a code case first.");
            Clipboard.SetText(ExpertEvidenceTextBox.Text);
            FooterStatusTextBlock.Text = "Copied technical details";
        });
    }

    private void CopyDiagnosticReportClicked(object sender, RoutedEventArgs e)
    {
        Execute("copy-diagnostic-report", () =>
        {
            Clipboard.SetText(CurrentSupportReport());
            FooterStatusTextBlock.Text = "Copied support report";
        });
    }

    private void SaveDiagnosticReportClicked(object sender, RoutedEventArgs e)
    {
        Execute("save-diagnostic-report", () =>
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Cyberpunk Conflict Studio Reports");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "support-report-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, CurrentSupportReport());
            WorkspaceStatusTextBlock.Text = $"Support report saved to {path}";
            FooterStatusTextBlock.Text = "Support report saved";
        });
    }

    private void OpenDiagnosticsFolderClicked(object sender, RoutedEventArgs e)
    {
        Execute("open-diagnostics-folder", () =>
        {
            Directory.CreateDirectory(_diagnostics.DirectoryPath);
            Process.Start(new ProcessStartInfo("explorer.exe", _diagnostics.DirectoryPath) { UseShellExecute = true });
        });
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (_historyBusy || !_noteSaveTask.IsCompleted)
        {
            WorkspaceStatusTextBlock.Text = "Wait for saved notes to finish loading or saving before exporting.";
            return;
        }
        Execute("support-export", () =>
        {
            if (_receipt is null) throw new InvalidOperationException("Run a profile scan before exporting.");
            string safeProfile = string.Concat(_receipt.ProfileName.Select(value => Path.GetInvalidFileNameChars().Contains(value) ? '_' : value));
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Cyberpunk Conflict Studio Exports", safeProfile + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture));
            SupportCapsuleWriter.Write(directory, SupportCapsuleBuilder.Build(_receipt, _decisions, _notes));
            WorkspaceStatusTextBlock.Text = $"Support bundle exported to {directory}";
            FooterStatusTextBlock.Text = "Support bundle exported";
        });
    }

    private void LoadReceipt(ProfileScanReceipt receipt, string managerRoot, object profile, bool preserveArchiveUndo = false)
    {
        bool restoreView = _receipt is null || _receipt.InstallationId != receipt.InstallationId || _receipt.ProfileName != receipt.ProfileName || _receipt.ManagerKind != receipt.ManagerKind;
        SaveCurrentView();
        _restoringView = true;
        try { LoadReceiptContents(receipt, managerRoot, profile, preserveArchiveUndo); }
        finally { _restoringView = false; }
        BeginInvestigation(receipt, restoreView);
    }

    private void LoadReceiptContents(ProfileScanReceipt receipt, string managerRoot, object profile, bool preserveArchiveUndo)
    {
        _codeComparisonWindow?.Close();
        EvidenceDecisionStore decisionStore = new(_decisionDirectory);
        EvidenceDecision[] decisions = decisionStore.Load();
        if (decisionStore.LastRecoveryPath is not null) RecordAction("evidence-decisions", "recovered", $"Preserved unreadable review data as {Path.GetFileName(decisionStore.LastRecoveryPath)} and started with an empty review set");
        ConflictWorkItem[] workItems = ConflictWorkQueueBuilder.Build(receipt, decisions, _notes);
        if (receipt.EditableArchiveInventory is null || receipt.EditableArchiveOrder is null) throw new InvalidDataException("The scan receipt has no editable legacy archive snapshot.");
        Mo2ArchiveWriteTarget target;
        Func<Mo2ArchiveProfile> refresh;
        string profileSource;
        string workspaceRoot;
        if (profile is VortexProfileOption vortex)
        {
            VortexManagerContext context = VortexManagerContextStore.Read(vortex.ContextPath);
            target = VortexArchiveWriteTargetResolver.Resolve(vortex.ContextPath, context);
            if (!receipt.DeploymentFresh) target = target with { WriteBlockedReason = "Open Vortex, deploy the active profile, and refresh before applying an archive order." };
            refresh = () =>
            {
                VortexManagerContext current = VortexManagerContextStore.Read(vortex.ContextPath);
                bool reliable = current.DeploymentFresh;
                return VortexArchiveProfileScanner.Scan(reliable ? current : current with { DeploymentFresh = false, Providers = [], DeployedWinners = [] });
            };
            profileSource = vortex.ContextPath;
            workspaceRoot = context.StagingRoot;
        }
        else if (profile is Mo2Profile mo2)
        {
            target = Mo2ArchiveWriteTargetResolver.Resolve(managerRoot, receipt.EditableArchiveOrderEvidence);
            refresh = () => Mo2ArchiveProfileScanner.ScanInstance(managerRoot, mo2.ModlistPath, null, true);
            profileSource = mo2.ModlistPath;
            workspaceRoot = managerRoot;
        }
        else if (profile is ManualProfileOption manual)
        {
            string orderPath = Path.Combine(manual.GameRoot, "archive", "pc", "mod", "modlist.txt");
            target = new Mo2ArchiveWriteTarget(orderPath, "Game directory", ModManagerKind.Manual, GameRoot: manual.GameRoot, CrossManagerContextPath: DefaultVortexContextPath);
            refresh = () => ManualArchiveProfileScanner.Scan(manual.GameRoot);
            profileSource = orderPath;
            workspaceRoot = manual.GameRoot;
        }
        else throw new InvalidOperationException("The selected manager profile is unsupported.");
        if (receipt.EditableArchiveOrderEvidence is { Kind: ArchiveOrderEvidenceKind.Unresolved } unresolvedOrder && !unresolvedOrder.IsRepairableLegacyOrder)
        {
            target = target with { WriteBlockedReason = unresolvedOrder.Message };
        }
        Mo2ArchiveProfile editableArchives = new(receipt.ProfileName, profileSource, receipt.EditableArchiveInventory, receipt.EditableArchiveOrder, receipt.EditableArchiveOrderEvidence);
        _workspace.LoadProfile(editableArchives, target, workspaceRoot, refresh, receipt.InstallationId, preserveArchiveUndo);
        _workspace.SetResourceProviders(receipt.InstallationId, receipt.ProfileName, receipt.ResourceConflicts.SelectMany(value => value.Providers).ToArray());
        _workspace.SetUnreadableArchives(receipt.ArchiveFailures.Select(value => value.ArchiveName).ToArray());
        _receipt = receipt;
        _sourceFileLocations = new SourceFileLocations(receipt);
        _decisions = decisions;
        _workItems = workItems;
        _archiveRelationshipResources = receipt.ResourceConflicts.SelectMany(value => value.Providers).ToArray();
        _archivePreviewResources = ArchiveConflictPreview.RehydrateResources(receipt.ArchiveSummaries ?? []);
        RefreshArchiveRail();
        _archiveTree.Load(receipt.ArchiveSummaries ?? []);
        ApplyArchiveTreeFilter();
        SetArchiveOverviewEntries(receipt);
        _previewingArchiveOrder = false;
        _archivePreviewUnavailable = false;
        ArchiveOrderEvidence evidence = receipt.ArchiveOrderEvidence ?? new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, null, null, "Archive order evidence is unavailable.");
        PresentArchiveOrderEvidence(evidence);
        if (evidence.IsRepairableLegacyOrder && receipt.ArchiveFailures.Length == 0)
        {
            _workspace.PreviewOrder();
            RefreshArchiveConflictPreview();
        }
        ArchiveConflictCountTextBlock.Text = _archiveTree.ResultSummary + " Non-conflicting files are hidden by default.";
        ConflictWorkItem[] codeItems = CodeWorkItems();
        UpdateCodeCaseCounts(codeItems);
        string? previousProvider = QueueProviderComboBox.SelectedItem as string;
        string? previousOtherProvider = QueueOtherProviderComboBox.SelectedItem as string;
        string[] providers = CodeCaseWorkspace.Providers(codeItems);
        QueueProviderComboBox.ItemsSource = providers;
        QueueProviderComboBox.SelectedItem = providers.Contains(previousProvider, StringComparer.Ordinal) ? previousProvider : "All mods";
        QueueOtherProviderComboBox.ItemsSource = QueueProviderComboBox.ItemsSource;
        QueueOtherProviderComboBox.SelectedItem = providers.Contains(previousOtherProvider, StringComparer.Ordinal) ? previousOtherProvider : "All mods";
        ApplyQueueFilter();
        ExportButton.IsEnabled = true;
        ScanProfileButton.Content = "Refresh";
        CodeCaseCounts codeCounts = CodeCaseWorkspace.Counts(codeItems);
        WorkspaceStatusTextBlock.Text = $"Scan complete: {receipt.ResourceConflicts.Length:N0} archive conflicts; {codeCounts.ProvenConflicts} confirmed code conflicts; {codeCounts.NeedsDecision} code items to review.";
        FooterStatusTextBlock.Text = receipt.Metrics is null ? "Scan complete" : $"Scan complete in {receipt.Metrics.TotalElapsedMilliseconds:N0} ms";
        if (receipt.Metrics?.RefreshedArchiveFingerprints > 0) FooterStatusTextBlock.Text += " · archive fingerprint cache refreshed";
        if (receipt.Metrics is { PackedCacheHits: > 0, CodeCacheHits: > 0 }) FooterStatusTextBlock.Text += " · unchanged analysis reused";
        int failureCount = receipt.ArchiveFailures.Length + (receipt.SourceFailures ?? []).Length + receipt.ArchiveXlFailures.Length;
        DiagnosticsSummaryTextBlock.Text = failureCount == 0 ? "No scan failures. If an action misbehaves, reproduce it once and copy the report." : $"{failureCount} scan issue{(failureCount == 1 ? string.Empty : "s")} recorded. Copy the report when requesting support.";
        UpdateSupportSurface();
        HistoricalDiagnosticsTextBox.Text = _diagnostics.ReadRecent();
    }

    private void PresentArchiveOrderEvidence(ArchiveOrderEvidence evidence)
    {
        ArchiveOrderEvidenceTextBlock.Text = evidence.Message;
        bool repairDraft = evidence.IsRepairableLegacyOrder;
        bool orderBlocked = evidence.Kind == ArchiveOrderEvidenceKind.Unresolved && !repairDraft;
        bool maintenance = evidence.IgnoredEntries.Length > 0 && !evidence.PreserveInactiveArchives || evidence.UnlistedArchives.Length > 0;
        _orderProblemLane = orderBlocked || repairDraft ? evidence.ProblemLane : ArchiveOrderProblemLane.None;
        ArchiveOrderEvidenceTitleTextBlock.Text = repairDraft ? "Archive-order repair draft ready" : orderBlocked ? ArchiveOrderGuidance.BlockedTitle(evidence) : maintenance ? ArchiveOrderGuidance.MaintenanceTitle(evidence) : $"Order verified · {evidence.Provider ?? "filename order"}";
        ArchiveOrderActionButton.Content = ArchiveOrderGuidance.ActionLabel(evidence);
        ArchiveOrderEvidenceTextBlock.Visibility = orderBlocked || repairDraft || maintenance ? Visibility.Visible : Visibility.Collapsed;
        ArchiveOrderActionButton.Visibility = orderBlocked || repairDraft || maintenance ? Visibility.Visible : Visibility.Collapsed;
        ArchiveOrderEvidenceBorder.ToolTip = evidence.Message;
        ArchiveOrderEvidenceTitleTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(orderBlocked || repairDraft ? "#FCEE0A" : maintenance ? "#FFB15A" : "#7EF0B2"));
        ArchiveOrderEvidenceBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(orderBlocked || repairDraft ? "#292600" : maintenance ? "#321701" : "#08251B"));
        ArchiveOrderEvidenceBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(orderBlocked || repairDraft ? "#8C8500" : maintenance ? "#8A4200" : "#1F6E4C"));
    }

    private void ApplyQueueFilter()
    {
        if (WorkQueueDataGrid is null) return;
        string query = QueueSearchTextBox?.Text?.Trim() ?? string.Empty;
        string view = (QueueViewComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Actionable";
        string surface = (QueueSurfaceComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        string provider = QueueProviderComboBox?.SelectedItem as string ?? "All mods";
        string otherProvider = QueueOtherProviderComboBox?.SelectedItem as string ?? "All mods";
        ConflictWorkItem[] rows = CodeCaseWorkspace.Filter(CodeWorkItems(), query, view, surface, provider, otherProvider);
        ConflictWorkItem? selected = WorkQueueDataGrid.SelectedItem as ConflictWorkItem;
        SortDescription[] sort = CurrentCodeSort();
        WorkQueueDataGrid.ItemsSource = rows;
        RestoreCodeSort(sort);
        WorkQueueDataGrid.SelectedItem = selected is null ? rows.FirstOrDefault() : rows.FirstOrDefault(value => value.Target == selected.Target && value.Surface == selected.Surface) ?? rows.FirstOrDefault();
    }

    private void ApplyArchiveTreeFilter()
    {
        if (ArchiveConflictTreeView is null) return;
        string? selectedName = _selectedArchiveNode?.ArchiveName;
        string[] selectedNames = _selectedArchiveNames;
        int revision = ++_archiveFilterRevision;
        _archiveTree.Filter(ArchiveModFilterTextBox?.Text ?? string.Empty, ArchiveFileFilterTextBox?.Text ?? string.Empty, ShowNonConflictingFilesCheckBox?.IsChecked == true, OnlyConflictingArchivesCheckBox?.IsChecked == true);
        ArchiveConflictTreeView.ItemsSource = _archiveTree.VisibleArchives;
        string previewState = _archivePreviewUnavailable ? "Preview unavailable · applied results shown · " : _previewingArchiveOrder ? "Preview · not applied · " : string.Empty;
        ArchiveConflictCountTextBlock.Text = previewState + _archiveTree.ResultSummary + (ShowNonConflictingFilesCheckBox?.IsChecked == true ? string.Empty : " Non-conflicting files are hidden.");
        ArchiveConflictNode? selected = selectedName is null ? null : _archiveTree.Find(selectedName);
        if (selected is null && selectedNames.Length > 1) selected = _archiveTree.VisibleArchives.FirstOrDefault(value => selectedNames.Contains(value.ArchiveName, StringComparer.OrdinalIgnoreCase));
        if (selected is null && selectedNames.Length == 1 && _archiveTree.VisibleArchives.Count > 0) selected = _archiveTree.VisibleArchives[0];
        if (selected is null)
        {
            _selectedArchiveNode = null;
            OpenSelectedArchiveButton.IsEnabled = false;
            if (selectedNames.Length == 0)
            {
                _syncingArchiveSelection = true;
                try { ArchiveOrderListBox.SelectedItems.Clear(); }
                finally { _syncingArchiveSelection = false; }
            }
            SetOverviewSelection(selectedNames);
            bool hasResults = _archiveTree.VisibleArchives.Count > 0;
            ArchiveSelectedTitleTextBlock.Text = hasResults ? "Select an archive" : "No matching archive";
            ArchiveSelectedMeaningTextBlock.Text = hasResults ? "Select an archive to show only the archives it overrides and the archives that override it." : "Clear or change the filters to restore archive results.";
            ArchiveSelectedTechnicalTextBlock.Text = string.Empty;
            return;
        }
        _selectedArchiveNode = selected;
        OpenSelectedArchiveButton.IsEnabled = true;
        if (selectedNames.Length <= 1)
        {
            SelectArchiveInLoadOrder(selected.ArchiveName);
            SetOverviewSelection(selected.ArchiveName);
        }
        else SetOverviewSelection(selectedNames);
        string[] overviewSelection = selectedNames.Length > 1 ? selectedNames : [selected.ArchiveName];
        Dispatcher.BeginInvoke(() =>
        {
            if (revision != _archiveFilterRevision) return;
            _syncingArchiveSelection = true;
            try { SelectVisibleArchive(selected, false); }
            finally { _syncingArchiveSelection = false; }
            if (overviewSelection.Length > 1) SelectArchivesInLoadOrder(overviewSelection);
            SetOverviewSelection(overviewSelection);
        }, DispatcherPriority.Loaded);
    }

    private void SetArchiveOverviewEntries(ProfileScanReceipt receipt)
    {
        SetOverviewSelection([]);
    }

    private void ReloadQueue(ConflictWorkItem? selected)
    {
        if (_receipt is null) return;
        _workItems = ConflictWorkQueueBuilder.Build(_receipt, _decisions, _notes);
        ConflictWorkItem[] codeItems = CodeWorkItems();
        UpdateCodeCaseCounts(codeItems);
        ApplyQueueFilter();
        if (selected is not null) WorkQueueDataGrid.SelectedItem = (WorkQueueDataGrid.ItemsSource as IEnumerable<ConflictWorkItem>)?.FirstOrDefault(value => value.Target == selected.Target && value.Surface == selected.Surface);
    }

    private ConflictWorkItem[] CodeWorkItems() => _workItems.Where(value => value.Surface != ConflictSurface.PackedResource).ToArray();

    private void UpdateCodeCaseCounts(IReadOnlyList<ConflictWorkItem> items)
    {
        CodeCaseCounts counts = CodeCaseWorkspace.Counts(items);
        AttentionCountTextBlock.Text = counts.ProvenConflicts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ReviewCountTextBlock.Text = counts.NeedsDecision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ReviewedCountTextBlock.Text = counts.Reviewed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        NoActionCountTextBlock.Text = counts.CompatibleEvidence.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string ExactEvidence(ConflictWorkItem item)
    {
        if (_receipt is null) return string.Empty;
        object evidence = new
        {
            item,
            resources = _receipt.ResourceConflicts.Where(value => value.DisplayName == item.Target),
            virtualFiles = _receipt.VirtualFileShadows.Where(value => item.RelatedTargets.Contains(value.RelativePath, StringComparer.OrdinalIgnoreCase)).Select(value => value with { Providers = value.Providers.Select(provider => provider with { PhysicalPath = string.Empty }).ToArray() }),
            interactions = _receipt.InteractionFindings.Where(value => value.Target == item.Target),
            redScript = _receipt.RedScriptFlows.Where(value => value.Target == item.Target),
            sharedState = _receipt.SharedStateWrites.Where(value => value.Target == item.Target),
            lua = _receipt.LuaCallbacks.Where(value => value.Target == item.Target),
            tweak = _receipt.TweakOverlaps.Where(value => value.Target == item.Target),
            archiveXl = _receipt.ArchiveXlChains.Where(value => value.Target == item.Target)
        };
        return JsonSerializer.Serialize(evidence, EvidenceJsonOptions);
    }

    private static string Diagnostics(ProfileScanReceipt receipt)
    {
        List<string> lines = [];
        if (receipt.ArchiveOrderEvidence is not null) lines.Add("archive order: " + receipt.ArchiveOrderEvidence.Message);
        if (receipt.Metrics is not null) lines.AddRange(receipt.Metrics.Phases.Select(value => $"{value.Name}: {value.ElapsedMilliseconds:N0} ms, {value.ItemCount:N0} items"));
        if (receipt.Metrics?.RefreshedArchiveFingerprints > 0) lines.Add($"archive fingerprint cache: {receipt.Metrics.RefreshedArchiveFingerprints:N0} mismatch detected; archive fingerprints were rebuilt once before the completed scan");
        if (receipt.Metrics is { } metrics) lines.Add($"analysis cache: packed {metrics.PackedCacheHits:N0}, code {metrics.CodeCacheHits:N0}");
        if (receipt.Metrics?.Scale is { } scale) lines.Add($"scan scale: {scale.Providers:N0} providers, {scale.Archives:N0} archives, {FormatBytes(scale.ArchiveBytes)}, {scale.EvidenceFiles:N0} validated files, {scale.PackedResources:N0} packed resources, {scale.SourceFiles:N0} source files, widest conflict chain {scale.MaxConflictProviders:N0}, {scale.CompetitorReferences:N0} competitor references");
        if (receipt.Metrics?.VortexBridge is { } bridge) lines.Add($"Vortex bridge: {bridge.RefreshMilliseconds:N0} ms export, {bridge.DeploymentFiles:N0} deployed files, {bridge.RelevantDeploymentFiles:N0} relevant files, {bridge.Winners:N0} mapped winners, {bridge.UnmappedRelevantFiles:N0} unmapped relevant files, {bridge.TargetRelocatedFiles:N0} target-relocated files, inventory complete: {bridge.InventoryComplete}");
        lines.AddRange(receipt.ArchiveFailures.Select(value => $"ARCHIVE · {value.Provider} · {value.ArchiveName} · {value.Message}"));
        lines.AddRange((receipt.ArchiveWarnings ?? []).Select(value => $"ARCHIVE PATHS · {value.Provider} · {value.ArchiveName} · {value.Message}"));
        if (receipt.ResourcePathIndexEvidence is { State: not ResourcePathIndexState.Resolved } pathEvidence) lines.Add($"RESOURCE PATH INDEX · {pathEvidence.State} · {pathEvidence.Message}");
        lines.AddRange((receipt.SourceFailures ?? []).Select(value => $"{value.Surface} · {value.Provider} · {value.FilePath} · {value.Message}"));
        lines.AddRange(receipt.ArchiveXlFailures.Select(value => $"ArchiveXL · {value.Provider} · {value.FilePath} · {value.Message}"));
        return lines.Count == 0 ? "No diagnostics." : string.Join(Environment.NewLine, lines);
    }

    private void UpdateProgress(ScanProgress progress)
    {
        ScanProgressBar.Maximum = Math.Max(1, progress.Total);
        ScanProgressBar.Value = progress.Completed;
        string item = progress.CurrentItem is null ? string.Empty : $" · {progress.CurrentItem}";
        string bytes = progress.BytesTotal <= 0 ? string.Empty : $" · {FormatBytes(progress.BytesCompleted)}/{FormatBytes(progress.BytesTotal)}";
        double bytesPerSecond = progress.ElapsedMilliseconds <= 0 ? 0 : progress.BytesRead * 1000d / progress.ElapsedMilliseconds;
        string throughput = bytesPerSecond <= 0 ? string.Empty : $" · {FormatBytes((long)bytesPerSecond)}/s";
        ScanPhaseTextBlock.Text = $"{progress.Phase} · {progress.Completed:N0}/{progress.Total:N0}{item}{bytes}{throughput}";
        FooterStatusTextBlock.Text = "Scanning " + progress.Phase;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }

    private void CompleteScanPresentation()
    {
        ScanProgressBar.Value = ScanProgressBar.Maximum;
        ScanPhaseTextBlock.Text = "Complete";
    }

    private void SetScanLocked(bool locked)
    {
        _scanLocked = locked;
        SetWorkspaceInputLocked(locked || _archiveOperationLocked);
        CancelScanButton.IsEnabled = locked;
    }

    private void SetArchiveOperationLocked(bool locked)
    {
        _archiveOperationLocked = locked;
        SetWorkspaceInputLocked(locked || _scanLocked);
        CancelScanButton.IsEnabled = _scanLocked;
    }

    private void SetWorkspaceInputLocked(bool locked)
    {
        if (locked) Keyboard.ClearFocus();
        ManagerModeComboBox.IsHitTestVisible = !locked;
        ManagerModeComboBox.Focusable = !locked;
        Mo2RootTextBox.IsHitTestVisible = !locked;
        Mo2RootTextBox.Focusable = !locked;
        FindProfilesButton.IsHitTestVisible = !locked;
        FindProfilesButton.Focusable = !locked;
        ProfileComboBox.IsHitTestVisible = !locked;
        ProfileComboBox.Focusable = !locked;
        ArchiveOrderTabContent.IsHitTestVisible = !locked;
        ScanProfileButton.IsHitTestVisible = !locked;
        ScanProfileButton.Focusable = !locked;
    }

    private void InvalidateReceipt()
    {
        SaveCurrentView();
        ClearInvestigation();
        _codeComparisonWindow?.Close();
        _workspace.ClearProfileState();
        if (WorkQueueDataGrid is null) return;
        _receipt = null;
        _sourceFileLocations = null;
        SelectedFileComboBox.ItemsSource = null;
        SelectedFilesPanel.Visibility = Visibility.Collapsed;
        _selectedArchiveNode = null;
        _orderProblemLane = ArchiveOrderProblemLane.None;
        _previewingArchiveOrder = false;
        _archivePreviewUnavailable = false;
        _workItems = [];
        _decisions = [];
        WorkQueueDataGrid.ItemsSource = null;
        QueueProviderComboBox.ItemsSource = null;
        QueueOtherProviderComboBox.ItemsSource = null;
        _archiveTree.Load([]);
        _archiveRelationshipResources = [];
        _archivePreviewResources = [];
        _archiveRailItems = [];
        _selectedArchiveNames = [];
        ArchiveConflictTreeView.ItemsSource = null;
        ArchiveOrderListBox.ItemsSource = null;
        ArchiveOrderEvidenceTextBlock.Text = "Run a profile scan to check the files inside your archives.";
        ArchiveOrderEvidenceTitleTextBlock.Text = "Archive order not loaded";
        ArchiveConflictCountTextBlock.Text = "Run a scan to inspect conflicts.";
        ArchiveSelectedTitleTextBlock.Text = "Select an archive or file";
        ArchiveSelectedMeaningTextBlock.Text = "Expand an archive to inspect Winning, Losing, Same content, and No conflicts inline.";
        ArchiveSelectedTechnicalTextBlock.Text = string.Empty;
        LoadOrderOverviewBar.Entries = [];
        ConflictOverviewBar.Entries = [];
        SetOverviewSelection((string?)null);
        AttentionCountTextBlock.Text = "0";
        ReviewCountTextBlock.Text = "0";
        ReviewedCountTextBlock.Text = "0";
        NoActionCountTextBlock.Text = "0";
        ExportButton.IsEnabled = false;
        ScanProfileButton.Content = "Scan profile";
        ExpertEvidenceTextBox.Text = string.Empty;
        TechnicalEvidenceExpander.IsExpanded = false;
        CopyEvidenceButton.IsEnabled = false;
        DiagnosticsTextBox.Text = string.Empty;
        DiagnosticsSummaryTextBlock.Text = "Run a scan for this profile.";
        UpdateSupportSurface();
    }

    private Mo2Profile SelectedProfile() => ProfileComboBox.SelectedItem as Mo2Profile ?? throw new InvalidOperationException("Select an MO2 profile first.");

    private string SelectedProfileName() => ProfileComboBox.SelectedItem switch { Mo2Profile mo2 => mo2.Name, VortexProfileOption vortex => vortex.Name, ManualProfileOption manual => manual.Name, _ => throw new InvalidOperationException("Select a manager profile first.") };

    private void LoadVortexContext(string path)
    {
        string fullPath = Path.GetFullPath(path);
        VortexManagerContext context = VortexManagerContextStore.Read(fullPath);
        _managerKind = ModManagerKind.Vortex;
        _vortexContextPath = fullPath;
        SelectManagerMode(ModManagerKind.Vortex);
        ManagerLocationLabel.Text = "VORTEX PROFILE INFORMATION";
        Mo2RootTextBox.Text = fullPath;
        VortexProfileOption option = new(context.ProfileName, fullPath);
        ProfileComboBox.ItemsSource = new[] { option };
        ProfileComboBox.SelectedItem = option;
        _preferences.TrySave(new WorkspacePreference(context.GameRoot, context.ProfileName, ModManagerKind.Vortex, fullPath));
        FindProfilesButton.Content = "Connect";
        WorkspaceStatusTextBlock.Text = context.DeploymentFresh ? "Vortex profile information loaded." : "Vortex has pending deployment changes. The scan will use deployed game files only.";
    }

    private static async Task<VortexManagerContext> RefreshVortexContextAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The Vortex bridge context path is invalid.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        VortexContextRefreshRequest request = new(1, Guid.NewGuid().ToString("N"), now, now.AddMinutes(2));
        VortexContextRefreshResponse response = await Task.Run(() => new VortexContextBridgeStore(root).Exchange(request, TimeSpan.FromMinutes(2), cancellationToken), cancellationToken);
        if (response.SchemaVersion != 1 || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal)) throw new InvalidDataException("Vortex returned an invalid profile refresh response.");
        if (!response.Refreshed || string.IsNullOrWhiteSpace(response.ContextId)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Message) ? "Vortex could not export the active profile." : response.Message);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Vortex reported a refreshed profile but did not write context.json.", fullPath);
        VortexManagerContext context = VortexManagerContextStore.Read(fullPath);
        if (!string.Equals(context.ContextId, response.ContextId, StringComparison.Ordinal)) throw new InvalidDataException("The Vortex profile refresh response does not match context.json.");
        return context;
    }

    private void SelectManagerMode(ModManagerKind kind)
    {
        _syncingManagerMode = true;
        try
        {
            ManagerModeComboBox.SelectedItem = ManagerModeComboBox.Items.Cast<ComboBoxItem>().First(value => string.Equals(value.Tag?.ToString(), kind.ToString(), StringComparison.OrdinalIgnoreCase));
            _managerKind = kind;
            ManagerLocationLabel.Text = kind switch { ModManagerKind.Vortex => "VORTEX PROFILE INFORMATION", ModManagerKind.Manual => "CYBERPUNK INSTALLATION", _ => "MO2 INSTALLATION" };
        }
        finally { _syncingManagerMode = false; }
    }

    private void Execute(string operation, Action action)
    {
        if (_scanLocked || _archiveOperationLocked)
        {
            RecordAction(operation, "blocked", "Another workspace operation is running");
            WorkspaceStatusTextBlock.Text = "Wait for the current operation to finish.";
            return;
        }
        try
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            RecordAction(operation, "started", string.Empty);
            action();
            RecordAction(operation, "completed", string.Empty);
        }
        catch (Exception exception)
        {
            ShowError(operation, exception);
        }
    }

    private bool RejectArchiveMutationWhileBusy(string operation)
    {
        if (!_scanLocked && !_archiveOperationLocked) return false;
        RecordAction(operation, "blocked", "Another workspace operation is running");
        WorkspaceStatusTextBlock.Text = "Wait for the current operation to finish.";
        return true;
    }

    private void ShowError(string operation, Exception exception)
    {
        bool logged = _diagnostics.TryWrite(operation, exception);
        RecordAction(operation, "failed", $"{exception.GetType().Name}: {exception.Message}");
        if (HistoricalDiagnosticsTextBox is not null) HistoricalDiagnosticsTextBox.Text = _diagnostics.ReadRecent();
        ErrorTextBlock.Text = logged ? $"{exception.Message} Open Support to copy the recorded details." : $"{exception.Message} The diagnostics log is unavailable.";
        ErrorTextBlock.Visibility = Visibility.Visible;
        WorkspaceStatusTextBlock.Text = operation is "archive-apply" or "archive-undo"
            ? "The archive-order operation could not be confirmed. Scan again before making another change."
            : "The action did not complete. Nothing was changed.";
    }

    private void InvalidateMixedManagerReceiptAndShowScanError(Exception exception)
    {
        if (exception is CrossManagerDeploymentException) InvalidateReceipt();
        ShowError("profile-scan", exception);
    }

    private void RecordAction(string operation, string outcome, string detail)
    {
        string safeDetail = PrivatePathRedactor.Redact(detail);
        _sessionActions.Add(new DiagnosticAction(DateTimeOffset.UtcNow, operation, outcome, safeDetail));
        if (_sessionActions.Count > 100) _sessionActions.RemoveRange(0, _sessionActions.Count - 100);
        _diagnostics.TryWriteAction(operation, outcome, safeDetail);
        UpdateSupportSurface();
    }

    private string CurrentSupportReport()
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "developer build";
        string profile = ProfileComboBox?.SelectedItem switch { Mo2Profile mo2 => mo2.Name, VortexProfileOption vortex => vortex.Name, ManualProfileOption manual => manual.Name, _ => string.Empty };
        string status = WorkspaceStatusTextBlock?.Text ?? "Ready";
        string scan = _receipt is null ? "No scan has run." : Diagnostics(_receipt);
        string errors = _diagnostics.ReadRecent();
        return DiagnosticReportBuilder.Build(version, profile, status, scan, _sessionActions, errors, _managerKind.ToString());
    }

    private void UpdateSupportSurface()
    {
        if (DiagnosticsTextBox is null) return;
        string status = WorkspaceStatusTextBlock?.Text ?? "Ready";
        string scan = _receipt is null ? "No scan has run." : Diagnostics(_receipt);
        DiagnosticsTextBox.Text = DiagnosticReportBuilder.BuildSessionView(status, scan, _sessionActions);
    }

    private void ArchiveOrderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggedArchives = [];
        _archivePressedForDrag = null;
        _archiveSelectionAtPress = [];
        _archivePressedWasSelected = false;
        _archiveSelectionModifier = false;
        if (RejectArchiveMutationWhileBusy("archive-drag"))
        {
            return;
        }
        _archiveDragStart = e.GetPosition(ArchiveOrderListBox);
        ListBoxItem? item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ArchiveRailItem row)
        {
            return;
        }
        string archive = row.ArchiveName;
        HashSet<string> editable = _workspace.ProposedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!editable.Contains(archive))
        {
            return;
        }
        _archivePressedForDrag = archive;
        _archivePressedWasSelected = item.IsSelected;
        _archiveSelectionAtPress = ArchiveOrderListBox.SelectedItems.Cast<ArchiveRailItem>().Select(value => value.ArchiveName).ToArray();
        _archiveSelectionModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        _draggedArchives = ArchiveDragSelection.Resolve(archive, item.IsSelected, _archiveSelectionAtPress, _archiveSelectionAtPress, false).Where(editable.Contains).ToArray();
    }

    private void ArchiveOrderMouseMove(object sender, MouseEventArgs e)
    {
        if (_scanLocked || _archiveOperationLocked) return;
        if (e.LeftButton != MouseButtonState.Pressed || _archivePressedForDrag is null) return;
        string[] selectedAfterPress = ArchiveOrderListBox.SelectedItems.Cast<ArchiveRailItem>().Select(value => value.ArchiveName).ToArray();
        HashSet<string> editable = _workspace.ProposedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _draggedArchives = ArchiveDragSelection.Resolve(_archivePressedForDrag, _archivePressedWasSelected, _archiveSelectionAtPress, selectedAfterPress, _archiveSelectionModifier).Where(editable.Contains).ToArray();
        if (_draggedArchives.Length == 0) return;
        Point current = e.GetPosition(ArchiveOrderListBox);
        if (Math.Abs(current.X - _archiveDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _archiveDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _archiveDragActive = true;
        _archiveDragWheelRemainder = 0;
        try { DragDrop.DoDragDrop(ArchiveOrderListBox, _draggedArchives, DragDropEffects.Move); }
        finally
        {
            _archiveDragActive = false;
            _archiveDragWheelRemainder = 0;
            StopArchiveDragScroll();
            ClearArchiveDropCues();
            _draggedArchives = [];
        }
    }

    private void ArchiveOrderDragOver(object sender, DragEventArgs e)
    {
        if (_scanLocked || _archiveOperationLocked)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        SetArchiveDragScroll(ArchiveDragAutoScroll.LinesAt(e.GetPosition(ArchiveOrderListBox).Y, ArchiveOrderListBox.ActualHeight));
        ClearArchiveDropCues();
        ListBoxItem? item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (_draggedArchives.Length == 0 || item?.DataContext is not ArchiveRailItem row)
        {
            e.Effects = _archiveDragScrollLines == 0 ? DragDropEffects.None : DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        row.DropCue = ArchiveDropCueProjection.For(_workspace.ProposedOrder, _draggedArchives, row.ArchiveName);
        e.Effects = row.DropCue == ArchiveDropCue.None ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void ArchiveOrderDragLeave(object sender, DragEventArgs e)
    {
        StopArchiveDragScroll();
        ClearArchiveDropCues();
    }

    private void ArchiveOrderDrop(object sender, DragEventArgs e)
    {
        StopArchiveDragScroll();
        if (_scanLocked || _archiveOperationLocked) return;
        if (_draggedArchives.Length == 0) return;
        try
        {
            ClearArchiveDropCues();
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            RecordAction("archive-order-drag", "started", string.Empty);
            ListBoxItem? item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            string? target = (item?.DataContext as ArchiveRailItem)?.ArchiveName;
            if (target is null || !_workspace.ProposedOrder.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                _draggedArchives = [];
                WorkspaceStatusTextBlock.Text = "REDmods cannot be moved here. Change their order through REDmod deployment.";
                RecordAction("archive-order-drag", "blocked", "REDmod positions are fixed by the active REDmod lane");
                return;
            }
            string[] moved = _draggedArchives;
            _draggedArchives = [];
            if (!MoveArchiveOrder(moved, target))
            {
                WorkspaceStatusTextBlock.Text = "Archive order did not change.";
                RecordAction("archive-order-drag", "completed", "The drop kept the current order");
                return;
            }
            RefreshArchiveRail();
            RecordAction("archive-order-drag", "completed", $"Moved {moved.Length} archive{(moved.Length == 1 ? string.Empty : "s")}");
            Dispatcher.BeginInvoke(() =>
            {
                SelectArchivesInLoadOrder(moved);
                SetOverviewSelection(moved);
            });
        }
        catch (Exception exception)
        {
            _draggedArchives = [];
            ShowError("archive-order-drag", exception);
        }
    }

    private void ClearArchiveDropCues()
    {
        foreach (ArchiveRailItem row in _archiveRailItems) row.DropCue = ArchiveDropCue.None;
    }

    private void SetArchiveDragScroll(int lines)
    {
        _archiveDragScrollLines = lines;
        if (lines == 0) _archiveDragScrollTimer.Stop();
        else if (!_archiveDragScrollTimer.IsEnabled) _archiveDragScrollTimer.Start();
    }

    private void StopArchiveDragScroll()
    {
        _archiveDragScrollLines = 0;
        _archiveDragScrollTimer.Stop();
    }

    private void ScrollArchiveOrderDuringDrag()
    {
        if (_archiveDragScrollLines == 0 || _draggedArchives.Length == 0) return;
        _loadOrderScrollViewer ??= FindDescendant<ScrollViewer>(ArchiveOrderListBox);
        if (_loadOrderScrollViewer is null) return;
        ArchiveDragAutoScroll.Scroll(_loadOrderScrollViewer, _archiveDragScrollLines);
        RefreshArchiveDropCueAtPointer();
    }

    private void ArchiveOrderMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_archiveDragActive || _draggedArchives.Length == 0) return;
        _loadOrderScrollViewer ??= FindDescendant<ScrollViewer>(ArchiveOrderListBox);
        if (_loadOrderScrollViewer is null) return;
        int lines = ArchiveDragAutoScroll.ConsumeWheelDelta(ref _archiveDragWheelRemainder, e.Delta);
        if (lines != 0)
        {
            ArchiveDragAutoScroll.Scroll(_loadOrderScrollViewer, lines);
            RefreshArchiveDropCueAtPointer();
        }
        e.Handled = true;
    }

    private void RefreshArchiveDropCueAtPointer()
    {
        Point pointer = Mouse.GetPosition(ArchiveOrderListBox);
        DependencyObject? source = ArchiveOrderListBox.InputHitTest(pointer) as DependencyObject;
        ClearArchiveDropCues();
        ListBoxItem? item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is ArchiveRailItem row) row.DropCue = ArchiveDropCueProjection.For(_workspace.ProposedOrder, _draggedArchives, row.ArchiveName);
    }

    private bool MoveArchiveOrder(IReadOnlyList<string> archives, string target)
    {
        string[] before = _workspace.ProposedOrder.ToArray();
        _workspace.MoveMany(archives, target);
        if (before.SequenceEqual(_workspace.ProposedOrder, StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            _workspace.PreviewOrder();
            if (_workspace.CanApply || _receipt?.ArchiveFailures.Length > 0) RefreshArchiveConflictPreview();
            else RestoreScannedArchiveConflictPresentation();
            return true;
        }
        catch
        {
            _workspace.SetProposedOrder(before);
            throw;
        }
    }

    private void RefreshArchiveConflictPreview()
    {
        if (_receipt?.ArchiveInventory is null) return;
        ArchiveOrderEvidence? evidence = _receipt.ArchiveOrderEvidence;
        if (evidence?.Kind == ArchiveOrderEvidenceKind.Unresolved && !evidence.IsRepairableLegacyOrder || _receipt.ArchiveFailures.Length > 0)
        {
            string reason = _receipt.ArchiveFailures.Length > 0 ? "Conflict preview is unavailable because an archive could not be read. Fix that archive or reset the proposed order." : evidence?.Message ?? "The preview is unavailable because the archive load order could not be determined.";
            if (!_workspace.CanApply) _workspace.BlockPreview(reason);
            _previewingArchiveOrder = false;
            _archivePreviewUnavailable = true;
            ApplyArchiveTreeFilter();
            ArchiveOrderEvidenceTitleTextBlock.Text = "Preview unavailable · order not applied";
            ArchiveOrderEvidenceTextBlock.Text = _workspace.CanApply ? "The results still show the scanned order because an archive could not be read. Tick the acknowledgement box before applying a change whose full effects cannot be previewed." : "The conflict pane still shows the scanned order because an archive could not be read. Reset the proposal or fix that archive first.";
            ArchiveOrderEvidenceTextBlock.Visibility = Visibility.Visible;
            ArchiveOrderEvidenceTitleTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCEE0A"));
            ArchiveOrderEvidenceBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#292600"));
            ArchiveOrderEvidenceBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8C8500"));
            return;
        }
        string[] effectiveOrder = ArchiveOverviewProjection.ComposeCombinedOrder(_workspace.ProposedOrder, _receipt.ArchiveOrder);
        ArchiveConflictSummary[] summaries = ArchiveConflictPreview.Build(_archivePreviewResources, _receipt.ArchiveInventory, effectiveOrder, _receipt.ArchiveFailures);
        _previewingArchiveOrder = true;
        _archivePreviewUnavailable = false;
        _archiveTree.Load(summaries);
        ApplyArchiveTreeFilter();
        bool repairDraft = evidence?.IsRepairableLegacyOrder == true;
        ArchiveOrderEvidenceTitleTextBlock.Text = repairDraft ? "Previewing archive-order repair draft" : "Previewing unapplied order";
        ArchiveOrderEvidenceTextBlock.Text = repairDraft ? "The conflict pane is showing the complete repair draft. Review it, then apply to save and verify the repaired order." : "The conflict pane is showing the proposed order. Apply it to save the change.";
        ArchiveOrderEvidenceTextBlock.Visibility = Visibility.Visible;
        ArchiveOrderEvidenceTitleTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCEE0A"));
        ArchiveOrderEvidenceBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#292600"));
        ArchiveOrderEvidenceBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8C8500"));
    }

    private void RestoreScannedArchiveConflictPresentation()
    {
        if (_receipt is null) return;
        _previewingArchiveOrder = false;
        _archivePreviewUnavailable = false;
        _archiveTree.Load(_receipt.ArchiveSummaries ?? []);
        PresentArchiveOrderEvidence(_receipt.ArchiveOrderEvidence ?? new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, null, null, "Archive order evidence is unavailable."));
        ApplyArchiveTreeFilter();
    }

    private void RefreshRelationshipOverviews(string[] selected)
    {
        if (_receipt is null || selected.Length == 0)
        {
            LoadOrderOverviewBar.Entries = [];
            ConflictOverviewBar.Entries = [];
            ArchiveRelationshipPresentation.Apply(_archiveRailItems, _archiveTree.VisibleArchives, []);
            return;
        }
        string[] effectiveOrder = ArchiveOverviewProjection.ComposeCombinedOrder(_workspace.ProposedOrder, _receipt.ArchiveOrder);
        string[] visibleOrder = _archiveTree.VisibleArchives.Select(value => value.ArchiveName).ToArray();
        ArchiveOrderEvidence? evidence = _receipt.ArchiveOrderEvidence;
        ArchiveOrderProblemLane unresolvedLane = evidence?.Kind == ArchiveOrderEvidenceKind.Unresolved && !(_previewingArchiveOrder && evidence.IsRepairableLegacyOrder) ? evidence.ProblemLane : ArchiveOrderProblemLane.None;
        ArchiveOverviewEntry[] loadOrderEntries = ArchiveOverviewProjection.BuildRelationships(effectiveOrder, effectiveOrder, _archiveRelationshipResources, selected, unresolvedLane, evidence?.UnlistedArchives);
        ArchiveOverviewEntry[] conflictEntries = ArchiveOverviewProjection.BuildRelationships(effectiveOrder, visibleOrder, _archiveRelationshipResources, selected, unresolvedLane, evidence?.UnlistedArchives);
        LoadOrderOverviewBar.Entries = loadOrderEntries;
        ConflictOverviewBar.Entries = conflictEntries;
        ArchiveRelationshipPresentation.Apply(_archiveRailItems, [], loadOrderEntries);
        ArchiveRelationshipPresentation.Apply([], _archiveTree.VisibleArchives, conflictEntries);
        Dispatcher.BeginInvoke(RefreshConflictMarkerPositions, DispatcherPriority.Loaded);
    }

    private void RefreshArchiveRail()
    {
        if (_receipt is null) return;
        string[] order = ArchiveOverviewProjection.ComposeCombinedOrder(_workspace.ProposedOrder, _receipt.ArchiveOrder);
        _archiveRailItems = order.Select(value => new ArchiveRailItem(value)).ToArray();
        ArchiveOrderListBox.ItemsSource = _archiveRailItems;
    }

    private void ConnectArchiveScrollbars(object sender, RoutedEventArgs e)
    {
        if (_loadOrderScrollViewer is null)
        {
            _loadOrderScrollViewer = FindDescendant<ScrollViewer>(ArchiveOrderListBox);
            if (_loadOrderScrollViewer is not null) ConnectArchiveScrollbar(_loadOrderScrollViewer, LoadOrderScrollBar);
        }
        if (_conflictScrollViewer is null)
        {
            _conflictScrollViewer = FindDescendant<ScrollViewer>(ArchiveConflictTreeView);
            if (_conflictScrollViewer is not null) ConnectArchiveScrollbar(_conflictScrollViewer, ConflictScrollBar);
        }
    }

    private void ConnectArchiveScrollbar(ScrollViewer viewer, ScrollBar scrollBar)
    {
        viewer.ScrollChanged += (_, args) =>
        {
            UpdateArchiveScrollbar(viewer, scrollBar);
            if (ReferenceEquals(viewer, _conflictScrollViewer) && (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0)) RefreshConflictMarkerPositions();
        };
        scrollBar.ValueChanged += (_, args) =>
        {
            if (!_syncingArchiveScrollbars) viewer.ScrollToVerticalOffset(args.NewValue);
        };
        scrollBar.PreviewMouseLeftButtonDown -= ScrollbarPreviewMouseLeftButtonDown;
        scrollBar.PreviewMouseLeftButtonDown += ScrollbarPreviewMouseLeftButtonDown;
        UpdateArchiveScrollbar(viewer, scrollBar);
    }

    private void ConnectStyledScrollbars()
    {
        foreach (ScrollBar scrollBar in FindDescendants<ScrollBar>(this))
        {
            scrollBar.PreviewMouseLeftButtonDown -= ScrollbarPreviewMouseLeftButtonDown;
            scrollBar.PreviewMouseLeftButtonDown += ScrollbarPreviewMouseLeftButtonDown;
        }
    }

    private void ScrollbarPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not ScrollBar scrollBar) return;
        if (ApplyScrollbarPointer(scrollBar, args.OriginalSource as DependencyObject, args.GetPosition(scrollBar), new Size(scrollBar.ActualWidth, scrollBar.ActualHeight))) args.Handled = true;
    }

    internal static bool ApplyScrollbarPointer(ScrollBar scrollBar, DependencyObject? originalSource, Point position, Size size)
    {
        double coordinate = scrollBar.Orientation == Orientation.Vertical ? position.Y : position.X;
        double trackLength = scrollBar.Orientation == Orientation.Vertical ? size.Height : size.Width;
        return ApplyScrollbarPointer(scrollBar, originalSource, coordinate, trackLength);
    }

    internal static bool ApplyScrollbarPointer(ScrollBar scrollBar, DependencyObject? originalSource, double pointerCoordinate, double trackLength)
    {
        if (originalSource is null || FindAncestor<Thumb>(originalSource) is not null) return false;
        scrollBar.Value = ArchiveScrollbarNavigation.OffsetAtPointer(pointerCoordinate, trackLength, scrollBar.Maximum);
        return true;
    }

    private void UpdateArchiveScrollbar(ScrollViewer viewer, ScrollBar scrollBar)
    {
        _syncingArchiveScrollbars = true;
        try
        {
            scrollBar.Maximum = Math.Max(0, viewer.ScrollableHeight);
            scrollBar.ViewportSize = Math.Max(0, viewer.ViewportHeight);
            scrollBar.LargeChange = Math.Max(1, viewer.ViewportHeight);
            scrollBar.Value = Math.Clamp(viewer.VerticalOffset, scrollBar.Minimum, scrollBar.Maximum);
            scrollBar.IsEnabled = scrollBar.Maximum > 0;
        }
        finally { _syncingArchiveScrollbars = false; }
    }

    private void ConflictTreeExtentChanged(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(RefreshConflictMarkerPositions, DispatcherPriority.Loaded);

    private void RefreshConflictMarkerPositions()
    {
        if (_conflictScrollViewer is null || ConflictOverviewBar.Entries.Count == 0) return;
        ArchiveConflictTreeView.UpdateLayout();
        double denominator = Math.Max(1, _conflictScrollViewer.ExtentHeight);
        ConflictOverviewBar.Entries = ConflictOverviewBar.Entries.Select(entry =>
        {
            ArchiveConflictNode? node = _archiveTree.Find(entry.ArchiveName);
            if (node is null || ArchiveConflictTreeView.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem item) return entry;
            Point position = item.TranslatePoint(new Point(0, 0), _conflictScrollViewer);
            double contentY = position.Y + _conflictScrollViewer.VerticalOffset;
            ContentPresenter? header = FindDescendant<ContentPresenter>(item);
            double headerHeight = Math.Max(1, header?.ActualHeight ?? item.FontSize * 2);
            return entry with { TrackRatio = Math.Clamp(contentY / denominator, 0, 1), TrackSizeRatio = Math.Clamp(headerHeight / denominator, 0, 1) };
        }).ToArray();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject source) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(source, index);
            if (child is T match) yield return match;
            foreach (T descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private static T? FindDescendant<T>(DependencyObject source) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(source, index);
            if (child is T match) return match;
            T? descendant = FindDescendant<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    public void Dispose()
    {
        DisposeInvestigation();
        _codeComparisonWindow?.Close();
        _archiveDragActive = false;
        _archiveDragWheelRemainder = 0;
        _archiveDragScrollTimer.Stop();
        _scanCancellation?.Cancel();
        GC.SuppressFinalize(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (FlushInvestigationBeforeClose(e)) return;
        if (_allowClose || !_workspace.CanReset && !_workspace.CanApply)
        {
            base.OnClosing(e);
            return;
        }
        if (_scanLocked || _archiveOperationLocked || _applyingPendingOrderForClose)
        {
            e.Cancel = true;
            return;
        }
        ArchiveOrderCloseDialog dialog = new(_workspace.CanApply) { Owner = this };
        dialog.ShowDialog();
        ArchivePendingCloseDisposition disposition = ArchivePendingClosePolicy.Resolve(dialog.Action);
        if (disposition == ArchivePendingCloseDisposition.KeepOpen)
        {
            e.Cancel = true;
            return;
        }
        if (disposition == ArchivePendingCloseDisposition.CloseNow)
        {
            RecordAction("archive-order-close", "discarded", "Unapplied archive order discarded on exit");
            _allowClose = true;
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        _ = ApplyPendingArchiveOrderAndCloseAsync();
    }

    private async Task ApplyPendingArchiveOrderAndCloseAsync()
    {
        if (_applyingPendingOrderForClose) return;
        _applyingPendingOrderForClose = true;
        SetArchiveOperationLocked(true);
        try
        {
            RecordAction("archive-order-close", "started", "Applying the pending archive order before exit");
            await Task.Run(_workspace.ApplyOrder);
            RecordAction("archive-order-close", "completed", "Pending archive order written and verified before exit");
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            ShowError("archive-order-close", exception);
            _applyingPendingOrderForClose = false;
            SetArchiveOperationLocked(false);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }
}
