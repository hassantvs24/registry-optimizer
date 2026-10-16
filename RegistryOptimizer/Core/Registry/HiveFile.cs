using Microsoft.Win32;

namespace RegistryOptimizer.Core.Registry;

/// <summary>Saves a registry subtree to a standalone hive file (RegSaveKey format).</summary>
public static class HiveFile
{
    /// <summary>Save <paramref name="keyPath"/> under <paramref name="hive"/> to <paramref name="filePath"/>.
    /// Overwrites an existing file. The result is a loadable registry hive.</summary>
    public static void SaveKey(string hive, string keyPath, string filePath, Action<string>? diagnostics = null)
    {
        diagnostics?.Invoke(NativeMethods.EnableBackupRestorePrivileges());
        using var key = RegistryWalker.OpenKey(hive, keyPath)
            ?? throw new InvalidOperationException($"Could not open '{hive}\\{keyPath}' for saving.");
        if (File.Exists(filePath)) File.Delete(filePath);
        NativeMethods.ThrowIfError(
            NativeMethods.RegSaveKeyExWithRetry(key.Handle, filePath, NativeMethods.REG_STANDARD_FORMAT, $"RegSaveKey of '{hive}\\{keyPath}'"),
            $"RegSaveKey of '{hive}\\{keyPath}'");
    }

    /// <summary>Load a hive file under a temporary subkey of HKLM (RegLoadKey). Returns a diagnostic string.</summary>
    public static string LoadKeyForProbe(string filePath, string mountName)
    {
        NativeMethods.EnableBackupRestorePrivileges();
        var err = NativeMethods.RegLoadKey(Microsoft.Win32.RegistryHive.LocalMachine, mountName, filePath);
        return err == NativeMethods.ERROR_SUCCESS ? "OK" : $"error {err}";
    }

    /// <summary>Unload a previously loaded hive. Returns a diagnostic string.</summary>
    public static string UnloadKeyForProbe(string mountName)
    {
        NativeMethods.EnableBackupRestorePrivileges();
        var err = NativeMethods.RegUnLoadKey(Microsoft.Win32.RegistryHive.LocalMachine, mountName);
        return err == NativeMethods.ERROR_SUCCESS ? "OK" : $"error {err}";
    }
}
