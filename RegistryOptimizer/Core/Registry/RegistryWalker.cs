using Microsoft.Win32;

namespace RegistryOptimizer.Core.Registry;

/// <summary>Read-only helpers for safely walking registry trees.</summary>
public static class RegistryWalker
{
    /// <summary>Hard-off-limits areas: fixing anything here can brick the machine.</summary>
    private static readonly string[] ProtectedRoots =
    {
        @"SYSTEM",        // HKLM\SYSTEM
        @"SAM",           // HKLM\SAM
        @"SECURITY",      // HKLM\SECURITY
        @"HARDWARE",      // volatile
        @"BCD00000000",
    };

    public static bool IsProtected(string hive, string keyPath) =>
        hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) &&
        ProtectedRoots.Any(p => keyPath.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                keyPath.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Map a logical hive name to a base key.</summary>
    public static RegistryKey MapHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => Microsoft.Win32.Registry.CurrentUser,
        "HKCR" or "HKEY_CLASSES_ROOT" => Microsoft.Win32.Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Microsoft.Win32.Registry.Users,
        "HKCC" or "HKEY_CURRENT_CONFIG" => Microsoft.Win32.Registry.CurrentConfig,
        _ => throw new ArgumentException($"Unknown hive: {hive}", nameof(hive)),
    };

    /// <summary>Open a subkey read-only. Returns null when missing or inaccessible.</summary>
    public static RegistryKey? OpenKey(string hive, string keyPath)
    {
        try
        {
            return MapHive(hive).OpenSubKey(keyPath, writable: false);
        }
        catch
        {
            return null;
        }
    }

    public static RegistryKey? OpenKeyWritable(string hive, string keyPath)
    {
        try
        {
            return MapHive(hive).OpenSubKey(keyPath, writable: true);
        }
        catch
        {
            return null;
        }
    }

    public static object? GetValue(string hive, string keyPath, string? valueName)
    {
        using var key = OpenKey(hive, keyPath);
        return key?.GetValue(valueName);
    }

    /// <summary>List direct subkey names, swallowing access errors.</summary>
    public static IReadOnlyList<string> SubKeyNames(string hive, string keyPath)
    {
        using var key = OpenKey(hive, keyPath);
        if (key is null) return Array.Empty<string>();
        try
        {
            return key.GetSubKeyNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Enumerate a subtree with guards. Yields full paths relative to the hive root. Never throws.</summary>
    public static IEnumerable<string> WalkSubKeys(string hive, string rootPath, int maxDepth = 8, int maxKeys = 20_000)
    {
        var visited = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((rootPath, 0));
        while (stack.Count > 0)
        {
            if (++visited > maxKeys) yield break;
            var (path, depth) = stack.Pop();
            if (IsProtected(hive, path)) continue;
            yield return path;
            if (depth >= maxDepth) continue;
            foreach (var child in SubKeyNames(hive, path).Reverse())
                stack.Push(($"{path}\\{child}", depth + 1));
        }
    }

    /// <summary>List value names plus a string preview of their data.</summary>
    public static IReadOnlyList<(string Name, object? Data)> Values(string hive, string keyPath)
    {
        using var key = OpenKey(hive, keyPath);
        if (key is null) return Array.Empty<(string, object?)>();
        var result = new List<(string, object?)>();
        string[] names;
        try
        {
            names = key.GetValueNames();
        }
        catch
        {
            return result;
        }
        foreach (var name in names)
        {
            object? data = null;
            try
            {
                data = key.GetValue(name);
            }
            catch
            {
                // skip unreadable value
            }
            result.Add((name, data));
        }
        return result;
    }

    /// <summary>Convert registry data to a displayable / testable string.</summary>
    public static string? DataToString(object? data) => data switch
    {
        null => null,
        string s => s,
        string[] multi => string.Join("; ", multi.Take(4)),
        byte[] bytes => $"({bytes.Length} bytes)",
        _ => data.ToString(),
    };
}
