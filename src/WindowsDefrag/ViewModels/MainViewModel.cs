using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;
using WindowsDefrag.Native;
using WindowsDefrag.Services;

namespace WindowsDefrag.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DriveInventoryService _inventory;
    private readonly AnalysisService _analysis;
    private readonly DefragEngine _engine;
    private readonly TrimService _trim;
    private readonly ProtectionService _protection;
    private readonly ReportService _reports;
    private readonly SchedulerService _scheduler;
    private readonly CheckDiskService _chkdsk;
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly ElevationService _elevation;
    private CancellationTokenSource? _cts;
    private readonly object _pauseLock = new();
    private volatile bool _paused;

    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];
    public ObservableCollection<string> Reports { get; } = [];
    public ObservableCollection<FileRecord> FileStatusItems { get; } = [];
    public IReadOnlyList<DefragMethod> Methods { get; } = DefragMethodInfo.All;
    public IReadOnlyList<string> FileStatusModes { get; } = ["Fragmented", "Largest"];

    [ObservableProperty] private DriveViewModel? _selectedDrive;
    [ObservableProperty] private FileRecord? _selectedFile;
    [ObservableProperty] private DefragMethod _selectedMethod = DefragMethod.Space;
    [ObservableProperty] private BottomTab _selectedTab = BottomTab.ClusterView;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _elevationBanner = "";
    [ObservableProperty] private string _methodDescription = "";
    [ObservableProperty] private string _driveStatusSummary = "Select a volume and click Analyse.";
    [ObservableProperty] private string _clusterCaption = "Cluster view";
    [ObservableProperty] private bool _shadedView = true;
    [ObservableProperty] private int _clusterBlockSize = 8;
    [ObservableProperty] private string _fileStatusMode = "Fragmented";
    [ObservableProperty] private string? _selectedReport;
    [ObservableProperty] private string _legendHint = "Double-click a block to inspect files in that range.";

    public bool CanStop => IsBusy;
    public bool CanPause => IsBusy && !IsPaused;
    public bool CanResume => IsBusy && IsPaused;
    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    public MainViewModel(
        DriveInventoryService inventory,
        AnalysisService analysis,
        DefragEngine engine,
        TrimService trim,
        ProtectionService protection,
        ReportService reports,
        SchedulerService scheduler,
        CheckDiskService chkdsk,
        ConfigService config,
        LogService log,
        ElevationService elevation)
    {
        _inventory = inventory;
        _analysis = analysis;
        _engine = engine;
        _trim = trim;
        _protection = protection;
        _reports = reports;
        _scheduler = scheduler;
        _chkdsk = chkdsk;
        _config = config;
        _log = log;
        _elevation = elevation;

        IsElevated = elevation.IsElevated;
        ElevationBanner = IsElevated
            ? ""
            : "Not running as administrator. Analyse still works; defragment, TRIM, and check/fix need elevation. File → Relaunch as administrator.";
        SelectedMethod = DefragMethodInfo.Parse(config.Config.General.DefaultMethod);
        ShadedView = config.Config.General.ShadedClusterView;
        ClusterBlockSize = Math.Clamp(config.Config.General.ClusterBlockSize, 4, 16);
        MethodDescription = DefragMethodInfo.Description(SelectedMethod);

        _log.MessageLogged += (_, line) => UiThread.Post(() =>
        {
            LogEntries.Add(line);
            while (LogEntries.Count > 800)
                LogEntries.RemoveAt(0);
        });
    }

    partial void OnSelectedMethodChanged(DefragMethod value) =>
        MethodDescription = DefragMethodInfo.Description(value);

    partial void OnSelectedDriveChanged(DriveViewModel? value)
    {
        RefreshFileStatus();
        UpdateDriveStatusSummary();
        UpdateClusterCaption();
        AnalyseCommand.NotifyCanExecuteChanged();
        DefragmentCommand.NotifyCanExecuteChanged();
        TrimCommand.NotifyCanExecuteChanged();
        CheckDriveCommand.NotifyCanExecuteChanged();
    }

    partial void OnFileStatusModeChanged(string value) => RefreshFileStatus();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        AnalyseCommand.NotifyCanExecuteChanged();
        DefragmentCommand.NotifyCanExecuteChanged();
        TrimCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CheckDriveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(PauseLabel));
        PauseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Loaded()
    {
        _log.Info($"{AppInfo.ProductName} {AppInfo.Version} started.");
        PrivilegeHelper.EnableDefragPrivileges();
        await RefreshAsync().ConfigureAwait(true);
        ReloadReports();

        if (_config.Config.General.AnalyzeOnStartup && SelectedDrive is not null && !SelectedDrive.IsSolidState)
            await AnalyseAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync().ConfigureAwait(true);

    private async Task RefreshAsync()
    {
        StatusText = "Reading volumes…";
        try
        {
            var list = await _inventory.QueryAsync(_config.Config.General.ShowRemovable).ConfigureAwait(true);
            var selected = SelectedDrive?.Letter;
            Drives.Clear();
            foreach (var info in list)
                Drives.Add(new DriveViewModel(info));

            SelectedDrive = Drives.FirstOrDefault(d => d.Letter == selected) ?? Drives.FirstOrDefault();
            SummaryText = $"{Drives.Count} volume(s) · {Drives.Count(d => d.IsSolidState)} SSD/NVMe";
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            StatusText = "Failed to read volumes.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunJob))]
    private async Task Analyse() => await AnalyseAsync().ConfigureAwait(true);

    private async Task AnalyseAsync()
    {
        if (SelectedDrive is null) return;
        var (ok, reason) = _protection.CanAnalyze(SelectedDrive.Info);
        if (!ok)
        {
            MessageBox.Show(reason, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunJobAsync(SelectedDrive, DriveJobStatus.Analyzing, "Analysing", async (drive, ct) =>
        {
            var progress = new Progress<AnalysisProgress>(p =>
            {
                drive.SetBusy(DriveJobStatus.Analyzing, "Analysing", p.CurrentPath, p.Percent);
                if (p.Map is { Length: > 0 })
                    drive.ApplyLiveMap(p.Map, p.ClustersPerBlock);
                ProgressValue = p.Percent;
                StatusText =
                    $"Analysing {drive.Volume} — {p.Percent}% of drive  ·  {p.FilesScanned:N0} files  ·  {p.FragmentedFiles:N0} fragmented";
            });

            var result = await Task.Run(
                () => _analysis.Analyze(drive.Info, progress, ct, () => _paused), ct).ConfigureAwait(true);
            UiThread.Send(() =>
            {
                drive.ApplyAnalysis(result);
                RefreshFileStatus();
                UpdateDriveStatusSummary();
                UpdateClusterCaption();
            });

            try
            {
                var path = _reports.Write(drive.Info, result);
                UiThread.Post(ReloadReports);
                _log.Info("Saved report " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _log.Warn("Report: " + ex.Message);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunJob))]
    private async Task Defragment()
    {
        if (SelectedDrive is null) return;
        var (ok, reason) = _protection.CanDefrag(SelectedDrive.Info, IsElevated);
        if (!ok)
        {
            if (SelectedDrive.IsSolidState && _config.Config.Protection.BlockSsdDefrag)
            {
                var pick = MessageBox.Show(
                    reason + "\n\nRun TRIM on this drive instead?",
                    AppInfo.ProductName,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (pick == MessageBoxResult.Yes)
                    await TrimAsync().ConfigureAwait(true);
                return;
            }

            MessageBox.Show(reason, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_config.Config.General.ConfirmBeforeDefrag)
        {
            var confirm = MessageBox.Show(
                $"Defragment {SelectedDrive.Volume} using {DefragMethodInfo.DisplayName(SelectedMethod)}?\n\n" +
                DefragMethodInfo.Description(SelectedMethod),
                AppInfo.ProductName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var method = SelectedMethod;
        await RunJobAsync(SelectedDrive, DriveJobStatus.Defragmenting, "Defragmenting", async (drive, ct) =>
        {
            ClusterKind[]? baseMap = null;
            FileRecord? lastFile = null;

            var progress = new Progress<DefragProgress>(p =>
            {
                if (baseMap is null)
                {
                    var src = drive.Analysis?.Map is { Length: > 0 } analysed
                        ? analysed
                        : drive.ClusterMap;
                    if (src.Length > 0)
                        baseMap = StripProcessing((ClusterKind[])src.Clone());
                }

                if (baseMap is not null && drive.Analysis is not null)
                {
                    if (lastFile is not null && !ReferenceEquals(lastFile, p.Current))
                    {
                        foreach (var ext in lastFile.Extents)
                            OverlayKind(baseMap, drive.Analysis, ext, ClusterKind.Allocated);
                    }

                    lastFile = p.Current;
                    var snapshot = (ClusterKind[])baseMap.Clone();
                    if (p.Current is not null)
                    {
                        foreach (var ext in p.Current.Extents)
                            OverlayKind(snapshot, drive.Analysis, ext, ClusterKind.Processing);
                    }

                    drive.ClusterMap = snapshot;
                }

                drive.SetBusy(DriveJobStatus.Defragmenting, "Defragmenting", p.Current?.Path ?? p.Status, p.Percent);
                ProgressValue = p.Percent;
                StatusText = $"Defragmenting {drive.Volume} — {p.Processed:N0}/{p.Total:N0}  {p.Status}";
            });

            var result = await Task.Run(
                () => _engine.Run(drive.Info, method, drive.Analysis, progress, ct), ct).ConfigureAwait(true);

            await AnalyseAsync().ConfigureAwait(true);
            var after = SelectedDrive?.Analysis;
            StatusText = after is null
                ? result.Message
                : $"{result.Message}  Now {after.FragmentedFiles:N0} fragmented files ({after.FragmentationPercent:0.#}%).";
            _log.Info(StatusText);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunJob))]
    private async Task Trim() => await TrimAsync().ConfigureAwait(true);

    private async Task TrimAsync()
    {
        if (SelectedDrive is null) return;
        var (ok, reason) = _protection.CanTrim(SelectedDrive.Info, IsElevated);
        if (!ok)
        {
            MessageBox.Show(reason, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunJobAsync(SelectedDrive, DriveJobStatus.Trimming, "TRIM", async (drive, ct) =>
        {
            var progress = new Progress<int>(p =>
            {
                WaitIfPaused();
                UiThread.Post(() =>
                {
                    drive.SetBusy(DriveJobStatus.Trimming, "TRIM", "", p);
                    ProgressValue = p;
                    StatusText = $"TRIM {drive.Volume}";
                });
            });

            var result = await Task.Run(() => _trim.Run(drive.Info, progress, ct), ct).ConfigureAwait(true);
            UiThread.Post(() =>
            {
                drive.SetIdle("TRIM done");
                StatusText = result.Message;
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanPauseOrResume))]
    private void Pause()
    {
        lock (_pauseLock)
        {
            _paused = !_paused;
            IsPaused = _paused;
        }

        StatusText = IsPaused ? "Paused" : "Resumed";
        if (SelectedDrive is not null)
            SelectedDrive.JobStatus = IsPaused ? DriveJobStatus.Paused : DriveJobStatus.Defragmenting;
    }

    private bool CanPauseOrResume() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _cts?.Cancel();
        StatusText = "Stopping…";
        if (SelectedDrive is not null)
            SelectedDrive.JobStatus = DriveJobStatus.Cancelling;
    }

    [RelayCommand(CanExecute = nameof(CanRunJob))]
    private async Task CheckDrive()
    {
        if (SelectedDrive is null) return;
        if (!IsElevated)
        {
            MessageBox.Show("Check drive needs administrator rights.", AppInfo.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fix = MessageBox.Show(
            $"Scan {SelectedDrive.Volume} for file system errors?\n\nYes = scan only\nNo = scan and fix (/F)",
            AppInfo.ProductName,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (fix == MessageBoxResult.Cancel)
            return;

        await RunJobAsync(SelectedDrive, DriveJobStatus.Analyzing, "Checking", async (drive, ct) =>
        {
            IsProgressIndeterminate = true;
            var text = await _chkdsk.ScanAsync(drive.Letter, fix == MessageBoxResult.No, ct).ConfigureAwait(true);
            UiThread.Post(() =>
            {
                IsProgressIndeterminate = false;
                MessageBox.Show(text.Length > 4000 ? text[..4000] + "…" : text,
                    "Check drive — " + drive.Volume,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DefragSelectedFile()
    {
        if (SelectedDrive is null || SelectedFile is null) return;
        var (ok, reason) = _protection.CanDefrag(SelectedDrive.Info, IsElevated);
        if (!ok)
        {
            MessageBox.Show(reason, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = SelectedFile.Path;
        await RunJobAsync(SelectedDrive, DriveJobStatus.Defragmenting, "File", async (drive, ct) =>
        {
            var result = await Task.Run(() => _engine.DefragSingleFile(drive.Info, path, ct), ct).ConfigureAwait(true);
            UiThread.Post(() => StatusText = result.Message);
            await AnalyseAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void FindFile()
    {
        if (SelectedDrive is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Locate file on disk",
            InitialDirectory = SelectedDrive.RootPath,
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            using var session = VolumeSession.Open(SelectedDrive.Letter);
            if (!session.TryGetExtents(dlg.FileName, out var extents, out _, out var size))
            {
                MessageBox.Show("Could not read that file's layout (locked or not on this volume).",
                    AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var allocated = extents.Where(e => !e.IsSparse).ToList();
            var record = new FileRecord
            {
                Path = dlg.FileName,
                Size = size,
                Fragments = allocated.Count,
                StartLcn = allocated.FirstOrDefault()?.Lcn ?? -1,
                ClusterCount = allocated.Sum(e => e.Clusters),
                Extents = allocated
            };

            if (SelectedDrive.Analysis is not null)
            {
                var src = SelectedDrive.Analysis.Map.Length > 0
                    ? SelectedDrive.Analysis.Map
                    : SelectedDrive.ClusterMap;
                var map = StripProcessing((ClusterKind[])src.Clone());
                foreach (var ext in allocated)
                    OverlayKind(map, SelectedDrive.Analysis, ext, ClusterKind.Processing);
                SelectedDrive.ClusterMap = map;
            }

            SelectedTab = BottomTab.FileStatus;
            FileStatusItems.Clear();
            FileStatusItems.Add(record);
            SelectedFile = record;
            StatusText = $"{Path.GetFileName(dlg.FileName)} — {allocated.Count} fragment(s), {ByteSizeFormatter.Format(size)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (string.IsNullOrWhiteSpace(SelectedReport) || !File.Exists(SelectedReport))
            return;
        Process.Start(new ProcessStartInfo(SelectedReport) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenReportsFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _config.ReportsDirectory,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _config.AppDataDirectory,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _log.LogDirectory,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void RelaunchElevated()
    {
        if (IsElevated)
            return;
        if (_elevation.TryRelaunchElevated([]))
            Application.Current.Shutdown();
    }

    [RelayCommand]
    private void ClearLog() => LogEntries.Clear();

    [RelayCommand]
    private void SaveSchedule()
    {
        _config.Config.Schedule.Enabled = true;
        _config.Config.Schedule.Method = DefragMethodInfo.DisplayName(SelectedMethod);
        _config.Config.Schedule.DriveLetters = SelectedDrive is null ? "" : SelectedDrive.Letter.ToString();
        _config.Save();
        if (_scheduler.CreateDailyTask(_config.Config.Schedule))
            StatusText = "Daily job created.";
        else
            StatusText = "Could not create scheduled task.";
    }

    [RelayCommand]
    private void RemoveSchedule()
    {
        _config.Config.Schedule.Enabled = false;
        _config.Save();
        _scheduler.RemoveTask();
        StatusText = "Scheduled task removed.";
    }

    public List<FileRecord> InspectBlock(int blockIndex)
    {
        if (SelectedDrive?.Analysis is null)
            return [];
        return _analysis.FilesInBlock(SelectedDrive.Analysis, blockIndex);
    }

    public void SaveGeneralSettings()
    {
        _config.Config.General.DefaultMethod = SelectedMethod.ToString();
        _config.Config.General.ShadedClusterView = ShadedView;
        _config.Config.General.ClusterBlockSize = ClusterBlockSize;
        _config.Save();
        OnPropertyChanged(nameof(ClusterBlockSize));
        OnPropertyChanged(nameof(ShadedView));
    }

    private bool CanRunJob() => !IsBusy && SelectedDrive is not null;

    private async Task RunJobAsync(
        DriveViewModel drive,
        DriveJobStatus job,
        string label,
        Func<DriveViewModel, CancellationToken, Task> work)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        IsPaused = false;
        _paused = false;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        drive.SetBusy(job, label, "", 0);
        StatusText = label + " " + drive.Volume;

        try
        {
            await work(drive, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped.";
            drive.SetIdle("Stopped");
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            StatusText = ex.Message;
            drive.SetIdle("Error");
            MessageBox.Show(ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsPaused = false;
            _paused = false;
            IsProgressIndeterminate = false;
            if (drive.JobStatus is not DriveJobStatus.Idle)
                drive.SetIdle(drive.Status);
            AnalyseCommand.NotifyCanExecuteChanged();
            DefragmentCommand.NotifyCanExecuteChanged();
            TrimCommand.NotifyCanExecuteChanged();
        }
    }

    private void WaitIfPaused()
    {
        while (_paused && _cts is { IsCancellationRequested: false })
            Thread.Sleep(80);
        _cts?.Token.ThrowIfCancellationRequested();
    }

    private void RefreshFileStatus()
    {
        FileStatusItems.Clear();
        var analysis = SelectedDrive?.Analysis;
        if (analysis is null)
            return;

        IEnumerable<FileRecord> items = FileStatusMode switch
        {
            "Largest" => analysis.Largest,
            _ => analysis.TopFragmented
        };
        foreach (var f in items.Take(400))
            FileStatusItems.Add(f);
    }

    private void UpdateDriveStatusSummary()
    {
        var d = SelectedDrive;
        if (d is null)
        {
            DriveStatusSummary = "Select a volume.";
            return;
        }

        var a = d.Analysis;
        DriveStatusSummary =
            $"{d.Volume}  {d.Label}\n" +
            $"Media: {d.MediaText}    File system: {d.FileSystem}\n" +
            $"Size: {d.SizeText}    Free: {d.FreeText} ({d.FreePercent:0.#}%)\n" +
            $"Model: {d.Info.DiskModel}\n" +
            (a is null
                ? "Not analysed yet."
                : $"Files: {a.TotalFiles:N0}    Fragmented: {a.FragmentedFiles:N0} ({a.FragmentationPercent:0.#}%)\n" +
                  $"Fragments: {a.TotalFragments:N0}    Cluster: {ByteSizeFormatter.Format(a.ClusterSize)}\n" +
                  $"Locked: {a.LockedFiles:N0}    Skipped: {a.SkippedFiles:N0}");
    }

    private void UpdateClusterCaption()
    {
        var d = SelectedDrive;
        if (d is null)
        {
            ClusterCaption = "Cluster view";
            return;
        }

        var per = d.ClustersPerBlock;
        ClusterCaption = per > 1
            ? $"Cluster view — {d.Volume}  ({per:N0} clusters / block)"
            : $"Cluster view — {d.Volume}";
    }

    private void ReloadReports()
    {
        Reports.Clear();
        foreach (var r in _reports.ListReports())
            Reports.Add(r);
        SelectedReport = Reports.FirstOrDefault();
    }

    private static ClusterKind[] StripProcessing(ClusterKind[] map)
    {
        for (var i = 0; i < map.Length; i++)
        {
            if (map[i] == ClusterKind.Processing)
                map[i] = ClusterKind.Allocated;
        }

        return map;
    }

    private static void OverlayKind(ClusterKind[] map, AnalysisResult analysis, Extent ext, ClusterKind kind)
    {
        if (map.Length == 0 || ext.IsSparse || ext.Lcn < 0)
            return;
        var per = Math.Max(1, analysis.ClustersPerBlock);
        var from = (int)Math.Clamp(ext.Lcn / per, 0, map.Length - 1);
        var to = (int)Math.Clamp((ext.Lcn + ext.Clusters - 1) / per, 0, map.Length - 1);
        if (from > to)
            return;
        for (var i = from; i <= to; i++)
            map[i] = kind;
    }
}
