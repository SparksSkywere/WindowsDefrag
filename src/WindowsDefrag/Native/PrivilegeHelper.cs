using System.Runtime.InteropServices;

namespace WindowsDefrag.Native;

internal static class PrivilegeHelper
{
    public static void EnableDefragPrivileges()
    {
        Enable("SeBackupPrivilege");
        Enable("SeRestorePrivilege");
        Enable("SeManageVolumePrivilege");
    }

    public static bool Enable(string name)
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery,
                out var token))
            return false;

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, name, out var luid))
                return false;

            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.SePrivilegeEnabled
            };

            return NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
