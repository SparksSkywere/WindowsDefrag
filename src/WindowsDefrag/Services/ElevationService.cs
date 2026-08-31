using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace WindowsDefrag.Services;

public sealed class ElevationService
{
    public const string SkipElevateArg = "--no-elevate";
    public const string ElevatingArg = "--elevating";

    private readonly LogService _log;

    public ElevationService(LogService log)
    {
        _log = log;
    }

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryRelaunchElevated(string[] args)
    {
        if (IsElevated)
            return false;

        if (args.Any(a => a.Equals(SkipElevateArg, StringComparison.OrdinalIgnoreCase) ||
                          a.Equals(ElevatingArg, StringComparison.OrdinalIgnoreCase)))
            return false;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return false;

        var forwarded = args
            .Where(a => !a.Equals(ElevatingArg, StringComparison.OrdinalIgnoreCase))
            .Append(ElevatingArg)
            .ToArray();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var arg in forwarded)
            psi.ArgumentList.Add(arg);

        try
        {
            Process.Start(psi);
            _log.Info("Relaunched with administrator privileges.");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _log.Warn("Elevation declined. Analysis still works; moving files needs administrator rights.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn("Elevation failed: " + ex.Message);
            return false;
        }
    }
}
