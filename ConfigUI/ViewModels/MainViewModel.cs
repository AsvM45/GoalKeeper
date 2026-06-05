using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConfigUI.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MaterialDesignThemes.Wpf;
using SkiaSharp;

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
            "Categories" => new CategoriesPageViewModel(),
            "Budgets"    => new BudgetsPageViewModel(),
            "Schedule"   => new SchedulePageViewModel(),
            "AI Smart"   => new AISmartPageViewModel(),
            "Settings"   => new SettingsPageViewModel(),
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
    // ── Summary cards ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _todayProductiveHours = "–";
    [ObservableProperty] private string _todayDistractingHours = "–";
    [ObservableProperty] private int _todayPickups;
    [ObservableProperty] private int _productivityScore;
    [ObservableProperty] private int _activeHaloZones;

    // ── Pillar 1: App & Website Activity ──────────────────────────────────────
    [ObservableProperty] private ISeries[] _weeklySeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _weeklyXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _weeklyYAxes = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _hourlyActivitySeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _hourlyActivityXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _hourlyActivityYAxes = Array.Empty<Axis>();

    // ── Pillar 2: Notifications (context-switch frequency) ────────────────────
    [ObservableProperty] private ISeries[] _notificationSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _notificationXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _notificationYAxes = Array.Empty<Axis>();

    // ── Pillar 3: Pickups (first-look triggers) ───────────────────────────────
    [ObservableProperty] private ISeries[] _pickupSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _pickupXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _pickupYAxes = Array.Empty<Axis>();

    // ── Productivity ring chart ───────────────────────────────────────────────
    [ObservableProperty] private ISeries[] _ringSeries = Array.Empty<ISeries>();

    // ── Top apps table ────────────────────────────────────────────────────────
    public ObservableCollection<TopAppRow> TopApps { get; } = [];

    private readonly DispatcherTimer _refreshTimer;

    public DashboardPageViewModel(MainViewModel parent)
    {
        _ = LoadStatsAsync();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += (_, _) => _ = LoadStatsAsync();
        _refreshTimer.Start();
    }

    private async Task LoadStatsAsync()
    {
        try
        {
            var db = new AppDatabase();

            var (prod, dist) = await db.GetTodayTimeAsync();
            var pickups = await db.GetTodayPickupsAsync();

            TodayProductiveHours  = FormatTime(prod);
            TodayDistractingHours = FormatTime(dist);
            TodayPickups          = pickups;
            ProductivityScore     = (prod + dist) == 0 ? 0 : (int)(prod * 100.0 / (prod + dist));

            var haloJson = await db.GetStateAsync("HaloZones") ?? "[]";
            var haloZones = System.Text.Json.JsonSerializer.Deserialize<List<HaloZoneItem>>(haloJson) ?? [];
            ActiveHaloZones = haloZones.Count(z => z.Enabled);

            BuildWeeklyChart(await db.GetWeeklyActivityAsync());
            BuildHourlyActivityChart(await db.GetTodayHourlyActivityAsync());
            BuildNotificationChart(await db.GetTodayPickupFrequencyAsync());
            BuildPickupChart(await db.GetFirstLookTriggersAsync());
            BuildRingChart(prod, dist);

            var topApps = await db.GetTopAppsTodayAsync(10);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                TopApps.Clear();
                foreach (var (appName, secs) in topApps)
                    TopApps.Add(new TopAppRow(appName, FormatTime(secs)));
            });
        }
        catch { /* DB not yet initialized or service not running */ }
    }

    private void BuildWeeklyChart(List<(string Date, int ProductiveSecs, int DistractingSecs, int NeutralSecs)> weekly)
    {
        var last7 = Enumerable.Range(0, 7)
            .Select(i => DateTime.Today.AddDays(-6 + i))
            .ToList();

        var prodValues  = new double[7];
        var distValues  = new double[7];
        var neutValues  = new double[7];
        var labels      = new string[7];

        for (int i = 0; i < 7; i++)
        {
            labels[i] = last7[i].ToString("ddd");
            var dt = last7[i].ToString("yyyy-MM-dd");
            var match = weekly.FirstOrDefault(d => d.Date == dt);
            if (match != default)
            {
                prodValues[i] = Math.Round(match.ProductiveSecs  / 3600.0, 2);
                distValues[i] = Math.Round(match.DistractingSecs / 3600.0, 2);
                neutValues[i] = Math.Round(match.NeutralSecs     / 3600.0, 2);
            }
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            WeeklySeries = new ISeries[]
            {
                MakeStackedColumn("Productive",  prodValues, 0x4C, 0xAF, 0x50),
                MakeStackedColumn("Distracting", distValues, 0xF4, 0x43, 0x36),
                MakeStackedColumn("Neutral",     neutValues, 0x9E, 0x9E, 0x9E),
            };
            WeeklyXAxes = MakeCategoryXAxes(labels);
            WeeklyYAxes = MakeHourYAxes();
        });
    }

    private void BuildHourlyActivityChart(List<(int Hour, int ProductiveSecs, int DistractingSecs, int NeutralSecs)> hourly)
    {
        var prod = new double[24];
        var dist = new double[24];
        var neut = new double[24];
        var labels = new string[24];

        for (int h = 0; h < 24; h++)
        {
            labels[h] = h % 3 == 0 ? $"{h:D2}" : "";
            var match = hourly.FirstOrDefault(x => x.Hour == h);
            if (match != default)
            {
                prod[h] = Math.Round(match.ProductiveSecs  / 60.0, 1);
                dist[h] = Math.Round(match.DistractingSecs / 60.0, 1);
                neut[h] = Math.Round(match.NeutralSecs     / 60.0, 1);
            }
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            HourlyActivitySeries = new ISeries[]
            {
                MakeStackedColumn("Productive",  prod, 0x4C, 0xAF, 0x50),
                MakeStackedColumn("Distracting", dist, 0xF4, 0x43, 0x36),
                MakeStackedColumn("Neutral",     neut, 0x9E, 0x9E, 0x9E),
            };
            HourlyActivityXAxes = MakeCategoryXAxes(labels);
            HourlyActivityYAxes = new[]
            {
                new Axis
                {
                    LabelsPaint = new SolidColorPaint(new SKColor(0xCC, 0xCC, 0xCC)),
                    TextSize = 10,
                    SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x33)),
                    Labeler = v => $"{v:0.#}m",
                }
            };
        });
    }

    private void BuildNotificationChart(List<(int Hour, int Count)> frequency)
    {
        var values = new double[24];
        var labels = new string[24];
        for (int h = 0; h < 24; h++)
        {
            labels[h] = h % 3 == 0 ? $"{h:D2}" : "";
            values[h] = frequency.FirstOrDefault(x => x.Hour == h).Count;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            NotificationSeries = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Name = "Context switches",
                    Values = values,
                    Fill = new SolidColorPaint(new SKColor(0xFF, 0x98, 0x00)),
                    Stroke = null,
                    MaxBarWidth = 18,
                }
            };
            NotificationXAxes = MakeCategoryXAxes(labels);
            NotificationYAxes = new[]
            {
                new Axis
                {
                    LabelsPaint = new SolidColorPaint(new SKColor(0xCC, 0xCC, 0xCC)),
                    TextSize = 10,
                    SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x33)),
                    MinLimit = 0,
                }
            };
        });
    }

    private void BuildPickupChart(List<(string ToApp, int Count)> triggers)
    {
        var labels = triggers.Select(t => TruncateApp(t.ToApp, 14)).ToArray();
        var values = triggers.Select(t => (double)t.Count).ToArray();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            PickupSeries = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Name = "First-look triggers",
                    Values = values,
                    Fill = new SolidColorPaint(new SKColor(0x7E, 0x57, 0xC2)),
                    Stroke = null,
                    MaxBarWidth = 36,
                }
            };
            PickupXAxes = MakeCategoryXAxes(labels);
            PickupYAxes = new[]
            {
                new Axis
                {
                    LabelsPaint = new SolidColorPaint(new SKColor(0xCC, 0xCC, 0xCC)),
                    TextSize = 10,
                    SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x33)),
                    MinLimit = 0,
                }
            };
        });
    }

    private static StackedColumnSeries<double> MakeStackedColumn(string name, double[] values, byte r, byte g, byte b) =>
        new()
        {
            Name = name,
            Values = values,
            Fill = new SolidColorPaint(new SKColor(r, g, b)),
            Stroke = null,
            MaxBarWidth = 40,
        };

    private static Axis[] MakeCategoryXAxes(string[] labels) => new[]
    {
        new Axis
        {
            Labels = labels,
            LabelsPaint = new SolidColorPaint(new SKColor(0xCC, 0xCC, 0xCC)),
            TextSize = 10,
            SeparatorsPaint = null,
        }
    };

    private static Axis[] MakeHourYAxes() => new[]
    {
        new Axis
        {
            LabelsPaint = new SolidColorPaint(new SKColor(0xCC, 0xCC, 0xCC)),
            TextSize = 11,
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x33)),
            Labeler = v => $"{v:0.#}h",
        }
    };

    private static string TruncateApp(string app, int max) =>
        app.Length <= max ? app : app[..(max - 1)] + "…";

    private void BuildRingChart(int prodSecs, int distSecs)
    {
        double total = prodSecs + distSecs;
        double prodPct  = total == 0 ? 50 : Math.Max(1, prodSecs  / total * 100);
        double distPct  = total == 0 ? 50 : Math.Max(1, distSecs  / total * 100);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            RingSeries = new ISeries[]
            {
                new PieSeries<double>
                {
                    Name        = "Productive",
                    Values      = new[] { prodPct },
                    Fill        = new SolidColorPaint(new SKColor(0x4C, 0xAF, 0x50)),
                    InnerRadius = 60,
                    MaxRadialColumnWidth = 28,
                },
                new PieSeries<double>
                {
                    Name        = "Distracting",
                    Values      = new[] { distPct },
                    Fill        = new SolidColorPaint(new SKColor(0xF4, 0x43, 0x36)),
                    InnerRadius = 60,
                    MaxRadialColumnWidth = 28,
                },
            };
        });
    }

    private static string FormatTime(int seconds)
    {
        int h = seconds / 3600, m = (seconds % 3600) / 60;
        return h > 0 ? $"{h}h {m}m" : m > 0 ? $"{m}m" : "0m";
    }
}

public record TopAppRow(string AppName, string TimeDisplay);

public class HaloZoneItem
{
    public string Name { get; set; } = "";
    public string BluetoothAddress { get; set; } = "";
    public int RadiusMeters { get; set; } = 5;
    public string BlockCategory { get; set; } = "distracting";
    public bool Enabled { get; set; } = true;
}

public record NuclearModeOption(string Value, string DisplayName, string Description, string Icon);

public partial class NuclearPageViewModel : ObservableObject
{
    private readonly MainViewModel _parent;

    public ObservableCollection<NuclearModeOption> Modes { get; } =
    [
        new("nuclear_strict",    "Strict Blocklist",
            "Immediately revokes all remaining daily allowances and enforces your full blocklist. Apps you haven't used yet still count.",
            "BlockHelper"),
        new("nuclear_offline",   "Full Offline",
            "Blocks all browsers and internet-connected apps. Only offline tools remain accessible. Ideal for deep work sessions.",
            "WifiOff"),
        new("nuclear_whitelist", "Whitelist Only",
            "Blocks everything except your explicitly approved productivity tools. Nothing outside your whitelist can launch.",
            "FormatListChecks"),
    ];

    [ObservableProperty] private NuclearModeOption? _selectedMode;
    [ObservableProperty] private int _durationHours = 2;

    public NuclearPageViewModel(MainViewModel parent)
    {
        _parent = parent;
        _selectedMode = Modes[0];
    }

    [RelayCommand]
    private async Task ActivateNuclearAsync()
    {
        if (SelectedMode is null) return;

        var challenge = new Views.TypingChallenge();
        if (challenge.ShowDialog() != true || challenge.CompletionToken == null) return;

        var response = await App.Pipe.SendAsync(PipeMessage.Create(
            MessageType.ActivateNuclear, new
            {
                mode = SelectedMode.Value,
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

public partial class CategoriesPageViewModel : ObservableObject
{
    private readonly AppDatabase _db = new();

    public ObservableCollection<CategoryRuleItem> Rules { get; } = [];

    [ObservableProperty] private int _editingRuleId = 0;
    [ObservableProperty] private string _newPattern  = "";
    [ObservableProperty] private string _newCategory = "distracting";
    [ObservableProperty] private string _newRuleType = "app";
    [ObservableProperty] private string _status = "";

    public string[] CategoryOptions { get; } =
        ["distracting", "productive", "neutral", "whitelist", "blacklist"];
    public string[] RuleTypeOptions { get; } =
        ["app", "domain", "window_title"];

    public CategoriesPageViewModel() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var rows = await _db.GetCategoryRulesAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                Rules.Clear();
                foreach (var r in rows)
                    Rules.Add(new CategoryRuleItem(r.Id, r.Pattern, r.Category, r.RuleType));
            });
        }
        catch (Exception ex) { Status = $"Could not load: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPattern)) { Status = "Pattern cannot be empty."; return; }
        if (!App.Pipe.IsConnected) { Status = "Not connected to service — start ServiceEngine first."; return; }
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.UpsertCategoryRule, new
        {
            id       = EditingRuleId,
            pattern  = NewPattern.Trim(),
            category = NewCategory,
            ruleType = NewRuleType
        }));
        NewPattern = "";
        EditingRuleId = 0;
        Status = "Rule saved.";
        await Task.Delay(200); // brief wait for service to commit before re-reading
        await LoadAsync();
    }

    [RelayCommand]
    private void EditRule(CategoryRuleItem item)
    {
        EditingRuleId = item.Id;
        NewPattern    = item.Pattern;
        NewCategory   = item.Category;
        NewRuleType   = item.RuleType;
        Status        = "Editing rule...";
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(CategoryRuleItem item)
    {
        if (!App.Pipe.IsConnected) { Status = "Not connected to service — start ServiceEngine first."; return; }
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.DeleteCategoryRule, new { id = item.Id }));
        Application.Current.Dispatcher.Invoke(() => Rules.Remove(item));
        Status = "Rule deleted.";
    }
}

public record CategoryRuleItem(int Id, string Pattern, string Category, string RuleType);

public partial class BudgetsPageViewModel : ObservableObject
{
    private readonly AppDatabase _db = new();

    public ObservableCollection<BudgetItem> Budgets { get; } = [];

    [ObservableProperty] private string _newCategory       = "";
    [ObservableProperty] private int    _newAllowedMinutes = 60;
    [ObservableProperty] private int    _newMaxLaunches    = -1;
    [ObservableProperty] private int    _newSessionMinutes = 5;
    [ObservableProperty] private int    _newFrictionSecs   = 20;
    [ObservableProperty] private string _status            = "";

    public BudgetsPageViewModel() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var rows = await _db.GetBudgetsAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                Budgets.Clear();
                foreach (var b in rows)
                    Budgets.Add(new BudgetItem
                    {
                        Category       = b.Category,
                        AllowedMinutes = b.AllowedSeconds / 60,
                        UsedMinutes    = b.UsedSeconds    / 60,
                        MaxLaunches    = b.MaxLaunches,
                        UsedLaunches   = b.UsedLaunches,
                        SessionMinutes = b.SessionMinutes,
                        FrictionSecs   = b.FrictionSeconds,
                    });
            });
        }
        catch (Exception ex) { Status = $"Could not load: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task AddBudgetAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCategory)) { Status = "Category name cannot be empty."; return; }
        if (!App.Pipe.IsConnected) { Status = "Not connected to service — start ServiceEngine first."; return; }
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.UpsertBudget, new
        {
            category        = NewCategory.Trim(),
            allowedSeconds  = NewAllowedMinutes * 60,
            maxLaunches     = NewMaxLaunches,
            sessionMinutes  = NewSessionMinutes,
            frictionSeconds = NewFrictionSecs
        }));
        NewCategory = "";
        Status = "Budget saved.";
        await Task.Delay(200);
        await LoadAsync();
    }

    [RelayCommand]
    private void EditBudget(BudgetItem item)
    {
        NewCategory       = item.Category;
        NewAllowedMinutes = item.AllowedMinutes;
        NewMaxLaunches    = item.MaxLaunches;
        NewSessionMinutes = item.SessionMinutes;
        NewFrictionSecs   = item.FrictionSecs;
        Status            = "Editing budget...";
    }

    [RelayCommand]
    private async Task DeleteBudgetAsync(BudgetItem item)
    {
        if (!App.Pipe.IsConnected) { Status = "Not connected to service — start ServiceEngine first."; return; }
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.DeleteBudget, new { category = item.Category }));
        Application.Current.Dispatcher.Invoke(() => Budgets.Remove(item));
        Status = "Budget deleted.";
    }
}

public class BudgetItem
{
    public string Category       { get; set; } = "";
    public int    AllowedMinutes { get; set; }
    public int    UsedMinutes    { get; set; }
    public int    MaxLaunches    { get; set; }
    public int    UsedLaunches   { get; set; }
    public int    SessionMinutes { get; set; }
    public int    FrictionSecs   { get; set; }
}

public partial class SchedulePageViewModel : ObservableObject
{
    private readonly AppDatabase _db = new();

    [ObservableProperty] private bool   _downtimeEnabled = false;
    [ObservableProperty] private string _downtimeStart   = "22:00";
    [ObservableProperty] private string _downtimeEnd     = "07:00";
    [ObservableProperty] private string _status          = "";

    public SchedulePageViewModel() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            DowntimeEnabled = (await _db.GetStateAsync("DowntimeEnabled")) == "1";
            DowntimeStart   = (await _db.GetStateAsync("DowntimeStart"))   ?? "22:00";
            DowntimeEnd     = (await _db.GetStateAsync("DowntimeEnd"))     ?? "07:00";
        }
        catch { /* DB not ready */ }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!App.Pipe.IsConnected) { Status = "Not connected to service — start ServiceEngine first."; return; }
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "DowntimeEnabled", value = DowntimeEnabled ? "1" : "0" }));
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "DowntimeStart", value = DowntimeStart }));
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "DowntimeEnd", value = DowntimeEnd }));
        Status = "Saved. Changes take effect immediately.";
    }
}

public partial class AISmartPageViewModel : ObservableObject
{
    private readonly AppDatabase _db = new();

    [ObservableProperty] private string _apiKey     = "";
    [ObservableProperty] private bool   _aiEnabled  = false;
    [ObservableProperty] private string _userGoals  = "";
    [ObservableProperty] private string _status     = "";
    [ObservableProperty] private bool   _isSaving   = false;

    public AISmartPageViewModel() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            ApiKey    = (await _db.GetStateAsync("AIApiKey"))  ?? "";
            AiEnabled = (await _db.GetStateAsync("AIEnabled")) == "1";
            var json  = (await _db.GetStateAsync("UserGoals")) ?? "[]";
            var goals = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            UserGoals = string.Join("\n", goals);
        }
        catch { /* DB not ready */ }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!App.Pipe.IsConnected) { Status = "Not connected to service — start ServiceEngine first."; return; }
        IsSaving = true;
        Status   = "";
        var goals = UserGoals
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "AIApiKey",  value = ApiKey.Trim() }));
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "AIEnabled", value = AiEnabled ? "1" : "0" }));
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "UserGoals", value = System.Text.Json.JsonSerializer.Serialize(goals) }));
        Status   = "Saved successfully.";
        IsSaving = false;
    }
}

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly AppDatabase _db = new();

    [ObservableProperty] private bool   _frictionEnabled     = true;
    [ObservableProperty] private int    _frictionDelaySeconds = 20;
    [ObservableProperty] private bool   _aiEnabled            = false;
    [ObservableProperty] private string _status               = "";

    public ObservableCollection<HaloZoneItem> HaloZones { get; } = [];
    [ObservableProperty] private string _newHaloName = "";
    [ObservableProperty] private string _newHaloAddress = "";
    [ObservableProperty] private int _newHaloRadius = 5;

    public SettingsPageViewModel() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            FrictionEnabled      = (await _db.GetStateAsync("FrictionEnabled")) != "0";
            AiEnabled            = (await _db.GetStateAsync("AIEnabled"))       == "1";
            var fStr             = await _db.GetStateAsync("FrictionSeconds");
            if (int.TryParse(fStr, out int fs)) FrictionDelaySeconds = fs;

            var haloJson = await _db.GetStateAsync("HaloZones") ?? "[]";
            var zones = System.Text.Json.JsonSerializer.Deserialize<List<HaloZoneItem>>(haloJson) ?? [];
            Application.Current?.Dispatcher.Invoke(() =>
            {
                HaloZones.Clear();
                foreach (var z in zones) HaloZones.Add(z);
            });
        }
        catch { /* DB not ready */ }
    }

    [RelayCommand]
    private void AddHaloZone()
    {
        if (string.IsNullOrWhiteSpace(NewHaloName) || string.IsNullOrWhiteSpace(NewHaloAddress))
        {
            Status = "Halo zone requires a name and Bluetooth address.";
            return;
        }
        HaloZones.Add(new HaloZoneItem
        {
            Name = NewHaloName.Trim(),
            BluetoothAddress = NewHaloAddress.Trim().ToUpperInvariant(),
            RadiusMeters = NewHaloRadius,
            BlockCategory = "distracting",
            Enabled = true
        });
        NewHaloName = "";
        NewHaloAddress = "";
        Status = "Zone added — save to apply.";
    }

    [RelayCommand]
    private void RemoveHaloZone(HaloZoneItem zone) => HaloZones.Remove(zone);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!App.Pipe.IsConnected) { Status = "Not connected to service — start ServiceEngine first."; return; }
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "FrictionEnabled", value = FrictionEnabled ? "1" : "0" }));
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "FrictionSeconds", value = FrictionDelaySeconds.ToString() }));
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "AIEnabled", value = AiEnabled ? "1" : "0" }));

        var haloJson = System.Text.Json.JsonSerializer.Serialize(HaloZones.ToList());
        await App.Pipe.FireAndForgetAsync(PipeMessage.Create(MessageType.SetState,
            new { key = "HaloZones", value = haloJson }));

        Status = "Saved. Changes take effect on next app launch.";
    }
}
