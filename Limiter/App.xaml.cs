using System.Windows;

namespace Limiter;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\Limiter.NetworkCapture", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("Limiter zaten açık. Önce eski pencereyi kapatın.", "Limiter");
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
