using System.Windows;
using ConfigUI.Services;
using ConfigUI.ViewModels;

namespace ConfigUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Wire up pipe messages to the ViewModel
        App.Pipe.MessageReceived += OnPipeMessage;
        App.Pipe.ConnectionChanged += OnConnectionChanged;

        var vm = (MainViewModel)DataContext;
        _ = vm.InitializeAsync();
    }

    private void OnPipeMessage(PipeMessage msg)
    {
        var vm = (MainViewModel)DataContext;
        switch (msg.Type)
        {
            case MessageType.ShowFriction:
                ShowFrictionOverlay(
                    msg.GetString("app") ?? "",
                    msg.GetString("category") ?? "",
                    msg.GetInt("delaySeconds", 20));
                break;

            case MessageType.SessionExpired:
                vm.OnSessionExpired(msg.GetString("app") ?? "");
                break;

            case MessageType.StateUpdate:
                vm.OnStateUpdate(
                    msg.GetString("mode") ?? "none",
                    msg.GetLong("nuclearEnd"),
                    msg.GetBool("isArmed"));
                break;
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        var vm = (MainViewModel)DataContext;
        vm.IsServiceConnected = connected;
    }

    private void ShowFrictionOverlay(string app, string category, int delaySecs)
    {
        var overlay = new FrictionOverlay(app, category, delaySecs);
        overlay.ShowDialog();
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Pipe.MessageReceived -= OnPipeMessage;
        App.Pipe.ConnectionChanged -= OnConnectionChanged;
        base.OnClosed(e);
    }
}
