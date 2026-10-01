using System.Windows;

namespace Limiter;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.SequenceEqual(new[] { "--verify-package" }))
        {
            try
            {
                foreach (string file in new[] { "WinDivert.dll", "WinDivert64.sys", "LICENSE", "NOTICE", "WinDivert-LICENSE.txt" })
                    if (!System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, file)))
                        throw new System.IO.FileNotFoundException(file);
                if (!Native.WinDivertHelperParseIPv6Address("::1", new byte[16]))
                    throw new InvalidOperationException("WinDivert native library check failed.");
                Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--installer-startup")
        {
            try
            {
                if (e.Args.Length != 2) throw new ArgumentException("A user SID is required.");
                StartupRegistration.EnableForUser(e.Args[1]);
                Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
            return;
        }
        if (e.Args.SequenceEqual(new[] { "--installer-remove-startup" }))
        {
            try { StartupRegistration.RemoveForInstalledExecutable(); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
            return;
        }
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
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
