using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App;

public partial class App : Application
{
    private SingleInstance? _instance;

    /// <summary>The app's root state, for views that are not handed it directly.</summary>
    public static AppModel Model { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception, "domain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception, "task");
            args.SetObserved();
        };

        var args = e.Args;

        // DSH.exe --write-recovery-usb <job.json>: the elevated helper the DGX Spark guide starts to
        // erase and write a recovery USB stick. It shows no window and exits when the job is done, so
        // the main app never needs administrator rights.
        if (RecoveryUsbWriter.TryRunHelper(args) is { } helperExit)
        {
            Shutdown(helperExit);
            return;
        }

        var selfTest = SelfTest.Parse(args);

        // One DSH per user: a second launch ("Open with DSH" on a folder) hands its folder over.
        if (selfTest is null)
        {
            _instance = new SingleInstance("DSH-Windows");
            if (!_instance.IsFirst && _instance.Forward(args.Length == 0 ? ["--activate"] : args.Select(a => a.StartsWith("--", StringComparison.Ordinal) ? a : Path.GetFullPath(a)).ToArray()))
            {
                Shutdown();
                return;
            }
        }

        if (selfTest is not null)
        {
            Environment.SetEnvironmentVariable("DSH_HOME", selfTest.Home);
            selfTest.Prepare();
        }
        FontResolver.Apply(Resources);
        // Deleting a skill is recoverable, like everything else deleted from the app.
        SkillManager.RecycleBin = ShellIntegration.MoveToRecycleBin;
        var config = new AppConfig();
        ThemeService.Instance.Apply(selfTest?.Theme ?? config.Theme);
        var log = new ConversationLog();
        Model = new AppModel(config, log, Dispatcher);

        var window = new MainWindow(Model);
        MainWindow = window;

        if (selfTest is not null)
        {
            selfTest.Run(window, Model);
            return;
        }

        window.Show();
        Model.Start();

        var folder = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (folder is not null && Directory.Exists(folder)) Model.OpenProject(folder);

        _instance?.Listen(forwarded => Dispatcher.BeginInvoke(() => HandleForwarded(forwarded)));

        if (Model.NeedsSetup)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => window.ShowWizard());
        if (config.CheckForUpdates) _ = CheckForUpdatesAsync();
    }

    private void HandleForwarded(string[] args)
    {
        if (MainWindow is MainWindow window) window.BringToFront();
        var folder = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (folder is not null && Directory.Exists(folder)) Model.OpenProject(folder);
    }

    private static async Task CheckForUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (await UpdateChecker.CheckAsync() is { } release)
            Model.Update = new AppModel.UpdateInfo(release.Tag, release.Url);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Model?.StopAll();
            Model?.Config.SaveNow();
            Model?.Spark.Dispose();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, "exit");
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteCrashLog(e.Exception, "ui");
        e.Handled = true;
        if (SelfTest.Current is { } test)
        {
            test.Fail("unhandled", e.Exception);
            return;
        }
        // Keep going: a failed redraw should not cost the user their chats. The log says what broke.
        try
        {
            Dialog.Info("Something went wrong",
                $"DSH hit an unexpected error and recovered. Details were saved to:\n{path}\n\n{e.Exception.GetBaseException().Message}");
        }
        catch (Exception)
        {
            // The dialog itself failed; the log is written.
        }
    }

    /// <summary>Write a crash report to %APPDATA%\DSH\logs and return its path.</summary>
    public static string? WriteCrashLog(Exception? exception, string source)
    {
        if (exception is null) return null;
        try
        {
            var directory = AppPaths.Ensure(AppPaths.Logs);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{source}.txt");
            var text = new StringBuilder()
                .AppendLine($"DSH for Windows {AppInfo.Version}")
                .AppendLine($"{Environment.OSVersion} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "64" : "32")}-bit")
                .AppendLine($"{DateTimeOffset.Now:O} ({source})")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();
            File.WriteAllText(path, text);
            // Keep the folder from growing without bound.
            foreach (var old in new DirectoryInfo(directory).GetFiles("crash-*.txt").OrderByDescending(f => f.CreationTimeUtc).Skip(20))
                old.Delete();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
