using System.IO;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Limiter;

internal static class StartupRegistration
{
    internal static string TaskName => "Limiter.Startup." + WindowsIdentity.GetCurrent().User!.Value;
    private static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service;
    }
    internal static bool IsEnabled()
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                dynamic task = folder.GetTask(TaskName);
                try { return task.Enabled; }
                finally { Marshal.FinalReleaseComObject(task); }
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return false; }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    // Startup executes with administrator rights, so only an administrator-protected
    // Program Files installation may register it.
    internal static bool CanRegisterCurrentExecutable()
    {
        string? path = Environment.ProcessPath;
        return path is not null &&
            string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "Limiter" &&
            HasProtectedInstallFiles(path);
    }

    internal static bool IsProtectedInstallPath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles)) return false;
            string root = Path.GetFullPath(programFiles)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Lexical containment above is deliberately usable by path-only checks. Never
    // use it alone to authorize an elevated task. Unknown ACLs fail closed.
    internal static bool HasProtectedInstallFiles(string? path)
    {
        if (!OperatingSystem.IsWindows() || !IsProtectedInstallPath(path)) return false;
        try
        {
            var executable = new FileInfo(Path.GetFullPath(path!));
            if (!executable.Exists || !IsProtectedEntry(executable)) return false;
            var install = executable.Directory!;
            if (!IsProtectedEntry(install)) return false;
            for (DirectoryInfo? parent = install.Parent; parent is not null; parent = parent.Parent)
                if (!IsProtectedEntry(parent, ancestor: true)) return false;
            // Protect dependencies as well as the executable, including nested native
            // runtime directories. Never recurse through a reparse point.
            return IsProtectedTree(install);
        }
        catch { return false; }
    }

    // Read-only failure context; never used to authorize registration. Preserve the
    // exact fail-closed policy above and identify the first rejected entry/ACL.
    internal static string DescribeInstallProtectionFailure()
    {
        try
        {
            string? path = Environment.ProcessPath;
            string context = $"Process={path}; Identity={WindowsIdentity.GetCurrent().User?.Value}";
            if (!IsProtectedInstallPath(path)) return context + "; outside Program Files";
            var executable = new FileInfo(Path.GetFullPath(path!));
            if (!executable.Exists) return context + "; executable missing";

            static string? Rejection(FileSystemInfo entry, bool ancestor = false)
            {
                if (IsProtectedEntry(entry, ancestor)) return null;
                try
                {
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return $"Rejected reparse point: {entry.FullName}";
                    FileSystemSecurity security = entry is DirectoryInfo directory
                        ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                        : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                    return $"Rejected entry={entry.FullName}; ancestor={ancestor}; attributes={attributes}; " +
                        security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
                }
                catch (Exception ex) { return $"Cannot inspect {entry.FullName}: {ex}"; }
            }

            static string? TreeRejection(DirectoryInfo directory)
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    string? reason = Rejection(entry);
                    if (reason is not null) return reason;
                    // Rejection checks reparse points before any recursion.
                    if (entry is DirectoryInfo child)
                    {
                        reason = TreeRejection(child);
                        if (reason is not null) return reason;
                    }
                }
                return null;
            }

            string? failure = Rejection(executable) ?? Rejection(executable.Directory!);
            for (DirectoryInfo? parent = executable.Directory!.Parent; failure is null && parent is not null; parent = parent.Parent)
                failure = Rejection(parent, ancestor: true);
            failure ??= TreeRejection(executable.Directory!);
            return context + "\n" + (failure ?? "Install protection currently passes.");
        }
        catch (Exception ex) { return $"Protection diagnostic failed: {ex}"; }
    }

    private static bool IsProtectedTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (!IsProtectedEntry(entry)) return false;
            if (entry is DirectoryInfo child && !IsProtectedTree(child)) return false;
        }
        return true;
    }

    private static bool IsTrustedInstallerIdentity(SecurityIdentifier sid)
        => sid.Value is "S-1-5-18" or "S-1-5-32-544" or
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    internal static bool IsProtectedEntry(FileSystemInfo entry, bool ancestor = false)
    {
        try
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            FileSystemSecurity security = entry is DirectoryInfo directory
                ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return IsProtectedSecurity(security, ancestor);
        }
        catch { return false; }
    }

    internal static bool IsProtectedSecurity(FileSystemSecurity security, bool ancestor = false)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null) return false;
        if (descriptor.Owner is null || !IsTrustedInstallerIdentity(descriptor.Owner)) return false;
        // Conservative allow-list: do not try to infer token membership or allow a
        // deny ACE to cancel an unsafe grant. Above the install, only effective
        // replacement rights matter; within it, inherit-only grants matter to DLLs.
        const FileSystemRights replacement = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x10000000;
        // Include unmapped GENERIC_WRITE / GENERIC_ALL masks in supplied ACLs.
        var mutation = replacement | FileSystemRights.Write | (FileSystemRights)0x50000000;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                (!ancestor || (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0) &&
                !IsTrustedInstallerIdentity((SecurityIdentifier)rule.IdentityReference) &&
                (rule.FileSystemRights & (ancestor ? replacement : mutation)) != 0)
                return false;
        return true;
    }

    // Preserve another installation's task and all existing task settings.
    internal static void MigrateCurrentExecutable()
    {
        if (!CanRegisterCurrentExecutable())
        {
            RemoveForInstalledExecutable();
            return;
        }
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                dynamic task;
                try { task = folder.GetTask(TaskName); }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return; }
                try
                {
                    if (!(bool)task.Enabled) return;
                    dynamic definition = task.Definition;
                    try
                    {
                        dynamic actions = definition.Actions;
                        try
                        {
                            if (actions.Count != 1) return;
                            dynamic action = actions[1];
                            try
                            {
                                if ((int)action.Type != 0) return;
                                string actionPath = (string)action.Path;
                                if (!string.Equals(actionPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Remove elevated startup for a portable or deleted copy.
                                    if (!HasProtectedInstallFiles(actionPath) || !File.Exists(actionPath))
                                        folder.DeleteTask(TaskName, 0);
                                    return;
                                }
                                if (!string.IsNullOrWhiteSpace((string)action.Arguments)) return;
                                action.Arguments = "--tray";
                            }
                            finally { Marshal.FinalReleaseComObject(action); }
                        }
                        finally { Marshal.FinalReleaseComObject(actions); }
                        dynamic updated = folder.RegisterTaskDefinition(TaskName, definition, 6,
                            WindowsIdentity.GetCurrent().User!.Value, null, 3);
                        Marshal.FinalReleaseComObject(updated);
                    }
                    finally { Marshal.FinalReleaseComObject(definition); }
                }
                finally { Marshal.FinalReleaseComObject(task); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    private static dynamic CreateDefinition(dynamic service, string path, string user)
    {
        dynamic definition = service.NewTask(0);
        try
        {
            definition.RegistrationInfo.Description = "Start Limiter when its owner signs in.";
            definition.Principal.UserId = user;
            definition.Principal.LogonType = 3;
            definition.Principal.RunLevel = 1;
            definition.Settings.Enabled = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2;
            dynamic trigger = definition.Triggers.Create(9);
            trigger.UserId = user;
            trigger.Delay = "PT5S";
            Marshal.FinalReleaseComObject(trigger);
            dynamic action = definition.Actions.Create(0);
            action.Path = path;
            action.Arguments = "--tray";
            action.WorkingDirectory = Path.GetDirectoryName(path);
            Marshal.FinalReleaseComObject(action);
            return definition;
        }
        catch { Marshal.FinalReleaseComObject(definition); throw; }
    }
    internal static string DefinitionXml(string path, string user)
    {
        dynamic service = Connect();
        try
        {
            dynamic definition = CreateDefinition(service, path, user);
            try { return definition.XmlText; }
            finally { Marshal.FinalReleaseComObject(definition); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    // Only this user's Limiter task is changed. InteractiveToken never stores a password.
    internal static void EnableForUser(string userSid)
    {
        var sid = new SecurityIdentifier(userSid);
        if (!sid.IsAccountSid()) throw new ArgumentException("An account SID is required.");
        SetEnabled(true, sid.Value);
    }
    // Move an existing per-user task to the newly installed executable so an
    // upgrade does not leave an elevated task pointing at a removed copy.
    internal static void AdoptForUser(string userSid)
    {
        var sid = new SecurityIdentifier(userSid);
        if (!sid.IsAccountSid()) throw new ArgumentException("An account SID is required.");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException(Localization.T("startup-executable-required"));
        if (!HasProtectedInstallFiles(executable)) throw new InvalidOperationException(Localization.T("startup-requires-program-files"));
        string taskName = "Limiter.Startup." + sid.Value;
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                dynamic task;
                try { task = folder.GetTask(taskName); }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return; }
                try
                {
                    dynamic definition = task.Definition;
                    try
                    {
                        dynamic actions = definition.Actions;
                        try
                        {
                            if (actions.Count != 1) return;
                            dynamic action = actions[1];
                            try
                            {
                                if ((int)action.Type != 0) return;
                                action.Path = executable;
                                action.Arguments = "--tray";
                                action.WorkingDirectory = Path.GetDirectoryName(executable);
                            }
                            finally { Marshal.FinalReleaseComObject(action); }
                        }
                        finally { Marshal.FinalReleaseComObject(actions); }
                        dynamic updated = folder.RegisterTaskDefinition(taskName, definition, 6, sid.Value, null, 3);
                        Marshal.FinalReleaseComObject(updated);
                    }
                    finally { Marshal.FinalReleaseComObject(definition); }
                }
                finally { Marshal.FinalReleaseComObject(task); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    // Uninstall removes only Limiter tasks targeting this installed executable.
    internal static void RemoveForInstalledExecutable()
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                dynamic tasks = folder.GetTasks(0);
                var names = new List<string>();
                try
                {
                    for (int i = 1; i <= tasks.Count; i++)
                    {
                        dynamic task = tasks[i];
                        try
                        {
                            string name = task.Name;
                            if (!name.StartsWith("Limiter.Startup.", StringComparison.Ordinal)) continue;
                            var xml = System.Xml.Linq.XDocument.Parse((string)task.Xml);
                            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                            string? command = xml.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value;
                            if (string.Equals(command, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) names.Add(name);
                        }
                        finally { Marshal.FinalReleaseComObject(task); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(tasks); }
                foreach (string name in names) folder.DeleteTask(name, 0);
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    internal static void SetEnabled(bool enabled)
        => SetEnabled(enabled, WindowsIdentity.GetCurrent().User!.Value);

    private static void SetEnabled(bool enabled, string user)
    {
        string taskName = "Limiter.Startup." + user;
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                if (!enabled)
                {
                    try { folder.DeleteTask(taskName, 0); }
                    catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                    return;
                }
                string path = Environment.ProcessPath!;
                if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name != "Limiter" ||
                    !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Localization.T("startup-executable-required"));
                if (!HasProtectedInstallFiles(path))
                    throw new InvalidOperationException(Localization.T("startup-requires-program-files"));
                dynamic definition = CreateDefinition(service, path, user);
                try
                {
                    dynamic task = folder.RegisterTaskDefinition(taskName, definition, 6, user, null, 3);
                    Marshal.FinalReleaseComObject(task);
                }
                finally { Marshal.FinalReleaseComObject(definition); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}
