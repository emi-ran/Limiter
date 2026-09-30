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

    public MainWindow()
    {
        InitializeComponent();
        MachineName.Text = Environment.MachineName.ToUpperInvariant();
        AppsGrid.ItemsSource = _rows;
        CollectionViewSource.GetDefaultView(_rows).Filter = item => item is AppRow row &&
            (!_rulesOnly || row.DownloadLimit > 0 || row.UploadLimit > 0) &&
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
            catch (Exception ex) { StatusText.Text = "İzleme başlatılamadı: " + ex.Message; }
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
        AppCount.Text = $"{_rowByPath.Count} uygulama · {_rowByPath.Values.Sum(row => row.Children.Count)} process";
        UpdateSelectedTraffic();
    }

    private void LimiterToggle_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetLimiterEnabled(LimiterToggle.IsChecked == true);
        LimiterState.Text = _engine.LimiterEnabled ? "Sınırlar etkin" : "Sınırlar duraklatıldı · İzleme devam ediyor";
        UpdateSelectedTraffic();
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        _rulesOnly = ReferenceEquals(sender, RulesView);
        if (AppsGrid?.ItemsSource is null) return;
        CollectionViewSource.GetDefaultView(_rows).Refresh();
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
            SelectedRule.Text = row.LimitText == "—" ? "Sınır yok" : row.LimitText + " KB/sn" + (_engine.LimiterEnabled ? "" : " · Duraklatıldı");
            RuleScope.Text = row.IsProcess
                ? $"Yalnızca PID {row.ProcessId} · Process kapanınca sona erer."
                : "Bu uygulamanın tüm process'lerinin toplamı.";
            ParentRule.Text = row.IsProcess && (row.AppDownloadLimit > 0 || row.AppUploadLimit > 0)
                ? $"Uygulama toplam sınırı: ↓ {row.AppDownloadLimit} / ↑ {row.AppUploadLimit} KB/sn"
                : "";
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
            nameof(AppRow.DownloadRate) => "İndirme",
            nameof(AppRow.UploadRate) => "Yükleme",
            nameof(AppRow.TotalBytes) => "Toplam trafik",
            nameof(AppRow.DownloadLimit) => "İndirme sınırı",
            _ => "Uygulama adı"
        };
        bool ascending = _sortDirection == ListSortDirection.Ascending;
        string direction = _sortProperty == nameof(AppRow.Name)
            ? (ascending ? "A → Z" : "Z → A")
            : (ascending ? "Düşükten yükseğe" : "Yüksekten düşüğe");
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
            SelectedName.Text = "Uygulama seçin";
            SelectedPath.Text = "Listeden bir uygulama seçin.";
            SelectedDownload.Text = SelectedUpload.Text = SelectedTotal.Text = "—";
            SelectedRule.Text = "—";
            RuleScope.Text = "Kural kapsamı seçilen satıra göre belirlenir.";
            ParentRule.Text = "";
            RuleControls.IsEnabled = false;
            return;
        }
        RuleControls.IsEnabled = true;
        SelectedName.Text = row.IsProcess ? $"{row.AppName} · PID {row.ProcessId}" : row.Name;
        SelectedPath.Text = row.Path;
        DownloadBox.Text = row.DownloadLimit.ToString();
        UploadBox.Text = row.UploadLimit.ToString();
        RuleMessage.Text = "";
        UpdateSelectedTraffic();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row) { RuleMessage.Text = "Önce bir uygulama seçin."; return; }
        if (!int.TryParse(DownloadBox.Text, out int down) || !int.TryParse(UploadBox.Text, out int up) ||
            (down != 0 && down < 16) || (up != 0 && up < 16) || down < 0 || up < 0)
        { RuleMessage.Text = "0 veya en az 16 KB/sn yazın."; return; }
        try
        {
            if (row.IsProcess) _engine.SetProcessLimits(row.Path, row.ProcessId, down, up);
            else _engine.SetLimits(row.Path, down, up);
            RuleMessage.Text = row.IsProcess ? $"Yalnızca PID {row.ProcessId} için kaydedildi." : "Uygulamanın toplam sınırı kaydedildi.";
            RefreshRows();
        }
        catch (Exception ex) { RuleMessage.Text = "Kaydedilemedi: " + ex.Message; }
    }

    private void ClearLimits_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row) return;
        try
        {
            if (row.IsProcess) _engine.SetProcessLimits(row.Path, row.ProcessId, 0, 0);
            else _engine.SetLimits(row.Path, 0, 0);
            DownloadBox.Text = UploadBox.Text = "0";
            RuleMessage.Text = row.IsProcess ? "PID sınırı kaldırıldı." : "Uygulamanın toplam sınırı kaldırıldı.";
            RefreshRows();
        }
        catch (Exception ex) { RuleMessage.Text = "Kaydedilemedi: " + ex.Message; }
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
    public string DownloadText => FormatRate(usage.DownloadRate);
    public string UploadText => FormatRate(usage.UploadRate);
    public string TotalText => FormatBytes(usage.DownloadBytes + usage.UploadBytes);
    public string LimitText => usage.DownloadLimit == 0 && usage.UploadLimit == 0 ? "—" : $"↓ {usage.DownloadLimit} / ↑ {usage.UploadLimit}";
    public static string FormatRate(double value) => FormatBytes(value) + "/sn";
    private static string FormatBytes(double value) => value >= 1024 * 1024 ? $"{value / 1048576:0.0} MB" : $"{value / 1024:0.0} KB";
}
