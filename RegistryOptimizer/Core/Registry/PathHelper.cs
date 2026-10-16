using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RegistryOptimizer.Core.Registry;

/// <summary>Expansion and classification helpers for registry file references.</summary>
public static class PathHelper
{
    private static readonly Dictionary<string, string> WellKnownReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["%SystemRoot%"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["%WinDir%"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["%SystemDrive%"] = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:",
        ["%ProgramFiles%"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["%ProgramFiles(x86)%"] = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ["%CommonProgramFiles%"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        ["%CommonProgramFiles(x86)%"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        ["%AppData%"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["%LocalAppData%"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["%UserProfile%"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["%Public%"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).Replace("Roaming", "Public"),
        ["%AllUsersProfile%"] = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
        ["%ProgramData%"] = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
        ["%Temp%"] = Path.GetTempPath(),
        ["%Tmp%"] = Path.GetTempPath(),
    };

    /// <summary>Expand environment variables and well-known CSIDL placeholders (some apps write %ProgramFiles% literally). Idempotent.</summary>
    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var result = Environment.ExpandEnvironmentVariables(path);
        foreach (var (token, value) in WellKnownReplacements)
            result = ReplaceOrdinalIgnoreCase(result, token, value);
        return result;
    }

    private static string ReplaceOrdinalIgnoreCase(string haystack, string needle, string replacement)
    {
        var idx = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            haystack = string.Concat(haystack.AsSpan(0, idx), replacement, haystack.AsSpan(idx + needle.Length));
            idx = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }
        return haystack;
    }

    /// <summary>
    /// References we deliberately never flag: relative names, plain executables (no path),
    /// UNC/network paths, devices, URLs, and entries Windows recreates constantly.
    /// </summary>
    public static bool IsSkippableReference(string reference)
    {
        var r = reference.Trim();
        if (r.Length < 3) return true;
        if (r.StartsWith(@"\\", StringComparison.Ordinal)) return true;        // network
        if (r.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        if (!r.Contains(':') && !r.StartsWith(@"\", StringComparison.Ordinal)) return true; // bare name (found via PATH)
        if (r.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            r.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (r.StartsWith("device\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Extract a filesystem path from common command-line styles: "C:\path\app.exe" -arg, rundll32 "C:\x.dll",Fn.</summary>
    public static string? ExtractFilePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Expand(command.Trim());
        // quoted path
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text.Substring(1, end - 1) : null;
        }
        var space = text.IndexOf(' ');
        if (space < 0) return text;
        var candidate = text.Substring(0, space);
        // rundll32 style: C:\path.dll,Entry
        var comma = candidate.IndexOf(',');
        if (comma > 0) candidate = candidate.Substring(0, comma);
        return candidate;
    }
}
