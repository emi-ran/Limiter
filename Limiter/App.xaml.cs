using System.Windows;

namespace Limiter;

public partial class App : Application
{
    private const string MutexName = @"Local\Limiter.NetworkCapture";
    private const string ActivationEventName = @"Local\Limiter.Activate";
    private const string ActivationStopEventName = @"Local\Limiter.ActivateStop";
    private Mutex? _singleInstance;
    private EventWaitHandle? _activationRequest;
    private EventWaitHandle? _activationStop;
    private bool _ownsMutex;
    private Task? _activationListener;

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
        if (e.Args.Length > 0 && e.Args[0] == "--installer-adopt-startup")
        {
            try
            {
                if (e.Args.Length != 2) throw new ArgumentException("A user SID is required.");
                StartupRegistration.AdoptForUser(e.Args[1]);
                Shutdown(0);
            }
            catch (Exception ex) { InstallerDiagnostics.ReportFailure("adopt-startup", ex); Shutdown(1); }
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
            catch (Exception ex) { InstallerDiagnostics.ReportFailure("enable-startup", ex); Shutdown(1); }
            return;
        }
        if (e.Args.SequenceEqual(new[] { "--installer-remove-startup" }))
        {
            try { StartupRegistration.RemoveForInstalledExecutable(); Shutdown(0); }
            catch (Exception ex) { InstallerDiagnostics.ReportFailure("remove-startup", ex); Shutdown(1); }
            return;
        }
        try { Localization.Current.SetLanguage(new SettingsStore().Load().Language); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Localization.Current.SetLanguage("system"); } // MainWindow reports unreadable settings.
        _singleInstance = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            RequestActivation();
            Shutdown();
            return;
        }
        _activationRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationStop = new EventWaitHandle(false, EventResetMode.ManualReset, ActivationStopEventName);
        base.OnStartup(e);
        bool startInTray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow();
        MainWindow = window;
        // Keep capture active even when the window has never been shown.
        bool monitoringStarted = window.StartMonitoring();
        if (!startInTray || !monitoringStarted || window.HasRuleLoadError) window.Show();
        _activationListener = Task.Run(() => ListenForActivation(window));
    }

    // A second launch asks the running instance to show its hidden window.
    private static void RequestActivation()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var request = EventWaitHandle.OpenExisting(ActivationEventName);
                request.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
        }
        MessageBox.Show(Localization.T("limiter-is-already-running-close-the-existing-window-first"), "Limiter");
    }

    private void ListenForActivation(MainWindow window)
    {
        try
        {
            var handles = new WaitHandle[] { _activationRequest!, _activationStop! };
            while (WaitHandle.WaitAny(handles) == 0)
                Dispatcher.InvokeAsync(() =>
                {
                    if (!Dispatcher.HasShutdownStarted) window.ShowFromTray();
                });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { } // The dispatcher is shutting down.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationStop?.Set();
        _activationListener?.GetAwaiter().GetResult();
        _activationRequest?.Dispose();
        _activationStop?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
