using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX.Archive;

/// <summary>Owned mutable state for one archive. All access is serialized through SyncRoot.</summary>
internal sealed class ArchiveState {
    internal object SyncRoot { get; } = new();
    internal Dictionary<Guid, VirtualFile> Entries { get; } = new();
    internal List<FileQueueItem> QueueItems { get; } = new();
    internal FreeChunkSet FreeChunks { get; } = new();
    internal Configuration Options { get; } = new();
    internal Guid ArchiveGuid { get; set; }
    internal Guid RootDirectoryId { get; set; }
    internal ArchiveFile.ReturnCode ErrorStatus { get; set; } = ArchiveFile.ReturnCode.None;
    internal string? Path { get; set; }
    internal uint ChunkCount;
    internal uint IndexChunks { get; set; }
    internal int ChunkSize { get; set; } = ArchiveFormat.ChunkSize;
    internal NodeFlags StorageFlags { get; set; }
    internal ArchiveCodec? Codec { get; set; }
    internal byte[]? ResumePassword { get; set; }
    internal string? ValidatedResumeQueue { get; set; }
    internal byte[]? ValidatedResumeQueueHash { get; set; }
    internal byte[] Salt { get; set; } = new byte[ArchiveCodec.SaltLength];
    internal int KdfIterations { get; set; }
    internal bool Disposed { get; set; }
    internal WriteBatch? CurrentBatch { get; set; }
    }
