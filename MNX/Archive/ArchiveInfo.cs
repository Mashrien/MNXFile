
namespace MNX;


/// <summary>A snapshot of archive occupancy and file counts.</summary>
public sealed record ArchiveInfo(
    Guid ArchiveGuid,
    Guid RootDirectoryId,
    string Path,
    ulong SizeOnDisk,
    uint ChunkCount,
    uint IndexChunkCount,
    uint FreeChunkCount,
    uint FreeRangeCount,
    uint FileCount,
    uint DirectoryCount
    );

