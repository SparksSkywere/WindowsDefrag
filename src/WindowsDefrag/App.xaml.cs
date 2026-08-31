using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WindowsDefrag.Cli;
using WindowsDefrag.Helpers;
using WindowsDefrag.Services;
using WindowsDefrag.ViewModels;

namespace WindowsDefrag;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private const int AttachParentProcess = -1;
    private static int _uiErrorCount;
    private LogService? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _log?.Error("Fatal: " + ex.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("Background task: " + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };

        var config = new ConfigService();
        ThemeManager.Apply(config.Config.General.Theme);
        _log = new LogService();
        var elevation = new ElevationService(_log);

        if (!CliHost.ShouldRunCli(e.Args) && elevation.TryRelaunchElevated(e.Args))
        {
            Shutdown(0);
            return;
        }

        var inventory = new DriveInventoryService(_log);
        var protection = new ProtectionService(config);
        var analysis = new AnalysisService(protection, _log);
        var engine = new DefragEngine(protection, _log);
        var trim = new TrimService(_log);
        var reports = new ReportService(config, _log);
        var scheduler = new SchedulerService(_log);
        var chkdsk = new CheckDiskService(_log);

        if (CliHost.ShouldRunCli(e.Args))
        {
            EnsureConsole();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await CliHost.RunAsync(
                e.Args, inventory, analysis, engine, trim, protection, config, elevation, _log).ConfigureAwait(true);
            Shutdown(code);
            return;
        }

        var mainVm = new MainViewModel(
            inventory, analysis, engine, trim, protection, reports, scheduler, chkdsk, config, _log, elevation);
        var window = new MainWindow(mainVm, config);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        var message = args.Exception.GetBaseException().Message;
        _log?.Error("UI: " + message);

        if (Interlocked.Increment(ref _uiErrorCount) <= 1)
        {
            MessageBox.Show(
                message + "\n\nFurther errors will be written to the activity log only.",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        args.Handled = true;
    }

    private static void EnsureConsole()
    {
        if (!AttachConsole(AttachParentProcess))
            AllocConsole();

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }
}
