using Microsoft.Win32;
using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Defrag;

/// <summary>
/// Offline compaction of a hive FILE on disk. The caller must guarantee the
/// file is a copy (not a live, in-use hive): the file is loaded under
/// HKLM with RegLoadKey and re-saved with RegSaveKeyEx(REG_LATEST_FORMAT),
/// which strips the hive's internal free space.
/// </summary>
public static class HiveCompactor
{
    private const int REG_LATEST_FORMAT = 2;

    /// <summary>Compact <paramref name="sourceFile"/> into <paramref name="destFile"/>;
    /// returns the final destFile size in bytes.</summary>
    public static long CompactFile(string sourceFile, string destFile)
    {
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("Hive file to compact does not exist.", sourceFile);

        NativeMethods.EnableBackupRestorePrivileges();

        // RegLoadKey requires the target subkey to already exist under HKLM.
        // (NB: use RegistryWalker.MapHive — a bare 'Registry.LocalMachine' would resolve to the
        //  RegistryOptimizer.Core.Registry namespace instead of Microsoft.Win32.Registry.)
        var hklm = RegistryWalker.MapHive("HKLM");
        string mountName = PickFreeMountName();
        // NOTE: RegLoadKey creates the mount subkey itself — pre-creating it is unnecessary
        // (and creating keys directly under HKLM is blocked by some security products).

        try
        {
            int loadError;
            try
            {
                loadError = NativeMethods.RegLoadKey(RegistryHive.LocalMachine, mountName, sourceFile);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"RegLoadKey P/Invoke of '{sourceFile}' threw: {ex.Message}", ex);
            }
            NativeMethods.ThrowIfError(loadError, $"RegLoadKey of '{sourceFile}' (mount '{mountName}')");

            try
            {
                using var key = hklm.OpenSubKey(mountName, writable: false);
                if (key is null)
                    throw new InvalidOperationException($"Could not open mounted hive '{mountName}'.");

                if (File.Exists(destFile)) File.Delete(destFile);
                NativeMethods.ThrowIfError(
                    NativeMethods.RegSaveKeyEx(key.Handle, destFile, IntPtr.Zero, REG_LATEST_FORMAT),
                    $"RegSaveKeyEx of '{destFile}'");
            }
            finally
            {
                NativeMethods.ThrowIfError(
                    NativeMethods.RegUnLoadKey(RegistryHive.LocalMachine, mountName),
                    $"RegUnLoadKey of '{mountName}'");
            }

            return new FileInfo(destFile).Length;
        }
        finally
        {
            // Best effort: remove the (now empty) mount point we created.
            try { hklm.DeleteSubKey(mountName); } catch { /* ignore */ }
        }
    }

    private static string PickFreeMountName()
    {
        for (int i = 0; i < 100; i++)
        {
            var name = $"RegistryOptimizerCompact{i}";
            using var existing = RegistryWalker.MapHive("HKLM").OpenSubKey(name, writable: false);
            if (existing is null) return name;
        }
        throw new InvalidOperationException("Could not find a free temporary hive mount name.");
    }
}
