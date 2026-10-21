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
        self.assertIn('(A;OICIIO;0x1f01ff;;;CO)', checks)
        self.assertIn('safeAncestorSecurity, ancestor: true', checks)
        self.assertIn('!StartupRegistration.IsProtectedSecurity(safeAncestorSecurity)', checks)
        self.assertIn('Security(unsafeAncestor.Sddl, unsafeAncestor.Mask), ancestor: true', checks)

    def test_fixture_masks_are_checked_before_policy(self):
        checks = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        self.assertIn('static DirectorySecurity Security(', checks)
        self.assertIn('// All fixture masks', checks)
        helper = checks[checks.index('static DirectorySecurity Security('):checks.index('// All fixture masks')]
        self.assertIn('params int[] expectedMasks', helper)
        self.assertIn('new RawSecurityDescriptor(result.GetSecurityDescriptorBinaryForm(), 0)', helper)
        self.assertIn('descriptor.DiscretionaryAcl.Count == expectedMasks.Length', helper)
        self.assertIn('((KnownAce)descriptor.DiscretionaryAcl[i]).AccessMask == expectedMasks[i]', helper)
        self.assertLess(helper.index('.AccessMask =='), helper.index('return result;'))
        self.assertIn('Security(safeAncestor, fullControl, fullControl, readExecute, fullControl)', checks)

    def test_file_right_fixtures_do_not_confuse_sddl_tokens(self):
        checks = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        for sddl, right in (
            ('0x2', 'WriteData'), ('0x40', 'DeleteSubdirectoriesAndFiles'),
            ('0x10000', 'Delete'), ('0x40000', 'ChangePermissions'),
            ('0x80000', 'TakeOwnership'),
        ):
            self.assertIn(f'("O:BAG:BAD:P(A;;{sddl};;;BU)", (int)FileSystemRights.{right})', checks)
        self.assertIn('("O:BAG:BAD:P(A;;0x2;;;AU)", (int)FileSystemRights.WriteData)', checks)
        self.assertNotIn('"O:BAG:BAD:P(A;;DC;;;BU)", // delete children', checks)
        self.assertNotIn('(A;;WD;;;AU)', checks)
        self.assertIn('Security("O:BAG:BAD:P(A;;DC;;;BU)", (int)FileSystemRights.WriteData)', checks)
        self.assertIn('Security("O:BAG:BAD:P(A;;WD;;;BU)", (int)FileSystemRights.ChangePermissions)', checks)

    def test_junction_fixture_uses_cmd_quoting_and_spaces(self):
        source = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        fixture = source[source.index('string root ='):source.index('CheckStartupSecurity();')]
        self.assertNotIn('ArgumentList', fixture)
        self.assertIn('Arguments = $"/d /c \\"mklink /J \\"{junction}\\" \\"{target}\\"\\""', fixture)
        self.assertIn('"target with spaces"', fixture)
        self.assertIn('"junction with spaces"', fixture)
        self.assertIn('(junctionInfo.Attributes & FileAttributes.ReparsePoint) != 0', fixture)
        self.assertLess(fixture.index('junctionInfo.Attributes'), fixture.index('IsProtectedEntry(junctionInfo)'))

    def test_independent_pid_removal_refreshes_activity(self):
        source = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        removal = source.index('engine.SetProcessLimits(owner.Path, owner.ProcessId, 0, 0);')
        self.assertIn('engine.RecordSent(owner, 1, false);', source[removal - 100:removal])
        self.assertIn('Inactive PID history was not pruned', source)


if __name__ == '__main__':
    unittest.main()
