using System.IO;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;
using WindowsDefrag.Native;

namespace WindowsDefrag.Services;

public sealed class AnalysisService
{
    private const int TopListSize = 400;
    private const int ExtentIndexCap = 12000;
    private const int LayoutCap = 80000;

    private readonly ProtectionService _protection;
    private readonly LogService _log;

    public AnalysisService(ProtectionService protection, LogService log)
    {
        _protection = protection;
        _log = log;
    }

    public AnalysisResult Analyze(
        DriveInfoModel drive,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken,
        Func<bool>? isPaused = null)
    {
        using var session = VolumeSession.Open(drive.Letter);
        var result = new AnalysisResult
        {
            Letter = drive.Letter,
            ClusterSize = session.ClusterSize,
            TotalClusters = session.TotalClusters
        };

        ClusterKind[] map;
        try
        {
            map = session.BuildBaseMap();
        }
        catch (Exception ex)
        {
            _log.Warn("Cluster map from bitmap failed: " + ex.Message);
            map = new ClusterKind[VolumeSession.MapBlockCount];
        }

        result.ClustersPerBlock = Math.Max(1, (session.TotalClusters + map.Length - 1) / map.Length);

        var topFrag = new List<FileRecord>();
        var largest = new List<FileRecord>();
        ScanSpecials(session, drive.RootPath, map, result);

        var state = new ScanState
        {
            Session = session,
            Result = result,
            Map = map,
            FragmentWeights = new int[map.Length],
            TopFrag = topFrag,
            Largest = largest,
            Progress = progress,
            IsPaused = isPaused,
            Cancellation = cancellationToken,
            PeakPercent = 8
        };

        Report(progress, 8, drive.RootPath, result, map, null);

        try
        {
            var roots = ListEntries(drive.RootPath);
            WalkDir(roots, 8.0, 90.0, state);
            session.ApplyFragmentCoverage(map, state.FragmentWeights);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Cannot enumerate {drive.RootPath}: {ex.Message}");
        }

        result.AllocatedClusters = Math.Max(0, session.TotalClusters - session.Ntfs.FreeClusters);
        if (result.AllocatedClusters <= 0)
            result.AllocatedClusters = session.TotalClusters - drive.FreeSpace / Math.Max(1, (long)session.ClusterSize);

        result.FragmentationPercent = result.AllocatedClusters <= 0
            ? 0
            : Math.Clamp(result.FragmentedClusters * 100.0 / result.AllocatedClusters, 0, 100);

        result.Map = map;
        result.TopFragmented.AddRange(topFrag);
        result.Largest.AddRange(largest);

        Report(progress, 100, drive.RootPath, result, map, null);
        _log.Info($"{drive.Letter}: {result.FragmentedFiles:N0} fragmented of {result.TotalFiles:N0} files ({result.FragmentationPercent:0.#}%).");
        return result;
    }

    private sealed class ScanState
    {
        public required VolumeSession Session { get; init; }
        public required AnalysisResult Result { get; init; }
        public required ClusterKind[] Map { get; init; }
        public required int[] FragmentWeights { get; init; }
        public required List<FileRecord> TopFrag { get; init; }
        public required List<FileRecord> Largest { get; init; }
        public IProgress<AnalysisProgress>? Progress { get; init; }
        public Func<bool>? IsPaused { get; init; }
        public CancellationToken Cancellation { get; init; }
        public int PeakPercent { get; set; }
        public int LastSentPercent { get; set; } = -1;
        public TimeSpan LastReport { get; set; }
        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
    }

    private void WalkDir(List<string> children, double basePct, double span, ScanState state)
    {
        if (children.Count == 0)
            return;

        var each = span / children.Count;
        for (var i = 0; i < children.Count; i++)
        {
            WaitIfPaused(state.IsPaused, state.Cancellation);
            var path = children[i];
            var childBase = basePct + i * each;
            var (isDir, extents) = ProcessNode(path, state);
            if (isDir)
                WalkDir(ListEntries(path), childBase, each, state);

            var pct = (int)Math.Clamp(childBase + each, 8, 98);
            if (pct < state.PeakPercent)
                pct = state.PeakPercent;
            state.PeakPercent = pct;
            MaybeReport(state, path, extents, pct);
        }
    }

    private (bool IsDirectory, List<Extent> Extents) ProcessNode(string path, ScanState state)
    {
        var result = state.Result;
        var session = state.Session;
        var map = state.Map;

        try
        {
            if (_protection.IsProtectedFile(path))
            {
                result.SkippedFiles++;
                return (false, []);
            }

            if (!session.TryGetExtents(path, out var extents, out var attrs, out var size))
            {
                result.SkippedFiles++;
                var asDir = (attrs & FileAttributes.Directory) != 0;
                if (!asDir)
                {
                    try { asDir = (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
                    catch { /* leave as file */ }
                }
                if (!asDir)
                    result.LockedFiles++;
                return (asDir, []);
            }

            var isDir = (attrs & FileAttributes.Directory) != 0;
            var isCompressed = (attrs & FileAttributes.Compressed) != 0;
            var allocatedExtents = extents.Where(e => !e.IsSparse).ToList();
            var fragments = allocatedExtents.Count;
            var clusters = allocatedExtents.Sum(e => e.Clusters);
            var startLcn = allocatedExtents.Count > 0 ? allocatedExtents[0].Lcn : -1;

            if (isDir)
                result.DirectoryCount++;
            else
                result.TotalFiles++;

            result.TotalFragments += Math.Max(1, fragments);

            DateTime write = default, access = default;
            try
            {
                write = File.GetLastWriteTimeUtc(path);
                access = File.GetLastAccessTimeUtc(path);
            }
            catch
            {
                // timestamps optional
            }

            var record = new FileRecord
            {
                Path = path,
                Size = size,
                Fragments = fragments,
                StartLcn = startLcn,
                ClusterCount = clusters,
                IsDirectory = isDir,
                IsCompressed = isCompressed,
                LastWrite = write,
                LastAccess = access,
                Extents = allocatedExtents
            };

            var kind = PickKind(record, fragments);
            foreach (var ext in allocatedExtents)
            {
                if (kind == ClusterKind.Fragmented)
                    session.AddFragmentCoverage(state.FragmentWeights, ext.Lcn, ext.Clusters);
                else
                    session.Overlay(map, ext.Lcn, ext.Clusters, kind);
            }

            if (fragments > 1)
            {
                result.FragmentedFiles++;
                result.FragmentedClusters += clusters;
                InsertTop(state.TopFrag, record, (a, b) => b.Fragments.CompareTo(a.Fragments), TopListSize);
                if (result.ExtentIndex.Count < ExtentIndexCap)
                    result.ExtentIndex.Add(record);
                result.AllFragmented.Add(record);
            }

            if (!isDir)
            {
                InsertTop(state.Largest, record, (a, b) => b.Size.CompareTo(a.Size), TopListSize);
                if (result.LayoutFiles.Count < LayoutCap)
                    result.LayoutFiles.Add(record);
            }

            return (isDir, allocatedExtents);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result.SkippedFiles++;
            return (false, []);
        }
    }

    private static List<string> ListEntries(string dir)
    {
        try
        {
            return Directory.GetFileSystemEntries(dir).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void WaitIfPaused(Func<bool>? isPaused, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isPaused is null)
            return;
        while (isPaused())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(50);
        }
    }

    private static void MaybeReport(ScanState state, string path, List<Extent> currentExtents, int percent)
    {
        if (state.Progress is null)
            return;
        var elapsed = state.Clock.Elapsed - state.LastReport;
        if (percent == state.LastSentPercent && elapsed < TimeSpan.FromMilliseconds(80))
            return;

        state.LastReport = state.Clock.Elapsed;
        state.LastSentPercent = percent;
        state.Session.ApplyFragmentCoverage(state.Map, state.FragmentWeights);
        Report(state.Progress, percent, path, state.Result, state.Map, currentExtents);
    }

    private static void Report(
        IProgress<AnalysisProgress>? progress,
        int percent,
        string path,
        AnalysisResult result,
        ClusterKind[] map,
        List<Extent>? currentExtents)
    {
        if (progress is null)
            return;

        var snapshot = (ClusterKind[])map.Clone();
        if (currentExtents is { Count: > 0 })
        {
            foreach (var ext in currentExtents)
            {
                if (ext.IsSparse) continue;
                OverlaySnapshot(snapshot, result.ClustersPerBlock, result.TotalClusters, ext.Lcn, ext.Clusters);
            }
        }

        progress.Report(new AnalysisProgress(
            Math.Clamp(percent, 0, 100),
            path,
            result.TotalFiles,
            result.FragmentedFiles,
            snapshot,
            result.ClustersPerBlock));
    }

    private static void OverlaySnapshot(ClusterKind[] map, long clustersPerBlock, long totalClusters, long startLcn, long clusters)
    {
        if (map.Length == 0 || clusters <= 0 || startLcn < 0)
            return;
        var total = Math.Max(1, totalClusters);
        var per = Math.Max(1, clustersPerBlock > 0 ? clustersPerBlock : (total + map.Length - 1) / map.Length);
        var from = (int)Math.Clamp(startLcn / per, 0, map.Length - 1);
        var to = (int)Math.Clamp((startLcn + clusters - 1) / per, 0, map.Length - 1);
        if (from > to)
            return;
        for (var i = from; i <= to; i++)
            map[i] = ClusterKind.Processing;
    }

    public List<FileRecord> FilesInBlock(AnalysisResult analysis, int blockIndex)
    {
        if (analysis.Map.Length == 0 || blockIndex < 0 || blockIndex >= analysis.Map.Length)
            return [];

        var from = blockIndex * analysis.ClustersPerBlock;
        var to = from + analysis.ClustersPerBlock;
        var hits = new List<FileRecord>();
        IEnumerable<FileRecord> pool = analysis.ExtentIndex.Count > 0
            ? analysis.ExtentIndex.Concat(analysis.LayoutFiles)
            : analysis.LayoutFiles;
        foreach (var file in pool.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var ext in file.Extents)
            {
                if (ext.IsSparse) continue;
                var end = ext.Lcn + ext.Clusters;
                if (ext.Lcn < to && end > from)
                {
                    hits.Add(file);
                    break;
                }
            }

            if (hits.Count >= 80)
                break;
        }

        return hits;
    }

    private void ScanSpecials(VolumeSession session, string root, ClusterKind[] map, AnalysisResult result)
    {
        foreach (var name in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
        {
            var path = Path.Combine(root, name);
            try
            {
                if (!File.Exists(path))
                    continue;
                if (!session.TryGetExtents(path, out var extents, out _, out var size))
                    continue;
                result.SkippedFiles++;
                var kind = name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)
                    ? ClusterKind.Paging
                    : ClusterKind.Unmovable;
                foreach (var ext in extents.Where(e => !e.IsSparse))
                    session.Overlay(map, ext.Lcn, ext.Clusters, kind);

                result.ExtentIndex.Add(new FileRecord
                {
                    Path = path,
                    Size = size,
                    Fragments = extents.Count(e => !e.IsSparse),
                    StartLcn = extents.FirstOrDefault(e => !e.IsSparse)?.Lcn ?? -1,
                    ClusterCount = extents.Where(e => !e.IsSparse).Sum(e => e.Clusters),
                    Extents = extents.Where(e => !e.IsSparse).ToList()
                });
            }
            catch
            {
                // hidden system files may refuse
            }
        }
    }

    private static ClusterKind PickKind(FileRecord record, int fragments)
    {
        if (ProtectionService.IsUnmovableName(record.Path))
            return ClusterKind.Unmovable;
        if (fragments > 1)
            return ClusterKind.Fragmented;
        if (record.IsDirectory)
            return ClusterKind.Directory;
        if (record.IsCompressed)
            return ClusterKind.Compressed;
        return ClusterKind.Allocated;
    }

    private static void InsertTop(List<FileRecord> list, FileRecord item, Comparison<FileRecord> cmp, int cap)
    {
        if (item.Size <= 0 && item.Fragments <= 1)
            return;

        var i = 0;
        while (i < list.Count && cmp(list[i], item) <= 0)
            i++;
        list.Insert(i, item);
        if (list.Count > cap)
            list.RemoveAt(list.Count - 1);
    }
}

public readonly record struct AnalysisProgress(
    int Percent,
    string CurrentPath,
    long FilesScanned,
    long FragmentedFiles,
    ClusterKind[]? Map = null,
    long ClustersPerBlock = 1);
