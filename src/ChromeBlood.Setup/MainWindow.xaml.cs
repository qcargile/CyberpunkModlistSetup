using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using ChromeBlood.Presentation;
using ChromeBlood.Setup.ViewModels;

namespace ChromeBlood.Setup;

public partial class MainWindow : UserControl
{
    private Window? _host;
    private string? _lastPage;

    public MainWindow()
    {
        InitializeComponent();
        LauncherTextTracking.Apply(TitleWordmark, 1);
        SizeChanged += (_, _) => ArrangeLayout();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MainWindow(PreparationViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _host = Window.GetWindow(this);
        if (_host is not null) _host.Activated += OnActivated;
        if (DataContext is PreparationViewModel model) model.PropertyChanged += RefreshPresentation;
        ArrangeLayout();
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_host is not null) _host.Activated -= OnActivated;
        if (DataContext is PreparationViewModel model) model.PropertyChanged -= RefreshPresentation;
        _host = null;
    }

    private async void OnActivated(object? sender, EventArgs args)
    {
        if (DataContext is PreparationViewModel model) await model.RefreshAsync();
    }

    private void ArrangeLayout()
    {
        bool compact = ActualWidth < 900;
        ProductLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        InstallationRail.Width = compact ? 0 : 202;
        InstallationScroll.Padding = compact ? new Thickness(16, 24, 16, 20) : new Thickness(40, 31, 40, 26);
        bool stackedSummary = ActualWidth < 1080;
        ChoicesGap.Width = new GridLength(stackedSummary ? 0 : 32);
        OptionalColumn.Width = stackedSummary ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(OptionalChoices, stackedSummary ? 0 : 2);
        Grid.SetRow(OptionalChoices, stackedSummary ? 1 : 0);
        OptionalChoices.Margin = stackedSummary ? new Thickness(0, 24, 0, 0) : new Thickness(0);
        bool showSummary = DataContext is not PreparationViewModel { IsPreparePage: true };
        PreparationSummary.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
        SummaryColumn.Width = new GridLength(stackedSummary || !showSummary ? 0 : 288);
        SummaryGap.Width = new GridLength(stackedSummary || !showSummary ? 0 : 32);
        Grid.SetColumn(PreparationSummary, stackedSummary ? 0 : 2);
        Grid.SetRow(PreparationSummary, stackedSummary ? 1 : 0);
        PreparationSummary.Margin = stackedSummary ? new Thickness(0, 24, 0, 0) : new Thickness(0);
        PreparationSummary.MinHeight = stackedSummary ? 0 : 398;
    }

    private void RefreshPresentation(object? sender, PropertyChangedEventArgs args)
    {
        if (DataContext is not PreparationViewModel model) return;
        if (_lastPage != model.PageTitle)
        {
            _lastPage = model.PageTitle;
            InstallationScroll.ScrollToTop();
            ArrangeLayout();
        }
        if (_host?.TaskbarItemInfo is not { } taskbar) return;
        taskbar.ProgressValue = Math.Clamp(model.ProgressFraction, 0, 1);
        taskbar.ProgressState = !model.IsBusy ? TaskbarItemProgressState.None : model.ProgressIndeterminate ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal;
    }

    public Task CancelAndDrainAsync() => DataContext is PreparationViewModel model ? model.CancelAndDrainAsync() : Task.CompletedTask;

    private void MinimizeWindow(object sender, RoutedEventArgs args)
    {
        if (Window.GetWindow(this) is { } host) host.WindowState = WindowState.Minimized;
    }

    private void ToggleMaximizeWindow(object sender, RoutedEventArgs args)
    {
        if (Window.GetWindow(this) is { } host) host.WindowState = host.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseWindow(object sender, RoutedEventArgs args) => Window.GetWindow(this)?.Close();
}
