using System.IO;
using System.Management;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;
using WindowsDefrag.Native;

namespace WindowsDefrag.Services;

public sealed class ProtectionService
{
    private readonly ConfigService _config;

    public ProtectionService(ConfigService config)
    {
        _config = config;
    }

    public (bool Allowed, string Reason) CanAnalyze(DriveInfoModel drive)
    {
        if (!drive.IsReady)
            return (false, "Volume is not ready.");
        if (drive.Media == MediaKind.Optical)
            return (false, "Optical drives cannot be analyzed.");
        return (true, "");
    }

    public (bool Allowed, string Reason) CanDefrag(DriveInfoModel drive, bool elevated)
    {
        if (!elevated)
            return (false, "Moving files requires administrator rights.");
        if (!drive.IsReady)
            return (false, "Volume is not ready.");
        if (!drive.IsNtfs)
            return (false, "Defragmentation is only supported on NTFS.");
        if (drive.IsDirty)
            return (false, "Volume is marked dirty. Run Check drive before defragmenting.");
        if (drive.Media == MediaKind.Optical)
            return (false, "Optical drives cannot be defragmented.");
        if (drive.IsRemovable && !_config.Config.General.ShowRemovable)
            return (false, "Removable drives are hidden in Options.");

        if (drive.IsSolidState && _config.Config.Protection.BlockSsdDefrag)
            return (false, "This is an SSD/NVMe drive. Use TRIM instead of classic defragmentation.");

        if (_config.Config.Protection.SkipOnBattery && IsOnBattery())
            return (false, "Running on battery. Plug in or disable the battery guard in Options.");

        var freePct = drive.TotalSize <= 0 ? 100.0 : drive.FreeSpace * 100.0 / drive.TotalSize;
        if (freePct < _config.Config.Protection.MinFreePercent)
            return (false, $"Not enough free space ({freePct:0.#}% free, need {_config.Config.Protection.MinFreePercent}%).");

        return (true, "");
    }

    public (bool Allowed, string Reason) CanTrim(DriveInfoModel drive, bool elevated)
    {
        if (!elevated)
            return (false, "TRIM requires administrator rights.");
        if (!drive.IsReady)
            return (false, "Volume is not ready.");
        if (!drive.IsSolidState && !drive.TrimSupported)
            return (false, "This drive does not report TRIM support.");
        return (true, "");
    }

    public bool IsProtectedFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            return true;

        if (_config.Config.Protection.SkipPagefile &&
            (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (_config.Config.Protection.SkipHibernation &&
            name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("DumpStack.log.tmp", StringComparison.OrdinalIgnoreCase))
            return true;

        if (PathHelper.IsExcluded(path, _config.Config.Protection.ExcludePaths))
            return true;

        return false;
    }

    public static bool IsUnmovableName(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            return true;
        if (name.StartsWith('$'))
            return true;
        if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static bool IsOnBattery()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT BatteryStatus FROM Win32_Battery");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    // 1 = discharging, 4 = low, 5 = critical
                    var status = Convert.ToInt32(mo["BatteryStatus"] ?? 0);
                    if (status is 1 or 4 or 5)
                        return true;
                }
            }
        }
        catch
        {
            // no battery or WMI blocked
        }

        return false;
    }
}
