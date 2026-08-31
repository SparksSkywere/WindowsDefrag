using System.IO;
using System.Management;
using WindowsDefrag.Models;
using WindowsDefrag.Native;

namespace WindowsDefrag.Services;

public sealed class DriveInventoryService
{
    private readonly LogService _log;

    public DriveInventoryService(LogService log)
    {
        _log = log;
    }

    public Task<List<DriveInfoModel>> QueryAsync(bool showRemovable, CancellationToken cancellationToken = default) =>
        Task.Run(() => Query(showRemovable), cancellationToken);

    public List<DriveInfoModel> Query(bool showRemovable)
    {
        var physical = QueryPhysical();
        var result = new List<DriveInfoModel>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady)
                    continue;
                if (d.DriveType is DriveType.Network or DriveType.Ram)
                    continue;
                if (d.DriveType == DriveType.CDRom)
                    continue;
                if (d.DriveType == DriveType.Removable && !showRemovable)
                    continue;

                var letter = char.ToUpperInvariant(d.Name[0]);
                physical.TryGetValue(letter, out var phys);

                VolumeSession.QuerySeekPenalty(letter, out var seekPenalty);
                VolumeSession.QueryTrim(letter, out var trim);

                var media = Classify(d, phys, seekPenalty);
                var dirty = VolumeSession.IsVolumeDirty(letter);

                uint clusterSize = 0;
                long totalClusters = 0;
                try
                {
                    GetDiskFreeSpace(d.RootDirectory.FullName, out var sectorsPerCluster, out var bytesPerSector, out _, out var total);
                    clusterSize = sectorsPerCluster * bytesPerSector;
                    totalClusters = total;
                }
                catch
                {
                    clusterSize = 4096;
                }

                result.Add(new DriveInfoModel
                {
                    Letter = letter,
                    RootPath = d.RootDirectory.FullName,
                    Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel,
                    FileSystem = d.DriveFormat ?? "",
                    TotalSize = d.TotalSize,
                    FreeSpace = d.AvailableFreeSpace,
                    Media = media,
                    MediaText = MediaText(media),
                    BusType = phys.Bus ?? "",
                    DiskModel = phys.Model ?? "",
                    DiskNumber = phys.Number,
                    ClusterSize = clusterSize,
                    TotalClusters = totalClusters,
                    FreeClusters = clusterSize > 0 ? d.AvailableFreeSpace / clusterSize : 0,
                    TrimSupported = trim || media is MediaKind.Ssd or MediaKind.Nvme,
                    IncursSeekPenalty = seekPenalty,
                    IsNtfs = string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                    IsDirty = dirty,
                    IsBitLocker = IsBitLocker(letter),
                    IsSystem = letter == Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?[0],
                    IsReady = true,
                    IsRemovable = d.DriveType == DriveType.Removable
                });
            }
            catch (Exception ex)
            {
                _log.Warn($"Skipped drive {d.Name}: {ex.Message}");
            }
        }

        _log.Info($"Inventory: {result.Count} volume(s).");
        return result.OrderBy(v => v.Letter).ToList();
    }

    private static MediaKind Classify(DriveInfo d, PhysicalDisk phys, bool seekPenalty)
    {
        if (d.DriveType == DriveType.Removable)
            return MediaKind.Usb;
        if (d.DriveType == DriveType.CDRom)
            return MediaKind.Optical;
        if (!string.IsNullOrEmpty(phys.Bus) &&
            phys.Bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
            return MediaKind.Nvme;
        if (phys.MediaType == 4)
            return MediaKind.Ssd;
        if (phys.MediaType == 3)
            return MediaKind.Hdd;
        if (phys.SpindleSpeed is 0 or 1 && phys.MediaType == 0 && !seekPenalty)
            return MediaKind.Ssd;
        if (!seekPenalty)
            return MediaKind.Ssd;
        if (!string.IsNullOrEmpty(phys.Bus) &&
            (phys.Bus.Contains("USB", StringComparison.OrdinalIgnoreCase)))
            return MediaKind.Usb;
        if (!string.IsNullOrEmpty(phys.Bus) &&
            (phys.Bus.Contains("Virtual", StringComparison.OrdinalIgnoreCase)))
            return MediaKind.Virtual;
        return MediaKind.Hdd;
    }

    private static string MediaText(MediaKind kind) => kind switch
    {
        MediaKind.Ssd => "SSD",
        MediaKind.Nvme => "NVMe",
        MediaKind.Usb => "USB",
        MediaKind.Optical => "Optical",
        MediaKind.Virtual => "Virtual",
        MediaKind.Hdd => "HDD",
        _ => "—"
    };

    private readonly struct PhysicalDisk
    {
        public int Number { get; init; }
        public string? Model { get; init; }
        public string? Bus { get; init; }
        public ushort MediaType { get; init; }
        public uint SpindleSpeed { get; init; }
    }

    private Dictionary<char, PhysicalDisk> QueryPhysical()
    {
        var map = new Dictionary<char, PhysicalDisk>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            var disks = new Dictionary<int, PhysicalDisk>();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var deviceId = Convert.ToInt32(mo["DeviceId"] ?? -1);
                        var bus = Convert.ToUInt16(mo["BusType"] ?? 0);
                        disks[deviceId] = new PhysicalDisk
                        {
                            Number = deviceId,
                            Model = Convert.ToString(mo["Model"]) ?? Convert.ToString(mo["FriendlyName"]),
                            Bus = BusName(bus),
                            MediaType = Convert.ToUInt16(mo["MediaType"] ?? 0),
                            SpindleSpeed = Convert.ToUInt32(mo["SpindleSpeed"] ?? 0)
                        };
                    }
                }
            }

            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var letterObj = mo["DriveLetter"];
                        if (letterObj is null)
                            continue;
                        var letter = Convert.ToString(letterObj);
                        if (string.IsNullOrEmpty(letter))
                            continue;
                        var ch = char.ToUpperInvariant(letter[0]);
                        var diskNumber = Convert.ToInt32(mo["DiskNumber"] ?? -1);
                        if (disks.TryGetValue(diskNumber, out var phys))
                            map[ch] = phys;
                        else
                            map[ch] = new PhysicalDisk { Number = diskNumber };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Storage query failed: " + ex.Message);
        }

        return map;
    }

    private static string BusName(ushort bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        14 => "Virtual",
        15 => "Virtual",
        _ => ""
    };

    private static bool IsBitLocker(char letter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{letter}:'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                    return Convert.ToInt32(mo["ProtectionStatus"] ?? 0) > 0;
            }
        }
        catch
        {
            // namespace missing
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);
}
