using Alphaleonis.Win32.Vss;

namespace RegistryOptimizer.Core.Vss;

/// <summary>
/// Read-only VSS snapshot of one or more volumes, used to read locked files
/// (registry hives) while the system is running. Follows the AlphaVSS
/// "non-VSS-writer backup" flow. Disposable: completes the backup and deletes
/// the snapshot set.
/// </summary>
public sealed class VssSnapshot : IDisposable
{
    private readonly IVssBackupComponents _backup;
    private readonly Guid _snapshotSetId;

    /// <summary>Drive-letter root ("C:") -> snapshot device object
    /// (e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1).</summary>
    private readonly Dictionary<string, string> _deviceByDrive;

    private bool _disposed;

    private VssSnapshot(IVssBackupComponents backup, Guid snapshotSetId, Dictionary<string, string> deviceByDrive)
    {
        _backup = backup;
        _snapshotSetId = snapshotSetId;
        _deviceByDrive = deviceByDrive;
    }

    /// <summary>
    /// Snapshot every unique fixed local drive hosting one of the given files.
    /// Nonexistent files and files on non-fixed drives are skipped.
    /// Throws InvalidOperationException when no snapshot could be created.
    /// </summary>
    public static VssSnapshot Create(IEnumerable<string> filePaths)
    {
        // Collect unique drive letters of existing files on fixed drives.
        var drives = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in filePaths)
        {
            string? root;
            try
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
                root = Path.GetPathRoot(Path.GetFullPath(file));
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') continue; // only drive-letter paths
            var drive = root.Substring(0, 2).ToUpperInvariant();
            try
            {
                if (new DriveInfo(drive).DriveType != DriveType.Fixed) continue;
            }
            catch
            {
                continue;
            }
            drives.Add(drive);
        }
        if (drives.Count == 0)
            throw new InvalidOperationException("VSS snapshot: no existing files on fixed local drives to snapshot.");

        IVssBackupComponents? backup = null;
        Guid snapshotSetId = Guid.Empty;
        try
        {
            var factory = VssFactoryProvider.Default.GetVssFactory();
            backup = factory.CreateVssBackupComponents();
            backup.InitializeForBackup(null!);
            backup.SetBackupState(selectComponents: false, backupBootableSystemState: false,
                VssBackupType.Copy, partialFileSupport: false); // required before PrepareForBackup
            snapshotSetId = backup.StartSnapshotSet();

            var snapshotIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in drives)
            {
                // AddToSnapshotSet takes a volume root path like "C:\" and only RESERVES the
                // shadow copy — the SnapshotDeviceObject does not exist until DoSnapshotSet
                // completes, so properties must be read afterwards.
                snapshotIds[drive] = backup.AddToSnapshotSet(drive + @"\");
            }

            backup.PrepareForBackup();
            backup.DoSnapshotSet(); // synchronous: blocks until the shadow copies are created

            var deviceByDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (drive, snapshotId) in snapshotIds)
            {
                var props = backup.GetSnapshotProperties(snapshotId);
                deviceByDrive[drive] = props.SnapshotDeviceObject;
            }

            return new VssSnapshot(backup, snapshotSetId, deviceByDrive);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup, then surface a single informative error.
            try { backup?.DeleteSnapshotSet(snapshotSetId, forceDelete: true); } catch { /* ignore */ }
            try { backup?.Dispose(); } catch { /* ignore */ }
            throw new InvalidOperationException($"VSS snapshot creation failed: {ex.Message}", ex);
        }
    }

    /// <summary>Map an original path to its shadow-copy equivalent.
    /// Returns the original path when the drive has no snapshot mapping.</summary>
    public string Map(string originalPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var root = Path.GetPathRoot(Path.GetFullPath(originalPath));
        if (!string.IsNullOrEmpty(root) && root.Length >= 2 &&
            _deviceByDrive.TryGetValue(root.Substring(0, 2).ToUpperInvariant(), out var device))
        {
            // root includes the trailing separator, e.g. "C:\" (length 3); the device object
            // (\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN) carries NO trailing backslash.
            return device + Path.DirectorySeparatorChar + originalPath.Substring(root.Length);
        }
        return originalPath;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _backup.BackupComplete(); } catch { /* snapshot may already be gone; nothing useful to do */ }
        try { _backup.DeleteSnapshotSet(_snapshotSetId, forceDelete: true); } catch { /* best effort */ }
        try { _backup.Dispose(); } catch { /* best effort */ }
    }
}
