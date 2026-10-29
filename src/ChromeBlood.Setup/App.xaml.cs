using System.Windows;
using ChromeBlood.Core.Preparation;
using ChromeBlood.Presentation;
using ChromeBlood.Setup.ViewModels;

namespace ChromeBlood.Setup;

public partial class App : Application
{
    private Mutex? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            if (e.Args.Length != 2 || e.Args[0] != "--prerequisite") { Shutdown(64); return; }
            try { Shutdown(await PreparationPackages.RunPrerequisiteAsync(e.Args[1], CancellationToken.None)); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Shutdown(1);
            }
            return;
        }
        try
        {
            _instance = new Mutex(false, "Local\\ChromeBloodSetup");
            bool acquired;
            try { acquired = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                _instance.Dispose();
                _instance = null;
                MessageBox.Show("Chrome & Blood setup is already open. Use its existing window to continue.", "Chrome & Blood setup");
                Shutdown(0);
                return;
            }
            LauncherTheme.Apply(Resources);
            PreparationViewModel model = new(new PreparationService());
            ApplicationWindow window = new();
            window.ShowView(new MainWindow(model));
            MainWindow = window;
            window.Show();
            await model.InitializeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MessageBox.Show(exception.Message, "Chrome & Blood setup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
