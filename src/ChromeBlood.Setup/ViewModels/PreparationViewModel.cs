using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using ChromeBlood.Core.Preparation;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ChromeBlood.Setup.ViewModels;

public sealed class PreparationViewModel : INotifyPropertyChanged
{
    private readonly PreparationService _service;
    private readonly IRelayCommand[] _commands;
    private PreparationSnapshot? _snapshot;
    private IReadOnlyList<CheckRow> _readyChecks = [];
    private IReadOnlyList<CheckRow> _manualChecks = [];
    private int _extraGameLocations;
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private GameCandidate? _selectedGame;
    private string? _requestedGame;
    private string? _requestedWabbajack;
    private string _page = "game";
    private bool _busy;
    private bool _closing;
    private bool _review;
    private bool _initialized;
    private bool _installRuntimes = true;
    private bool _enableLongPaths = true;
    private bool _downloadWabbajack = true;
    private bool _cleanGame;
    private bool _resetSettings;
    private bool _clearDiagnostics;
    private bool _steamLaunchOnly;
    private bool _legacyDirectX;
    private string _progressTitle = "Checking preparation";
    private string _progressDetail = "";
    private double? _progressFraction;
    private string _message = "";
    private string? _backupRoot;
    private bool _handoffOpened;
    private bool _rebootRequired;

    public PreparationViewModel(PreparationService service)
    {
        _service = service;
        GameCommand = new RelayCommand(() => Navigate("game"), () => IsIdle);
        PrepareCommand = new RelayCommand(() => Navigate("prepare"), () => IsIdle && HasGame);
        WabbajackCommand = new RelayCommand(() => Navigate("wabbajack"), () => IsIdle && ReadyForHandoff);
        PrimaryCommand = new RelayCommand(Primary, () => IsIdle && HasGame && _snapshot is not null && (!IsWabbajackPage || ReadyForHandoff));
        BackCommand = new RelayCommand(Back, () => IsIdle && (!IsGamePage || _review));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(true), () => IsIdle);
        BrowseGameCommand = new RelayCommand(BrowseGame, () => IsIdle);
        BrowseWabbajackCommand = new RelayCommand(BrowseWabbajack, () => IsIdle);
        ReviewCleanupCommand = new RelayCommand(ReviewCleanup, () => IsIdle && HasGame && (!CleanupBlocked || PendingCleanup));
        CheckGameFilesCommand = new RelayCommand(CheckGameFiles, () => IsIdle && HasGame && !PendingCleanup);
        ContinueWithoutCleanupCommand = new RelayCommand(ContinueWithoutCleanup, () => IsIdle && HasGame && !PendingCleanup && !NeedsGameRepair && !CleanupBlocked);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && _cancellation?.IsCancellationRequested != true);
        OpenBackupCommand = new RelayCommand(() => OpenPath(_backupRoot), () => HasBackup && IsIdle);
        OpenLinkCommand = new RelayCommand<object?>(OpenLink, parameter => IsIdle && parameter is string { Length: > 0 });
        _commands = [GameCommand, PrepareCommand, WabbajackCommand, PrimaryCommand, BackCommand, RefreshCommand, BrowseGameCommand, BrowseWabbajackCommand, ReviewCleanupCommand, CheckGameFilesCommand, ContinueWithoutCleanupCommand, CancelCommand, OpenBackupCommand, OpenLinkCommand];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IRelayCommand GameCommand { get; }
    public IRelayCommand PrepareCommand { get; }
    public IRelayCommand WabbajackCommand { get; }
    public IRelayCommand PrimaryCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand BrowseGameCommand { get; }
    public IRelayCommand BrowseWabbajackCommand { get; }
    public IRelayCommand ReviewCleanupCommand { get; }
    public IRelayCommand CheckGameFilesCommand { get; }
    public IRelayCommand ContinueWithoutCleanupCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand OpenBackupCommand { get; }
    public IRelayCommand<object?> OpenLinkCommand { get; }
    public bool IsBusy => _busy;
    public bool IsIdle => !_busy && !_closing;
    public bool IsGamePage => _page == "game";
    public bool IsPreparePage => _page == "prepare";
    public bool IsWabbajackPage => _page == "wabbajack";
    public bool IsReview => _review;
    public bool ShowBack => _review || !IsGamePage;
    public bool CanChooseFreshCleanup => IsIdle && !PendingCleanup;
    public bool ShowChecklist => IsPreparePage && !_review;
    public bool ShowCleanup => IsPreparePage;
    public bool ShowPageHeading => !IsPreparePage;
    public bool ShowSecondaryContinue => ShowChecklist && !PendingCleanup && !NeedsGameRepair && !CleanupBlocked && CleanupState is "Dirty" or "Unverified";
    public bool HasGame => !string.IsNullOrWhiteSpace(_snapshot?.SelectedGameRoot);
    public bool HasMultipleGames => GameCandidates.Count > 1;
    public bool IsSteam => _selectedGame?.Store.Equals("Steam", StringComparison.OrdinalIgnoreCase) == true;
    public bool ReadyForHandoff => _snapshot?.ReadyForHandoff == true;
    public bool PendingCleanup => _snapshot?.PendingCleanup == true;
    public bool HasRecoveryDetail => !string.IsNullOrWhiteSpace(RecoveryDetail);
    public bool HasBackup => !string.IsNullOrWhiteSpace(_backupRoot);
    public string BackupRoot => _backupRoot ?? "";
    public string GameRoot => _snapshot?.SelectedGameRoot ?? "No game installation selected";
    public string WabbajackDirectory => _snapshot?.WabbajackDirectory ?? "Checking...";
    public string RecoveryDetail => _snapshot?.RecoveryDetail ?? "";
    public IReadOnlyList<GameCandidate> GameCandidates => _snapshot?.GameCandidates ?? [];
    public IReadOnlyList<CheckRow> ReadyChecks => _readyChecks;
    public bool HasReadyChecks => _readyChecks.Count > 0;
    public IReadOnlyList<CheckRow> ManualChecks => _manualChecks;
    public bool HasManualChecks => _manualChecks.Count > 0;
    public bool NeedsRuntimes => NeedsPreparation("vcredist");
    public bool NeedsLongPaths => NeedsPreparation("longpaths");
    public bool NeedsWabbajack => NeedsPreparation("wabbajack");
    public bool HasAutomaticPreparation => NeedsRuntimes || NeedsLongPaths || NeedsWabbajack;
    public bool AutomaticPreparationReady => new[] { "vcredist", "longpaths", "wabbajack" }
        .All(id => _snapshot?.Checks.Any(check => check.Id == id && check.Status == CheckState.Ready) == true);
    public bool ShowSteamChoice => IsSteam && NeedsPreparation("steamupdate");
    public bool ShowDirectXChoice => NeedsPreparation("directx");
    public bool NeedsWabbajackFolder => NeedsPreparation("wabbajackfolder");
    public string ReadyChecksTitle => _readyChecks.Count == 1 ? "1 requirement ready" : $"{_readyChecks.Count} requirements ready";
    public IReadOnlyList<CleanupItemLabel> CleanupItems => _snapshot?.CleanupItems ?? [];
    public IReadOnlyList<CleanupItemLabel> SelectedCleanupItems => CleanupItems.Where(item =>
        item.Scope == "Game" && CleanGame || item.Scope == "Settings" && ResetSettings || item.Scope == "Diagnostics" && ClearDiagnostics).ToArray();
    public string GameMarker => HasGame && !IsGamePage ? "✓" : "1";
    public string PrepareMarker => ReadyForHandoff && !IsPreparePage ? "✓" : "2";
    public string GameDetail => _selectedGame is null ? "Select the folder containing bin\\x64\\Cyberpunk2077.exe." : $"{_selectedGame.Store} installation. Version {_selectedGame.Version}";
    public string CleanupReferenceDetail => _snapshot?.CleanupReferenceDetail ?? "";
    public string PlannedBackupRoot => _snapshot?.PlannedBackupRoot ?? "";
    public bool HasPlannedBackup => !string.IsNullOrWhiteSpace(PlannedBackupRoot);
    public string GameFileCheckDetail => _snapshot?.GameFileHashesChecked == true
        ? $"Official file hashes checked. {_snapshot.ChangedOfficialFiles} changed, {_snapshot.MissingOfficialFiles} missing, {_snapshot.UnreadableGameFiles} unreadable."
            + (_snapshot.GameFileVerifiedAt is { } verified ? $" Verified {verified.ToLocalTime():g}." : "")
        : "Official file contents have not been verified. A full check can take several minutes.";
    public bool NeedsGameRepair => (_snapshot?.ChangedOfficialFiles ?? 0) > 0 || (_snapshot?.MissingOfficialFiles ?? 0) > 0;
    public bool ShowOfficialBackupAction => ShowChecklist && CleanupState == "Repair" && !CleanupBlocked && (_snapshot?.ChangedOfficialFiles ?? 0) > 0;
    public int ExtraGameLocations => _extraGameLocations;
    public bool CleanupBlocked => _snapshot?.CleanupIssue == true || (_snapshot?.UnreadableGameFiles ?? 0) > 0
        || _snapshot is { GameFileReferenceMatched: true, GameFileScanComplete: false };
    public bool GameIsClean => _snapshot is { GameFileReferenceMatched: true, GameFileScanComplete: true, GameFileHashesChecked: true, CleanupIssue: false, PendingCleanup: false,
        ChangedOfficialFiles: 0, MissingOfficialFiles: 0, UnreadableGameFiles: 0 } && ExtraGameLocations == 0;
    public string CleanupState => PendingCleanup ? "Pending" : CleanupBlocked ? "Blocked" : NeedsGameRepair ? "Repair" : ExtraGameLocations > 0 ? "Dirty"
        : GameIsClean ? "Clean" : _snapshot?.GameFileReferenceMatched != true ? "Unsupported" : "Unverified";
    public string CleanupTitle => CleanupState switch
    {
        "Pending" => "Finish your cleanup",
        "Repair" => "Your game needs repair",
        "Blocked" => "Check needs attention",
        "Dirty" => "Your game needs cleanup",
        "Clean" => _snapshot?.PreservedDlssFiles > 0 ? "Your game is ready" : "Your game is clean",
        "Unsupported" => "Full check unavailable",
        _ => "Ready to check your game"
    };
    public string CleanupDetail => (CleanupState switch
    {
        "Pending" => "Resume safely from the saved backup.",
        "Repair" => _review && CleanGame ? "Changed official files will be copied to backup; extra files will be removed." : "Repair official files through your game store, then verify again.",
        "Blocked" => "Close the game and mod tools, resolve the findings in Details, then check again.",
        "Dirty" => _review && CleanGame ? $"{ExtraGameLocations:N0} extra {(ExtraGameLocations == 1 ? "item" : "items")} will be backed up and removed." : $"{ExtraGameLocations:N0} extra {(ExtraGameLocations == 1 ? "item" : "items")} found.",
        "Clean" => _snapshot?.PreservedDlssFiles > 0 ? "Game files checked. No cleanup needed." : "Official files verified. No extra game files found.",
        "Unsupported" => "Known mod locations were checked; a full file reference is unavailable for this installation.",
        _ => "Verify the game files before installing Chrome & Blood."
    }) + (_snapshot?.PreservedDlssFiles > 0 ? " Custom DLSS files and verified Swapper backups are kept." : "");
    public string CleanupSymbol => CleanupState is "Clean" ? "✓" : CleanupState is "Unverified" ? "?" : "!";
    public string SecondaryContinueText => CleanupState == "Dirty" ? "Continue without cleanup" : "Continue without verification";
    public string ReviewTitle => PendingCleanup ? "Resume the saved cleanup" : CleanGame ? "Review cleanup" : "Review preparation";
    public string ReviewReminder => CleanGame || ResetSettings || ClearDiagnostics ? "Close the game, Steam and mod managers before continuing." : "Only your selected preparation will run.";
    public IReadOnlyList<string> ReviewedPreparationActions => AdditionalActions();
    public bool HasAdditionalActions => ReviewedPreparationActions.Count > 0;
    public string BackupSizeDetail => _snapshot?.CleanupBytes > 0 ? $"Identified game-folder files: {_snapshot.CleanupBytes:N0} bytes. Optional settings and diagnostics may need additional space." : "";
    public string SummaryStatus => IsBusy ? "Checking" : ReadyForHandoff ? "Ready for Wabbajack" : HasGame ? "Preparation" : "Find your game";
    public string PageTitle => _review ? "Review preparation" : IsGamePage ? "Your Cyberpunk 2077 installation" : IsWabbajackPage ? "Open Chrome & Blood" : "Prepare for installation";
    public string PageDetail => _review ? "Only the selected actions below will run. Existing files are preserved before cleanup." : IsGamePage ? "Confirm the game installation you want to use for Chrome & Blood." : IsWabbajackPage ? "Preparation ends here. Wabbajack handles sign-in, installation and downloads." : "Check the requirements and choose any preparation your PC needs.";
    public bool HasSelectedWork => CleanGame || ResetSettings || ClearDiagnostics || InstallRuntimes && NeedsPreparation("vcredist")
        || EnableLongPaths && NeedsPreparation("longpaths") || DownloadWabbajack && NeedsPreparation("wabbajack")
        || SteamLaunchOnly && IsSteam && NeedsPreparation("steamupdate") || LegacyDirectX && NeedsPreparation("directx");
    public string PrimaryText => _review ? CleanGame || ResetSettings || ClearDiagnostics ? PendingCleanup ? "Resume cleanup" : "Back up and clean" : "Prepare for installation"
        : IsGamePage ? "Continue" : IsWabbajackPage ? "Open C&B in Wabbajack" : CleanupState switch
        {
            "Pending" => "Resume cleanup",
            "Repair" => "Verify again",
            "Blocked" => "Check again",
            "Dirty" => "Clean up game",
            "Unverified" => "Verify game files",
            _ => ReadyForHandoff && !HasSelectedWork ? "Continue to Wabbajack" : "Review preparation"
        };
    public string BackText => _review ? "Back to preparation" : IsWabbajackPage ? "Back to preparation" : IsPreparePage ? "Change game installation" : "";
    public string ProgressTitle => _closing ? "Stopping preparation" : _progressTitle;
    public string ProgressDetail => _closing ? "Waiting for the current operation to finish safely before closing." : _progressDetail;
    public double ProgressFraction => _progressFraction ?? 0;
    public bool ProgressIndeterminate => _progressFraction is null;
    public string CancelText => _cancellation?.IsCancellationRequested == true ? "Stopping..." : "Cancel";
    public string Message => _message;
    public string HandoffDetail => _handoffOpened ? "Chrome & Blood is open in Wabbajack. Continue there." : "The required preparation checks are ready.";

    public GameCandidate? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!IsIdle || value is null || value.Root == _selectedGame?.Root) return;
            _requestedGame = value.Root;
            _ = ScanAsync(false);
        }
    }

    public bool InstallRuntimes { get => _installRuntimes; set => SetChoice(ref _installRuntimes, value); }
    public bool EnableLongPaths { get => _enableLongPaths; set => SetChoice(ref _enableLongPaths, value); }
    public bool DownloadWabbajack { get => _downloadWabbajack; set => SetChoice(ref _downloadWabbajack, value); }
    public bool CleanGame { get => _cleanGame; set => SetChoice(ref _cleanGame, value); }
    public bool ResetSettings { get => _resetSettings; set => SetChoice(ref _resetSettings, value); }
    public bool ClearDiagnostics { get => _clearDiagnostics; set => SetChoice(ref _clearDiagnostics, value); }
    public bool SteamLaunchOnly { get => _steamLaunchOnly; set => SetChoice(ref _steamLaunchOnly, value); }
    public bool LegacyDirectX { get => _legacyDirectX; set => SetChoice(ref _legacyDirectX, value); }

    public IReadOnlyList<string> SelectedActions
    {
        get
        {
            List<string> actions = AdditionalActions();
            if (CleanGame) actions.Add(PendingCleanup ? "Continue the interrupted cleanup using its recorded selections and saved backup." : $"Move {ExtraGameLocations} extra game-folder locations into a retained backup.");
            if (actions.Count == 0) actions.Add("Recheck preparation. No selected installation or cleanup actions are needed.");
            return actions;
        }
    }

    private List<string> AdditionalActions()
    {
        List<string> actions = [];
        AddMissing(actions, "vcredist", InstallRuntimes);
        AddMissing(actions, "longpaths", EnableLongPaths);
        AddMissing(actions, "wabbajack", DownloadWabbajack);
        if (SteamLaunchOnly && IsSteam && NeedsPreparation("steamupdate")) actions.Add("Set Steam to update Cyberpunk 2077 only on launch. Close Steam first.");
        if (LegacyDirectX && NeedsPreparation("directx")) actions.Add("Install legacy DirectX components.");
        if (ResetSettings && !PendingCleanup) actions.Add("Back up and reset Cyberpunk settings and user mod settings. Saves are kept.");
        if (ClearDiagnostics && !PendingCleanup) actions.Add("Back up and clear shared REDEngine diagnostics.");
        return actions;
    }

    public Task InitializeAsync()
    {
        if (_initialized) return _operation;
        _initialized = true;
        return ScanAsync(true);
    }

    public Task RefreshAsync(bool force = false)
    {
        if (!_initialized || !IsIdle || _review && !force) return _operation;
        return ScanAsync(false);
    }

    public async Task CancelAndDrainAsync()
    {
        _closing = true;
        _cancellation?.Cancel();
        Notify();
        await _operation;
    }

    private Task ScanAsync(bool choosePage)
    {
        return StartOperation(async token =>
        {
            _progressTitle = "Checking preparation";
            _progressDetail = "Finding the game and checking Windows requirements.";
            Notify();
            PreparationSnapshot snapshot = await _service.ScanAsync(_requestedGame, _requestedWabbajack, token);
            UpdateSnapshot(snapshot);
            if (choosePage || !HasGame || IsWabbajackPage && !ReadyForHandoff) _page = HasGame ? "prepare" : "game";
            _review = false;
        });
    }

    private Task StartOperation(Func<CancellationToken, Task> operation)
    {
        if (!IsIdle) return _operation;
        _busy = true;
        _progressFraction = null;
        _cancellation = new CancellationTokenSource();
        Notify();
        _operation = RunOperationAsync(operation, _cancellation);
        return _operation;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, CancellationTokenSource cancellation)
    {
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { _message = "Preparation cancelled. Completed actions and preserved files are kept."; }
        catch (Exception exception) { _message = exception.Message; }
        finally
        {
            _busy = false;
            _cancellation = null;
            cancellation.Dispose();
            Notify();
        }
    }

    internal void UpdateSnapshot(PreparationSnapshot snapshot)
    {
        _snapshot = snapshot;
        _extraGameLocations = snapshot.CleanupItems.Count(item => item.Scope == "Game");
        if (GameIsClean) _cleanGame = false;
        if (snapshot.PendingCleanup) { _resetSettings = false; _clearDiagnostics = false; }
        _readyChecks = snapshot.Checks.Where(check => check.Required && check.Status == CheckState.Ready).Select(check => new CheckRow(check)).ToArray();
        _manualChecks = snapshot.Checks.Where(check => check.Required && check.Status != CheckState.Ready
            && check.Id is not ("vcredist" or "longpaths" or "wabbajack")).Select(check => new CheckRow(check)).ToArray();
        _requestedGame = snapshot.SelectedGameRoot;
        _requestedWabbajack = snapshot.WabbajackDirectory;
        _selectedGame = snapshot.GameCandidates.FirstOrDefault(candidate => candidate.Root.Equals(snapshot.SelectedGameRoot, StringComparison.OrdinalIgnoreCase));
    }

    private void Primary()
    {
        if (_snapshot is null || !IsIdle) return;
        if (_review)
        {
            PreparationSnapshot reviewed = _snapshot;
            PreparationOptions options = new()
            {
                InstallRuntimes = InstallRuntimes,
                EnableLongPaths = EnableLongPaths,
                DownloadWabbajack = DownloadWabbajack,
                CleanGame = CleanGame,
                ResetSettings = ResetSettings,
                ClearDiagnostics = ClearDiagnostics,
                SteamLaunchOnly = SteamLaunchOnly && IsSteam,
                LegacyDirectX = LegacyDirectX
            };
            _ = StartOperation(async token =>
            {
                Progress<PreparationProgress> progress = new(value =>
                {
                    _progressTitle = value.Title;
                    _progressDetail = value.Detail;
                    _progressFraction = value.Fraction;
                    Notify();
                });
                PreparationResult result = await _service.ApplyAsync(reviewed, options, progress, token);
                UpdateSnapshot(result.Snapshot);
                _message = result.Message;
                _backupRoot = result.BackupRoot ?? _backupRoot;
                _rebootRequired |= result.RebootRequired;
                if (_rebootRequired) _message += " Restart Windows before launching the game. Wabbajack can download meanwhile.";
                _review = false;
                _page = "prepare";
                if (result.Completed) { _cleanGame = false; _resetSettings = false; _clearDiagnostics = false; }
            });
        }
        else if (IsGamePage) Navigate("prepare");
        else if (IsWabbajackPage)
        {
            PreparationSnapshot snapshot = _snapshot;
            _ = StartOperation(async token =>
            {
                _progressTitle = "Opening Chrome & Blood in Wabbajack";
                _progressDetail = "";
                Notify();
                _message = await _service.OpenWabbajackAsync(snapshot, token);
                _handoffOpened = true;
            });
        }
        else if (PendingCleanup || CleanupState == "Dirty") ReviewCleanup();
        else if (CleanupBlocked) _ = RefreshAsync(true);
        else if (NeedsGameRepair || CleanupState == "Unverified") CheckGameFiles();
        else ReviewOrContinue();
    }

    private void ContinueWithoutCleanup()
    {
        if (!IsIdle || PendingCleanup || NeedsGameRepair || CleanupBlocked) return;
        _cleanGame = false;
        ReviewOrContinue();
    }

    private void ReviewOrContinue()
    {
        if (ReadyForHandoff && !HasSelectedWork) Navigate("wabbajack");
        else { _review = true; Notify(); }
    }

    private void ReviewCleanup()
    {
        if (!IsIdle || CleanupBlocked && !PendingCleanup) return;
        _page = "prepare";
        _review = true;
        _cleanGame = true;
        Notify();
    }

    private void CheckGameFiles()
    {
        if (_snapshot is null || !IsIdle || PendingCleanup) return;
        PreparationSnapshot reviewed = _snapshot;
        _ = StartOperation(async token =>
        {
            _progressTitle = "Verifying game files";
            _progressDetail = "Reading official file contents. This can take several minutes.";
            Notify();
            Progress<PreparationProgress> progress = new(value =>
            {
                _progressTitle = value.Title;
                _progressDetail = value.Detail;
                _progressFraction = value.Fraction;
                Notify();
            });
            UpdateSnapshot(await _service.VerifyGameFilesAsync(reviewed, progress, token));
        });
    }

    private bool NeedsPreparation(string id) => _snapshot?.Checks.FirstOrDefault(check => check.Id == id) is { Status: not CheckState.Ready };

    private void Navigate(string page)
    {
        _page = page;
        _review = false;
        Notify();
    }

    private void Back()
    {
        if (_review || IsWabbajackPage) Navigate("prepare");
        else if (IsPreparePage) Navigate("game");
    }

    private void BrowseGame()
    {
        OpenFolderDialog dialog = new() { Title = "Choose your Cyberpunk 2077 game folder" };
        if (HasGame && Directory.Exists(GameRoot)) dialog.InitialDirectory = GameRoot;
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        _requestedGame = dialog.FolderName;
        _ = ScanAsync(false);
    }

    private void BrowseWabbajack()
    {
        OpenFolderDialog dialog = new() { Title = "Choose the folder for the Wabbajack app" };
        if (Directory.Exists(WabbajackDirectory)) dialog.InitialDirectory = WabbajackDirectory;
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        _requestedWabbajack = dialog.FolderName;
        _ = ScanAsync(false);
    }

    private void Cancel()
    {
        _cancellation?.Cancel();
        Notify();
    }

    private void OpenLink(object? parameter)
    {
        if (parameter is string url && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http" or "steam") OpenPath(url);
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { _message = exception.Message; Notify(); }
    }

    private void AddMissing(List<string> actions, string id, bool selected)
    {
        if (!selected) return;
        PreparationCheck? check = _snapshot?.Checks.FirstOrDefault(candidate => candidate.Id == id);
        if (check is not null && check.Status != CheckState.Ready) actions.Add(check.Title);
    }

    private void SetChoice(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value || !IsIdle || PendingCleanup && name is nameof(CleanGame) or nameof(ResetSettings) or nameof(ClearDiagnostics)) return;
        field = value;
        Notify();
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
        foreach (IRelayCommand command in _commands) command.NotifyCanExecuteChanged();
    }

    public sealed class CheckRow(PreparationCheck check)
    {
        public string Title => check.Title;
        public string Detail => check.Detail;
        public string State => check.Status.ToString();
        public string? ActionUrl => check.Status == CheckState.Ready ? null : check.ActionUrl;
        public bool HasActionUrl => !string.IsNullOrWhiteSpace(ActionUrl);
        public string StatusText => check.Status switch
        {
            CheckState.Ready => "Ready",
            CheckState.Needed => "Needed",
            CheckState.Action => "Your action",
            CheckState.Optional => "Optional",
            _ => "Failed"
        };
    }

}

