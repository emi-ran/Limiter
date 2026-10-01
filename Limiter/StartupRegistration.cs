using System.IO;
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
