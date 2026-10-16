using RegistryOptimizer.Core.Scanning.Scanners;

namespace RegistryOptimizer.Core.Scanning;

/// <summary>
/// Orchestrates a full scan run: builds the shared <see cref="ScanContext"/>,
/// enables the scanners that apply to the requested profile, runs them
/// concurrently, and aggregates every reported issue into one <see cref="ScanResult"/>.
/// The whole operation is read-only.
/// </summary>
public sealed class ScanService
{
    private readonly AppSettings _settings;

    public ScanService(AppSettings settings)
    {
        _settings = settings;
        Scanners = new IIssueScanner[]
        {
            // Quick-profile safe scanners first, then the Deep-only ones.
            new MissingFileRefScanner(),
            new SharedDllScanner(),
            new OrphanedUninstallScanner(),
            new BrokenFileAssocScanner(),
            new StartupEntryScanner(),
            new InvalidComClsidScanner(),
            new MruHistoryScanner(),
            new OrphanedKeyScanner(),
        };
    }

    /// <summary>All registered scanners, in execution/display order.</summary>
    public IReadOnlyList<IIssueScanner> Scanners { get; }

    public async Task<ScanResult> ScanAsync(
        ScanProfile profile,
        IProgress<ScanIssue>? progress = null,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var result = new ScanResult { Profile = profile };
        var context = new ScanContext { IgnoreList = _settings.IgnoredSet, Profile = profile };
        var excluded = _settings.ExcludedSet;

        // Skip categories the user excluded, and scanners that don't support this profile.
        var enabled = Scanners
            .Where(s => !excluded.Contains(s.Category) && s.Supports(profile))
            .ToList();

        status?.Report($"Starting {profile} scan with {enabled.Count} scanner(s)...");

        var tasks = enabled.Select(scanner => Task.Run(() => RunScanner(scanner, context, result, progress, status, ct), ct)).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        result.CompletedUtc = DateTime.UtcNow;
        status?.Report(result.Cancelled
            ? "Scan cancelled."
            : $"Scan complete: {result.Issues.Count} issue(s) found in {result.Duration:mm\\:ss}.");
        return result;
    }

    /// <summary>Runs one scanner, funnelling its issues into the shared result. Never throws for scanner failures.</summary>
    private void RunScanner(
        IIssueScanner scanner,
        ScanContext context,
        ScanResult result,
        IProgress<ScanIssue>? progress,
        IProgress<string>? status,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        status?.Report($"Scanning {scanner.Category}...");

        // Collect into a local buffer first so the shared list is mutated under a single lock.
        // NB: a plain Progress<T> would marshal callbacks to whatever SynchronizationContext was
        // current at construction (or to the thread pool in console/background services), which
        // both races the buffer and can deliver issues AFTER ScanAsync returned. A synchronous
        // collector keeps delivery on the scanning thread and makes flushing exact.
        var buffer = new SynchronousIssueBuffer();
        try
        {
            scanner.ScanAsync(context, buffer, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // let Task.WhenAll surface the cancellation
        }
        catch (Exception ex)
        {
            status?.Report($"Scanner {scanner.Category} failed and was skipped: {ex.Message}");
            return;
        }

        var issues = buffer.Drain();
        lock (result.Issues)
        {
            foreach (var issue in issues)
            {
                progress?.Report(issue);
                result.Issues.Add(issue);
            }
        }
        status?.Report($"Finished {scanner.Category}: {issues.Count} issue(s).");
    }

    /// <summary>Thread-safe, synchronous <see cref="IProgress{T}"/> collector: reports are appended
    /// immediately on the caller's thread, so a scanner cannot outlive its own findings.</summary>
    private sealed class SynchronousIssueBuffer : IProgress<ScanIssue>
    {
        private readonly List<ScanIssue> _items = new();
        public void Report(ScanIssue issue)
        {
            lock (_items) _items.Add(issue);
        }
        public List<ScanIssue> Drain()
        {
            lock (_items)
            {
                var copy = new List<ScanIssue>(_items);
                _items.Clear();
                return copy;
            }
        }
    }
}
