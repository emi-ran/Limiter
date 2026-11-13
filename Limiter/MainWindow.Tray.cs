using System.ComponentModel;
using System.Windows;

namespace Limiter;

public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _notificationIcon;
    private System.Windows.Forms.ToolStripMenuItem? _openTrayItem;
    private System.Windows.Forms.ToolStripMenuItem? _exitTrayItem;
    private System.Windows.Forms.ToolStripMenuItem? _updateTrayItem;
    private readonly CancellationTokenSource _updateCancellation = new();
    private Version? _availableVersion;
    private bool _exiting;
    private int _reportedMonitoringError;

    private void InitializeTray()
    {
        using var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Limiter;component/Assets/Limiter.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(resource);
        _notificationIcon = (System.Drawing.Icon)icon.Clone();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _openTrayItem = new System.Windows.Forms.ToolStripMenuItem();
        _exitTrayItem = new System.Windows.Forms.ToolStripMenuItem();
        _updateTrayItem = new System.Windows.Forms.ToolStripMenuItem { Visible = false };
        _updateTrayItem.Click += (_, _) => OpenReleasePage();
        _openTrayItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _exitTrayItem.Click += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            _exiting = true;
            System.Windows.Application.Current.Shutdown();
        });
        menu.Items.Add(_updateTrayItem);
        menu.Items.Add(_openTrayItem);
        menu.Items.Add(_exitTrayItem);
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _notificationIcon,
            Text = "Limiter",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        Localization.Current.PropertyChanged += TrayLanguageChanged;
        UpdateTrayLanguage();
        _ = CheckForUpdatesAsync(_updateCancellation.Token);
    }

    private void TrayLanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateTrayLanguage();

    private void UpdateTrayLanguage()
    {
        if (_openTrayItem is not null) _openTrayItem.Text = Localization.T("tray-open");
        if (_exitTrayItem is not null) _exitTrayItem.Text = Localization.T("tray-exit");
        if (_updateTrayItem is not null && _availableVersion is not null)
            _updateTrayItem.Text = Localization.T("update-menu", _availableVersion);
        if (_trayIcon is not null)
            _trayIcon.Text = _reportedMonitoringError == 0 ? "Limiter" : Localization.T("tray-monitoring-error");
    }

    internal void ReportMonitoringError(int error)
    {
        if (error == _reportedMonitoringError) return;
        _reportedMonitoringError = error;
        if (_trayIcon is null) return;
        _trayIcon.Text = error == 0 ? "Limiter" : Localization.T("tray-monitoring-error");
        if (error == 0) return;
        _trayIcon.BalloonTipTitle = "Limiter";
        _trayIcon.BalloonTipText = Localization.T("packet-monitoring-stopped-windows-error-0", error);
        _trayIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Error;
        _trayIcon.ShowBalloonTip(10000);
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var version = await UpdateChecker.CheckAsync(cancellationToken);
                if (version is not null && (_availableVersion is null || version > _availableVersion))
                {
                    _availableVersion = version;
                    UpdateTrayLanguage();
                    if (_updateTrayItem is not null) _updateTrayItem.Visible = true;
                    if (_trayIcon is not null)
                    {
                        _trayIcon.BalloonTipTitle = Localization.T("update-title");
                        _trayIcon.BalloonTipText = Localization.T("update-message", version);
                        _trayIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                        _trayIcon.ShowBalloonTip(10000);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
            try { await Task.Delay(TimeSpan.FromHours(12), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void OpenReleasePage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/emi-ran/Limiter/releases/latest") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { System.Windows.MessageBox.Show(Localization.T("update-open-error"), "Limiter"); }
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_exiting) return;
        e.Cancel = true;
        Hide();
    }

    private void DisposeTray()
    {
        _updateCancellation.Cancel();
        _updateCancellation.Dispose();
        Localization.Current.PropertyChanged -= TrayLanguageChanged;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            var menu = _trayIcon.ContextMenuStrip;
            _trayIcon.Dispose();
            menu?.Dispose();
            _trayIcon = null;
        }
        _notificationIcon?.Dispose();
        _notificationIcon = null;
    }
}
