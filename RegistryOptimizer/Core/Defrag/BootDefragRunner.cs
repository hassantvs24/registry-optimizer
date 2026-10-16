using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Defrag;

/// <summary>Per-hive outcome written into the boot-defrag report.</summary>
public sealed class HiveReportEntry
{
    public required string Name { get; init; }
    public long BeforeBytes { get; set; }
    public long AfterBytes { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    /// <summary>Internal hive free space before the pass (null when unknown).</summary>
    public double? BeforeFreePercent { get; set; }
    /// <summary>Internal hive free space of the file that will be swapped in.</summary>
    public double? AfterFreePercent { get; set; }
    /// <summary>Why a hive was left untouched (in use / already compact). Success is false and Error is null.</summary>
    public string? SkippedReason { get; set; }
    public bool Skipped => !Success && Error is null && SkippedReason is not null;
}

/// <summary>Result of a boot-time defrag run, persisted as JSON.</summary>
public sealed class DefragReport
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DateTime RanAtUtc { get; set; } = DateTime.UtcNow;
    public List<HiveReportEntry> Hives { get; } = new();
    public string? SavedTo { get; set; }
}

/// <summary>
/// Runs at the next boot (invoked via HKLM RunOnce with --boot-defrag, at first logon).
/// Safe NTREGOPT-style flow:
///   System hives: RegSaveKey (live hive) -> [coalesce if internally fragmented] -> RegReplaceKey,
///     which makes Session Manager atomically swap the hive files at the FOLLOWING boot.
///   Per-user hives (other, logged-off users): backup -> offline coalesce -> MoveFileEx
///     (MOVEFILE_DELAY_UNTIL_REBOOT); the logged-on user's hives are skipped (locked).
/// Never replaces hive files directly at runtime. Robust and quiet: nothing here may
/// block boot, so everything is caught and recorded.
/// </summary>
public static class BootDefragRunner
{
    /// <summary>Hives at or above this internal free-space percentage get the coalescing pass.</summary>
    public const double CoalesceThresholdPercent = 5.0;

    public static int Run(Action<string>? log = null)
    {
        AppPaths.EnsureCreated();
        var report = new DefragReport();
        string? workDir = null;

        void Write(string line)
        {
            try { log?.Invoke(line); } catch { /* UI callback must never break boot */ }
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.DefragReportsRoot, "boot-defrag-log.txt"),
                    $"{DateTime.UtcNow:u}  {line}{Environment.NewLine}");
            }
            catch { /* logging is best effort */ }
        }

        try
        {
            Write("Boot defrag started.");
            NativeMethods.EnableBackupRestorePrivileges();

            // System AND per-user hives; PrepareHive decides what can be done with each.
            var hives = HiveLocator.Locate();

            // Replacement hive files AND backup paths are consumed by Session Manager (or the
            // delayed-move engine) at the FOLLOWING boot, so they must survive this session —
            // keep everything in a persistent "pending" folder and prune stale folders from
            // earlier runs (the newest one may still be awaited).
            PruneStalePendingFolders();
            workDir = Path.Combine(AppPaths.DefragReportsRoot, $"pending-{DateTime.UtcNow.Ticks}");
            Directory.CreateDirectory(workDir);

            foreach (var hive in hives)
            {
                Write($"Processing {hive.Name} ...");
                var entry = PrepareHive(hive, workDir);
                report.Hives.Add(entry);

                var free = entry.BeforeFreePercent is null ? "" :
                    $", internal free {entry.BeforeFreePercent:F1}% -> {(entry.AfterFreePercent ?? entry.BeforeFreePercent):F1}%";
                if (entry.Success)
                    Write($"{hive.Name}: OK — {entry.BeforeBytes:N0} -> {entry.AfterBytes:N0} bytes{free} (swap scheduled at next boot).");
                else if (entry.Skipped)
                    Write($"{hive.Name}: SKIPPED — {entry.SkippedReason}");
                else
                    Write($"{hive.Name}: FAILED — {entry.Error}");
            }

            // Persist report, update status, defensively remove the RunOnce entry.
            try
            {
                var reportPath = DefragScheduler.BootDefragReportPath();
                report.SavedTo = reportPath;
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, DefragReport.JsonOptions));

                var status = DefragStatus.Load();
                status.Scheduled = false;
                status.BootRunCompleted = true;
                status.BootRunUtc = DateTime.UtcNow;
                status.LastReportFile = reportPath;
                status.Save();

                Write($"Report saved to {reportPath}.");
            }
            catch (Exception ex)
            {
                Write($"Could not save report/status: {ex.Message}");
            }
            finally
            {
                try
                {
                    using var runOnce = RegistryWalker.MapHive("HKLM").OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
                    runOnce?.DeleteValue(DefragScheduler.RunOnceValueName, throwOnMissingValue: false);
                }
                catch { /* best effort */ }
            }

            var ok = report.Hives.Count(e => e.Success);
            var skipped = report.Hives.Count(e => e.Skipped);
            Write($"Boot defrag finished: {ok} optimized, {skipped} skipped, {report.Hives.Count - ok - skipped} failed.");
            return 0; // always a quiet success at boot, even with partial failures
        }
        catch (Exception ex)
        {
            Write($"Boot defrag aborted unexpectedly: {ex}");
            return 1;
        }
        finally
        {
            // Deliberately NOT deleting workDir: SM needs the replacement hives + backups at the
            // next boot. Old pending folders are pruned at the start of the next boot-defrag run.
        }
    }

    /// <summary>
    /// Prepare one hive for swap at the next boot. System hives go through RegSaveKey +
    /// RegReplaceKey; per-user hives of logged-off users through offline compaction +
    /// a delayed file move. Never throws.
    /// </summary>
    public static HiveReportEntry PrepareHive(HiveInfo hive, string workDir)
    {
        var entry = new HiveReportEntry { Name = hive.Name };
        try
        {
            NativeMethods.EnableBackupRestorePrivileges();
            var safeName = string.Concat(hive.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            var backupFile = Path.Combine(workDir, safeName + ".bak");

            if (hive.IsSystemHive)
                PrepareSystemHive(hive, workDir, safeName, backupFile, entry);
            else
                PrepareUserHive(hive, workDir, safeName, backupFile, entry);

            if (entry.SkippedReason is null)
                entry.Success = true;
        }
        catch (Exception ex)
        {
            entry.Success = false;
            entry.Error = ex.Message;
        }
        return entry;
    }

    /// <summary>System hive: save live key, coalesce when fragmented, register the SM swap.</summary>
    private static void PrepareSystemHive(HiveInfo hive, string workDir, string safeName, string backupFile, HiveReportEntry entry)
    {
        var relativeKey = hive.LogicalKey.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
            ? hive.LogicalKey.Substring(@"HKLM\".Length)
            : hive.LogicalKey;
        if (string.IsNullOrEmpty(relativeKey))
            throw new InvalidOperationException("Hive has no logical key to open.");

        entry.BeforeBytes = File.Exists(hive.FilePath) ? new FileInfo(hive.FilePath).Length : hive.FileSizeBytes;

        var savedFile = Path.Combine(workDir, safeName + ".save");
        if (File.Exists(savedFile)) File.Delete(savedFile);
        using (var key = RegistryWalker.MapHive("HKLM").OpenSubKey(relativeKey, writable: false))
        {
            if (key is null)
                throw new InvalidOperationException($"Could not open '{hive.LogicalKey}' (access denied or hive not loaded).");
            NativeMethods.ThrowIfError(
                NativeMethods.RegSaveKeyExWithRetry(key.Handle, savedFile, NativeMethods.REG_STANDARD_FORMAT, $"RegSaveKey of '{hive.LogicalKey}'"),
                $"RegSaveKey of '{hive.LogicalKey}'");
        }

        // The RegSaveKey output already reclaims file-level slack. If the hive is also
        // internally fragmented (>= threshold free cells), the load + re-save round-trip
        // coalesces those gaps — keep whichever output is smaller.
        var chosen = ChooseCompactedOutput(savedFile, workDir, safeName, entry);
        entry.AfterBytes = new FileInfo(chosen).Length;

        // Back up the original hive file; without a backup path RegReplaceKey cannot be used.
        if (File.Exists(backupFile)) File.Delete(backupFile);
        File.Copy(hive.FilePath, backupFile);

        // Ask Session Manager to atomically swap in the prepared hive at the next boot.
        using (var key = RegistryWalker.MapHive("HKLM").OpenSubKey(relativeKey, writable: false))
        {
            if (key is null)
                throw new InvalidOperationException($"Hive '{hive.LogicalKey}' disappeared before RegReplaceKey.");
            // lpSubKey must be NULL per RegReplaceKey semantics (NativeMethods declares it as string).
            NativeMethods.ThrowIfError(
                NativeMethods.RegReplaceKey(key.Handle, (string)null!, chosen, backupFile),
                $"RegReplaceKey of '{hive.LogicalKey}'");
        }
    }

    /// <summary>
    /// Per-user hive of a logged-off user: the file is unloaded, so it can be compacted offline
    /// and scheduled for an atomic replace at the next boot via MOVEFILE_DELAY_UNTIL_REBOOT.
    /// The logged-on user's hives are locked and reported as skipped.
    /// </summary>
    private static void PrepareUserHive(HiveInfo hive, string workDir, string safeName, string backupFile, HiveReportEntry entry)
    {
        if (IsFileLocked(hive.FilePath))
        {
            entry.SkippedReason = "in use (user is logged on) — will be optimized at a boot when this account is logged off";
            return;
        }

        entry.BeforeBytes = new FileInfo(hive.FilePath).Length;
        entry.BeforeFreePercent = InternalFreePercent(hive.FilePath);
        if (entry.BeforeFreePercent is < CoalesceThresholdPercent)
        {
            entry.SkippedReason = $"already compact ({entry.BeforeFreePercent:F1}% internal free space)";
            return;
        }

        // Same-volume requirement of MOVEFILE_DELAY_UNTIL_REBOOT.
        if (!string.Equals(Path.GetPathRoot(hive.FilePath), Path.GetPathRoot(workDir), StringComparison.OrdinalIgnoreCase))
        {
            entry.SkippedReason = "hive is on a different volume than the pending folder; delayed replace unsupported";
            return;
        }

        if (File.Exists(backupFile)) File.Delete(backupFile);
        File.Copy(hive.FilePath, backupFile);

        var compacted = Path.Combine(workDir, safeName + ".compact");
        if (File.Exists(compacted)) File.Delete(compacted);
        HiveCompactor.CompactFile(hive.FilePath, compacted);
        entry.AfterBytes = new FileInfo(compacted).Length;
        entry.AfterFreePercent = InternalFreePercent(compacted);

        NativeMethods.ThrowIfError(
            NativeMethods.MoveFileEx(hive.FilePath, compacted,
                NativeMethods.MOVEFILE_REPLACE_EXISTING | NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT)
                ? NativeMethods.ERROR_SUCCESS : Marshal.GetLastWin32Error(),
            $"MoveFileEx delayed replace of '{hive.FilePath}'");
    }

    /// <summary>
    /// Decide which file should be swapped in for a saved hive. Measures the internal free
    /// space of <paramref name="savedFile"/>; when fragmented (>= <see cref="CoalesceThresholdPercent"/>)
    /// it attempts the RegLoadKey + RegSaveKeyEx coalescing round-trip and prefers the smaller
    /// result. Fills the entry's free-percent fields. Never throws.
    /// </summary>
    public static string ChooseCompactedOutput(string savedFile, string workDir, string safeName, HiveReportEntry entry)
    {
        entry.BeforeFreePercent = InternalFreePercent(savedFile);
        var chosen = savedFile;
        if (entry.BeforeFreePercent >= CoalesceThresholdPercent)
        {
            try
            {
                var compacted = Path.Combine(workDir, safeName + ".compact");
                if (File.Exists(compacted)) File.Delete(compacted);
                HiveCompactor.CompactFile(savedFile, compacted);
                if (new FileInfo(compacted).Length < new FileInfo(savedFile).Length)
                    chosen = compacted;
            }
            catch { /* coalescing is best effort — the plain save is always a valid fallback */ }
        }
        entry.AfterFreePercent = InternalFreePercent(chosen);
        return chosen;
    }

    /// <summary>Internal free-space percentage of a plain (unlocked) hive file; NaN when unreadable.</summary>
    public static double InternalFreePercent(string hiveFile)
    {
        try
        {
            var probe = new HiveInfo { Name = "probe", LogicalKey = string.Empty, FilePath = hiveFile, IsSystemHive = false };
            var analysis = HiveFragmentationAnalyzer.Analyze(new[] { probe });
            var h = analysis.Hives[0];
            return h.Error is null ? h.FreeSpacePercent : double.NaN;
        }
        catch
        {
            return double.NaN;
        }
    }

    /// <summary>True when the file cannot be exclusively opened (in use) or is not readable/writable.</summary>
    private static bool IsFileLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Keeps only the newest pending-defrag folder (older ones were already consumed by SM).</summary>
    private static void PruneStalePendingFolders()
    {
        try
        {
            var pending = Directory.GetDirectories(AppPaths.DefragReportsRoot, "pending-*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var dir in pending.Skip(1))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* locked by SM -> skip */ }
            }
        }
        catch { /* pruning is best effort */ }
    }
}
