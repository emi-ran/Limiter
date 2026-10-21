"""Supplemental source contracts, not a substitute for Windows package smoke tests."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
WIX = {'w': 'http://wixtoolset.org/schemas/v4/wxs'}


class DistributionContracts(unittest.TestCase):
    def test_all_distribution_formats_are_required_and_hashed(self):
        script = (ROOT / 'installer/Test-Packages.ps1').read_text(encoding='utf-8')
        for variable in ('portable', 'msi', 'setup', 'zip'):
            self.assertIn(f'${variable}.Count -ne 1', script)
        self.assertIn('$package.Name -notin $verifiedNames', script)
        self.assertIn('Duplicate checksum entry:', script)
        self.assertIn('Checksum mismatch:', script)

    def test_zip_executes_its_own_extracted_payload(self):
        script = (ROOT / 'installer/Test-Packages.ps1').read_text(encoding='utf-8')
        self.assertIn('Expand-Archive -LiteralPath $zip[0].FullName -DestinationPath $extractDirectory', script)
        self.assertIn("Invoke-PackageProcess (Join-Path $extractDirectory 'Limiter.exe') @('--verify-package')", script)
        self.assertIn('Remove-Item -LiteralPath $extractDirectory -Recurse -Force', script)
        self.assertIn('[guid]::NewGuid()', script)

    def test_setup_has_independent_install_and_uninstall(self):
        script = (ROOT / 'installer/Test-Packages.ps1').read_text(encoding='utf-8')
        self.assertIn("foreach ($kind in @('MSI', 'Setup'))", script)
        self.assertIn("Invoke-PackageProcess $setup[0].FullName @('/install', '/quiet', '/norestart'", script)
        self.assertIn("Invoke-PackageProcess $setup[0].FullName @('/uninstall', '/quiet', '/norestart'", script)
        self.assertIn('Silent setup enabled an opt-in desktop shortcut or startup task.', script)
        self.assertIn('finally {', script)
        self.assertIn('$kind uninstall left installed files, desktop shortcut or startup task.', script)

    def test_install_smoke_guards_and_tray_task_remain(self):
        script = (ROOT / 'installer/Test-Packages.ps1').read_text(encoding='utf-8')
        for guard in ('elevated disposable runner', 'Refusing to replace an existing Limiter installation.',
                      'Refusing to replace an existing desktop shortcut.',
                      'Refusing to replace an existing startup task.'):
            self.assertIn(guard, script)
        self.assertIn("$task.Actions.Arguments -ne '--tray'", script)
        self.assertIn("$task.Principal.RunLevel -ne 'Highest'", script)
        self.assertIn('@($task.Actions).Count -ne 1', script)
        install_loop = script.index("foreach ($kind in @('MSI', 'Setup'))")
        guard = script.index('Refusing to replace an existing Limiter installation.', install_loop)
        install = script.index("Invoke-PackageProcess 'msiexec.exe' @('/i'", install_loop)
        self.assertLess(guard, install)

    def test_cleanup_preserves_the_primary_install_failure(self):
        script = (ROOT / 'installer/Test-Packages.ps1').read_text(encoding='utf-8')
        lifecycle = script[script.index("foreach ($kind in @('MSI', 'Setup'))"):]
        self.assertIn('$lifecycleError = $null', lifecycle)
        self.assertIn('$lifecycleError = $_', lifecycle)
        self.assertIn('if ($null -eq $lifecycleError) { throw }', lifecycle)
        self.assertIn('Write-Warning "$kind cleanup also failed: $_"', lifecycle)
        # Neither a failed install nor an unexpected uninstall may silently pass.
        self.assertIn('if ($process.ExitCode -notin @(0, 3010))', script)
        self.assertNotIn('@(0, 3010, 1605)', script)

    def test_installer_folder_is_fixed_and_options_default_off(self):
        package = ET.parse(ROOT / 'installer/Package.wxs')
        options = ET.parse(ROOT / 'installer/Options.wxs')
        folder = package.find(".//w:StandardDirectory[@Id='ProgramFiles6432Folder']/w:Directory[@Id='INSTALLFOLDER']", WIX)
        if folder is None:
            self.fail('Missing fixed Program Files installation folder')
        self.assertEqual(folder.get('Name'), 'Limiter')
        for name in ('STARTWITHWINDOWS', 'DESKTOPSHORTCUT'):
            prop = package.find(f".//w:Property[@Id='{name}']", WIX)
            if prop is None:
                self.fail(f'Missing opt-in property: {name}')
            self.assertNotEqual(prop.get('Value'), '1')
        self.assertIsNone(options.find(".//w:Control[@Type='PathEdit']", WIX))
        self.assertIsNone(options.find(".//w:DialogRef[@Id='InstallDirDlg']", WIX))

    def test_windows_ci_runs_supplemental_and_runtime_checks(self):
        workflow = (ROOT / '.github/workflows/windows.yml').read_text(encoding='utf-8')
        self.assertIn('actions/setup-python@', workflow)
        self.assertIn('python -m unittest discover -s tests -v', workflow)
        self.assertIn('dotnet run --project Limiter.EngineChecks/Limiter.EngineChecks.csproj', workflow)
        self.assertIn('./installer/Test-Packages.ps1 -Install', workflow)


if __name__ == '__main__':
    unittest.main()
