namespace RegistryOptimizer.Core.Scanning;

/// <summary>One registry issue category scanner. Implementations must be read-only.</summary>
public interface IIssueScanner
{
    ScanCategory Category { get; }

    /// <summary>Highest severity this scanner can emit.</summary>
    Severity Severity { get; }

    /// <summary>Quick profile covers the everyday-safe categories; Deep adds the aggressive ones.</summary>
    bool Supports(ScanProfile profile);

    /// <summary>Emit every found issue via <paramref name="progress"/>. Must observe <paramref name="ct"/> and never write to the registry.</summary>
    Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct);
}
