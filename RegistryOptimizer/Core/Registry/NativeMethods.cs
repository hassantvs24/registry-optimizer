using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RegistryOptimizer.Core.Registry;

/// <summary>Minimal advapi32/kernel32 interop for hive save/load/replace and privilege elevation.</summary>
internal static class NativeMethods
{
    // ---- registry hive operations ----
    [DllImport("advapi32.dll", EntryPoint = "RegSaveKeyExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int RegSaveKeyEx(SafeRegistryHandle hKey, string lpFile, IntPtr lpSecurityAttributes, int Flags);

    [DllImport("advapi32.dll", EntryPoint = "RegLoadKeyW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int RegLoadKey(RegistryHive hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", EntryPoint = "RegUnLoadKeyW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int RegUnLoadKey(RegistryHive hKey, string lpSubKey);

    [DllImport("advapi32.dll", EntryPoint = "RegReplaceKeyW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int RegReplaceKey(SafeRegistryHandle hKey, string lpSubKey, string lpNewFile, string lpOldFile);

    // ---- privileges ----
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, IntPtr newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // RegSaveKeyEx format flags: 0 is NOT accepted by Windows (ERROR_INVALID_PARAMETER).
    internal const int REG_STANDARD_FORMAT = 1;
    internal const int REG_LATEST_FORMAT = 2;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    internal const int ERROR_SUCCESS = 0;

    /// <summary>Enables SE_BACKUP_NAME and SE_RESTORE_NAME for the current process (needed for RegSaveKey/RegReplaceKey).
    /// Returns a diagnostic line per privilege for troubleshooting.</summary>
    public static string EnableBackupRestorePrivileges()
    {
        var log = new List<string>();
        try
        {
            // VSS/COM work can leave the calling thread impersonating; privilege enabling and
            // the subsequent Reg* calls must run under the process token, so revert first.
            RevertToSelf();
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            {
                return $"OpenProcessToken failed: {Marshal.GetLastWin32Error()}";
            }
            try
            {
                foreach (var privilege in new[] { "SeBackupPrivilege", "SeRestorePrivilege" })
                {
                    if (!LookupPrivilegeValue(null, privilege, out var luid))
                    {
                        log.Add($"{privilege}: LookupPrivilegeValue failed ({Marshal.GetLastWin32Error()})");
                        continue;
                    }
                    // Manual buffer marshaling: the struct-based route has proven unreliable
                    // here (AdjustTokenPrivileges kept returning ERROR_NOT_ALL_ASSIGNED for
                    // privileges that are clearly present), while raw buffers work.
                    var buffer = Marshal.AllocHGlobal(16);
                    try
                    {
                        Marshal.WriteInt32(buffer, 0, 1);            // PrivilegeCount
                        Marshal.WriteInt64(buffer, 4, luid);         // Luid
                        Marshal.WriteInt32(buffer, 12, (int)SE_PRIVILEGE_ENABLED);
                        if (!AdjustTokenPrivileges(token, false, buffer, 0, IntPtr.Zero, IntPtr.Zero))
                        {
                            log.Add($"{privilege}: AdjustTokenPrivileges failed ({Marshal.GetLastWin32Error()})");
                            continue;
                        }
                        var err = Marshal.GetLastWin32Error();
                        log.Add(err == ERROR_SUCCESS
                            ? $"{privilege}: enabled"
                            : $"{privilege}: NOT enabled (Win32 {err})");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception ex)
        {
            log.Add("exception: " + ex.Message);
        }
        return string.Join("; ", log);
    }

    /// <summary>RegSaveKeyEx with a single retry after re-enabling privileges — privilege
    /// enabling has proven occasionally flaky right after process start.</summary>
    public static int RegSaveKeyExWithRetry(SafeRegistryHandle hKey, string filePath, int flags, string operation)
    {
        var err = RegSaveKeyEx(hKey, filePath, IntPtr.Zero, flags);
        if (err == 1314) // ERROR_PRIVILEGE_NOT_HELD
        {
            EnableBackupRestorePrivileges();
            err = RegSaveKeyEx(hKey, filePath, IntPtr.Zero, flags);
        }
        return err;
    }

    // ---- delayed file operations (used by the defrag boot helper fallback) ----
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    internal const int MOVEFILE_REPLACE_EXISTING = 0x1;
    internal const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    public static void ThrowIfError(int error, string operation)
    {
        if (error != ERROR_SUCCESS)
            throw new InvalidOperationException($"{operation} failed with Win32 error {error}.");
    }
}
