using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;

namespace WindowsDefrag.Native;

/// <summary>Open volume / file handles and issue FSCTL defrag I/O.</summary>
internal sealed class VolumeSession : IDisposable
{
    public const int MapBlockCount = 16384;
    private const int BitmapChunkBytes = 1024 * 1024;

    public char Letter { get; }
    public string RootPath { get; }
    public SafeFileHandle VolumeHandle { get; }
    public NtfsVolumeDataBuffer Ntfs { get; }
    public byte[] Bitmap { get; }
    public long BitmapClusterCount { get; }
    public bool IsNtfs { get; }

    private VolumeSession(
        char letter,
        string rootPath,
        SafeFileHandle volumeHandle,
        NtfsVolumeDataBuffer ntfs,
        byte[] bitmap,
        long bitmapClusterCount,
        bool isNtfs)
    {
        Letter = letter;
        RootPath = rootPath;
        VolumeHandle = volumeHandle;
        Ntfs = ntfs;
        Bitmap = bitmap;
        BitmapClusterCount = bitmapClusterCount;
        IsNtfs = isNtfs;
    }

    public uint ClusterSize => Ntfs.BytesPerCluster == 0 ? 4096 : Ntfs.BytesPerCluster;
    public long TotalClusters => Ntfs.TotalClusters > 0 ? Ntfs.TotalClusters : BitmapClusterCount;

    public static VolumeSession Open(char letter)
    {
        PrivilegeHelper.EnableDefragPrivileges();

        var root = char.ToUpperInvariant(letter) + @":\";
        var volumePath = @"\\.\" + char.ToUpperInvariant(letter) + ":";
        var handle = NativeMethods.CreateFile(
            volumePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle = NativeMethods.CreateFile(
                volumePath,
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);
        }

        if (handle.IsInvalid)
            throw new IOException($"Cannot open volume {letter}: (error {Marshal.GetLastWin32Error()}). Run as administrator.");

        var ntfs = new NtfsVolumeDataBuffer();
        var isNtfs = TryGetNtfsData(handle, out ntfs);

        var bitmap = ReadBitmap(handle, isNtfs ? ntfs.TotalClusters : 0, out var bitmapClusters);
        if (!isNtfs)
        {
            ntfs.TotalClusters = bitmapClusters;
            ntfs.BytesPerCluster = 4096;
            try
            {
                var di = new DriveInfo(root);
                if (bitmapClusters > 0)
                    ntfs.BytesPerCluster = (uint)Math.Max(512, di.TotalSize / bitmapClusters);
                ntfs.FreeClusters = bitmapClusters > 0
                    ? di.AvailableFreeSpace / Math.Max(1, ntfs.BytesPerCluster)
                    : 0;
            }
            catch
            {
                // keep defaults
            }
        }

        return new VolumeSession(letter, root, handle, ntfs, bitmap, bitmapClusters, isNtfs);
    }

    public bool IsAllocated(long cluster)
    {
        if (cluster < 0 || Bitmap.Length == 0)
            return false;
        var index = cluster >> 3;
        if (index >= Bitmap.Length)
            return false;
        var bit = (int)(cluster & 7);
        return (Bitmap[index] & (1 << bit)) != 0;
    }

    public void SetAllocated(long cluster, bool allocated)
    {
        if (cluster < 0 || Bitmap.Length == 0)
            return;
        var index = cluster >> 3;
        if (index >= Bitmap.Length)
            return;
        var bit = (int)(cluster & 7);
        if (allocated)
            Bitmap[index] |= (byte)(1 << bit);
        else
            Bitmap[index] &= (byte)~(1 << bit);
    }

    public bool TryFindFreeRun(long minClusters, long searchFrom, out long startLcn)
    {
        startLcn = -1;
        if (minClusters <= 0)
            minClusters = 1;

        var total = BitmapClusterCount;
        var start = Math.Max(0, searchFrom);
        for (var pass = 0; pass < 2; pass++)
        {
            var from = pass == 0 ? start : 0L;
            var to = pass == 0 ? total : start;
            var run = 0L;
            var runStart = -1L;
            for (var c = from; c < to; c++)
            {
                if (!IsAllocated(c))
                {
                    if (run == 0) runStart = c;
                    run++;
                    if (run >= minClusters)
                    {
                        startLcn = runStart;
                        return true;
                    }
                }
                else
                {
                    run = 0;
                    runStart = -1;
                }
            }
        }

        return false;
    }

    public bool TryGetExtents(string path, out List<Extent> extents, out FileAttributes attributes, out long size)
    {
        extents = [];
        attributes = 0;
        size = 0;

        if (!NativeMethods.GetFileAttributesEx(PathHelper.LongPath(path), 0, out var data))
        {
            try
            {
                var info = new FileInfo(path);
                attributes = info.Attributes;
                size = info.Exists ? info.Length : 0;
            }
            catch
            {
                return false;
            }
        }
        else
        {
            attributes = (FileAttributes)data.FileAttributes;
            size = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
        }

        using var file = OpenFile(path);
        if (file is null || file.IsInvalid)
            return false;

        var vcn = 0L;
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            while (true)
            {
                var input = new StartingVcnInputBuffer { StartingVcn = vcn };
                if (!NativeMethods.DeviceIoControl(
                        file,
                        NativeMethods.FsctlGetRetrievalPointers,
                        ref input,
                        (uint)Marshal.SizeOf<StartingVcnInputBuffer>(),
                        buffer,
                        64 * 1024,
                        out var returned,
                        IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == 38) // ERROR_HANDLE_EOF
                        break;
                    if (extents.Count == 0)
                        return false;
                    break;
                }

                if (returned < 16)
                    break;

                var extentCount = Marshal.ReadInt32(buffer, 0);
                var startingVcn = Marshal.ReadInt64(buffer, 8);
                var offset = 16;
                var currentVcn = startingVcn;
                for (var i = 0; i < extentCount; i++)
                {
                    if (offset + 16 > returned)
                        break;
                    var nextVcn = Marshal.ReadInt64(buffer, offset);
                    var lcn = Marshal.ReadInt64(buffer, offset + 8);
                    var clusters = nextVcn - currentVcn;
                    if (clusters > 0)
                    {
                        extents.Add(new Extent
                        {
                            Vcn = currentVcn,
                            Lcn = lcn,
                            Clusters = clusters
                        });
                    }

                    currentVcn = nextVcn;
                    offset += 16;
                }

                if (extentCount == 0)
                    break;
                vcn = currentVcn;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return extents.Count > 0 || size == 0;
    }

    public bool TryMoveClusters(string path, long startingVcn, long destLcn, long clusterCount)
    {
        while (clusterCount > 0)
        {
            var chunk = (uint)Math.Min(clusterCount, uint.MaxValue);
            if (!MoveOnce(path, startingVcn, destLcn, chunk))
                return false;
            startingVcn += chunk;
            destLcn += chunk;
            clusterCount -= chunk;
        }

        return true;
    }

    public bool TryRelocateExtents(string path, List<Extent> extents, long destLcn, FreeRunMap? free)
    {
        var dest = destLcn;
        foreach (var ext in extents)
        {
            if (ext.IsSparse || ext.Clusters <= 0)
                continue;
            if (!TryMoveClusters(path, ext.Vcn, dest, ext.Clusters))
                return false;

            MarkRange(ext.Lcn, ext.Clusters, allocated: false);
            MarkRange(dest, ext.Clusters, allocated: true);
            free?.Free(ext.Lcn, ext.Clusters);
            free?.Allocate(dest, ext.Clusters);
            dest += ext.Clusters;
        }

        return true;
    }

    public bool TryMoveWholeFile(string path, List<Extent> extents, out long destLcn)
    {
        destLcn = -1;
        var allocated = extents.Where(e => !e.IsSparse).Sum(e => e.Clusters);
        if (allocated <= 0)
            return true;
        if (!TryFindFreeRun(allocated, 0, out destLcn))
            return false;
        return TryRelocateExtents(path, extents, destLcn, null);
    }

    public void MarkRange(long start, long count, bool allocated)
    {
        var end = start + count;
        for (var c = start; c < end; c++)
            SetAllocated(c, allocated);
    }

    public void AddFragmentCoverage(int[] weights, long startLcn, long clusters)
    {
        if (weights.Length == 0 || clusters <= 0 || startLcn < 0)
            return;
        var total = Math.Max(1, TotalClusters);
        var per = Math.Max(1, (total + weights.Length - 1) / weights.Length);
        var from = (int)Math.Clamp(startLcn / per, 0, weights.Length - 1);
        var to = (int)Math.Clamp((startLcn + clusters - 1) / per, 0, weights.Length - 1);
        for (var i = from; i <= to; i++)
        {
            var blockStart = i * per;
            var blockEnd = blockStart + per;
            var a = Math.Max(startLcn, blockStart);
            var b = Math.Min(startLcn + clusters, blockEnd);
            var overlap = b - a;
            if (overlap > 0)
                weights[i] += (int)Math.Min(overlap, int.MaxValue - weights[i]);
        }
    }

    public void ApplyFragmentCoverage(ClusterKind[] map, int[] weights)
    {
        if (map.Length == 0 || weights.Length != map.Length)
            return;
        var per = Math.Max(1, (Math.Max(1, TotalClusters) + map.Length - 1) / map.Length);
        var threshold = Math.Max(1, per / 8);
        for (var i = 0; i < map.Length; i++)
        {
            if (weights[i] >= threshold)
                map[i] = ClusterKind.Fragmented;
        }
    }

    public int LastMoveError { get; private set; }

    private bool MoveOnce(string path, long startingVcn, long destLcn, uint clusterCount)
    {
        LastMoveError = 0;
        using var file = OpenFile(path, forMove: true);
        if (file is null || file.IsInvalid)
        {
            LastMoveError = Marshal.GetLastWin32Error();
            if (LastMoveError == 0)
                LastMoveError = 5; // access denied
            return false;
        }

        var handle = file.DangerousGetHandle();
        var data = new MoveFileData
        {
            FileHandle = handle,
            StartingVcn = startingVcn,
            StartingLcn = destLcn,
            ClusterCount = clusterCount
        };

        var ok = NativeMethods.DeviceIoControl(
            VolumeHandle,
            NativeMethods.FsctlMoveFile,
            ref data,
            (uint)Marshal.SizeOf<MoveFileData>(),
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
        if (!ok)
            LastMoveError = Marshal.GetLastWin32Error();
        return ok;
    }

    public ClusterKind[] BuildBaseMap()
    {
        var blocks = MapBlockCount;
        var map = new ClusterKind[blocks];
        var total = Math.Max(1, TotalClusters);
        var per = Math.Max(1, (total + blocks - 1) / blocks);

        var mftStart = Ntfs.MftZoneStart;
        var mftEnd = Ntfs.MftZoneEnd;

        for (var i = 0; i < blocks; i++)
        {
            var from = i * per;
            var to = Math.Min(total, from + per);
            var allocated = 0;
            var free = 0;
            var mft = 0;
            var span = Math.Max(1, to - from);
            var step = span > 256 ? span / 256 : 1;
            for (var c = from; c < to; c += step)
            {
                if (c >= mftStart && c < mftEnd)
                    mft++;
                else if (IsAllocated(c))
                    allocated++;
                else
                    free++;
            }

            if (mft > allocated && mft > free)
                map[i] = ClusterKind.Mft;
            else if (allocated > 0 && free == 0)
                map[i] = ClusterKind.Allocated;
            else if (allocated > 0)
                map[i] = ClusterKind.Allocated;
            else
                map[i] = ClusterKind.Free;
        }

        return map;
    }

    public void Overlay(ClusterKind[] map, long startLcn, long clusters, ClusterKind kind)
    {
        if (map.Length == 0 || clusters <= 0 || startLcn < 0)
            return;
        var total = Math.Max(1, TotalClusters);
        var per = Math.Max(1, (total + map.Length - 1) / map.Length);
        var from = (int)Math.Clamp(startLcn / per, 0, map.Length - 1);
        var to = (int)Math.Clamp((startLcn + clusters - 1) / per, 0, map.Length - 1);
        if (from > to)
            return;
        for (var i = from; i <= to; i++)
        {
            if (Priority(kind) >= Priority(map[i]))
                map[i] = kind;
        }
    }

    public static int Priority(ClusterKind kind) => kind switch
    {
        ClusterKind.Processing => 100,
        ClusterKind.Fragmented => 80,
        ClusterKind.Locked => 70,
        ClusterKind.Paging => 60,
        ClusterKind.Unmovable => 55,
        ClusterKind.Mft => 50,
        ClusterKind.System => 40,
        ClusterKind.Directory => 30,
        ClusterKind.Compressed => 20,
        ClusterKind.Allocated => 10,
        _ => 0
    };

    public static bool IsVolumeDirty(char letter)
    {
        try
        {
            var path = @"\\.\" + char.ToUpperInvariant(letter) + ":";
            using var handle = NativeMethods.CreateFile(
                path,
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
                return false;

            var buffer = Marshal.AllocHGlobal(4);
            try
            {
                if (!NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.FsctlIsVolumeDirty,
                        IntPtr.Zero,
                        0,
                        buffer,
                        4,
                        out _,
                        IntPtr.Zero))
                    return false;
                var flags = Marshal.ReadInt32(buffer);
                return (flags & 1) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool QuerySeekPenalty(char letter, out bool incursPenalty)
    {
        incursPenalty = true;
        return QueryStorageFlag(letter, NativeMethods.StorageDeviceSeekPenaltyProperty, 12, 8, out incursPenalty);
    }

    public static bool QueryTrim(char letter, out bool trimEnabled)
    {
        trimEnabled = false;
        return QueryStorageFlag(letter, NativeMethods.StorageDeviceTrimProperty, 12, 8, out trimEnabled);
    }

    public bool TryTrimFreeRanges(Action<int, string>? progress, CancellationToken cancellationToken)
    {
        const int maxRanges = 64;
        var ranges = new List<DeviceDataSetRange>(maxRanges);
        var clusterSize = ClusterSize;
        var total = BitmapClusterCount;
        long runStart = -1;
        long runLen = 0;
        var submitted = 0;

        void Flush()
        {
            if (ranges.Count == 0)
                return;
            SubmitTrim(ranges);
            submitted += ranges.Count;
            ranges.Clear();
        }

        for (long c = 0; c < total; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAllocated(c))
            {
                if (runStart < 0) runStart = c;
                runLen++;
            }
            else if (runStart >= 0)
            {
                ranges.Add(new DeviceDataSetRange
                {
                    StartingOffset = runStart * clusterSize,
                    LengthInBytes = runLen * clusterSize
                });
                runStart = -1;
                runLen = 0;
                if (ranges.Count >= maxRanges)
                    Flush();
            }

            if (c % 1_000_000 == 0)
                progress?.Invoke((int)(c * 100 / Math.Max(1, total)), "TRIM free space");
        }

        if (runStart >= 0)
        {
            ranges.Add(new DeviceDataSetRange
            {
                StartingOffset = runStart * clusterSize,
                LengthInBytes = runLen * clusterSize
            });
        }

        Flush();
        return submitted >= 0;
    }

    private void SubmitTrim(List<DeviceDataSetRange> ranges)
    {
        var headerSize = Marshal.SizeOf<DeviceManageDataSetAttributes>();
        var rangeSize = Marshal.SizeOf<DeviceDataSetRange>();
        var total = headerSize + rangeSize * ranges.Count;
        var ptr = Marshal.AllocHGlobal(total);
        try
        {
            var header = new DeviceManageDataSetAttributes
            {
                Size = (uint)headerSize,
                Action = NativeMethods.DeviceDsmActionTrim,
                Flags = 0,
                ParameterBlockOffset = 0,
                ParameterBlockLength = 0,
                DataSetRangesOffset = (uint)headerSize,
                DataSetRangesLength = (uint)(rangeSize * ranges.Count)
            };
            Marshal.StructureToPtr(header, ptr, false);
            for (var i = 0; i < ranges.Count; i++)
                Marshal.StructureToPtr(ranges[i], ptr + headerSize + i * rangeSize, false);

            NativeMethods.DeviceIoControl(
                VolumeHandle,
                NativeMethods.IoctlStorageManageDataSetAttributes,
                ptr,
                (uint)total,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static bool QueryStorageFlag(char letter, uint propertyId, int outSize, int flagOffset, out bool value)
    {
        value = false;
        var path = @"\\.\" + char.ToUpperInvariant(letter) + ":";
        using var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
            return false;

        var query = new StoragePropertyQuery
        {
            PropertyId = propertyId,
            QueryType = NativeMethods.PropertyStandardQuery
        };
        var inSize = Marshal.SizeOf<StoragePropertyQuery>();
        var inPtr = Marshal.AllocHGlobal(inSize);
        var outPtr = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(query, inPtr, false);
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.IoctlStorageQueryProperty,
                    inPtr,
                    (uint)inSize,
                    outPtr,
                    (uint)outSize,
                    out var returned,
                    IntPtr.Zero))
                return false;
            if (returned <= flagOffset)
                return false;
            value = Marshal.ReadByte(outPtr, flagOffset) != 0;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    private static bool TryGetNtfsData(SafeFileHandle handle, out NtfsVolumeDataBuffer data)
    {
        data = default;
        var size = Marshal.SizeOf<NtfsVolumeDataBuffer>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlGetNtfsVolumeData,
                    IntPtr.Zero,
                    0,
                    ptr,
                    (uint)size,
                    out _,
                    IntPtr.Zero))
                return false;
            data = Marshal.PtrToStructure<NtfsVolumeDataBuffer>(ptr);
            return data.TotalClusters > 0 && data.BytesPerCluster > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static byte[] ReadBitmap(SafeFileHandle handle, long expectedClusters, out long clusterCount)
    {
        clusterCount = 0;
        using var ms = new MemoryStream();
        var starting = 0L;
        var totalBits = 0L;
        var inputSize = (uint)Marshal.SizeOf<StartingLcnInputBuffer>();
        var outSize = BitmapChunkBytes + 16;
        var outPtr = Marshal.AllocHGlobal(outSize);
        try
        {
            while (true)
            {
                var input = new StartingLcnInputBuffer { StartingLcn = starting };
                var ok = NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlGetVolumeBitmap,
                    ref input,
                    inputSize,
                    outPtr,
                    (uint)outSize,
                    out var returned,
                    IntPtr.Zero);
                var err = ok ? 0 : Marshal.GetLastWin32Error();
                // 234 = ERROR_MORE_DATA: buffer holds a slice, not the rest of the volume.
                if (!ok && err != 234)
                    break;
                if (returned < 16)
                    break;

                var startOut = Marshal.ReadInt64(outPtr, 0);
                var remaining = Marshal.ReadInt64(outPtr, 8);
                var payload = (int)(returned - 16);
                if (payload <= 0 || remaining <= 0)
                    break;

                var bitsThis = Math.Min(remaining, (long)payload * 8);
                var bytesThis = (int)((bitsThis + 7) / 8);
                bytesThis = Math.Min(bytesThis, payload);
                if (bytesThis <= 0)
                    break;

                var chunk = new byte[bytesThis];
                Marshal.Copy(outPtr + 16, chunk, 0, bytesThis);
                ms.Write(chunk, 0, bytesThis);

                totalBits += bitsThis;
                var next = startOut + bitsThis;
                if (next <= starting)
                    break;
                starting = next;

                if (expectedClusters > 0 && starting >= expectedClusters)
                    break;
                if (ok && remaining <= bitsThis)
                    break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(outPtr);
        }

        clusterCount = totalBits;
        return ms.Length == 0 ? [] : ms.ToArray();
    }

    private static SafeFileHandle? OpenFile(string path, bool forMove = false)
    {
        try
        {
            var flags = NativeMethods.FileFlagBackupSemantics;
            if (forMove)
                flags |= NativeMethods.FileFlagNoBuffering | NativeMethods.FileFlagSequentialScan;

            var handle = NativeMethods.CreateFile(
                PathHelper.LongPath(path),
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                flags,
                IntPtr.Zero);
            if (!handle.IsInvalid)
                return handle;

            handle = NativeMethods.CreateFile(
                PathHelper.LongPath(path),
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics,
                IntPtr.Zero);
            return handle.IsInvalid ? null : handle;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        VolumeHandle.Dispose();
    }
}
