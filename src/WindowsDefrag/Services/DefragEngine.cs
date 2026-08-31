using System.IO;
using WindowsDefrag.Models;
using WindowsDefrag.Native;

namespace WindowsDefrag.Services;

public sealed class DefragEngine
{
    private readonly ProtectionService _protection;
    private readonly LogService _log;

    public DefragEngine(ProtectionService protection, LogService log)
    {
        _protection = protection;
        _log = log;
    }

    public JobResult Run(
        DriveInfoModel drive,
        DefragMethod method,
        AnalysisResult? prior,
        IProgress<DefragProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var session = VolumeSession.Open(drive.Letter);
        var free = FreeRunMap.FromSession(session);
        var files = CollectMovable(session, drive, prior, progress, cancellationToken);
        var fragmentsBefore = files.Where(f => f.Fragments > 1).Sum(f => f.Fragments);

        var searchFrom = Math.Max(session.Ntfs.MftZoneEnd, 0);
        var moved = 0L;
        var processed = 0L;
        var failed = 0L;
        var lastError = 0;

        List<FileRecord> phase1;
        List<FileRecord> phase2;
        switch (method)
        {
            case DefragMethod.Stealth:
                phase1 = files.Where(f => f.Fragments > 1).OrderBy(f => f.ClusterCount).ToList();
                phase2 = [];
                break;
            case DefragMethod.Space:
                phase1 = files.Where(f => f.Fragments > 1).OrderBy(f => f.ClusterCount).ThenBy(f => f.Size).ToList();
                phase2 = files.Where(f => f.Fragments <= 1 && f.ClusterCount > 0 && !f.IsDirectory)
                    .OrderBy(f => f.StartLcn)
                    .ToList();
                break;
            default:
                phase1 = files.Where(f => f.ClusterCount > 0).ToList();
                phase1 = method switch
                {
                    DefragMethod.CompleteName => phase1.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                    DefragMethod.CompleteModified => phase1.OrderByDescending(f => f.LastWrite).ToList(),
                    _ => phase1.OrderByDescending(f => f.LastAccess).ToList()
                };
                phase2 = [];
                break;
        }

        var totalWork = Math.Max(1, phase1.Count + phase2.Count);
        var cursor = searchFrom;

        foreach (var file in phase1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            Report(progress, processed, totalWork, file);

            if (!CanTouch(file))
                continue;

            if (DefragFile(session, free, file, searchFrom, packTo: 0))
                moved++;
            else
            {
                failed++;
                if (session.LastMoveError != 0)
                    lastError = session.LastMoveError;
            }
        }

        if (method == DefragMethod.Space)
        {
            cursor = searchFrom;
            foreach (var file in phase2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                Report(progress, processed, totalWork, file);
                if (!CanTouch(file))
                    continue;
                if (PackFile(session, free, file, cursor))
                {
                    moved++;
                    cursor += file.ClusterCount;
                }
            }
        }
        else if (method is DefragMethod.CompleteName or DefragMethod.CompleteModified or DefragMethod.CompleteAccess)
        {
            cursor = searchFrom;
            foreach (var file in phase1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanTouch(file))
                    continue;
                if (PackFile(session, free, file, cursor))
                    cursor += file.ClusterCount;
            }
        }

        progress?.Report(new DefragProgress(100, "Done", processed, totalWork, null));
        var errText = lastError != 0 ? $" Last move error {lastError}." : "";
        var message = $"SPACE joined {moved:N0} file(s); {failed:N0} could not be moved.{errText}";
        if (method != DefragMethod.Space)
            message = $"Moved {moved:N0} of {processed:N0} files.{errText}";
        _log.Success($"{drive.Letter}: {message}");

        return new JobResult
        {
            Success = true,
            Message = message,
            FilesProcessed = processed,
            FilesMoved = moved,
            FilesFailed = failed,
            FragmentsBefore = fragmentsBefore,
            FragmentsAfter = Math.Max(0, fragmentsBefore - moved)
        };
    }

    public JobResult DefragSingleFile(
        DriveInfoModel drive,
        string path,
        CancellationToken cancellationToken)
    {
        using var session = VolumeSession.Open(drive.Letter);
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.TryGetExtents(path, out var extents, out _, out _))
            return new JobResult { Success = false, Message = "Cannot read file layout." };

        var allocated = extents.Where(e => !e.IsSparse).ToList();
        if (allocated.Count <= 1)
            return new JobResult { Success = true, Message = "File is already contiguous.", FilesProcessed = 1 };

        var free = FreeRunMap.FromSession(session);
        var rec = new FileRecord { Path = path, Extents = allocated, ClusterCount = allocated.Sum(e => e.Clusters), Fragments = allocated.Count };
        if (!DefragFile(session, free, rec, 0, packTo: 0))
            return new JobResult { Success = false, Message = "No free space large enough, or the file is locked." };

        _log.Success("Defragmented " + path);
        return new JobResult
        {
            Success = true,
            Message = "File defragmented.",
            FilesProcessed = 1,
            FilesMoved = 1,
            FragmentsBefore = allocated.Count,
            FragmentsAfter = 1
        };
    }

    private bool CanTouch(FileRecord file) =>
        file.ClusterCount > 0 &&
        file.Extents.Count > 0 &&
        !_protection.IsProtectedFile(file.Path) &&
        !ProtectionService.IsUnmovableName(file.Path);

    private bool DefragFile(VolumeSession session, FreeRunMap free, FileRecord file, long searchFrom, long packTo)
    {
        if (!session.TryGetExtents(file.Path, out var live, out _, out _))
            return false;

        var extents = live.Where(e => !e.IsSparse && e.Clusters > 0).ToList();
        if (extents.Count <= 1)
            return false;

        var total = extents.Sum(e => e.Clusters);
        var from = packTo > 0 ? packTo : searchFrom;
        if (free.TryFirstFit(total, from, out var dest) || free.TryFirstFit(total, 0, out dest))
        {
            if (session.TryRelocateExtents(file.Path, extents, dest, free))
                return true;
            _log.Warn($"Move failed {file.Path} (error {session.LastMoveError}).");
        }

        // No single hole — pack as many extents as possible into the largest gaps.
        var improved = false;
        for (var round = 0; round < extents.Count + 4; round++)
        {
            if (!session.TryGetExtents(file.Path, out live, out _, out _))
                break;
            extents = live.Where(e => !e.IsSparse && e.Clusters > 0).ToList();
            if (extents.Count <= 1)
                return true;

            if (!free.TryLargest(out var hole, out var holeLen) || holeLen <= 0)
                break;

            long used = 0;
            var movedAny = false;
            foreach (var ext in extents)
            {
                var room = holeLen - used;
                if (room <= 0)
                    break;
                var take = Math.Min(ext.Clusters, room);
                if (take <= 0)
                    break;
                if (!session.TryMoveClusters(file.Path, ext.Vcn, hole + used, take))
                    break;

                session.MarkRange(ext.Lcn, take, allocated: false);
                session.MarkRange(hole + used, take, allocated: true);
                free.Free(ext.Lcn, take);
                free.Allocate(hole + used, take);
                used += take;
                movedAny = true;
                improved = true;
            }

            if (!movedAny)
                break;
        }

        return improved;
    }

    private static bool PackFile(VolumeSession session, FreeRunMap free, FileRecord file, long cursor)
    {
        if (!session.TryGetExtents(file.Path, out var live, out _, out _))
            return false;
        var extents = live.Where(e => !e.IsSparse && e.Clusters > 0).ToList();
        if (extents.Count == 0)
            return false;

        var total = extents.Sum(e => e.Clusters);
        var current = extents[0].Lcn;
        if (!free.TryFirstFit(total, cursor, out var dest))
            return false;
        if (dest >= current)
            return false;
        return session.TryRelocateExtents(file.Path, extents, dest, free);
    }

    private static void Report(IProgress<DefragProgress>? progress, long processed, long total, FileRecord file)
    {
        var pct = 5 + (int)(processed * 90 / Math.Max(1, total));
        progress?.Report(new DefragProgress(pct, Path.GetFileName(file.Path), processed, total, file));
    }

    private List<FileRecord> CollectMovable(
        VolumeSession session,
        DriveInfoModel drive,
        AnalysisResult? prior,
        IProgress<DefragProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (prior is { AllFragmented.Count: > 0 })
        {
            return prior.AllFragmented
                .Concat(prior.LayoutFiles)
                .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Where(f => f.ClusterCount > 0)
                .ToList();
        }

        if (prior is not null)
        {
            return prior.TopFragmented
                .Concat(prior.ExtentIndex)
                .Concat(prior.LayoutFiles)
                .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Where(f => f.ClusterCount > 0)
                .ToList();
        }

        progress?.Report(new DefragProgress(1, "Scanning files", 0, 0, null));
        var analysis = new AnalysisService(_protection, _log);
        var result = analysis.Analyze(drive, new Progress<AnalysisProgress>(p =>
        {
            progress?.Report(new DefragProgress(Math.Min(4, p.Percent / 25), p.CurrentPath, p.FilesScanned, 0, null));
        }), cancellationToken);

        return result.AllFragmented
            .Concat(result.TopFragmented)
            .Concat(result.ExtentIndex)
            .Concat(result.LayoutFiles)
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Where(f => f.ClusterCount > 0)
            .ToList();
    }
}

public readonly record struct DefragProgress(
    int Percent,
    string Status,
    long Processed,
    long Total,
    FileRecord? Current);
