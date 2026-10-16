using Microsoft.Win32;
using RegistryOptimizer.Core;
using RegistryOptimizer.Core.Backup;
using RegistryOptimizer.Core.Defrag;
using RegistryOptimizer.Core.Registry;
using RegistryOptimizer.Core.Scanning;
using SmokeTests;

// SmokeTests — end-to-end validation of the engine against the real machine (no admin needed).
// Exercises: Quick scan (read-only), fix + undo round-trip on a disposable HKCU startup value,
// .reg backup round-trip, fragmentation analysis (direct + VSS fallback when elevated).

int failures = 0;
void Check(bool condition, string label)
{
    Console.WriteLine(condition ? $"  PASS  {label}" : $"  FAIL  {label}");
    if (!condition) failures++;
}

AppPaths.EnsureCreated();
var settings = new AppSettings();
var scanner = new ScanService(settings);
var elevatedOnly = args.Contains("--elevated");

// Elevated runs are launched detached (ShellExecute) so their console output is lost —
// tee everything to ProgramData so results are inspectable afterwards.
TextWriter logFile;
try
{
    AppPaths.EnsureCreated();
    logFile = new StreamWriter(Path.Combine(AppPaths.DataRoot, "elevated-smoke.log")) { AutoFlush = true };
    Console.SetOut(new TeeWriter(Console.Out, logFile));
}
catch { /* logging is best effort */ }

if (args.Contains("--vssprobe")) return VssProbe.Run();

// ---------------------------------------------------------------------------
Console.WriteLine("== 1. Quick scan (read-only) ==");
if (elevatedOnly) goto fragmentation;
var status = new Progress<string>(s => Console.WriteLine($"    [{s}]"));
var counts = new Dictionary<ScanCategory, int>();
var sw = System.Diagnostics.Stopwatch.StartNew();
var result = await scanner.ScanAsync(ScanProfile.Quick, null, status);
sw.Stop();
foreach (var issue in result.Issues)
    counts[issue.Category] = counts.TryGetValue(issue.Category, out var c) ? c + 1 : 1;
Console.WriteLine($"    completed in {sw.Elapsed:mm\\:ss}, cancelled={result.Cancelled}");
foreach (var (cat, n) in counts.OrderBy(kv => kv.Key))
    Console.WriteLine($"    {cat,-28} {n}");
Check(!result.Cancelled, "quick scan completes without cancellation");
Check(result.Issues.All(i => i.CanFix), "every reported issue has a fix action");
Check(result.Issues.All(i => !string.IsNullOrEmpty(i.FullPath) && !string.IsNullOrEmpty(i.Reason)),
    "every issue has a path and a reason");

// ---------------------------------------------------------------------------
Console.WriteLine("== 2. Fix + undo round-trip on a disposable HKCU Run value ==");
const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
const string testValue = "RegistryOptimizerSmokeTest";
using (var runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true)!)
    runKey.SetValue(testValue, @"C:\Nonexistent\SmokeTest.exe");
Console.WriteLine($"    planted {testValue} under HKCU\\{runKeyPath}");

var result2 = await scanner.ScanAsync(ScanProfile.Quick, null, status);
var planted = result2.Issues.FirstOrDefault(i =>
    i.Hive == "HKCU" && i.ValueName == testValue);
Check(planted is not null, "scanner detects the planted missing startup entry");

if (planted is not null)
{
    var backup = new BackupService(settings);
    var session = await backup.BackupForFixAsync(new[] { planted });
    Check(session.RegFiles.Count > 0 && session.RegFiles.All(File.Exists),
        $"backup exported ({session.RegFiles.Count} .reg file(s))");

    var fix = IssueFixer.Apply(new[] { planted });
    Check(fix.Failed == 0, $"fixer applied (fixed={fix.Fixed})");
    using var afterFix = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
    Check(afterFix?.GetValue(testValue) is null, "value deleted from registry");

    var undo = new UndoService(settings);
    var undoResult = await undo.RollbackAsync(session);
    if (undoResult.Failed > 0)
        Console.WriteLine("    failures: " + string.Join(" | ", undoResult.Failures));
    Check(undoResult.Failed == 0, $"undo imported backups (restored={undoResult.Restored})");
    using var afterUndo = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
    Check(Equals(afterUndo?.GetValue(testValue), @"C:\Nonexistent\SmokeTest.exe"),
        "value restored byte-for-byte by undo");
}
using (var cleanup = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true))
    cleanup?.DeleteValue(testValue, throwOnMissingValue: false);

// ---------------------------------------------------------------------------
Console.WriteLine("== 3. .reg export round-trip (all registry value kinds) ==");
var exportOk = TestRegExportRoundTrip();
Check(exportOk, "export -> delete -> import restores every value kind");

// ---------------------------------------------------------------------------
fragmentation:
Console.WriteLine("== 4. Fragmentation analysis ==");
try
{
    var elevated = Environment.IsPrivilegedProcess;
    var hives = HiveLocator.Locate();
    Check(elevated ? hives.Count >= 4 : hives.Count >= 2,
        $"hive locator found {hives.Count} hives (non-elevated runs cannot see {Environment.GetFolderPath(Environment.SpecialFolder.Windows)}\\System32\\config)");
    var analysis = HiveFragmentationAnalyzer.Analyze(hives,
        new Progress<string>(s => Console.WriteLine($"    [{s}]")));
    foreach (var h in analysis.Hives)
        Console.WriteLine($"    {h.Name,-28} {(h.Error is null ? $"{h.FileSizeBytes / 1024.0 / 1024.0:F1} MB, frag {h.FragmentationPercent:F1}%, free {h.FreeSpacePercent:F1}%" : "error: " + h.Error)}");
    Console.WriteLine($"    elevated={elevated}");
    Check(elevated || analysis.Hives.All(h => h.Error is not null),
        "non-elevated run degrades gracefully (hives locked, VSS needs admin)");
    if (elevated)
    {
        Check(analysis.Hives.All(h => h.Error is null), "VSS path parsed ALL hives when elevated");
        Check(analysis.OverallFragmentationPercent is >= 0 and <= 100, "fragmentation percent in range");

        // 5) Compactor round-trip: copy the SOFTWARE hive out of the VSS snapshot, compact the copy.
        var software = hives.First(h => h.Name == "SOFTWARE");
        var copy = Path.Combine(Path.GetTempPath(), $"ro-hivecopy-{Guid.NewGuid():N}.dat");
        var compacted = Path.ChangeExtension(copy, ".compact");
        try
        {
            // Production path (same as the boot-time defrag): RegSaveKey the LIVE hive,
            // then compact the saved file. Raw file copies of in-use hives are not loadable.
            Console.WriteLine("    stage: RegSaveKey live SOFTWARE hive ...");
            RegistryOptimizer.Core.Registry.HiveFile.SaveKey("HKLM", "SOFTWARE", copy,
                diag => Console.WriteLine("    savediag: " + diag));
            Console.WriteLine($"    stage: saved {new FileInfo(copy).Length:N0} bytes (live hive file: {software.FileSizeBytes:N0})");
            Check(new FileInfo(copy).Length < software.FileSizeBytes,
                "RegSaveKey output is smaller than the bloated live hive file (compaction)");
            Console.WriteLine("    stage: RegLoadKey + re-save round-trip (loadability check) ...");
            var after = HiveCompactor.CompactFile(copy, compacted);
            Check(after > 0, "saved hive reloads via RegLoadKey and re-saves (stays loadable)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    compactor exception: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Console.WriteLine($"    inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            Check(false, "offline compactor shrinks hive copy and stays loadable");
        }
        finally
        {
            try { File.Delete(copy); } catch { }
            try { File.Delete(compacted); } catch { }
        }

        // 6) Defrag scheduling: RunOnce registration must round-trip.
        try
        {
            DefragScheduler.Cancel();
            Check(!DefragScheduler.IsScheduled(), "no stale defrag schedule before test");
            DefragScheduler.Schedule();
            Check(DefragScheduler.IsScheduled(), "boot-time defrag scheduled via RunOnce");
            var schedStatus = DefragStatus.Load();
            Check(schedStatus.Scheduled, "defrag status persisted");
            DefragScheduler.Cancel();
            Check(!DefragScheduler.IsScheduled(), "defrag schedule cancelled cleanly");
        }
        catch (Exception ex)
        {
            Check(false, $"defrag scheduler test threw: {ex.Message}");
        }

        // 7) Coalescing: build a genuinely fragmented RAW hive (fill a test key, save, mount,
        //    delete half the content, unload — the file then carries free cells; probe-verified
        //    to reach >50% internal free space), then verify the compaction round-trip produces
        //    a smaller hive with the gaps coalesced.
        string? fragPath = null;
        try
        {
            const string fragKeyPath = @"Software\RegistryOptimizerFragTest";
            try { Registry.CurrentUser.DeleteSubKeyTree(fragKeyPath); } catch { }
            using (var key = Registry.CurrentUser.CreateSubKey(fragKeyPath)!)
                for (int i = 0; i < 200; i++) key.SetValue($"V{i:D4}", new string('z', 150));

            fragPath = Path.Combine(Path.GetTempPath(), $"ro-frag-{Guid.NewGuid():N}.dat");
            var workDir = Path.Combine(Path.GetTempPath(), $"ro-fragwork-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            try
            {
                HiveFile.SaveKey("HKCU", fragKeyPath, fragPath);
                var mount = "RegistryOptimizerFragTest";
                Check(HiveFile.LoadKeyForProbe(fragPath, mount) == "OK", "fragmented hive mounts via RegLoadKey");
                using (var mk = Registry.LocalMachine.OpenSubKey(mount, writable: true))
                {
                    if (mk is not null)
                        for (int i = 0; i < 200; i += 2) mk.DeleteValue($"V{i:D4}", throwOnMissingValue: false);
                }
                Check(HiveFile.UnloadKeyForProbe(mount) == "OK", "hive unloads, keeping free cells on disk");
                try { Registry.CurrentUser.DeleteSubKeyTree(fragKeyPath); } catch { }

                var beforeFree = BootDefragRunner.InternalFreePercent(fragPath);
                var fragEntry = new HiveReportEntry { Name = "FragTest" };
                var chosen = BootDefragRunner.ChooseCompactedOutput(fragPath, workDir, "FragTest", fragEntry);
                Console.WriteLine($"    fragmented raw hive: {new FileInfo(fragPath).Length:N0} bytes, free {fragEntry.BeforeFreePercent:F1}% -> {(fragEntry.AfterFreePercent ?? -1):F1}%");
                Check(beforeFree >= BootDefragRunner.CoalesceThresholdPercent,
                    $"synthetic raw hive is fragmented enough to trigger coalescing ({beforeFree:F1}% free)");
                Check(chosen != fragPath, "coalesced output preferred over raw file");
                Check(fragEntry.AfterFreePercent < fragEntry.BeforeFreePercent,
                    "internal free space reduced (gaps coalesced — 'free from gaps and fragments')");
                Check(new FileInfo(chosen).Length < new FileInfo(fragPath).Length,
                    "coalesced hive file is smaller");
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Check(false, $"coalescing test threw: {ex.Message}");
        }

        // 8) Per-user hives: locked (current user) -> skipped with reason; unlocked -> full path.
        try
        {
            var myHive = hives.FirstOrDefault(h => !h.IsSystemHive &&
                h.FilePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase));
            Check(myHive is not null, "current user's hive located");
            if (myHive is not null)
            {
                var skipEntry = BootDefragRunner.PrepareHive(myHive, Path.GetTempPath());
                Check(skipEntry.Skipped && skipEntry.SkippedReason?.Contains("logged on") == true,
                    $"locked user hive skipped safely ({skipEntry.SkippedReason})");
            }

            // Unlocked stand-in: reuse the synthetic fragmented hive as a fake user hive file.
            var fakePath = fragPath ?? Path.Combine(Path.GetTempPath(), $"ro-fakentuser-{Guid.NewGuid():N}.dat");
            var fakeDir = Path.Combine(Path.GetTempPath(), $"ro-fakework-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fakeDir);
            try
            {
                var fake = new HiveInfo { Name = "NTUSER.DAT (test)", LogicalKey = string.Empty, FilePath = fakePath, IsSystemHive = false };
                var entry = BootDefragRunner.PrepareHive(fake, fakeDir);
                Console.WriteLine($"    fake user hive: {entry.BeforeBytes:N0} -> {entry.AfterBytes:N0} bytes, free {entry.BeforeFreePercent:F1}% -> {(entry.AfterFreePercent ?? -1):F1}%, success={entry.Success}, error={entry.Error}");
                Check(entry.Success, "unlocked user hive compacted and queued for delayed replace");
                Check(entry.AfterBytes < entry.BeforeBytes && entry.AfterFreePercent < entry.BeforeFreePercent,
                    "user hive output smaller and less fragmented");
                Check(Directory.GetFiles(fakeDir, "*.bak").Length > 0, "user hive backup written");
            }
            finally
            {
                try { File.Delete(fakePath); } catch { }
                try { Directory.Delete(fakeDir, recursive: true); } catch { }
                try { NativeMove.Clear(null, null, 4); } catch { } // clear this process's delayed moves
            }
        }
        catch (Exception ex)
        {
            Check(false, $"per-user hive test threw: {ex.Message}");
        }
    }
}
catch (Exception ex)
{
    Check(false, $"analysis threw: {ex.Message}");
}

// ---------------------------------------------------------------------------
Console.WriteLine(failures == 0 ? "ALL SMOKE TESTS PASSED" : $"{failures} SMOKE TEST(S) FAILED");
return failures == 0 ? 0 : 1;

static bool TestRegExportRoundTrip()
{
    const string testKeyPath = @"Software\RegistryOptimizerSmokeTest";
    try
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(testKeyPath); } catch { }
        using (var key = Registry.CurrentUser.CreateSubKey(testKeyPath)!)
        {
            key.SetValue("String", "hello \"world\" \\ path");
            key.SetValue("Expand", "%SystemRoot%\\system32");
            key.SetValue("Multi", new[] { "one", "two" });
            key.SetValue("Dword", unchecked((int)0xDEADBEEF), RegistryValueKind.DWord);
            key.SetValue("Qword", 0x1122334455667788, RegistryValueKind.QWord);
            key.SetValue("Binary", new byte[] { 0, 1, 2, 255, 254 }, RegistryValueKind.Binary);
            key.CreateSubKey("Child")!.SetValue("Nested", "nested-value");
        }

        var file = Path.Combine(Path.GetTempPath(), $"ro-smoke-{Guid.NewGuid():N}.reg");
        RegistrySnapshot.ExportKey("HKCU", testKeyPath, file);

        try { Registry.CurrentUser.DeleteSubKeyTree(testKeyPath); } catch { }

        var psi = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("import");
        psi.ArgumentList.Add(file);
        using (var proc = System.Diagnostics.Process.Start(psi)!)
        {
            proc.WaitForExit(30_000);
            if (proc.ExitCode != 0) return false;
        }

        using var verify = Registry.CurrentUser.OpenSubKey(testKeyPath)!;
        if (!Equals(verify.GetValue("String"), "hello \"world\" \\ path")) return false;
        if (!Equals(verify.GetValue("Expand"), "%SystemRoot%\\system32")) return false;
        if (!Equals(string.Join(",", (string[])verify.GetValue("Multi")!), "one,two")) return false;
        if (!Equals(verify.GetValue("Dword"), unchecked((int)0xDEADBEEF))) return false;
        if (!Equals(verify.GetValue("Qword"), 0x1122334455667788)) return false;
        if (!((byte[])verify.GetValue("Binary")!).SequenceEqual(new byte[] { 0, 1, 2, 255, 254 })) return false;
        using var child = verify.OpenSubKey("Child");
        return Equals(child?.GetValue("Nested"), "nested-value");
    }
    catch (Exception ex)
    {
        Console.WriteLine("    reg-export exception: " + ex.Message);
        return false;
    }
    finally
    {
        try { try { Registry.CurrentUser.DeleteSubKeyTree(testKeyPath); } catch { } } catch { }
    }
}

sealed class NativeMove
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern bool Clear(string? existing, string? newName, int flags);
}

sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _a, _b;
    public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override System.Text.Encoding Encoding => _a.Encoding;
    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
}
