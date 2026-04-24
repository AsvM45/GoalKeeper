using System.Windows;
using System.IO;
using ConfigUI.Services;
using Serilog;

namespace ConfigUI;

public partial class App : Application
{
    public static PipeClient Pipe { get; private set; } = null!;
    public static Serilog.ILogger Log { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(@"C:\ProgramData\GoalKeeper\logs\config-ui-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            Log.Fatal((Exception)ev.ExceptionObject, "AppDomain Unhandled Exception");
        TaskScheduler.UnobservedTaskException += (s, ev) =>
        {
            Log.Fatal(ev.Exception, "TaskScheduler Unobserved Exception");
            ev.SetObserved();
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            Log.Fatal(ev.Exception, "Dispatcher Unhandled Exception");
            ev.Handled = true; // prevent immediate hard crash
        };

        Pipe = new PipeClient();
        _ = Pipe.ConnectAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Pipe?.Dispose();
        base.OnExit(e);
    }
}
