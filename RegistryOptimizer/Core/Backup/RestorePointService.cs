using System.Diagnostics;

namespace RegistryOptimizer.Core.Backup;

/// <summary>
/// Best-effort creation of a Windows system restore point via PowerShell's Checkpoint-Computer
/// (the supported way; wmic is deprecated). Never throws — failures are reported as
/// (false, explanation).
/// </summary>
public static class RestorePointService
{
    private static readonly string PowerShellExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<(bool Created, string Message)> TryCreateAsync(
        string description = "Registry Optimizer", CancellationToken ct = default)
    {
        try
        {
            // Escape single quotes for the embedded PowerShell string literal.
            var safeDescription = description.Replace("'", "''");
            var command = $"Checkpoint-Computer -Description '{safeDescription}' -RestorePointType MODIFY_SETTINGS";

            var startInfo = new ProcessStartInfo
            {
                FileName = PowerShellExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo);
            if (process is null) return (false, "Could not start PowerShell to create a restore point.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            // CancelAfter implements the 60 s timeout while still honouring the caller's token.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (false, $"Restore point creation timed out after {Timeout.TotalSeconds:0} seconds.");
            }

            if (ct.IsCancellationRequested)
                return (false, "Restore point creation was cancelled.");

            if (process.ExitCode == 0)
                return (true, $"System restore point \"{description}\" created.");

            var output = ((await stdout) + " " + (await stderr)).Trim();
            return (false, $"PowerShell exited with code {process.ExitCode}: {output}");
        }
        catch (Exception ex)
        {
            return (false, $"Could not create a restore point: {ex.Message}");
        }
    }
}
