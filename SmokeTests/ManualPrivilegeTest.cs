using System.Runtime.InteropServices;

namespace SmokeTests;

/// <summary>Ground-truth privilege enabling with manual buffer marshaling.</summary>
static class ManualPrivilegeTest
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll, IntPtr newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;

    public static string EnableBackupRestore()
    {
        var bits = Environment.Is64BitProcess ? 64 : 32;
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return $"OpenProcessToken failed ({Marshal.GetLastWin32Error()}) bits={bits}";

        try
        {
            var results = new List<string>();
            foreach (var priv in new[] { "SeBackupPrivilege", "SeRestorePrivilege" })
            {
                if (!LookupPrivilegeValue(null, priv, out var luid))
                {
                    results.Add($"{priv}: lookup failed ({Marshal.GetLastWin32Error()}) luid={luid:X}");
                    continue;
                }
                // TOKEN_PRIVILEGES: DWORD count; LUID (2xDWORD); DWORD attributes
                var buffer = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.WriteInt32(buffer, 0, 1);                 // PrivilegeCount
                    Marshal.WriteInt64(buffer, 4, luid);              // Luid
                    Marshal.WriteInt32(buffer, 12, 0x2);              // SE_PRIVILEGE_ENABLED
                    var ok = AdjustTokenPrivileges(token, false, buffer, 0, IntPtr.Zero, IntPtr.Zero);
                    var err = Marshal.GetLastWin32Error();
                    results.Add($"{priv}: adjusted={ok} err={err} luid={luid:X}");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return string.Join("; ", results);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
