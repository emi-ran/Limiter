"""Supplemental source contracts; Windows EngineChecks exercises actual ACL APIs."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


class StartupSecurityContracts(unittest.TestCase):
    def test_elevated_entry_points_use_actual_protection(self):
        source = (ROOT / 'Limiter/StartupRegistration.cs').read_text()
        self.assertIn('HasProtectedInstallFiles(path);', source)
        self.assertIn('if (!HasProtectedInstallFiles(path))', source)
        self.assertIn('if (!HasProtectedInstallFiles(executable))', source)
        self.assertIn('!HasProtectedInstallFiles(actionPath)', source)
        self.assertIn('FileAttributes.ReparsePoint', source)
        self.assertIn('descriptor.DiscretionaryAcl is null', source)
        self.assertIn('IsProtectedTree(child)', source)

    def test_ancestor_policy_is_separate_from_install_mutation(self):
        source = (ROOT / 'Limiter/StartupRegistration.cs').read_text()
        self.assertIn('if (!IsProtectedEntry(install)) return false;', source)
        self.assertIn('DirectoryInfo? parent = install.Parent;', source)
        self.assertIn('IsProtectedEntry(parent, ancestor: true)', source)
        self.assertIn('(!ancestor || (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)', source)
        self.assertIn('(ancestor ? replacement : mutation)', source)
        self.assertIn('FileSystemRights.TakeOwnership | (FileSystemRights)0x10000000', source)
        checks = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        self.assertIn('(A;OICIIO;FA;;;CO)', checks)
        self.assertIn('Security(safeAncestor), ancestor: true', checks)
        self.assertIn('!StartupRegistration.IsProtectedSecurity(Security(safeAncestor))', checks)
        self.assertIn('Security(unsafeAncestor), ancestor: true', checks)
        for rights in ('DC', 'SD', 'WD', 'WO', 'GA'):
            self.assertIn(f'"O:BAG:BAD:P(A;;{rights};;;BU)"', checks)

    def test_junction_fixture_uses_cmd_quoting_and_spaces(self):
        source = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        fixture = source[source.index('string root ='):source.index('CheckStartupSecurity();')]
        self.assertNotIn('ArgumentList', fixture)
        self.assertIn('Arguments = $"/d /c \\"mklink /J \\"{junction}\\" \\"{target}\\"\\""', fixture)
        self.assertIn('"target with spaces"', fixture)
        self.assertIn('"junction with spaces"', fixture)

    def test_independent_pid_removal_refreshes_activity(self):
        source = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        removal = source.index('engine.SetProcessLimits(owner.Path, owner.ProcessId, 0, 0);')
        self.assertIn('engine.RecordSent(owner, 1, false);', source[removal - 100:removal])
        self.assertIn('Inactive PID history was not pruned', source)


if __name__ == '__main__':
    unittest.main()
