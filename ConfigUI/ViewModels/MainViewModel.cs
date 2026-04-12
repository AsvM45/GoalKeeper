using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConfigUI.Services;
using MaterialDesignThemes.Wpf;

namespace ConfigUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ── Connection state ──────────────────────────────────────────────────────

    [ObservableProperty] private bool _isServiceConnected;
    [ObservableProperty] private string _connectionLabel = "Disconnected";
    [ObservableProperty] private Brush _connectionColor = Brushes.Gray;

    partial void OnIsServiceConnectedChanged(bool value)
    {
        ConnectionColor = value ? Brushes.LimeGreen : Brushes.Tomato;
        ConnectionLabel = value ? "Service connected" : "Service not running";
    }

    // ── Armed state ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private string _armedLabel = "Audit Mode";
    [ObservableProperty] private string _armedIcon = "ShieldOutline";
    [ObservableProperty] private Brush _armedBadgeColor = Brushes.Gray;

    partial void OnIsArmedChanged(bool value)
    {
        ArmedLabel = value ? "Armed" : "Audit Mode";
        ArmedIcon = value ? "ShieldLock" : "ShieldOutline";
        ArmedBadgeColor = value
            ? new SolidColorBrush(Color.FromRgb(0xCC, 0x22, 0x22))
            : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
    }

    // ── Nuclear mode ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _nuclearCountdown = "";
    [ObservableProperty] private Visibility _nuclearBadgeVisibility = Visibility.Collapsed;

    private long _nuclearEndEpoch;
    private readonly DispatcherTimer _nuclearTimer;

    // ── Navigation ────────────────────────────────────────────────────────────

    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new NavItem("Dashboard",   "ChartBar"),
        new NavItem("Categories",  "Tag"),
        new NavItem("Budgets",     "Timer"),
        new NavItem("Schedule",    "Clock"),
        new NavItem("Nuclear",     "Radioactive"),
        new NavItem("AI Smart",    "Brain"),
        new NavItem("Security",    "ShieldLock"),
        new NavItem("Settings",    "Cog"),
    ];

    [ObservableProperty] private NavItem? _selectedNavItem;
    [ObservableProperty] private object? _currentPage;

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value == null) return;
        CurrentPage = value.Label switch
        {
            "Dashboard"  => new DashboardPageViewModel(this),
            "Nuclear"    => new NuclearPageViewModel(this),
            "Security"   => new SecurityPageViewModel(this),
            _            => new PlaceholderPageViewModel(value.Label)
        };
    }

    // ── Session notifications ─────────────────────────────────────────────────

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private Visibility _statusVisibility = Visibility.Collapsed;

    // ── Initialization ────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _nuclearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nuclearTimer.Tick += UpdateNuclearCountdown;

        SelectedNavItem = NavItems[0];
    }

    public async Task InitializeAsync()
    {
        var response = await App.Pipe.SendAsync(PipeMessage.Create(MessageType.GetState, new { }));
        if (response != null)
        {
            OnStateUpdate(
                response.GetString("mode") ?? "none",
                response.GetLong("nuclearEnd"),
                response.GetBool("isArmed"));
        }
    }

    public void OnStateUpdate(string mode, long nuclearEnd, bool isArmed)
    {
        IsArmed = isArmed;
        _nuclearEndEpoch = nuclearEnd;

        if (nuclearEnd > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < nuclearEnd)
        {
            NuclearBadgeVisibility = Visibility.Visible;
            _nuclearTimer.Start();
        }
        else
        {
            NuclearBadgeVisibility = Visibility.Collapsed;
            _nuclearTimer.Stop();
        }
    }

    public void OnSessionExpired(string appName)
    {
        StatusMessage = $"⏱ Session ended: {appName} has been closed.";
        StatusVisibility = Visibility.Visible;

        var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        hideTimer.Tick += (_, _) =>
        {
            StatusVisibility = Visibility.Collapsed;
            hideTimer.Stop();
        };
        hideTimer.Start();
    }

    private void UpdateNuclearCountdown(object? sender, EventArgs e)
    {
        var remaining = _nuclearEndEpoch - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= 0)
        {
            _nuclearTimer.Stop();
            NuclearBadgeVisibility = Visibility.Collapsed;
            return;
        }

        var ts = TimeSpan.FromSeconds(remaining);
        NuclearCountdown = ts.TotalHours >= 1
            ? $"NUCLEAR {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"NUCLEAR {ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}

// ── Nav item ──────────────────────────────────────────────────────────────────

public record NavItem(string Label, string Icon);

// ── Stub page view models ─────────────────────────────────────────────────────

public class PlaceholderPageViewModel
{
    public string Label { get; }
    public PlaceholderPageViewModel(string label) => Label = label;
}

public partial class DashboardPageViewModel : ObservableObject
{
    private readonly MainViewModel _parent;

    [ObservableProperty] private string _todayProductiveHours = "0h 0m";
    [ObservableProperty] private string _todayDistractingHours = "0h 0m";
    [ObservableProperty] private int _todayPickups;
    [ObservableProperty] private int _productivityScore;

    public DashboardPageViewModel(MainViewModel parent)
    {
        _parent = parent;
        _ = LoadStatsAsync();
    }

    private async Task LoadStatsAsync()
    {
        // Stats are read directly from SQLite (read-only from UI)
        await Task.CompletedTask; // Placeholder – full implementation reads from DB
    }
}

public partial class NuclearPageViewModel : ObservableObject
{
    private readonly MainViewModel _parent;

    [ObservableProperty] private string _selectedMode = "nuclear_strict";
    [ObservableProperty] private int _durationHours = 2;

    public ObservableCollection<string> Modes { get; } =
    [
        "nuclear_strict",
        "nuclear_offline",
        "nuclear_whitelist"
    ];

    public NuclearPageViewModel(MainViewModel parent) => _parent = parent;

    [RelayCommand]
    private async Task ActivateNuclearAsync()
    {
        var challenge = new Views.TypingChallenge();
        if (challenge.ShowDialog() != true || challenge.CompletionToken == null) return;

        var response = await App.Pipe.SendAsync(PipeMessage.Create(
            MessageType.ActivateNuclear, new
            {
                mode = SelectedMode,
                durationMinutes = DurationHours * 60,
                challengeToken = challenge.CompletionToken
            }));

        if (response != null)
        {
            _parent.OnStateUpdate(
                response.GetString("mode") ?? "none",
                response.GetLong("nuclearEnd"),
                response.GetBool("isArmed"));
        }
    }
}

public partial class SecurityPageViewModel : ObservableObject
{
    private readonly MainViewModel _parent;
    public SecurityPageViewModel(MainViewModel parent) => _parent = parent;

    [RelayCommand]
    private void OpenSetupWizard()
    {
        var wizard = new Views.SetupWizard();
        wizard.ShowDialog();
    }
}
