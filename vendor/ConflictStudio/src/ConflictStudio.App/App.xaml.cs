using System.Windows;
using System.Windows.Threading;
using System.IO;

namespace ConflictStudio.App;

public partial class App : Application
{
    private readonly DiagnosticLog _diagnostics = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cyberpunk Conflict Studio"));

    public App()
    {
        DispatcherUnhandledException += DispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += AppDomainUnhandled;
        TaskScheduler.UnobservedTaskException += UnobservedTask;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);
            MainWindow window = new();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            ReportStartupFailure(exception, true);
            Shutdown(-1);
        }
    }

    private void DispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportStartupFailure(e.Exception, true);
        Shutdown(-1);
    }

    private void AppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown unhandled application failure.");
        _diagnostics.TryWriteStartupFailure(exception);
    }

    private void UnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _diagnostics.TryWriteStartupFailure(e.Exception);
        e.SetObserved();
    }

    private void ReportStartupFailure(Exception exception, bool showMessage)
    {
        bool recorded = _diagnostics.TryWriteStartupFailure(exception);
        if (!showMessage) return;
        string detail = recorded ? $"Details were saved to {_diagnostics.DirectoryPath}\\startup-failure.log." : "Conflict Studio could not write its startup-failure log.";
        MessageBox.Show($"Conflict Studio encountered an error and must close. {detail}", "Conflict Studio error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
