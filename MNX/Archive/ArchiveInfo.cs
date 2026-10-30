using ArchiveFile = Mash.IO.Format.MNX.MNX;

namespace Mash.IO.Format.MNX.Archive;


/// <summary>A snapshot of archive occupancy and file counts.</summary>
public sealed record ArchiveInfo(
    Guid ArchiveGuid,
    Guid RootDirectoryId,
    string Path,
    long SizeOnDisk,
    uint ChunkCount,
    uint IndexChunkCount,
    uint FreeChunkCount,
    uint FreeRangeCount,
    uint FileCount,
    uint DirectoryCount
    );

