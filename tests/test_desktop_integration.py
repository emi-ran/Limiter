"""Fast source/resource contract checks; Windows smoke tests are still required."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


class DesktopIntegrationChecks(unittest.TestCase):
    def test_logon_launch_requests_tray(self):
        registration = (ROOT / 'Limiter/StartupRegistration.cs').read_text()
        app = (ROOT / 'Limiter/App.xaml.cs').read_text()
        self.assertIn('action.Arguments = "--tray";', registration)
        self.assertIn('e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)', app)
        self.assertIn('if (!startInTray || !monitoringStarted || window.HasRuleLoadError) window.Show();', app)

    def test_hidden_startup_failure_is_visible(self):
        app = (ROOT / 'Limiter/App.xaml.cs').read_text()
        self.assertIn('bool monitoringStarted = window.StartMonitoring();', app)
        self.assertIn('if (!startInTray || !monitoringStarted || window.HasRuleLoadError) window.Show();', app)

    def test_migration_only_updates_matching_executable(self):
        startup = (ROOT / 'Limiter/StartupRegistration.cs').read_text()
        self.assertIn('internal static void MigrateCurrentExecutable()', startup)
        self.assertIn('string.Equals(actionPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)', startup)
        self.assertIn('internal static bool IsProtectedInstallPath', startup)
        self.assertIn('startup-requires-program-files', startup)
        self.assertIn('folder.RegisterTaskDefinition(TaskName, definition, 6,', startup)
        window = (ROOT / 'Limiter/MainWindow.xaml.cs').read_text()
        self.assertIn('StartupRegistration.MigrateCurrentExecutable();', window)
        self.assertNotIn('if (StartupRegistration.IsEnabled()) StartupRegistration.SetEnabled(true);', window)

    def test_small_icon_frames_use_windows_dib(self):
        import struct
        data = (ROOT / 'Limiter/Assets/Limiter.ico').read_bytes()
        reserved, kind, count = struct.unpack_from('<HHH', data)
        self.assertEqual((reserved, kind), (0, 1))
        sizes = set()
        for index in range(count):
            width, height, colors, reserved, planes, bits, length, offset = struct.unpack_from('<BBBBHHII', data, 6 + 16 * index)
            size = width or 256
            sizes.add(size)
            self.assertEqual(height or 256, size)
            self.assertLessEqual(offset + length, len(data))
            if size < 256:
                self.assertEqual(struct.unpack_from('<I', data, offset)[0], 40,
                                 f'{size}px frame must use BITMAPINFOHEADER, not PNG')
                self.assertEqual((planes, bits), (1, 32))
        self.assertTrue({16, 24, 32, 48, 64, 128, 256}.issubset(sizes))

    def test_tray_lifetime_and_hidden_monitoring(self):
        source = ROOT / 'Limiter/MainWindow.Tray.cs'
        self.assertTrue(source.exists(), 'No system tray implementation')
        tray = source.read_text()
        window = (ROOT / 'Limiter/MainWindow.xaml.cs').read_text()
        self.assertIn('new System.Windows.Forms.NotifyIcon', tray)
        self.assertIn('OnClosing', tray)
        self.assertIn('e.Cancel = true;', tray)
        self.assertIn('Hide();', tray)
        self.assertIn('System.Windows.Application.Current.Shutdown();', tray)
        self.assertIn('_trayIcon.Dispose();', tray)
        self.assertNotIn('Loaded +=', window)
        self.assertIn('window.StartMonitoring();', (ROOT / 'Limiter/App.xaml.cs').read_text())


if __name__ == '__main__':
    unittest.main()
