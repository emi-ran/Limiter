using System.ComponentModel;
using System.Windows;

namespace Limiter;

public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _notificationIcon;
    private System.Windows.Forms.ToolStripMenuItem? _openTrayItem;
    private System.Windows.Forms.ToolStripMenuItem? _exitTrayItem;
    private bool _exiting;

    private void InitializeTray()
    {
        using var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Limiter;component/Assets/Limiter.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(resource);
        _notificationIcon = (System.Drawing.Icon)icon.Clone();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _openTrayItem = new System.Windows.Forms.ToolStripMenuItem();
        _exitTrayItem = new System.Windows.Forms.ToolStripMenuItem();
        _openTrayItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _exitTrayItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            _exiting = true;
            System.Windows.Application.Current.Shutdown();
        });
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
    }

    private void TrayLanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateTrayLanguage();

    private void UpdateTrayLanguage()
    {
        if (_openTrayItem is not null) _openTrayItem.Text = Localization.T("tray-open");
        if (_exitTrayItem is not null) _exitTrayItem.Text = Localization.T("tray-exit");
    }

    private void ShowFromTray()
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
