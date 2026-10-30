namespace Mash.IO.Format.MNX.Archive.Codecs;

public enum ArchiveCompression : byte {
    None   = 0,
    Brotli = 1,
    Snappy = 4,
    LZ4    = 5,
    }


/// <summary>Physical chunk layout for a newly compressed archive.</summary>
public enum ArchiveCompressionLayout : byte {
    IndependentChunks = 0,
    Continuous        = 1,
    }

