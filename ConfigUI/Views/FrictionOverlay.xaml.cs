using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ConfigUI.Helpers;
using ConfigUI.Services;

namespace ConfigUI.Views;

public partial class FrictionOverlay : Window
{
    private readonly string _appName;
    private readonly string _category;
    private readonly int _totalDelaySecs;
    private int _secondsRemaining;
    private readonly double _lambda;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _missionTimer;
    private readonly Random _rng = new();
    private readonly LowLevelKeyboardHook _keyboardHook = new();
    private bool _decided;
    private bool _countdownComplete;
    private int _missionIndex;

    private static readonly (string Type, Func<Random, string> Generator)[] Missions =
    [
        ("Breathe", _ => "Inhale for 4… hold 4… exhale 6. Repeat with the circle."),
        ("Math", rng => $"Solve mentally: {rng.Next(10, 99)} + {rng.Next(10, 99)} = ?"),
        ("Math", rng => $"Solve mentally: {rng.Next(5, 20)} × {rng.Next(3, 12)} = ?"),
        ("Reflect", _ => "Name one task you will finish before opening this app."),
        ("Breathe", _ => "Place both feet flat. Relax your jaw. Notice three sounds around you."),
    ];

    public FrictionOverlay(string appName, string category, int delaySecs)
    {
        InitializeComponent();
        _appName = appName;
        _category = category;
        _totalDelaySecs = Math.Clamp(delaySecs, 10, 30);
        _secondsRemaining = _totalDelaySecs;
        _lambda = Math.Log(2) / _totalDelaySecs;

        AppLabel.Text = appName;
        CountdownLabel.Text = _secondsRemaining.ToString();
        UpdateProbabilityLabel();
        ShowMission();

        OverlayWindowHelper.EnforceTopmost(this);
        _keyboardHook.Install();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        _missionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _missionTimer.Tick += (_, _) => ShowMission();
        _missionTimer.Start();

        _timer.Start();
    }

    private void ShowMission()
    {
        var mission = Missions[_missionIndex % Missions.Length];
        _missionIndex++;
        MissionLabel.Text = mission.Type;
        MissionTaskLabel.Text = mission.Generator(_rng);
    }

    private void UpdateProbabilityLabel()
    {
        int elapsed = _totalDelaySecs - _secondsRemaining;
        double probability = Math.Exp(-_lambda * elapsed) * 100.0;
        ProbabilityLabel.Text = $"Impulse probability P(E) = e^(-λt): {probability:F0}%";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        CountdownLabel.Text = Math.Max(0, _secondsRemaining).ToString();
        UpdateProbabilityLabel();

        if (_secondsRemaining > 0) return;

        _timer.Stop();
        _missionTimer.Stop();
        _countdownComplete = true;
        CountdownLabel.Text = "Ready";
        ProbabilityLabel.Text = "Pause before pause — one more moment…";
        BeginPauseBeforePause();
    }

    private async void BeginPauseBeforePause()
    {
        int pauseMs = _rng.Next(1000, 2001);
        PauseBeforePauseLabel.Visibility = Visibility.Visible;

        await Task.Delay(pauseMs);

        if (_decided) return;

        PauseBeforePauseLabel.Visibility = Visibility.Collapsed;
        UnlockButton.Visibility = Visibility.Visible;
        UnlockButton.IsEnabled = true;
        _keyboardHook.SetBlocking(false);
        ProbabilityLabel.Text = "You may unlock — choose deliberately.";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_decided) return;
        _decided = true;
        _timer.Stop();
        _missionTimer.Stop();

        _ = App.Pipe.SendAsync(PipeMessage.Create(MessageType.EnforceClose, new { app = _appName }));
        Close();
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_decided || !_countdownComplete) return;
        _decided = true;
        _timer.Stop();
        _missionTimer.Stop();

        _ = App.Pipe.SendAsync(PipeMessage.Create(MessageType.AllowSession,
            new { app = _appName, category = _category }));
        _ = App.Pipe.SendAsync(PipeMessage.Create(MessageType.ResumeProcess, new { app = _appName }));
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_countdownComplete || UnlockButton.Visibility != Visibility.Visible)
            e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_decided && !_countdownComplete)
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _keyboardHook.Dispose();
        base.OnClosed(e);
    }
}
