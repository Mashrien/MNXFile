using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;

namespace Mash.IO.Format.MNX.Chunk;

internal enum NodeType : byte {
    Free         = 0,
    File         = 1,
    Directory    = 2,
    Continuation = 3,
    }

/// <summary>Shared disk storage flags. Archive headers permit only the first three bits.</summary>
[Flags]
internal enum NodeFlags : byte {
    None             = 0,
    Compressed       = 1 << 0,
    Encrypted        = 1 << 1,
    Streamed         = 1 << 2,
    DoubleNameLength = 1 << 3,
    IndexName        = 1 << 4,
    }

/// <summary>Parsed metadata held by value. The variable-length name is never raw-marshalled.</summary>
internal struct ChunkHeader {
    internal NodeType Type;
    internal NodeFlags Flags;
    internal Guid Id;
    internal Guid Parent;
    internal uint Next;
    internal uint Sequence;
    internal uint DataLength;
    internal ulong Size;
    internal DateTime CreatedUtc;
    internal DateTime ModifiedUtc;
    internal DateTime AccessedUtc;
    internal ushort Attributes;
    internal string Name;
    internal bool NameInIndex;
    internal bool WideName;
    internal ChunkFingerprint Hash;

    public ChunkHeader() {
        Name = string.Empty;
        }
    }

/// <summary>Opaque checksum or nonce/tag bytes stored inline, without a byte-array allocation.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ChunkFingerprint {
    private ulong _first;
    private ulong _second;
    private ulong _third;
    private ulong _fourth;

    internal static ChunkFingerprint FromBytes(ReadOnlySpan<byte> bytes) {
        return MemoryMarshal.Read<ChunkFingerprint>(bytes);
        }

    internal static ChunkFingerprint Compute(ReadOnlySpan<byte> bytes) {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return FromBytes(digest);
        }

    internal readonly void CopyTo(Span<byte> destination) {
        MemoryMarshal.Write(destination, in this);
        }

    internal readonly bool Matches(ReadOnlySpan<byte> bytes) {
        Span<byte> fingerprint = stackalloc byte[32];
        CopyTo(fingerprint);
        return fingerprint.SequenceEqual(bytes);
        }
    }

/// <summary>The exact 128-byte fixed disk header. GUIDs occupy two longs each.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RawChunkHeader {
    internal uint Magic;
    internal NodeType Type;
    internal NodeFlags Flags;
    internal ushort Reserved;
    internal long IdFirst;
    internal long IdSecond;
    internal long ParentFirst;
    internal long ParentSecond;
    internal uint Next;
    internal uint Sequence;
    internal uint DataLength;
    internal ulong Size;
    internal long CreatedTicks;
    internal long ModifiedTicks;
    internal long AccessedTicks;
    internal ushort Attributes;
    internal ushort NameLength;
    internal ChunkFingerprint Fingerprint;
    internal ulong HeaderChecksum;
    }
