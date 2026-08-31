using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsDefrag.Native;

internal static class NativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint FileShareDelete = 0x00000004;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagNoBuffering = 0x20000000;
    public const uint FileFlagSequentialScan = 0x08000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FileAttributeNormal = 0x00000080;

    public const uint FsctlGetNtfsVolumeData = 0x00090064;
    public const uint FsctlGetVolumeBitmap = 0x0009006F;
    public const uint FsctlGetRetrievalPointers = 0x00090073;
    public const uint FsctlMoveFile = 0x00090074;
    public const uint FsctlIsVolumeDirty = 0x00090078;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlStorageManageDataSetAttributes = 0x002D9404;
    public const uint IoctlStorageGetDeviceNumber = 0x002D1080;

    public const uint TokenAdjustPrivileges = 0x0020;
    public const uint TokenQuery = 0x0008;
    public const uint SePrivilegeEnabled = 0x00000002;

    public const uint StorageDeviceSeekPenaltyProperty = 7;
    public const uint StorageDeviceTrimProperty = 8;
    public const uint PropertyStandardQuery = 0;
    public const uint DeviceDsmActionTrim = 1;
    public const uint DeviceDsmFlagTrimNotSupported = 1;

    public static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StartingLcnInputBuffer lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StartingVcnInputBuffer lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MoveFileData lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileAttributesEx(
        string lpFileName,
        int fInfoLevelId,
        out Win32FileAttributeData lpFileInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(
        string? lpSystemName,
        string lpName,
        out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern uint GetLastError();
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartingLcnInputBuffer
{
    public long StartingLcn;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartingVcnInputBuffer
{
    public long StartingVcn;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MoveFileData
{
    public IntPtr FileHandle;
    public long StartingVcn;
    public long StartingLcn;
    public uint ClusterCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NtfsVolumeDataBuffer
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TokenPrivileges
{
    public uint PrivilegeCount;
    public Luid Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32FileAttributeData
{
    public uint FileAttributes;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public uint FileSizeHigh;
    public uint FileSizeLow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StoragePropertyQuery
{
    public uint PropertyId;
    public uint QueryType;
    public byte AdditionalParameters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceSeekPenaltyDescriptor
{
    public uint Version;
    public uint Size;
    [MarshalAs(UnmanagedType.U1)]
    public bool IncursSeekPenalty;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceTrimDescriptor
{
    public uint Version;
    public uint Size;
    [MarshalAs(UnmanagedType.U1)]
    public bool TrimEnabled;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageDeviceNumber
{
    public uint DeviceType;
    public uint DeviceNumber;
    public uint PartitionNumber;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceDataSetRange
{
    public long StartingOffset;
    public long LengthInBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceManageDataSetAttributes
{
    public uint Size;
    public uint Action;
    public uint Flags;
    public uint ParameterBlockOffset;
    public uint ParameterBlockLength;
    public uint DataSetRangesOffset;
    public uint DataSetRangesLength;
}
