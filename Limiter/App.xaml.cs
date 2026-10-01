using System.Windows;

namespace Limiter;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        try { Localization.Current.SetLanguage(new SettingsStore().Load().Language); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Localization.Current.SetLanguage("system"); } // MainWindow reports unreadable settings.
        _singleInstance = new Mutex(true, @"Local\Limiter.NetworkCapture", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show(Localization.T("limiter-is-already-running-close-the-existing-window-first"), "Limiter");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
