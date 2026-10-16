using System.Globalization;
using System.Text.Json;
using RegistryOptimizer.Core.Scanning;

namespace RegistryOptimizer.Core.Backup;

/// <summary>Manifest of one backup: which registry subtrees were exported and where.</summary>
public sealed class BackupSession
{
    private const string ManifestFile = "session.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Folder name / session id: yyyyMMdd-HHmmss in UTC.</summary>
    public required string Id { get; init; }
    public DateTime CreatedUtc { get; init; }
    /// <summary>Absolute path of the session folder.</summary>
    public required string Folder { get; init; }
    /// <summary>Absolute paths of the exported .reg files, import order.</summary>
    public List<string> RegFiles { get; } = new();
    /// <summary>Number of keys that were exported successfully.</summary>
    public int KeyCount { get; set; }
    public string? RestorePointId { get; set; }
    /// <summary>"Fix", "Manual" or "Defrag".</summary>
    public string Kind { get; set; } = "Fix";
    /// <summary>Non-fatal problems (e.g. keys that could not be exported).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Loads the session manifest stored in <paramref name="folder"/>.</summary>
    public static BackupSession FromFolder(string folder)
    {
        var json = File.ReadAllText(Path.Combine(folder, ManifestFile));
        return JsonSerializer.Deserialize<BackupSession>(json, JsonOptions)
               ?? throw new InvalidDataException($"Corrupt backup manifest: {folder}");
    }

    /// <summary>Writes session.json into <see cref="Folder"/>.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, ManifestFile), JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>
/// Creates registry backups before fixes / manual exports and enforces session retention.
/// Each session lives in %ProgramData%\RegistryOptimizer\Backups\&lt;Id&gt;\ and contains
/// one .reg file per exported key subtree plus a session.json manifest.
/// </summary>
public sealed class BackupService
{
    private readonly AppSettings _settings;

    /// <summary>Curated safe roots for a full manual backup. Never HKLM\SYSTEM/SAM/SECURITY.</summary>
    private static readonly (string Hive, string KeyPath)[] ManualRoots =
    {
        ("HKLM", @"SOFTWARE"),
        ("HKLM", @"SOFTWARE\WOW6432Node"),
        ("HKCU", @"SOFTWARE"),
    };

    public BackupService(AppSettings settings) => _settings = settings;

    /// <summary>
    /// Backs up every registry key affected by <paramref name="issues"/> (unique by
    /// Hive + KeyPath, case-insensitive) before the fixer touches them. Failures for a
    /// single key are recorded in the session notes; the remaining keys are still exported.
    /// </summary>
    public Task<BackupSession> BackupForFixAsync(IReadOnlyList<ScanIssue> issues, string? restorePointId = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var keys = issues
            .GroupBy(i => i.Hive + "|" + i.KeyPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Hive, g.First().KeyPath))
            .ToList();
        return BackupKeysAsync(keys, "Fix", restorePointId, progress, ct);
    }

    /// <summary>Full manual backup of the curated safe roots. Same folder structure as a fix backup.</summary>
    public Task<BackupSession> BackupManualAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        BackupKeysAsync(ManualRoots, "Manual", null, progress, ct);

    /// <summary>All backup sessions under BackupsRoot, newest first.</summary>
    public IReadOnlyList<BackupSession> ListSessions()
    {
        AppPaths.EnsureCreated();
        var sessions = new List<BackupSession>();
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.BackupsRoot))
        {
            if (!File.Exists(Path.Combine(dir, "session.json"))) continue;
            try
            {
                sessions.Add(BackupSession.FromFolder(dir));
            }
            catch
            {
                // unreadable manifest: leave the folder alone, skip it in the listing
            }
        }
        return sessions.OrderByDescending(s => s.CreatedUtc).ToList();
    }

    /// <summary>Keeps the newest <see cref="AppSettings.RetentionSessions"/> sessions and deletes the rest.</summary>
    public void ApplyRetention()
    {
        var keep = Math.Max(0, _settings.RetentionSessions);
        var sessions = ListSessions(); // newest first
        for (var i = keep; i < sessions.Count; i++)
        {
            try
            {
                Directory.Delete(sessions[i].Folder, recursive: true);
            }
            catch
            {
                // folder locked or partially deleted: leave it; retried on the next run
            }
        }
    }

    private async Task<BackupSession> BackupKeysAsync(IEnumerable<(string Hive, string KeyPath)> keys, string kind,
        string? restorePointId, IProgress<string>? progress, CancellationToken ct)
    {
        AppPaths.EnsureCreated();

        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(AppPaths.BackupsRoot, id);
        Directory.CreateDirectory(folder);

        var session = new BackupSession
        {
            Id = id,
            CreatedUtc = DateTime.UtcNow,
            Folder = folder,
            Kind = kind,
            RestorePointId = restorePointId,
        };

        var index = 0;
        foreach (var (hive, keyPath) in keys)
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var fileName = SanitizeFileName($"{index:D2}-{hive}-{LastSegment(keyPath)}.reg");
            var file = Path.Combine(folder, fileName);

            progress?.Report($"Backing up {hive}\\{keyPath} …");
            try
            {
                // The walk can touch thousands of keys; keep it off the UI thread.
                await Task.Run(() => RegistrySnapshot.ExportKey(hive, keyPath, file), ct);
                session.RegFiles.Add(file);
                session.KeyCount++;
            }
            catch (Exception ex)
            {
                session.Notes.Add($"Failed to export {hive}\\{keyPath}: {ex.Message}");
            }
        }

        session.Save();
        progress?.Report($"Backup {id} saved ({session.KeyCount} keys{(session.Notes.Count > 0 ? $", {session.Notes.Count} failed" : "")}).");
        ApplyRetention();
        return session;
    }

    /// <summary>Last path segment of the key, used to make the .reg file name readable.</summary>
    private static string LastSegment(string keyPath) =>
        string.IsNullOrEmpty(keyPath) ? "root" : keyPath.Split('\\').Last();

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
