using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ConfigUI.Services;

namespace ConfigUI.Views;

public partial class FrictionOverlay : Window
{
    private readonly string _appName;
    private readonly string _category;
    private int _secondsRemaining;
    private readonly DispatcherTimer _timer;
    private bool _decided;

    public FrictionOverlay(string appName, string category, int delaySecs)
    {
        InitializeComponent();
        _appName = appName;
        _category = category;
        _secondsRemaining = delaySecs;

        AppLabel.Text = appName;
        CountdownLabel.Text = _secondsRemaining.ToString();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        CountdownLabel.Text = _secondsRemaining.ToString();

        if (_secondsRemaining <= 0)
        {
            _timer.Stop();
            ContinueButton.IsEnabled = true;
            CountdownLabel.Text = "Ready";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_decided) return;
        _decided = true;
        _timer.Stop();

        // Tell service to close the blocked app
        _ = App.Pipe.SendAsync(PipeMessage.Create(MessageType.EnforceClose, new { app = _appName }));
        Close();
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_decided) return;
        _decided = true;
        _timer.Stop();

        // Tell service to allow the session and start the auto-close timer
        _ = App.Pipe.SendAsync(PipeMessage.Create(MessageType.AllowSession,
            new { app = _appName, category = _category }));

        // Resume the suspended process
        _ = App.Pipe.SendAsync(PipeMessage.Create(MessageType.ResumeProcess, new { app = _appName }));
        Close();
    }

    // Block Alt+F4 and other keyboard escapes during countdown
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_secondsRemaining > 0)
        {
            e.Handled = true; // Swallow all keys during the delay
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_decided && _secondsRemaining > 0)
        {
            e.Cancel = true; // Prevent window from closing during countdown
        }
        base.OnClosing(e);
    }
}
