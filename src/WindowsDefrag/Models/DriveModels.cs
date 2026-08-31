namespace WindowsDefrag.Models;

public enum MediaKind
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
    Usb,
    Optical,
    Virtual
}

public enum DriveJobStatus
{
    Idle,
    Analyzing,
    Defragmenting,
    Trimming,
    Paused,
    Cancelling,
    Error
}

public enum DefragMethod
{
    Stealth,
    Space,
    CompleteName,
    CompleteModified,
    CompleteAccess
}

public enum ClusterKind : byte
{
    Free = 0,
    Allocated = 1,
    Fragmented = 2,
    Directory = 3,
    Compressed = 4,
    System = 5,
    Mft = 6,
    Paging = 7,
    Locked = 8,
    Processing = 9,
    Unmovable = 10
}

public enum BottomTab
{
    ClusterView,
    FileStatus,
    DriveStatus,
    Reports,
    Jobs
}

public sealed class DriveInfoModel
{
    public char Letter { get; init; }
    public string RootPath { get; init; } = "";
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long TotalSize { get; init; }
    public long FreeSpace { get; init; }
    public MediaKind Media { get; init; }
    public string MediaText { get; init; } = "";
    public string BusType { get; init; } = "";
    public string DiskModel { get; init; } = "";
    public int DiskNumber { get; init; } = -1;
    public uint ClusterSize { get; set; }
    public long TotalClusters { get; set; }
    public long FreeClusters { get; set; }
    public long MftZoneStart { get; set; }
    public long MftZoneEnd { get; set; }
    public bool TrimSupported { get; init; }
    public bool IncursSeekPenalty { get; init; }
    public bool IsNtfs { get; init; }
    public bool IsDirty { get; init; }
    public bool IsBitLocker { get; init; }
    public bool IsSystem { get; init; }
    public bool IsReady { get; init; }
    public bool IsRemovable { get; init; }
    public bool IsSolidState => Media is MediaKind.Ssd or MediaKind.Nvme;
}

public sealed class Extent
{
    public long Vcn { get; init; }
    public long Lcn { get; init; }
    public long Clusters { get; init; }
    public bool IsSparse => Lcn < 0;
}

public sealed class FileRecord
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public int Fragments { get; init; }
    public long StartLcn { get; init; }
    public long ClusterCount { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsCompressed { get; init; }
    public DateTime LastWrite { get; init; }
    public DateTime LastAccess { get; init; }
    public List<Extent> Extents { get; init; } = [];
}

public sealed class AnalysisResult
{
    public char Letter { get; init; }
    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;
    public long TotalFiles { get; set; }
    public long DirectoryCount { get; set; }
    public long FragmentedFiles { get; set; }
    public long TotalFragments { get; set; }
    public long SkippedFiles { get; set; }
    public long LockedFiles { get; set; }
    public long FragmentedClusters { get; set; }
    public long AllocatedClusters { get; set; }
    public double FragmentationPercent { get; set; }
    public ClusterKind[] Map { get; set; } = [];
    public long ClustersPerBlock { get; set; } = 1;
    public long TotalClusters { get; set; }
    public uint ClusterSize { get; set; }
    public List<FileRecord> TopFragmented { get; } = [];
    public List<FileRecord> Largest { get; } = [];
    public List<FileRecord> ExtentIndex { get; } = [];
    public List<FileRecord> LayoutFiles { get; } = [];
    public List<FileRecord> AllFragmented { get; } = [];
}

public sealed class JobResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public long FilesProcessed { get; init; }
    public long FilesMoved { get; init; }
    public long FragmentsBefore { get; init; }
    public long FragmentsAfter { get; init; }
    public long FilesFailed { get; init; }
}

public sealed class DefragJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Defrag job";
    public string DriveLetters { get; set; } = "C";
    public DefragMethod Method { get; set; } = DefragMethod.Space;
    public bool TrimSsds { get; set; } = true;
    public string ScheduleText { get; set; } = "Manual";
}

public static class DefragMethodInfo
{
    public static string DisplayName(DefragMethod method) => method switch
    {
        DefragMethod.Stealth => "STEALTH",
        DefragMethod.Space => "SPACE",
        DefragMethod.CompleteName => "COMPLETE/Name",
        DefragMethod.CompleteModified => "COMPLETE/Modified",
        DefragMethod.CompleteAccess => "COMPLETE/Access",
        _ => method.ToString()
    };

    public static string Description(DefragMethod method) => method switch
    {
        DefragMethod.Stealth =>
            "Fast, low-memory pass. Joins fragments of files that are already split. Best on large or nearly full disks.",
        DefragMethod.Space =>
            "Joins file fragments and packs free space together so new files are less likely to split.",
        DefragMethod.CompleteName =>
            "Full rewrite of layout. Places files by folder and name. Slow; for system disks after big installs.",
        DefragMethod.CompleteModified =>
            "Full rewrite of layout. Places files by last-modified time. For data disks.",
        DefragMethod.CompleteAccess =>
            "Full rewrite of layout. Places files by last-access time so recently used data sits together.",
        _ => ""
    };

    public static DefragMethod Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DefragMethod.Space;

        var t = text.Trim();
        if (t.Equals("Stealth", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("STEALTH", StringComparison.OrdinalIgnoreCase))
            return DefragMethod.Stealth;
        if (t.Contains("Name", StringComparison.OrdinalIgnoreCase))
            return DefragMethod.CompleteName;
        if (t.Contains("Modified", StringComparison.OrdinalIgnoreCase))
            return DefragMethod.CompleteModified;
        if (t.Contains("Access", StringComparison.OrdinalIgnoreCase))
            return DefragMethod.CompleteAccess;
        return DefragMethod.Space;
    }

    public static IReadOnlyList<DefragMethod> All { get; } =
    [
        DefragMethod.Stealth,
        DefragMethod.Space,
        DefragMethod.CompleteName,
        DefragMethod.CompleteModified,
        DefragMethod.CompleteAccess
    ];
}
