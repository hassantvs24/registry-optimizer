using RegistryOptimizer.Core.Vss;

namespace RegistryOptimizer.Core.Defrag;

/// <summary>
/// Measures internal free space and fragmentation of registry hive files by
/// parsing the 'regf'/'hbin' hive format. Locked hives are read through a
/// single shared VSS snapshot.
/// </summary>
public static class HiveFragmentationAnalyzer
{
    private const int HiveHeaderSize = 4096;
    private const int HbinHeaderSize = 32;

    public static DefragAnalysis Analyze(IReadOnlyList<HiveInfo> hives, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var analysis = new DefragAnalysis();

        // Decide how each hive can be read: directly, or via a VSS snapshot for locked ones.
        // CRITICAL: the snapshot must stay alive until every mapped file has been parsed —
        // disposing it deletes the shadow copies and their device paths become unreadable.
        var readPaths = new Dictionary<HiveInfo, string>();
        var locked = new List<HiveInfo>();
        foreach (var hive in hives)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Analyzing {hive.Name} ...");
            try
            {
                using var probe = new FileStream(hive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                readPaths[hive] = hive.FilePath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                locked.Add(hive);
            }
        }

        VssSnapshot? snapshot = null;
        if (locked.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("Taking a VSS snapshot to read locked hives ...");
            try
            {
                snapshot = VssSnapshot.Create(locked.Select(h => h.FilePath));
                foreach (var hive in locked)
                    readPaths[hive] = snapshot.Map(hive.FilePath);
            }
            catch (Exception ex)
            {
                var message = $"Could not read hive file: {ex.Message}";
                foreach (var hive in locked)
                    hive.Error ??= message;
            }
        }

        try
        {
            foreach (var hive in hives)
            {
                ct.ThrowIfCancellationRequested();
                if (!readPaths.TryGetValue(hive, out var path))
                {
                    hive.Error ??= "Hive file is not accessible.";
                    hive.FileSizeBytes = 0;
                    hive.FreeSpacePercent = 0;
                    hive.FragmentationPercent = 0;
                }
                else
                {
                    try
                    {
                        ParseHive(path, hive);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        hive.Error = $"Hive parse failed: {ex.Message}";
                        hive.FreeSpacePercent = 0;
                        hive.FragmentationPercent = 0;
                    }
                }
                analysis.Hives.Add(hive);
            }
        }
        finally
        {
            snapshot?.Dispose();
        }

        return analysis;
    }

    /// <summary>
    /// Walk the hive file: 'regf' header (major version must be 1), then 4096-byte-aligned
    /// 'hbin' blocks. Inside each hbin, cells are prefixed with a signed int32 size
    /// (negative = allocated, positive = free). Accumulates free/total bytes and the
    /// bytes held in small free cells (fragmentation contributors).
    /// </summary>
    private static void ParseHive(string path, HiveInfo hive)
    {
        long freeBytes = 0, totalBytes = 0, smallFreeBytes = 0;

        // A freshly created VSS shadow device can answer ERROR_NOT_READY for a short moment
        // right after DoSnapshotSet returns — brief retries absorb that transient state.
        using (var fs = OpenWithRetry(path))
        {
            hive.FileSizeBytes = fs.Length;
            if (fs.Length < HiveHeaderSize)
                throw new InvalidDataException("File is smaller than a hive header.");

            var header = new byte[HiveHeaderSize];
            fs.ReadExactly(header, 0, HiveHeaderSize);
            if (header[0] != (byte)'r' || header[1] != (byte)'e' || header[2] != (byte)'g' || header[3] != (byte)'f')
                throw new InvalidDataException("Missing 'regf' signature — not a registry hive file.");
            uint major = BitConverter.ToUInt32(header, 0x14);
            if (major != 1)
                throw new InvalidDataException($"Unsupported hive major version {major}.");

            var hbinHeader = new byte[HbinHeaderSize];
            var cellPrefix = new byte[4];
            long offset = HiveHeaderSize;
            bool firstHbin = true;
            while (offset + HbinHeaderSize <= fs.Length)
            {
                fs.Position = offset;
                fs.ReadExactly(hbinHeader, 0, HbinHeaderSize);
                if (hbinHeader[0] != (byte)'h' || hbinHeader[1] != (byte)'b' ||
                    hbinHeader[2] != (byte)'i' || hbinHeader[3] != (byte)'n')
                {
                    if (firstHbin)
                        throw new InvalidDataException("Missing 'hbin' signature after hive header.");
                    break; // sparse/partial tail — treat as end of hive data
                }
                firstHbin = false;

                uint hbinSize = BitConverter.ToUInt32(hbinHeader, 8);
                if (hbinSize < HbinHeaderSize)
                    throw new InvalidDataException($"Corrupt hbin size {hbinSize} at offset {offset}.");
                long hbinEnd = Math.Min(offset + hbinSize, fs.Length);

                long cellPos = offset + HbinHeaderSize;
                while (cellPos + 4 <= hbinEnd)
                {
                    fs.Position = cellPos;
                    fs.ReadExactly(cellPrefix, 0, 4);
                    int raw = BitConverter.ToInt32(cellPrefix, 0);
                    long cellSize = Math.Abs((long)raw);
                    if (cellSize < 8) break; // corrupt cell chain — stop this hbin

                    totalBytes += cellSize;
                    if (raw > 0) // positive = free cell
                    {
                        freeBytes += cellSize;
                        if (cellSize <= 16) smallFreeBytes += cellSize;
                    }
                    cellPos += cellSize;
                }

                // Advance to the next 4096-byte-aligned hbin.
                offset += ((hbinSize + HiveHeaderSize - 1) / HiveHeaderSize) * HiveHeaderSize;
            }
        }

        double freePercent = totalBytes == 0 ? 0 : freeBytes * 100.0 / totalBytes;
        double smallCellRatio = smallFreeBytes / Math.Max(1.0, freeBytes);
        hive.FreeSpacePercent = freePercent;
        hive.FragmentationPercent = Math.Clamp(freePercent * 0.6 + smallCellRatio * 100.0 * 0.4, 0.0, 100.0);
    }

    /// <summary>Open a hive file read-only, retrying transient "device not ready" / sharing states
    /// for ~5 seconds (fresh VSS shadow devices need a moment to become readable).</summary>
    private static FileStream OpenWithRetry(string path)
    {
        const int attempts = 12;
        for (var i = 1; ; i++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < attempts)
            {
                Thread.Sleep(400);
            }
        }
    }
}
