"""Source contracts only; actual helper/Event Log behavior needs Windows CI."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class InstallerDiagnosticContracts(unittest.TestCase):
    def test_helper_failures_use_os_event_service_not_an_elevated_file_sink(self):
        app = (ROOT / 'Limiter/App.xaml.cs').read_text()
        helper_path = ROOT / 'Limiter/InstallerDiagnostics.cs'
        self.assertTrue(helper_path.exists(), 'Missing rollback-independent helper diagnostics')
        helper = helper_path.read_text()
        for operation in ('adopt-startup', 'enable-startup', 'remove-startup'):
            self.assertIn(f'InstallerDiagnostics.ReportFailure("{operation}", ex); Shutdown(1);', app)
        for api in ('RegisterEventSourceW', 'ReportEventW', 'DeregisterEventSource'):
            self.assertIn(api, helper)
        self.assertIn('exception.ToString()', helper)
        self.assertIn('finally { DeregisterEventSource(source); }', helper)
        self.assertNotIn('File.Write', helper)
        self.assertNotIn('CreateEventSource', helper)

    def test_acl_diagnostics_are_read_only_and_include_rejected_entry(self):
        source = (ROOT / 'Limiter/StartupRegistration.cs').read_text()
        self.assertIn('internal static string DescribeInstallProtectionFailure()', source)
        diagnostic = source[source.index('internal static string DescribeInstallProtectionFailure()'):source.index('private static bool IsProtectedTree')]
        self.assertIn('GetSecurityDescriptorSddlForm', diagnostic)
        self.assertIn('IsProtectedEntry(entry, ancestor)', diagnostic)
        self.assertIn('FileAttributes.ReparsePoint', diagnostic)
        self.assertNotIn('SetAccessControl', diagnostic)
        self.assertNotIn('CreateDirectory', diagnostic)

    def test_ci_captures_raw_event_data_without_masking_install_error(self):
        script = (ROOT / 'installer/Test-Packages.ps1').read_text()
        self.assertIn('$lifecycleStarted = Get-Date', script)
        self.assertIn("$_.ProviderName -eq 'Limiter Installer'", script)
        self.assertIn('StartTime = $Since', script)
        self.assertIn('$_.ToXml()', script)
        self.assertIn('"$Kind-helper-events.log"', script)
        self.assertIn('Could not collect installer helper events:', script)
        catch = script[script.index('$lifecycleError = $_'):script.index('finally {', script.index('$lifecycleError = $_'))]
        self.assertIn('Export-InstallerHelperEvents', catch)
        self.assertIn('throw', catch)


if __name__ == '__main__':
    unittest.main()
