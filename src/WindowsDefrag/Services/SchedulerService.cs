using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using WindowsDefrag.Models;

namespace WindowsDefrag.Services;

public sealed class SchedulerService
{
    public static string TaskName => AppInfo.ScheduledTaskName;
    private readonly LogService _log;

    public SchedulerService(LogService log)
    {
        _log = log;
    }

    public bool CreateDailyTask(ScheduleSettings schedule)
    {
        try
        {
            var exe = Environment.ProcessPath ??
                       Path.Combine(AppContext.BaseDirectory, AppInfo.ExeFileName);
            var start = DateTime.Today.Add(new TimeSpan(schedule.Hour, schedule.Minute, 0));
            if (start < DateTime.Now)
                start = start.AddDays(1);

            var letters = string.IsNullOrWhiteSpace(schedule.DriveLetters)
                ? "ALL"
                : schedule.DriveLetters.Replace(" ", "");
            var args = $"--defrag {letters} --method {schedule.Method} --no-ui";
            var xml = BuildTaskXml(exe, args, start);
            var temp = Path.Combine(Path.GetTempPath(), $"wd_task_{Guid.NewGuid():N}.xml");
            File.WriteAllText(temp, xml, System.Text.Encoding.Unicode);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Create /TN \"{TaskName}\" /XML \"{temp}\" /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                    return false;

                proc.WaitForExit(30_000);
                var ok = proc.ExitCode == 0;
                if (ok)
                    _log.Success($"Scheduled task '{TaskName}' created.");
                else
                    _log.Error("Failed to create scheduled task: " + proc.StandardError.ReadToEnd());
                return ok;
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Scheduler error: " + ex.Message);
            return false;
        }
    }

    public bool RemoveTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15_000);
            var ok = proc is { ExitCode: 0 };
            if (ok) _log.Info("Scheduled task removed.");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Error("Scheduler remove: " + ex.Message);
            return false;
        }
    }

    public bool TaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            return proc is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static string BuildTaskXml(string exe, string args, DateTime start)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", AppInfo.ProductName + " scheduled defragmentation")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", start.ToString("s")),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "ScheduleByDay",
                            new XElement(ns + "DaysInterval", "1")))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "true"),
                    new XElement(ns + "StopIfGoingOnBatteries", "true"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT8H"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions",
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", exe),
                        new XElement(ns + "Arguments", args)))));

        return doc.ToString();
    }
}
