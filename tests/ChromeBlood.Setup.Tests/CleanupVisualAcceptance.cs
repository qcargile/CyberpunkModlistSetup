using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChromeBlood.Core.Preparation;
using ChromeBlood.Presentation;
using ChromeBlood.Setup;
using ChromeBlood.Setup.ViewModels;

namespace ChromeBlood.Setup.Tests;

[TestClass]
public sealed class CleanupVisualAcceptance
{
    [STATestMethod]
    [TestCategory("CleanupVisual")]
    public void RenderCleanupStatesForVisualReview()
    {
        string? requested = Environment.GetEnvironmentVariable("CHROMEBLOOD_CLEANUP_RENDER_ROOT");
        if (string.IsNullOrWhiteSpace(requested)) Assert.Inconclusive("Set CHROMEBLOOD_CLEANUP_RENDER_ROOT to render cleanup states.");
        string output = Path.GetFullPath(requested!);
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        Assert.IsTrue(output.StartsWith(temporary, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(output);
        Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "ChromeBlood.Presentation;component/Themes/Launcher.xaml", "ChromeBloodSetup;component/Resources/Typography.xaml", "ChromeBloodSetup;component/Resources/Controls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/" + resource) });
        LauncherTheme.Apply(application.Resources);
        PreparationSnapshot basis = new()
        {
            SelectedGameRoot = @"D:\Games\Cyberpunk 2077",
            GameCandidates = [new(@"D:\Games\Cyberpunk 2077", "Steam", "2.5", "26161058")],
            WabbajackDirectory = @"D:\Wabbajack",
            GameFileReferenceMatched = true,
            GameFileScanComplete = true,
            Checks = [new("game", "Cyberpunk 2077", "Game found", CheckState.Ready, true),
                new("vcredist", "Visual C++ runtimes", "Installed", CheckState.Ready, true),
                new("longpaths", "Windows long paths", "Enabled", CheckState.Ready, true),
                new("wabbajack", "Wabbajack", "Ready", CheckState.Ready, true)],
            ReadyForHandoff = true,
            CleanupReferenceDetail = "Steam 2.5 English public build 26161058 with REDmod and Phantom Liberty matches the reference.",
            GameBackupRoot = @"D:\Games\Cyberpunk 2077.ChromeBlood-Backups",
            SettingsBackupRoot = @"C:\ChromeBlood-Backups\LocalAppData",
            PlannedBackupRoot = @"D:\Games\Cyberpunk 2077.ChromeBlood-Backups"
        };
        PreparationSnapshot dirty = basis with
        {
            CleanupItems = [new(@"archive\pc\mod", "Added mod files"), new(@"bin\x64\version.dll", "Added framework loader")],
            CleanupBytes = 4096
        };
        PreparationSnapshot missing = dirty with
        {
            ReadyForHandoff = false,
            Checks = [new("game", "Cyberpunk 2077", "Game found", CheckState.Ready, true),
                new("vcredist", "Visual C++ runtimes", "Install runtimes", CheckState.Needed, true),
                new("longpaths", "Windows long paths", "Enable long paths", CheckState.Needed, true),
                new("wabbajack", "Wabbajack", "Download the app", CheckState.Needed, true),
                new("steamupdate", "Steam update preference", "Launch only", CheckState.Optional, false),
                new("directx", "Legacy DirectX", "Optional", CheckState.Optional, false)]
        };
        Capture("preparation-needed", missing, false, 1280, 800);
        Capture("preparation-review", missing, true, 1280, 800);
        Capture("preparation-narrow", missing, false, 640, 700);
        Capture("manual-action", missing with { Checks = [.. missing.Checks, new("wabbajackfolder", "Wabbajack app folder", "Choose a folder outside the game, Windows, Program Files and cloud sync.", CheckState.Action, true)] }, false, 1280, 800);
        Capture("cleanup-needed", dirty, false, 1280, 800);
        Capture("cleanup-review", dirty, true, 1280, 800);
        Capture("clean", basis with { GameFileHashesChecked = true, GameFileVerifiedAt = DateTimeOffset.UtcNow }, false, 1280, 800);
        Capture("not-verified", basis, false, 1280, 800);
        Capture("repair-needed", basis with { ChangedOfficialFiles = 1, MissingOfficialFiles = 2, ReadyForHandoff = false }, false, 1280, 800);
        Capture("intentional-dlss", basis with
        {
            ChangedOfficialFiles = 1,
            CleanupItems = [new(@"bin\x64\nvngx_dlss.dll", "Different from the store version", "Changed official")]
        }, false, 640, 700, fileChoices: true);
        Capture("cleanup-compact", dirty, false, 760, 800);
        Capture("cleanup-recovery", dirty with { PendingCleanup = true, ReadyForHandoff = false, RecoveryDetail = "The previous cleanup was interrupted. Continue with its recorded backup." }, false, 1280, 800);
        PreparationSnapshot failed = dirty with
        {
            PendingCleanup = true, ReadyForHandoff = false,
            ManualCleanupGuidance = "Close the game and mod tools. Keep the saved backup and its journal. Move only identified extra mod files to a backup outside the game. Do not delete official files; repair them through your game store. Settings and crash reports can be left alone. You can continue with other preparation while cleanup remains unfinished.",
            ManualCleanupItems = [new(@"D:\Games\Cyberpunk 2077\archive\pc\mod", "Added mod files: back up outside the game, then remove from the game folder.")],
            RetainedBackupRoot = @"D:\Games\Cyberpunk 2077.ChromeBlood-Backups",
            CleanupFailure = "Could not preserve CleanSlate_Quarantine\\2026-10-28_004428\\archive\\pc\\mod\\example.archive. The file is being used by another process. Windows error 32.\nClose the application using this file, then retry cleanup. Saved files are retained."
        };
        Capture("cleanup-failed", failed, false, 1280, 800);
        Capture("cleanup-failed-narrow", failed, false, 640, 700);
        Capture("cleanup-manual-continue", failed with { CleanupDeferred = true, ReadyForHandoff = true }, false, 1280, 800);
        Capture("game-selection", basis, false, 1280, 800, "game");
        Capture("wabbajack-handoff", basis, false, 1280, 800, "wabbajack");
        VerifyClose(false);
        VerifyClose(true);

        void VerifyClose(bool pending)
        {
            SynchronizationContext? priorContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            PreparationViewModel model = new(new PreparationService());
            Assert.IsTrue(model.RefreshAsync().IsCompletedSuccessfully);
            Assert.IsFalse(model.IsBusy);
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancelled = false;
            if (pending)
            {
                Func<CancellationToken, Task> work = async token =>
                {
                    using CancellationTokenRegistration registration = token.Register(() => cancelled = true);
                    await completion.Task;
                };
                typeof(PreparationViewModel).GetMethod("StartOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(model, [work]);
            }
            ApplicationWindow window = new() { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.ShowView(new MainWindow(model));
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            Exception? failure = null;
            DispatcherUnhandledExceptionEventHandler capture = (_, args) => { failure = args.Exception; args.Handled = true; };
            application.DispatcherUnhandledException += capture;
            try
            {
                window.Show();
                window.Close();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.IsNull(failure);
                if (pending)
                {
                    Assert.IsTrue(cancelled);
                    Assert.IsFalse(closed);
                    Assert.IsTrue(model.IsBusy);
                    window.Close();
                    completion.SetResult();
                }
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                while (!closed && failure is null && clock.Elapsed < TimeSpan.FromSeconds(2))
                {
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Thread.Sleep(1);
                }
                Assert.IsNull(failure);
                Assert.IsTrue(closed, "The window did not close after its operation completed.");
                Assert.IsFalse(model.IsBusy);
                Assert.IsFalse(model.IsIdle);
                Console.WriteLine($"Window close after {(pending ? "drain" : "idle")}: {clock.Elapsed.TotalMilliseconds:F1} ms");
            }
            finally
            {
                completion.TrySetResult();
                application.DispatcherUnhandledException -= capture;
                if (!closed) window.Close();
                SynchronizationContext.SetSynchronizationContext(priorContext);
            }
        }

        void Capture(string name, PreparationSnapshot snapshot, bool review, int width, int height, string page = "prepare", bool fileChoices = false)
        {
            PreparationViewModel model = new(new PreparationService());
            model.UpdateSnapshot(snapshot);
            model.ShowFileChoices = fileChoices;
            model.PrepareCommand.Execute(null);
            if (page == "game") model.GameCommand.Execute(null);
            if (page == "wabbajack") model.WabbajackCommand.Execute(null);
            if (review)
            {
                model.ResetSettings = true;
                model.ReviewCleanupCommand.Execute(null);
            }
            MainWindow view = new(model) { Width = width, Height = height };
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            Button skip = Descendants(view).OfType<Button>().Single(button => ReferenceEquals(button.Command, model.ContinueWithoutCleanupCommand));
            Assert.AreEqual(model.ShowSecondaryContinue ? Visibility.Visible : Visibility.Collapsed, skip.Visibility);
            CheckBox runtimes = Descendants(view).OfType<CheckBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Install missing Visual C++ runtimes");
            Assert.AreEqual(model.NeedsRuntimes ? Visibility.Visible : Visibility.Collapsed, runtimes.Visibility);
            Assert.IsTrue(runtimes.IsChecked);
            if (!review && model.IsPreparePage && model.NeedsRuntimes)
            {
                Assert.IsTrue(runtimes.TranslatePoint(new Point(0, 0), view).Y < height - 100);
                Assert.IsTrue(runtimes.ActualWidth > 200);
                runtimes.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                Assert.IsFalse(model.InstallRuntimes);
                runtimes.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Assert.IsTrue(model.InstallRuntimes);
            }
            RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream target = new(Path.Combine(output, name + ".png"), FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(target);
            view.DataContext = null;
        }

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
