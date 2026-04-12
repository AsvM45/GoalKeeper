using System.Windows;
using ConfigUI.Services;

namespace ConfigUI;

public partial class App : Application
{
    public static PipeClient Pipe { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Pipe = new PipeClient();
        _ = Pipe.ConnectAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Pipe?.Dispose();
        base.OnExit(e);
    }
}
