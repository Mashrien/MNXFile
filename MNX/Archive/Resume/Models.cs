namespace Mash.IO.Format.MNX.Archive.Resume;

public sealed record QueueArchive(
    string Name,
    string FullPath,
    Guid ArchiveId,
    Guid RootDirectoryId,
    int FormatVersion,
    int ChunkSize,
    long Length,
    uint IndexChunks,
    uint IndexBytes,
    string HeaderSha256,
    string IndexSha256
    );

public sealed record RemainingEntry(
    Guid EntryId,
    Guid ParentId,
    string Name,
    string SourcePath,
    bool IsDirectory,
    long? SourceLength,
    DateTime? SourceModifiedUtc
    );

public sealed record QueuePayload(DateTime CreatedUtc, QueueArchive Archive, RemainingEntry[] Entries);

