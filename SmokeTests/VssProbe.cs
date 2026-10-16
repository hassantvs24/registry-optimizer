using System.Diagnostics;
using System.Runtime.InteropServices;
using Alphaleonis.Win32.Vss;
using RegistryOptimizer.Core.Vss;

namespace SmokeTests;

/// <summary>Minimal VSS diagnostic: create a snapshot of C:, inspect provider state, and probe reads.</summary>
static class VssProbe
{
    public static int Run()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "config", "SYSTEM");
        Console.WriteLine($"elevated={Environment.IsPrivilegedProcess}");
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe"))
            { Arguments = "/priv", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var line in outp.Split('\n'))
                if (line.Contains("Backup") || line.Contains("Restore") || line.Contains("Privilege Name"))
                    Console.WriteLine("    whoami: " + line.Trim());
        }
        catch { }
        Console.WriteLine($"target={target}, exists={File.Exists(target)}");

        // Probe D: does load+unload+save strip internal free cells?
        try
        {
            const string dKey = @"Software\RegistryOptimizerProbeD";
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(dKey); } catch { }
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(dKey)!)
                for (int i = 0; i < 200; i++) k.SetValue($"V{i:D4}", new string('z', 150));
            var dSrc = Path.Combine(Path.GetTempPath(), "ro-probeD.dat");
            var dCompact = Path.Combine(Path.GetTempPath(), "ro-probeD.compact");
            foreach (var f in new[] { dSrc, dCompact }) try { File.Delete(f); } catch { }
            RegistryOptimizer.Core.Registry.HiveFile.SaveKey("HKLM", dKey, dSrc,
                diag => Console.WriteLine("    D privdiag: " + diag));
            Console.WriteLine($"    D saved: {new FileInfo(dSrc).Length:N0} bytes, free {RegistryOptimizer.Core.Defrag.BootDefragRunner.InternalFreePercent(dSrc):F1}%");

            // Mount, delete half, unload — file should now carry free cells.
            var mount = "RegistryOptimizerProbeD";
            int le = RegistryOptimizer.Core.Defrag.HiveCompactor.CompactFile(dSrc, dCompact) > 0 ? 0 : -1;
            // (Compaction proved loadable.) Now mount the compacted file directly and delete content.
            var hklm = Microsoft.Win32.Registry.LocalMachine;
            var load = RegistryOptimizer.Core.Registry.HiveFile.LoadKeyForProbe(dCompact, mount);
            Console.WriteLine($"    D RegLoadKey: {load}");
            using (var mk = hklm.OpenSubKey(mount, writable: true))
            {
                if (mk is not null)
                {
                    for (int i = 0; i < 200; i += 2) mk.DeleteValue($"V{i:D4}", throwOnMissingValue: false);
                }
            }
            var unload = RegistryOptimizer.Core.Registry.HiveFile.UnloadKeyForProbe(mount);
            Console.WriteLine($"    D RegUnLoadKey: {unload}");
            Console.WriteLine($"    D after delete+unload: {new FileInfo(dCompact).Length:N0} bytes, free {RegistryOptimizer.Core.Defrag.BootDefragRunner.InternalFreePercent(dCompact):F1}%");

            var dOut = Path.Combine(Path.GetTempPath(), "ro-probeD.out");
            try { File.Delete(dOut); } catch { }
            RegistryOptimizer.Core.Defrag.HiveCompactor.CompactFile(dCompact, dOut);
            Console.WriteLine($"    D after load+save: {new FileInfo(dOut).Length:N0} bytes, free {RegistryOptimizer.Core.Defrag.BootDefragRunner.InternalFreePercent(dOut):F1}%");
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(dKey); } catch { }
        }
        catch (Exception ex) { Console.WriteLine($"    probe D FAILED: {ex.GetType().Name}: {ex.Message}"); }

        // Probe C: compactor on a small self-made hive (no VSS involved).
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"))
            { Arguments = "add HKLM\\ZZSmokeProbe123 /f", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Console.WriteLine($"    reg add ZZSmokeProbe123 exit={p.ExitCode}: {o.Trim()}");
            var del = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"))
            { Arguments = "delete HKLM\\ZZSmokeProbe123 /f", UseShellExecute = false, CreateNoWindow = true };
            using var pd = Process.Start(del)!;
            pd.WaitForExit();

            var psi2 = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"))
            { Arguments = "add HKLM\\RegistryOptimizerDirectProbe /f", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p2 = Process.Start(psi2)!;
            var o2 = p2.StandardOutput.ReadToEnd() + p2.StandardError.ReadToEnd();
            p2.WaitForExit();
            Console.WriteLine($"    reg add exit={p2.ExitCode}: {o2.Trim()}");
            var del2 = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"))
            { Arguments = "delete HKLM\\RegistryOptimizerDirectProbe /f", UseShellExecute = false, CreateNoWindow = true };
            using var pd2 = Process.Start(del2)!;
            pd2.WaitForExit();
        }
        catch (Exception ex) { Console.WriteLine($"    reg add FAILED: {ex.Message}"); }
        try
        {
            using var direct = Microsoft.Win32.Registry.LocalMachine.CreateSubKey("RegistryOptimizerDirectProbe");
            Console.WriteLine("    direct CreateSubKey OK: " + (direct is not null));
            direct?.Dispose();
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKey("RegistryOptimizerDirectProbe", throwOnMissingSubKey: false);
        }
        catch (Exception ex) { Console.WriteLine($"    direct CreateSubKey FAILED: {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine("    manual-priv-test: " + ManualPrivilegeTest.EnableBackupRestore());
        try
        {
            const string probeKeyPath = @"Software\RegistryOptimizerCompactProbe";
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(probeKeyPath); } catch { }
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(probeKeyPath)!)
                k.SetValue("Name", "compact-me");
            var src = Path.Combine(Path.GetTempPath(), "ro-compact-src.dat");
            var dst = Path.Combine(Path.GetTempPath(), "ro-compact-dst.dat");
            foreach (var f in new[] { src, dst }) try { File.Delete(f); } catch { }

            // Save HKLM\SOFTWARE itself (like the boot defrag does) and diagnose.
            try
            {
                RegistryOptimizer.Core.Registry.HiveFile.SaveKey("HKLM", "SOFTWARE",
                    Path.Combine(Path.GetTempPath(), "ro-soft-probe.dat"),
                    diag => Console.WriteLine("    softdiag: " + diag));
                Console.WriteLine($"    save HKLM\\SOFTWARE OK: {new FileInfo(Path.Combine(Path.GetTempPath(), "ro-soft-probe.dat")).Length:N0} bytes");
            }
            catch (Exception ex) { Console.WriteLine($"    save HKLM\\SOFTWARE FAILED: {ex.Message}"); }

            RegistryOptimizer.Core.Registry.HiveFile.SaveKey("HKLM", probeKeyPath, src,
                diag => Console.WriteLine("    privdiag: " + diag));
            Console.WriteLine($"probe save OK ({new FileInfo(src).Length:N0} bytes)");
            var after = RegistryOptimizer.Core.Defrag.HiveCompactor.CompactFile(src, dst);
            Console.WriteLine($"probe compact OK: {new FileInfo(src).Length:N0} -> {after:N0} bytes");
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(probeKeyPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"probe compact FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Console.WriteLine($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }

        // 0) Provider snapshot capability
        try
        {
            using var probeSvc = VssFactoryProvider.Default.GetVssFactory().CreateVssBackupComponents();
            Console.WriteLine("CreateVssBackupComponents: OK");
        }
        catch (Exception ex) { Console.WriteLine("factory probe failed: " + ex.Message); }

        var snapshot = VssSnapshot.Create(new[] { target });
        try
        {
            var mapped = snapshot.Map(target);
            Console.WriteLine("mapped=" + mapped);

            // Probe A: run the real analyzer on two locked hives inside this same process.
            try
            {
                var two = new[]
                {
                    new RegistryOptimizer.Core.Defrag.HiveInfo
                    {
                        Name = "SYSTEM", LogicalKey = @"HKLM\SYSTEM",
                        FilePath = target, IsSystemHive = true,
                    },
                };
                var analysis = RegistryOptimizer.Core.Defrag.HiveFragmentationAnalyzer.Analyze(
                    two, new Progress<string>(s => Console.WriteLine("    [" + s + "]")));
                foreach (var h in analysis.Hives)
                    Console.WriteLine($"    analyzer result: {h.Name}: {(h.Error ?? $"{h.FileSizeBytes} bytes, frag {h.FragmentationPercent:F1}%")}");
            }
            catch (Exception ex) { Console.WriteLine("    analyzer threw: " + ex.Message); }

            // Probe 0: open immediately with timestamped retries (no external commands first).
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var fs0 = new FileStream(mapped, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buf0 = new byte[4];
                    fs0.ReadExactly(buf0, 0, 4);
                    Console.WriteLine($"Probe0 OK on attempt {attempt} after {stopwatch.ElapsedMilliseconds} ms: {System.Text.Encoding.ASCII.GetString(buf0)}");
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt >= 30) { Console.WriteLine($"Probe0 gave up after {attempt} attempts: {ex.Message}"); break; }
                    Thread.Sleep(500);
                }
            }

            // Did the snapshot survive creation?
            Run("vssadmin", "list shadows");

            // Probe 1: open the hive via the mapped device path.
            try
            {
                using var fs = new FileStream(mapped, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[16];
                fs.ReadExactly(buf, 0, 16);
                Console.WriteLine($"Probe1 OK: opened mapped hive, sig={System.Text.Encoding.ASCII.GetString(buf)}");
            }
            catch (Exception ex) { Console.WriteLine("Probe1 FAILED: " + ex.Message); }

            // Probe 2: root directory listing on the shadow device.
            try
            {
                var root = mapped.Substring(0, mapped.IndexOf('\\', 4)); // up to device object
                var dirs = Directory.GetDirectories(root + "\\");
                Console.WriteLine($"Probe2 OK: {dirs.Length} dirs on shadow root");
            }
            catch (Exception ex) { Console.WriteLine("Probe2 FAILED: " + ex.Message); }
        }
        finally
        {
            snapshot.Dispose();
        }
        return 0;
    }

    private static void Run(string exe, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe + ".exe"))
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15_000);
            Console.WriteLine($"> {exe} {arguments}:\n{output.Trim()}");
        }
        catch (Exception ex) { Console.WriteLine($"> {exe} failed: {ex.Message}"); }
    }
}
