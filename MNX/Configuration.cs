using System.Collections.ObjectModel;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Tools;

namespace Mash.IO.Format.MNX;

/// <summary>Configuration for read and write operations.</summary>
public sealed class Configuration {
    public int ProgressReportFrequency { get; set; } = 250;
    /// <summary>Cooperative cancellation for queue scans and adding files; completed entries remain valid.</summary>
    public CancellationToken CancellationToken { get; set; }
    public bool OverrideByDefault { get; set; } = true;
    public ArchiveCompression Compression { get; set; } = ArchiveCompression.None;
    /// <summary>Set before creation. Compressed archives default to one stream per file.</summary>
    public ArchiveCompressionLayout CompressionLayout { get; set; } = ArchiveCompressionLayout.Continuous;
    /// <summary>Creation-time effort from 1 (fast) to 9 (small); Snappy and LZ4 use 0.</summary>
    public int CompressionLevel { get; set; } = 4;
    /// <summary>Physical chunk size for new archives: 32768 by default, or 4096 for smaller chunks.</summary>
    public int ChunkSize { get; set; } = ArchiveFormat.LargeChunkSize;

    public void ChangeSettings(int reportFreq, bool overrideFiles, int chunkSize) {
        if (reportFreq <= 0) {
            throw new ArgumentOutOfRangeException(nameof(reportFreq));
            }

        if (chunkSize is not (ArchiveFormat.ChunkSize or ArchiveFormat.LargeChunkSize)) {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

        ProgressReportFrequency = reportFreq;
        OverrideByDefault = overrideFiles;
        ChunkSize = chunkSize;
        }
    }
