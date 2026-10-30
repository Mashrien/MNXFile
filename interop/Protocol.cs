using System.Text.Json.Serialization;
using Mash.IO.Format.MNX.Archive.Resume;

namespace Mash.IO.Format.MNX.Interop;

// Protocol 1 is independent of MNX's on-disk format and native ABI1.
// Additive fields have defaults; changing existing meanings requires a new protocol.
internal sealed class ClientRequest {
    public string Operation { get; set; } = "";
    public ulong Handle { get; set; }
    public string? Path { get; set; }
    public string? Password { get; set; }
    public string? Name { get; set; }
    public Guid Id { get; set; }
    public Guid Parent { get; set; }
    public int Compression { get; set; }
    public int Level { get; set; } = 4;
    public int ChunkSize { get; set; } = 32768;
    public int Layout { get; set; } = 1;
    public int Interval { get; set; } = 250;
    public bool Overwrite { get; set; } = true;
    public bool ExportKey { get; set; }
    public long StreamId { get; set; }
    public long Length { get; set; }
    public long SourceCallback { get; set; }
    public long SourceContext { get; set; }
    public QueueSnapshot[] Queue { get; set; } = [];
    public QueuePayload? Resume { get; set; }
    }

internal sealed class ClientResponse {
    public string? Error { get; set; }
    public string? ErrorType { get; set; }
    public ulong ReturnCode { get; set; } = 1;
    public ulong Handle { get; set; }
    public string? Text { get; set; }
    public bool Value { get; set; }
    public Guid ArchiveId { get; set; }
    public Guid RootId { get; set; }
    public int FormatVersion { get; set; }
    public int ChunkSize { get; set; }
    public int Compression { get; set; }
    public int Level { get; set; }
    public int Layout { get; set; }
    public bool Encrypted { get; set; }
    public long SizeOnDisk { get; set; }
    public uint ChunkCount { get; set; }
    public uint IndexChunks { get; set; }
    public uint FreeChunks { get; set; }
    public uint FreeRanges { get; set; }
    public uint FileCount { get; set; }
    public uint DirectoryCount { get; set; }
    public long StreamId { get; set; }
    public long Length { get; set; }
    public EntrySnapshot[] Entries { get; set; } = [];
    public QueueSnapshot[] Queue { get; set; } = [];
    public QueuePayload? Resume { get; set; }
    }

internal sealed class EntrySnapshot {
    public Guid Id { get; set; }
    public Guid Parent { get; set; }
    public string Name { get; set; } = "";
    public bool Directory { get; set; }
    public ulong Size { get; set; }
    public uint Chunks { get; set; }
    public int Extents { get; set; }
    public short Attributes { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public DateTime Accessed { get; set; }
    }

internal sealed class QueueSnapshot {
    public Guid Id { get; set; }
    public Guid Parent { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Directory { get; set; }
    public ulong Length { get; set; }
    }

[JsonSerializable(typeof(ClientRequest))]
[JsonSerializable(typeof(ClientResponse))]
internal partial class ClientJson : JsonSerializerContext {
    }
