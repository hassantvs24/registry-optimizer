using System.Globalization;
using System.Text;
using Microsoft.Win32;
using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Backup;

/// <summary>
/// Exports a registry key subtree (the key itself plus all readable subkeys and values)
/// to a standard Windows .reg file that can be re-imported later with `reg.exe import`
/// to undo a fix.
/// </summary>
public static class RegistrySnapshot
{
    private const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>Hex lines are wrapped with a "\" continuation after this many characters.</summary>
    private const int HexLineWidth = 76;

    private static readonly Dictionary<string, string> HiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HKLM"] = "HKEY_LOCAL_MACHINE",
        ["HKCU"] = "HKEY_CURRENT_USER",
        ["HKCR"] = "HKEY_CLASSES_ROOT",
        ["HKU"]  = "HKEY_USERS",
        ["HKCC"] = "HKEY_CURRENT_CONFIG",
    };

    /// <summary>
    /// Writes a REGEDIT5 (.reg) file capturing <paramref name="keyPath"/> (including the key
    /// itself and every readable subkey/value) under <paramref name="hive"/>.
    /// </summary>
    /// <param name="hive">Logical hive name: HKLM, HKCU, HKCR, HKU or HKCC.</param>
    /// <param name="keyPath">Key path without the hive prefix; empty exports the hive root.</param>
    /// <param name="filePath">Destination .reg file. Its parent directory is created if missing.</param>
    public static void ExportKey(string hive, string keyPath, string filePath)
    {
        if (!HiveNames.TryGetValue(hive, out var fullHive))
            throw new ArgumentException($"Unknown hive: {hive}", nameof(hive));

        using var root = RegistryWalker.OpenKey(hive, keyPath)
            ?? throw new InvalidOperationException($"Cannot open {hive}\\{keyPath} for export.");

        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var sb = new StringBuilder();
        sb.AppendLine(Header).AppendLine();

        var fullPath = string.IsNullOrEmpty(keyPath) ? fullHive : fullHive + "\\" + keyPath;
        WriteKey(sb, root, fullPath);

        // UTF-16LE with BOM: the encoding reg.exe expects for Version 5.00 files.
        File.WriteAllText(filePath, sb.ToString(), Encoding.Unicode);
    }

    /// <summary>Appends one [key] section and recurses into readable subkeys. Never throws on access errors.</summary>
    private static void WriteKey(StringBuilder sb, RegistryKey key, string fullPath)
    {
        sb.Append('[').Append(fullPath).AppendLine("]");

        string[] valueNames;
        try
        {
            valueNames = key.GetValueNames();
        }
        catch
        {
            valueNames = Array.Empty<string>();
        }

        foreach (var name in valueNames)
        {
            object? data;
            RegistryValueKind kind;
            try
            {
                kind = key.GetValueKind(name);
                data = key.GetValue(name);
            }
            catch
            {
                continue; // unreadable value: skip it
            }

            sb.Append(EncodeName(name)).Append('=');
            WriteValueData(sb, kind, data);
            sb.AppendLine();
        }
        sb.AppendLine();

        string[] subKeyNames;
        try
        {
            subKeyNames = key.GetSubKeyNames();
        }
        catch
        {
            subKeyNames = Array.Empty<string>();
        }

        foreach (var subName in subKeyNames)
        {
            RegistryKey? child = null;
            try
            {
                child = key.OpenSubKey(subName, writable: false);
            }
            catch
            {
                // No permission or other error: skip this subtree (best effort).
            }
            if (child is null) continue;
            using (child)
            {
                WriteKey(sb, child, fullPath + "\\" + subName);
            }
        }
    }

    /// <summary>The default value is written as "@", everything else as a quoted (escaped) name.</summary>
    private static string EncodeName(string name)
    {
        if (name.Length == 0) return "@";
        var sb = new StringBuilder(name.Length + 2);
        sb.Append('"');
        foreach (var c in name)
        {
            if (c is '\\' or '"') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void WriteValueData(StringBuilder sb, RegistryValueKind kind, object? data)
    {
        switch (kind)
        {
            case RegistryValueKind.String:
                WriteString(sb, data as string ?? string.Empty);
                break;
            case RegistryValueKind.ExpandString:
                WriteHex(sb, "hex(2)", Utf16Bytes(data as string ?? string.Empty, terminate: true));
                break;
            case RegistryValueKind.DWord:
                var dword = data switch
                {
                    int i => (uint)i,
                    uint u => u,
                    _ => Convert.ToUInt32(data, CultureInfo.InvariantCulture),
                };
                sb.Append("dword:").Append(dword.ToString("x8", CultureInfo.InvariantCulture));
                break;
            case RegistryValueKind.QWord:
                var qword = data switch
                {
                    long l => (ulong)l,
                    ulong ul => ul,
                    _ => Convert.ToUInt64(data, CultureInfo.InvariantCulture),
                };
                WriteHex(sb, "hex(b)", BitConverter.GetBytes(qword)); // BitConverter is little-endian
                break;
            case RegistryValueKind.MultiString:
                WriteHex(sb, "hex(7)", MultiStringBytes(data as string[] ?? Array.Empty<string>()));
                break;
            case RegistryValueKind.Binary:
            case RegistryValueKind.Unknown:
            default:
                WriteHex(sb, "hex", data as byte[] ?? Array.Empty<byte>());
                break;
        }
    }

    /// <summary>
    /// Plain ASCII strings are written as quoted, escaped text; strings containing non-ASCII
    /// characters are written as hex(1) UTF-16LE byte lists so they round-trip independently
    /// of any file-encoding assumptions.
    /// </summary>
    private static void WriteString(StringBuilder sb, string value)
    {
        if (value.All(c => c <= 0x7F))
        {
            sb.Append('"');
            foreach (var c in value)
            {
                if (c is '\\' or '"') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
        }
        else
        {
            WriteHex(sb, "hex(1)", Utf16Bytes(value, terminate: true));
        }
    }

    /// <summary>REG_MULTI_SZ: strings separated by a single NUL, terminated by a double NUL.</summary>
    private static byte[] MultiStringBytes(string[] values)
    {
        var joined = values.Length == 0
            ? "\0"
            : string.Join("\0", values) + "\0\0";
        return Encoding.Unicode.GetBytes(joined);
    }

    private static byte[] Utf16Bytes(string value, bool terminate) =>
        Encoding.Unicode.GetBytes(terminate ? value + "\0" : value);

    /// <summary>
    /// Writes "tag:xx,xx,…" with comma-separated lowercase hex bytes, wrapped with a
    /// backslash-continuation whenever a line would exceed <see cref="HexLineWidth"/>.
    /// The trailing comma stays at the end of the previous line, exactly like regedit does.
    /// </summary>
    private static void WriteHex(StringBuilder sb, string tag, byte[] bytes)
    {
        sb.Append(tag).Append(':');
        var col = tag.Length + 1;
        for (var i = 0; i < bytes.Length; i++)
        {
            var token = bytes[i].ToString("x2", CultureInfo.InvariantCulture);
            var trailingComma = i < bytes.Length - 1 ? 1 : 0;
            if (col + token.Length + trailingComma > HexLineWidth)
            {
                sb.Append("\\\r\n");
                col = 0;
            }
            sb.Append(token);
            col += token.Length;
            if (trailingComma == 1)
            {
                sb.Append(',');
                col++;
            }
        }
    }
}

/*
 * SELF-TEST / ENCODING RULES
 * --------------------------
 * Output format: "Windows Registry Editor Version 5.00" (REGEDIT5), saved as UTF-16LE with BOM
 * — the exact encoding reg.exe expects for Unicode .reg files.
 *
 *  - Keys become "[HKEY_...\path]" sections; unreadable subkeys/values are skipped silently.
 *  - The default value is written as "@"="" (or its typed equivalent).
 *  - Value names and REG_SZ text escape '\' as '\\' and '"' as '\"' (the only escapes .reg supports).
 *  - REG_SZ values that contain only ASCII are written as quoted text. Non-ASCII REG_SZ values
 *    are written as hex(1): with UTF-16LE bytes (NUL-terminated) — reg.exe imports these as REG_SZ,
 *    and the data survives regardless of file encoding.
 *  - REG_EXPAND_SZ -> hex(2): UTF-16LE bytes, NUL-terminated.
 *  - REG_MULTI_SZ  -> hex(7): UTF-16LE bytes, strings joined by NUL, double NUL at the end
 *    (empty list = a single NUL).
 *  - REG_DWORD     -> dword:xxxxxxxx (8 lowercase hex digits, little-endian value).
 *  - REG_QWORD     -> hex(b): followed by the 8 little-endian bytes of the value.
 *  - REG_BINARY / unknown -> hex: comma-separated bytes.
 *  - Hex lists wrap at 76 columns using a trailing '\' line continuation (comma stays on the
 *    previous line), matching regedit's layout; reg.exe joins the lines before parsing.
 *
 * Round-trip verification: export any key, then run
 *     reg.exe import "<file>"
 * and confirm "The operation completed successfully." plus the values being recreated.
 */
