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
    internal static void SetEnabled(bool enabled)
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                if (!enabled)
                {
                    try { folder.DeleteTask(TaskName, 0); }
                    catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                    return;
                }
                string path = Environment.ProcessPath!;
                if (!string.Equals(Path.GetFileName(path), "Limiter.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Localization.T("startup-executable-required"));
                string user = WindowsIdentity.GetCurrent().Name;
                dynamic definition = CreateDefinition(service, path, user);
                try
                {
                    dynamic task = folder.RegisterTaskDefinition(TaskName, definition, 6, user, null, 3);
                    Marshal.FinalReleaseComObject(task);
                }
                finally { Marshal.FinalReleaseComObject(definition); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}
