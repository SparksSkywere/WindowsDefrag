using System.Reflection;

namespace WindowsDefrag;

/// <summary>Product branding and version metadata for Windows Defrag.</summary>
public static class AppInfo
{
    public const string ProductName = "Windows Defrag";
    public const string ProductNameShort = "Defrag";
    public const string Company = "Skywere Industries";
    public const string Copyright = "Copyright © Skywere Industries";
    public const string Description =
        "Analyse and defragment hard disks, TRIM solid-state drives, and inspect how files " +
        "sit on disk — with SSD detection, locked-file skips, and battery/free-space guards.";

    public const string ExeFileName = "WindowsDefrag.exe";
    public const string InstallFolderName = "WindowsDefrag";
    public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsDefrag";
    public const string AppDataFolderName = "WindowsDefrag";
    public const string CompanyFolderName = "Skywere Industries";
    public const string ScheduledTaskName = "Windows Defrag";

    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static string AboutText =>
        $"{ProductName}\n" +
        $"Version {Version}\n\n" +
        $"{Description}\n\n" +
        $"{Copyright}\n" +
        $"Created by {Company}";
}
