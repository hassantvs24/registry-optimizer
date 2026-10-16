using System.Diagnostics;

namespace RegistryOptimizer.Core.Backup;

/// <summary>Outcome of rolling back a backup session.</summary>
public sealed class UndoResult
{
    public int Restored { get; set; }
    public int Failed { get; set; }
    /// <summary>Per-file failure descriptions (path + reason).</summary>
    public List<string> Failures { get; } = new();
}

/// <summary>
/// Reverts fixes by importing a session's .reg files back into the registry via reg.exe,
/// and removes sessions that are no longer needed.
/// </summary>
public sealed class UndoService
{
    private static string RegExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");

    private readonly AppSettings _settings;

    public UndoService(AppSettings settings) => _settings = settings;

    /// <summary>
    /// Imports every .reg file of <paramref name="session"/> with `reg.exe import`, waiting for
    /// each process and requiring exit code 0. Reports per-file progress; a failing file does
    /// not stop the remaining files.
    /// </summary>
    public async Task<UndoResult> RollbackAsync(BackupSession session, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new UndoResult();
        foreach (var file in session.RegFiles)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Restoring {Path.GetFileName(file)} …");
            try
            {
                var (exitCode, output) = await RunImportAsync(file);
                if (exitCode == 0)
                {
                    result.Restored++;
                }
                else
                {
                    result.Failed++;
                    result.Failures.Add($"{file}: reg.exe exited with code {exitCode}. {output}");
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Failures.Add($"{file}: {ex.Message}");
            }
        }
        progress?.Report($"Rollback finished: {result.Restored} restored, {result.Failed} failed.");
        return result;
    }

    /// <summary>Deletes the session folder with all its .reg files and the manifest.</summary>
    public void DeleteSession(BackupSession session)
    {
        try
        {
            Directory.Delete(session.Folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // already gone
        }
    }

    /// <summary>Runs `reg.exe import "file"` hidden and captures both output streams.</summary>
    private static async Task<(int ExitCode, string Output)> RunImportAsync(string file)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // ArgumentList handles quoting/escaping automatically.
        startInfo.ArgumentList.Add("import");
        startInfo.ArgumentList.Add(file);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start reg.exe.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = ((await stdout) + " " + (await stderr)).Trim();
        return (process.ExitCode, output);
    }
}
