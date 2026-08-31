using CommunityToolkit.Mvvm.ComponentModel;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;

namespace WindowsDefrag.ViewModels;

public partial class DriveViewModel : ObservableObject
{
    public DriveViewModel(DriveInfoModel info)
    {
        ApplyInfo(info);
    }

    public DriveInfoModel Info { get; private set; } = null!;

    public char Letter => Info.Letter;
    public string Volume => $"{Info.Letter}:";
    public string RootPath => Info.RootPath;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _fileSystem = "";
    [ObservableProperty] private string _mediaText = "";
    [ObservableProperty] private MediaKind _media;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private double _progressRemaining = 100;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private long _fragmentedFiles;
    [ObservableProperty] private long _totalFragments;
    [ObservableProperty] private double _fragmentationPercent;
    [ObservableProperty] private long _totalSize;
    [ObservableProperty] private long _freeSpace;
    [ObservableProperty] private string _currentFile = "";
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private bool _isSolidState;
    [ObservableProperty] private bool _isNtfs;
    [ObservableProperty] private bool _trimSupported;
    [ObservableProperty] private ClusterKind[] _clusterMap = [];
    [ObservableProperty] private long _clustersPerBlock = 1;
    [ObservableProperty] private AnalysisResult? _analysis;
    [ObservableProperty] private DriveJobStatus _jobStatus = DriveJobStatus.Idle;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _freeText = "";
    [ObservableProperty] private string _fragPercentText = "—";
    [ObservableProperty] private string _usedPercentText = "";

    public double UsedPercent =>
        TotalSize <= 0 ? 0 : Math.Clamp((TotalSize - FreeSpace) * 100.0 / TotalSize, 0, 100);

    public double FreePercent => 100 - UsedPercent;

    public void ApplyInfo(DriveInfoModel info)
    {
        Info = info;
        Label = info.Label;
        FileSystem = info.FileSystem;
        MediaText = info.MediaText;
        Media = info.Media;
        TotalSize = info.TotalSize;
        FreeSpace = info.FreeSpace;
        SizeText = ByteSizeFormatter.Format(info.TotalSize);
        FreeText = ByteSizeFormatter.Format(info.FreeSpace);
        IsSolidState = info.IsSolidState;
        IsNtfs = info.IsNtfs;
        TrimSupported = info.TrimSupported;
        HealthText = BuildHealth(info);
        UsedPercentText = $"{UsedPercent:0.#}% used";
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(UsedPercent));
        OnPropertyChanged(nameof(FreePercent));
    }

    public void ApplyAnalysis(AnalysisResult analysis)
    {
        Analysis = analysis;
        FragmentedFiles = analysis.FragmentedFiles;
        TotalFragments = analysis.TotalFragments;
        FragmentationPercent = analysis.FragmentationPercent;
        FragPercentText = $"{analysis.FragmentationPercent:0.#}%";
        ApplyLiveMap(analysis.Map, analysis.ClustersPerBlock);
        Status = analysis.FragmentedFiles == 0 ? "OK" : $"{analysis.FragmentedFiles:N0} fragmented";
        CurrentFile = "";
        SetProgress(0);
    }

    public void ApplyLiveMap(ClusterKind[] map, long clustersPerBlock)
    {
        if (map.Length == 0)
            return;
        ClusterMap = map;
        ClustersPerBlock = Math.Max(1, clustersPerBlock);
    }

    public void SetBusy(DriveJobStatus job, string status, string current, double progress)
    {
        JobStatus = job;
        Status = progress > 0 ? $"{status} {progress:0}%" : status;
        CurrentFile = current;
        SetProgress(progress);
    }

    public void SetIdle(string status = "Ready")
    {
        JobStatus = DriveJobStatus.Idle;
        Status = status;
        CurrentFile = "";
        SetProgress(0);
    }

    public void SetProgress(double value)
    {
        var pct = Math.Clamp(value, 0, 100);
        Progress = pct;
        ProgressRemaining = Math.Max(0.1, 100 - pct);
        ProgressText = pct > 0 ? $"{pct:0}%" : "";
    }

    private static string BuildHealth(DriveInfoModel info)
    {
        if (info.IsDirty) return "Dirty";
        if (info.IsBitLocker) return "BitLocker";
        if (info.IsSolidState) return info.TrimSupported ? "SSD · TRIM" : "SSD";
        return "HDD";
    }
}
