using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UsageWidget.Interop;
using UsageWidget.Models;
using UsageWidget.Services;

namespace UsageWidget;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush BarWarning = Frozen("#FAB219");
    private static readonly SolidColorBrush BarCritical = Frozen("#D03B3B");
    private static readonly SolidColorBrush DotOk = Frozen("#0CA30C");
    private static readonly SolidColorBrush DotStale = Frozen("#FAB219");
    private static readonly SolidColorBrush DotError = Frozen("#D03B3B");

    private readonly ClaudeUsageProvider _provider = new();
    private readonly WidgetSettings _settings;
    private readonly ObservableCollection<LimitRowVm> _rows = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly Dictionary<string, double> _notifiedThresholds = new();
    private DateTimeOffset? _lastSuccess;
    private UsageSnapshot? _lastSnapshot;
    private string? _lastPlan;
    private bool _refreshing;
    private DateTimeOffset _lastIdeaAttempt = DateTimeOffset.MinValue;
    private bool _ideaBusy;

    public MainWindow()
    {
        _settings = SettingsService.Load();
        ThemeService.Apply(_settings.Theme == "light");
        InitializeComponent();

        LimitsList.ItemsSource = _rows;
        LockMenuItem.IsChecked = _settings.Locked;
        NotifyMenuItem.IsChecked = _settings.NotificationsEnabled;
        ThemeMenuItem.IsChecked = _settings.Theme == "light";
        StartupMenuItem.IsChecked = StartupService.IsEnabled();
        PositionWindow();

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tickTimer.Tick += (_, _) => UpdateCountdowns();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.PollMinutes)) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            _tickTimer.Start();
            _pollTimer.Start();
            await RefreshAsync();
        };

        // Show Desktop (Win+D) minimizes every top-level window; quietly restore.
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) => SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DesktopPinning.Pin(this);
    }

    private void PositionWindow()
    {
        var screen = SystemParameters.VirtualScreenWidth > 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                       SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : SystemParameters.WorkArea;

        if (_settings.X is double x && _settings.Y is double y
            && x >= screen.Left - 40 && x <= screen.Right - 40
            && y >= screen.Top - 10 && y <= screen.Bottom - 40)
        {
            Left = x;
            Top = y;
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Top + 24;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8)); // let the network come back up
            await RefreshAsync();
        });
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var result = await _provider.FetchAsync();
            switch (result.Status)
            {
                case FetchStatus.Ok when result.Snapshot is not null:
                    _lastSuccess = result.Snapshot.FetchedAt;
                    _lastSnapshot = result.Snapshot;
                    _lastPlan = result.Plan;
                    ApplySnapshot(result.Snapshot, result.Plan);
                    SetStatus(DotOk, $"updated {_lastSuccess:HH:mm}");
                    CheckNotifications(result.Snapshot.Limits);
                    break;

                case FetchStatus.NoCredentials:
                    if (_rows.Count == 0)
                        ShowEmpty("Claude Code sign in not found on this PC. Run Claude Code and sign in, then use Refresh now in the right click menu.");
                    SetStatus(DotError, "no sign in");
                    break;

                case FetchStatus.Unauthorized:
                    SetStatus(DotStale, _lastSuccess is null
                        ? "sign in expired"
                        : $"stale · updated {_lastSuccess:HH:mm}");
                    if (_rows.Count == 0)
                        ShowEmpty("Claude sign in expired and could not be refreshed automatically. Open Claude Code once to fix it.");
                    break;

                default: // NetworkError
                    SetStatus(DotStale, _lastSuccess is null ? "offline" : $"offline · updated {_lastSuccess:HH:mm}");
                    if (_rows.Count == 0)
                        ShowEmpty("Can't reach Anthropic right now — will keep retrying.");
                    break;
            }
        }
        finally
        {
            _refreshing = false;
        }
        await UpdateIdeaAsync();
    }

    /// <summary>
    /// Shows the daily project idea. The Gemini API is called at most once per day;
    /// the result is cached in settings. Errors back off for an hour and surface only
    /// as a small "API error" note in the footer.
    /// </summary>
    private async Task UpdateIdeaAsync(bool force = false)
    {
        string? key = _settings.GeminiApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            IdeaSection.Visibility = Visibility.Collapsed;
            ApiErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!force && _settings.LastIdeaDate == today && !string.IsNullOrEmpty(_settings.LastIdeaText))
        {
            IdeaText.Text = _settings.LastIdeaText;
            IdeaSection.Visibility = Visibility.Visible;
            ApiErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        if (_ideaBusy) return;
        if (!force && DateTimeOffset.Now - _lastIdeaAttempt < TimeSpan.FromHours(1)) return;
        _ideaBusy = true;
        _lastIdeaAttempt = DateTimeOffset.Now;
        try
        {
            var result = await GeminiIdeaProvider.GenerateAsync(key, _settings.GeminiModel);
            if (result.Ok && result.Idea is not null)
            {
                if (result.ModelUsed is not null) _settings.GeminiModel = result.ModelUsed;
                _settings.LastIdeaDate = today;
                _settings.LastIdeaText = result.Idea;
                SettingsService.Save(_settings);
                IdeaText.Text = result.Idea;
                IdeaSection.Visibility = Visibility.Visible;
                ApiErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                IdeaSection.Visibility = Visibility.Collapsed;
                ApiErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _ideaBusy = false;
        }
    }

    private void ApplySnapshot(UsageSnapshot snapshot, string? plan)
    {
        EmptyText.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(plan))
        {
            PlanText.Text = plan.ToUpperInvariant();
            PlanBadge.Visibility = Visibility.Visible;
        }

        _rows.Clear();
        foreach (var limit in snapshot.Limits)
            _rows.Add(new LimitRowVm(limit));
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
    }

    private void SetStatus(Brush dot, string text)
    {
        StatusDot.Fill = dot;
        StatusText.Text = text;
    }

    private void UpdateCountdowns()
    {
        foreach (var row in _rows)
            row.RefreshResetText();
        if (_lastSuccess is not null && StatusText.Text.StartsWith("updated"))
            StatusText.Text = $"updated {_lastSuccess:HH:mm}";
    }

    private void CheckNotifications(IReadOnlyList<LimitInfo> limits)
    {
        if (!_settings.NotificationsEnabled) return;
        foreach (var limit in limits)
        {
            double crossed = limit.Percent >= 95 ? 95 : limit.Percent >= 80 ? 80 : 0;
            if (crossed == 0) continue;

            // Key includes the reset time so a new period notifies fresh.
            string key = $"{limit.Label}|{limit.ResetsAt?.ToUnixTimeSeconds()}";
            if (_notifiedThresholds.TryGetValue(key, out double prev) && prev >= crossed) continue;
            _notifiedThresholds[key] = crossed;

            NotificationWindow.ShowToast(
                "Claude usage",
                $"{limit.Label} at {limit.Percent:0}% · {Formatting.FormatReset(limit.ResetsAt)}",
                critical: crossed >= 95);
        }
    }

    // --- interactions -------------------------------------------------------

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Locked || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
        _settings.X = Left;
        _settings.Y = Top;
        SettingsService.Save(_settings);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _settings.Locked = LockMenuItem.IsChecked;
        SettingsService.Save(_settings);
    }

    private void Notify_Click(object sender, RoutedEventArgs e)
    {
        _settings.NotificationsEnabled = NotifyMenuItem.IsChecked;
        SettingsService.Save(_settings);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeMenuItem.IsChecked ? "light" : "dark";
        SettingsService.Save(_settings);
        ThemeService.Apply(_settings.Theme == "light");
        // Meter fills hold a resolved accent brush, so rebuild the rows.
        if (_lastSnapshot is not null)
            ApplySnapshot(_lastSnapshot, _lastPlan);
    }

    private void Startup_Click(object sender, RoutedEventArgs e) =>
        StartupService.SetEnabled(StartupMenuItem.IsChecked);

    private async void GeminiKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings.GeminiApiKey);
        if (dialog.ShowDialog() == true)
        {
            _settings.GeminiApiKey = dialog.ApiKey;
            SettingsService.Save(_settings);
            await UpdateIdeaAsync(force: true);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public sealed class LimitRowVm : INotifyPropertyChanged
    {
        private readonly DateTimeOffset? _resetsAt;
        private string _resetText;

        public LimitRowVm(LimitInfo limit)
        {
            Label = limit.Label;
            Percent = Math.Clamp(limit.Percent, 0, 100);
            PercentText = $"{limit.Percent:0}%";
            BarBrush = limit.Percent >= 95 ? BarCritical
                : limit.Percent >= 80 ? BarWarning
                : (Brush)Application.Current.Resources["Accent"];
            _resetsAt = limit.ResetsAt;
            _resetText = BuildResetText();
        }

        public string Label { get; }
        public double Percent { get; }
        public string PercentText { get; }
        public Brush BarBrush { get; }
        public string ResetText => _resetText;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void RefreshResetText()
        {
            string updated = BuildResetText();
            if (updated == _resetText) return;
            _resetText = updated;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResetText)));
        }

        private string BuildResetText()
        {
            string reset = Formatting.FormatReset(_resetsAt);
            string left = $"{100 - Percent:0}% left";
            return reset.Length > 0 ? $"{left} · {reset}" : left;
        }
    }
}
