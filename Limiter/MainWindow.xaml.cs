using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Limiter;

public partial class MainWindow : Window
{
    private readonly TrafficEngine _engine = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<AppRow> _rows = [];
    private readonly Dictionary<string, AppRow> _rowByPath = new(StringComparer.OrdinalIgnoreCase);
    private string _sortProperty = nameof(AppRow.DownloadRate);
    private ListSortDirection _sortDirection = ListSortDirection.Descending;
    private string _search = "";
    private bool _rulesOnly;
    private readonly SettingsStore _settingsStore;
    private UserSettings _settings = new();
    private bool _startupEnabled;

    public MainWindow() : this(new SettingsStore()) { }

    internal MainWindow(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        string? settingsError = null;
        try { _settings = _settingsStore.Load(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.Text.Json.JsonException)
        { settingsError = ex.Message; }
        Localization.Current.SetLanguage(_settings.Language);
        InitializeComponent();
        LanguageChoice.SelectedValue = _settings.Language;
        if (settingsError is not null) SettingsMessage.Text = Localization.T("could-not-save-settings") + settingsError;
        UpdateSortDescription();
        MachineName.Text = Environment.MachineName.ToUpperInvariant();
        AppsGrid.ItemsSource = _rows;
        CollectionViewSource.GetDefaultView(_rows).Filter = item => item is AppRow row &&
            (!_rulesOnly || row.HasRules || row.Children.Values.Any(child => child.HasRules)) &&
            (row.AppName.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
             row.Path.Contains(_search, StringComparison.CurrentCultureIgnoreCase));
        AppsGrid.Columns[1].SortDirection = ListSortDirection.Descending;
        Loaded += (_, _) =>
        {
            try
            {
                _engine.Start();
                StatusText.Text = _engine.Status;
                _timer.Tick += (_, _) => RefreshRows();
                _timer.Start();
            }
            catch (Exception ex) { StatusText.Text = Localization.T("could-not-start-monitoring") + ex.Message; }
        };
        Closed += (_, _) => { _timer.Stop(); _engine.Dispose(); };
    }

    private void RefreshRows()
    {
        StatusText.Text = _engine.Status;
        var snapshot = _engine.Snapshot();
        TotalDownload.Text = AppRow.FormatRate(snapshot.Sum(item => item.DownloadRate));
        TotalUpload.Text = AppRow.FormatRate(snapshot.Sum(item => item.UploadRate));
        foreach (var item in snapshot)
        {
            if (_rowByPath.TryGetValue(item.Path, out var row)) row.Update(item);
            else
            {
                row = new AppRow(item);
                _rowByPath[item.Path] = row;
                _rows.Add(row);
                _ = row.LoadIconAsync();
            }
            row.UpdateProcesses(item.Processes);
        }
        ReorderRows();
        if (_rulesOnly) CollectionViewSource.GetDefaultView(_rows).Refresh();
        AppCount.Text = Localization.T("0-applications-1-processes", _rowByPath.Count, _rowByPath.Values.Sum(row => row.Children.Count));
        UpdateSelectedTraffic();
    }

    private void LimiterToggle_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetLimiterEnabled(LimiterToggle.IsChecked == true);
        UpdateGlobalState();
        UpdateSelectedTraffic();
    }

    private void BlockerToggle_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetBlockerEnabled(BlockerToggle.IsChecked == true);
        UpdateGlobalState();
        UpdateSelectedTraffic();
    }

    private void UpdateGlobalState()
    {
        LimiterState.Text = $"Limiter {(_engine.LimiterEnabled ? "On" : "Off")} · Blocker {(_engine.BlockerEnabled ? "On" : "Off")}";
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel is null) return;
        bool settings = ReferenceEquals(sender, SettingsView);
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        var trafficVisibility = settings ? Visibility.Collapsed : Visibility.Visible;
        TrafficSummary.Visibility = SortingPanel.Visibility = AppsGrid.Visibility = trafficVisibility;
        DetailsPanel.Visibility = DetailsSplitter.Visibility = trafficVisibility;
        if (settings)
        {
            try
            {
                _startupEnabled = StartupRegistration.IsEnabled();
                StartWithWindows.IsChecked = _startupEnabled;
                StartWithWindows.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StartWithWindows.IsEnabled = false;
                SettingsMessage.Text = Localization.T("could-not-change-startup-setting") + ex.Message;
            }
            return;
        }
        _rulesOnly = ReferenceEquals(sender, RulesView);
        if (AppsGrid?.ItemsSource is null) return;
        CollectionViewSource.GetDefaultView(_rows).Refresh();
    }

    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        string preference = LanguageChoice.SelectedValue as string ?? "system";
        try
        {
            var next = _settings with { Language = preference };
            _settingsStore.Save(next);
            _settings = next;
            Localization.Current.SetLanguage(preference);
            foreach (var row in _rowByPath.Values)
            {
                row.RefreshLanguage();
                foreach (var child in row.Children.Values) child.RefreshLanguage();
            }
            UpdateSortDescription();
            RuleMessage.Text = "";
            if (AppsGrid.SelectedItem is null) AppsGrid_SelectionChanged(AppsGrid, null!);
            RefreshRows();
            SettingsMessage.Text = Localization.T("settings-saved");
        }
        catch (Exception ex)
        {
            LanguageChoice.SelectedValue = _settings.Language;
            SettingsMessage.Text = Localization.T("could-not-save-settings") + ex.Message;
        }
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = StartWithWindows.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            _startupEnabled = enabled;
            SettingsMessage.Text = Localization.T("startup-setting-updated");
        }
        catch (Exception ex)
        {
            StartWithWindows.IsChecked = _startupEnabled;
            SettingsMessage.Text = Localization.T("could-not-change-startup-setting") + ex.Message;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AppsGrid is null || AppsGrid.ItemsSource is null) return;
        _search = SearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_rows).Refresh();
    }

    private void UpdateSelectedTraffic()
    {
        if (AppsGrid.SelectedItem is AppRow row)
        {
            SelectedDownload.Text = row.DownloadText;
            SelectedUpload.Text = row.UploadText;
            SelectedTotal.Text = row.TotalText;
            SelectedRule.Text = row.HasRules ? row.LimitText : Localization.T("no-rules");
            RuleRuntime.Text = string.Join(Environment.NewLine, new[]
            {
                !_engine.LimiterEnabled ? Localization.T("limiter-off-speed-limits-paused") : "",
                !_engine.BlockerEnabled ? Localization.T("blocker-off-blocking-paused") : ""
            }.Where(message => message.Length > 0));
            RuleScope.Text = row.IsProcess
                ? Localization.T("pid-0-only-expires-when-the-process-exits", row.ProcessId)
                : Localization.T("total-for-all-processes-of-this-application");
            ParentRule.Text = row.ParentRuleText;

        }
    }

    private void AppsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Descending
            ? ListSortDirection.Ascending
            : e.Column.SortDirection == ListSortDirection.Ascending || e.Column.SortMemberPath != nameof(AppRow.Name)
                ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in AppsGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        _sortProperty = e.Column.SortMemberPath;
        _sortDirection = direction;
        UpdateSortDescription();
        ReorderRows();
    }

    private void UpdateSortDescription()
    {
        string label = _sortProperty switch
        {
            nameof(AppRow.DownloadRate) => Localization.T("download"),
            nameof(AppRow.UploadRate) => Localization.T("upload"),
            nameof(AppRow.TotalBytes) => Localization.T("total-traffic"),
            nameof(AppRow.DownloadLimit) => Localization.T("download-limit"),
            _ => Localization.T("application-name")
        };
        bool ascending = _sortDirection == ListSortDirection.Ascending;
        string direction = _sortProperty == nameof(AppRow.Name)
            ? (ascending ? "A → Z" : "Z → A")
            : (ascending ? Localization.T("lowest-first") : Localization.T("highest-first"));
        SortDescription.Text = $"{(ascending ? "↑" : "↓")}  {label} · {direction}";
    }

    private void ReorderRows()
    {
        var ordered = _rowByPath.Values.OrderBy(row => row, Comparer<AppRow>.Create(CompareRows))
            .SelectMany(row => row.IsExpanded ? new[] { row }.Concat(row.Children.Values.OrderBy(child => child.ProcessId)) : new[] { row })
            .ToArray();
        var desired = ordered.ToHashSet();
        for (int index = _rows.Count - 1; index >= 0; index--)
            if (!desired.Contains(_rows[index])) _rows.RemoveAt(index);
        foreach (var row in ordered)
            if (!_rows.Contains(row)) _rows.Add(row);
        bool fromEnd = CountMoves(ordered, true) < CountMoves(ordered, false);
        for (int step = 0; step < ordered.Length; step++)
        {
            int target = fromEnd ? ordered.Length - step - 1 : step;
            if (ReferenceEquals(_rows[target], ordered[target])) continue;
            int current = _rows.IndexOf(ordered[target]);
            _rows.Move(current, target);
        }
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppRow row } || row.IsProcess) return;
        if (row.IsExpanded && AppsGrid.SelectedItem is AppRow selected && selected.Parent == row)
            AppsGrid.SelectedItem = row;
        row.IsExpanded = !row.IsExpanded;
        ReorderRows();
    }

    private int CountMoves(AppRow[] ordered, bool fromEnd)
    {
        var preview = _rows.ToList();
        int moves = 0;
        for (int step = 0; step < ordered.Length; step++)
        {
            int target = fromEnd ? ordered.Length - step - 1 : step;
            if (ReferenceEquals(preview[target], ordered[target])) continue;
            preview.Remove(ordered[target]);
            preview.Insert(target, ordered[target]);
            moves++;
        }
        return moves;
    }

    private int CompareRows(AppRow left, AppRow right)
    {
        int result = _sortProperty switch
        {
            nameof(AppRow.DownloadRate) => RateBucket(left.DownloadRate).CompareTo(RateBucket(right.DownloadRate)),
            nameof(AppRow.UploadRate) => RateBucket(left.UploadRate).CompareTo(RateBucket(right.UploadRate)),
            nameof(AppRow.TotalBytes) => left.TotalBytes.CompareTo(right.TotalBytes),
            nameof(AppRow.DownloadLimit) => left.DownloadLimit.CompareTo(right.DownloadLimit),
            _ => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
        };
        if (_sortDirection == ListSortDirection.Descending) result = -result;
        return result != 0 ? result : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
    }

    private static long RateBucket(double bytesPerSecond) => (long)Math.Round(bytesPerSecond / 102.4);

    private void AppsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row)
        {
            SelectedName.Text = Localization.T("select-an-application");
            SelectedPath.Text = Localization.T("select-an-application-from-the-list");
            SelectedDownload.Text = SelectedUpload.Text = SelectedTotal.Text = "—";
            SelectedRule.Text = "—";
            RuleScope.Text = Localization.T("rule-scope-depends-on-the-selected-row");
            ParentRule.Text = "";
            RuleRuntime.Text = "";
            RuleControls.IsEnabled = false;
            return;
        }
        RuleControls.IsEnabled = true;
        SelectedName.Text = row.IsProcess ? $"{row.AppName} · PID {row.ProcessId}" : row.Name;
        SelectedPath.Text = row.Path;
        DownloadBox.Text = row.DownloadLimit.ToString();
        UploadBox.Text = row.UploadLimit.ToString();
        LoadRuleChecks(row);
        RuleMessage.Text = "";
        UpdateSelectedTraffic();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row) { RuleMessage.Text = Localization.T("select-an-application-first"); return; }
        if (!int.TryParse(DownloadBox.Text, out int down) || !int.TryParse(UploadBox.Text, out int up) ||
            (down != 0 && down < 16) || (up != 0 && up < 16) || down < 0 || up < 0)
        { RuleMessage.Text = Localization.T("enter-0-or-at-least-16-kb-s"); return; }
        try
        {
            _engine.SetRule(ReadRule(row, down, up), row.IsProcess ? row.ProcessId : 0);
            RuleMessage.Text = row.IsProcess ? Localization.T("saved-for-pid-0-only", row.ProcessId) : Localization.T("application-limit-saved");
            RefreshRows();
        }
        catch (Exception ex) { RuleMessage.Text = Localization.T("could-not-save") + ex.Message; }
    }

    private void LoadRuleChecks(AppRow row)
    {
        DownloadEnabled.IsChecked = row.DownloadLimitEnabled;
        UploadEnabled.IsChecked = row.UploadLimitEnabled;
        BlockDownload.IsChecked = row.BlockDownload;
        BlockUpload.IsChecked = row.BlockUpload;
    }

    private AppRule ReadRule(AppRow row, int down, int up) => new()
    {
        Path = row.Path, DownloadKBps = down, UploadKBps = up,
        DownloadLimitEnabled = DownloadEnabled.IsChecked == true, UploadLimitEnabled = UploadEnabled.IsChecked == true,
        BlockDownload = BlockDownload.IsChecked == true, BlockUpload = BlockUpload.IsChecked == true
    };

    private void RuleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row) return;
        try
        {
            _engine.SetRule(ReadRule(row, row.DownloadLimit, row.UploadLimit), row.IsProcess ? row.ProcessId : 0);
            RuleMessage.Text = Localization.T("rule-updated");
            RefreshRows();
        }
        catch (Exception ex)
        {
            LoadRuleChecks(row);
            RuleMessage.Text = Localization.T("could-not-save") + ex.Message;
        }
    }

    private void ClearLimits_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row) return;
        try
        {
            _engine.SetRule(new AppRule { Path = row.Path, BlockDownload = row.BlockDownload, BlockUpload = row.BlockUpload },
                row.IsProcess ? row.ProcessId : 0);
            DownloadEnabled.IsChecked = UploadEnabled.IsChecked = true;
            DownloadBox.Text = UploadBox.Text = "0";
            RuleMessage.Text = row.IsProcess ? Localization.T("pid-limit-removed") : Localization.T("application-limit-removed");
            RefreshRows();
        }
        catch (Exception ex) { RuleMessage.Text = Localization.T("could-not-save") + ex.Message; }
    }
}

public sealed class AppRow(AppUsage initialUsage, AppRow? parent = null) : INotifyPropertyChanged
{
    private AppUsage usage = initialUsage;
    public AppRow? Parent { get; } = parent;
    public bool IsProcess => Parent is not null;
    public int ProcessId => usage.ProcessId;
    public string AppName => usage.Name;
    public Thickness Indent => IsProcess ? new Thickness(24, 0, 0, 0) : new Thickness(0);
    public Dictionary<int, AppRow> Children { get; } = new();
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Notify(nameof(ExpandGlyph)); }
    }
    public bool CanExpand => !IsProcess && Children.Count > 0;
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    public ImageSource Icon { get; private set; } = AppIcons.Default;
    internal async Task LoadIconAsync()
    {
        Icon = await AppIcons.GetAsync(Path);
        Notify(nameof(Icon));
        foreach (var child in Children.Values) { child.Icon = Icon; child.Notify(nameof(Icon)); }
    }
    internal void UpdateProcesses(List<AppUsage> processes)
    {
        foreach (var process in processes)
        {
            if (Children.TryGetValue(process.ProcessId, out var row)) row.Update(process);
            else Children[process.ProcessId] = new AppRow(process, this) { Icon = Icon };
        }
        Notify(nameof(CanExpand));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(AppUsage current)
    {
        string oldName = Name, oldDownload = DownloadText, oldUpload = UploadText;
        string oldTotal = TotalText, oldLimit = LimitText;
        usage = current;
        if (oldName != Name) Notify(nameof(Name));
        if (oldDownload != DownloadText) Notify(nameof(DownloadText));
        if (oldUpload != UploadText) Notify(nameof(UploadText));
        if (oldTotal != TotalText) Notify(nameof(TotalText));
        if (oldLimit != LimitText) Notify(nameof(LimitText));
    }
    internal void RefreshLanguage()
    {
        Notify(nameof(Name)); Notify(nameof(DownloadText)); Notify(nameof(UploadText));
        Notify(nameof(TotalText)); Notify(nameof(LimitText)); Notify(nameof(ParentRuleText));
    }
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public string Path => usage.Path;
    public string Name => IsProcess ? $"Process {ProcessId}" : usage.Name;
    public double DownloadRate => usage.DownloadRate;
    public double UploadRate => usage.UploadRate;
    public long TotalBytes => usage.DownloadBytes + usage.UploadBytes;
    public int DownloadLimit => usage.DownloadLimit;
    public int UploadLimit => usage.UploadLimit;
    public int AppDownloadLimit => usage.AppDownloadLimit;
    public int AppUploadLimit => usage.AppUploadLimit;
    public bool DownloadLimitEnabled => usage.DownloadLimitEnabled;
    public bool UploadLimitEnabled => usage.UploadLimitEnabled;
    public bool BlockDownload => usage.BlockDownload;
    public bool BlockUpload => usage.BlockUpload;
    public bool HasRules => DownloadLimit > 0 || UploadLimit > 0 || BlockDownload || BlockUpload;
    public string ParentRuleText => usage.ParentRule is { } rule && (rule.DownloadKBps > 0 || rule.UploadKBps > 0 || rule.BlockDownload || rule.BlockUpload)
        ? Localization.T("inherited-from-application") + DescribeRule(rule.DownloadKBps, rule.UploadKBps, rule.DownloadLimitEnabled, rule.UploadLimitEnabled, rule.BlockDownload, rule.BlockUpload) : "";
    public string DownloadText => FormatRate(usage.DownloadRate);
    public string UploadText => FormatRate(usage.UploadRate);
    public string TotalText => FormatBytes(usage.DownloadBytes + usage.UploadBytes);
    public string LimitText => HasRules ? DescribeRule(DownloadLimit, UploadLimit, DownloadLimitEnabled, UploadLimitEnabled, BlockDownload, BlockUpload) : "—";
    private static string DescribeRule(int down, int up, bool downEnabled, bool upEnabled, bool blockDown, bool blockUp)
        => $"↓ {Direction(down, downEnabled, blockDown)} / ↑ {Direction(up, upEnabled, blockUp)}";
    private static string Direction(int value, bool enabled, bool blocked)
        => blocked ? Localization.T("blocked") : value == 0 ? Localization.T("unlimited") : Localization.T("0-kb-s-1", value, enabled ? "" : Localization.T("disabled"));
    public static string FormatRate(double value) => FormatBytes(value) + (Localization.Current.Language == "tr" ? "/sn" : "/s");
    private static string FormatBytes(double value) => value >= 1024 * 1024 ? $"{value / 1048576:0.0} MB" : $"{value / 1024:0.0} KB";
}
