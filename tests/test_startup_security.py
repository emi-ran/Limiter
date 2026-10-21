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
        self.assertIn('Require(StartupRegistration.IsProtectedSecurity(safeAncestorSecurity),', checks)
        self.assertIn('Security(unsafeAncestor.Sddl, unsafeAncestor.Mask), ancestor: true', checks)

    def test_creator_owner_exception_is_narrow_and_after_owner_validation(self):
        source = (ROOT / 'Limiter/StartupRegistration.cs').read_text()
        policy = source[source.index('internal static bool IsProtectedSecurity('):source.index('// Preserve another installation')]
        self.assertIn('descriptor.Owner is null || !IsTrustedInstallerIdentity(descriptor.Owner)', policy)
        self.assertIn('((SecurityIdentifier)rule.IdentityReference).Value == "S-1-3-0" &&', policy)
        self.assertIn('(rule.PropagationFlags & PropagationFlags.InheritOnly) != 0', policy)
        self.assertLess(policy.index('!IsTrustedInstallerIdentity(descriptor.Owner)'), policy.index('"S-1-3-0"'))
        self.assertIn('if (!IsProtectedEntry(entry)) return false;', source)
        self.assertIn('if (entry is DirectoryInfo child && !IsProtectedTree(child)) return false;', source)
        checks = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        self.assertIn('const string installedDirectorySddl = "O:SYD:AI', checks)
        self.assertIn('(A;OICIIOID;GA;;;CO)', checks)
        self.assertIn('Security(installedDirectorySddl, installedDirectoryMasks)', checks)
        self.assertIn('installedDirectorySddl.Replace("O:SY", "O:BU")', checks)
        self.assertIn('installedDirectorySddl.Replace("OICIIOID;GA;;;CO", "OICIID;GA;;;CO")', checks)
        self.assertIn('installedDirectorySddl.Replace("GA;;;CO", "GA;;;BU")', checks)
        self.assertIn('("O:BAG:BAD:P(A;;0x1f01ff;;;CO)", fullControl)', checks)
        self.assertIn('("O:BAG:BAD:P(A;OICIIOID;0x1f01ff;;;BU)", fullControl)', checks)
        self.assertIn('safeAncestor.Replace(";;;CO", ";;;BU")', checks)

    def test_fixture_masks_are_checked_before_policy(self):
        checks = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        self.assertIn('static DirectorySecurity Security(', checks)
        self.assertIn('// All fixture masks', checks)
        helper = checks[checks.index('static DirectorySecurity Security('):checks.index('// All fixture masks')]
        self.assertIn('params int[] expectedMasks', helper)
        self.assertIn('var descriptor = new RawSecurityDescriptor(sddl);', helper)
        self.assertIn('descriptor.DiscretionaryAcl.Count == expectedMasks.Length', helper)
        self.assertIn('ace.AccessMask == expectedMasks[i]', helper)
        self.assertLess(helper.index('ace.AccessMask =='), helper.index('result.SetSecurityDescriptorSddlForm(sddl)'))
        self.assertIn('expected identity={ace.SecurityIdentifier}', helper)
        self.assertIn('actual identity={ace.SecurityIdentifier}', helper)
        self.assertIn('expectedMasks[i]:x8', helper)
        self.assertIn('ace.AccessMask:x8', helper)
        self.assertIn('new RawSecurityDescriptor(result.GetSecurityDescriptorBinaryForm(), 0)', helper)
        self.assertLess(helper.index('expectedAces.SequenceEqual(actualAces)'), helper.index('return result;'))

    def test_normalized_fixture_preserves_identity_mask_flags_multiset(self):
        checks = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        helper = checks[checks.index('static DirectorySecurity Security('):checks.index('// All fixture masks')]
        self.assertIn('static string AceIdentity(GenericAce ace)', helper)
        self.assertIn('known.SecurityIdentifier', helper)
        self.assertIn('known.AccessMask:x8', helper)
        self.assertIn('ace.AceFlags', helper)
        self.assertIn('ace.AceType', helper)
        self.assertIn('static string[] AceMultiset(RawAcl? acl)', helper)
        self.assertIn('.Select(AceIdentity).OrderBy(identity => identity, StringComparer.Ordinal).ToArray()', helper)
        self.assertNotIn('.Distinct(', helper)  # Duplicate ACE counts must not be discarded.
        self.assertIn('var expectedAces = AceMultiset(descriptor.DiscretionaryAcl);', helper)
        self.assertIn('var actualAces = AceMultiset(normalized.DiscretionaryAcl);', helper)
        self.assertIn('expectedAces.SequenceEqual(actualAces)', helper)
        self.assertIn('expected identity/mask/flags=', helper)
        self.assertIn('actual identity/mask/flags=', helper)
        self.assertIn('(descriptor.DiscretionaryAcl is null) == (normalized.DiscretionaryAcl is null)', helper)
        self.assertIn('Security(safeAncestor, fullControl, fullControl, readExecute, fullControl)', checks)

    def test_fixture_order_contract_keeps_safe_ancestor_trustees_paired(self):
        checks = (ROOT / 'Limiter.EngineChecks/Program.cs').read_text()
        self.assertIn('(A;OICI;0x1200a9;;;BU)(A;OICIIO;0x1f01ff;;;CO)', checks)
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
