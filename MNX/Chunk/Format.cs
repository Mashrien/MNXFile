using System.Buffers.Binary;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static Mash.IO.Format.MNX.Archive.ArchiveFormat;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Tools;

namespace Mash.IO.Format.MNX.Chunk;

/// <summary>Reads and writes physical chunks using the fixed packed header and an existing byte buffer.</summary>
internal static class ChunkFormat {
    private const uint ChunkMagic = 0x43584e4d; // MNXC in little-endian disk order.
    private static readonly UTF8Encoding Utf8 = new(false, true);

    static ChunkFormat() {
        if (Unsafe.SizeOf<RawChunkHeader>() != NextHeaderSize || Unsafe.SizeOf<ChunkFingerprint>() != 32) {
            throw new InvalidOperationException("Packed chunk header size does not match the disk layout.");
            }
        }

    internal static ChunkHeader? ReadSlot(Stream stream, uint index, int chunkSize = ChunkSize) {
        Span<byte> buffer = stackalloc byte[FirstHeaderSize];
        long offset = checked((long)index * chunkSize);
        if (stream.Position != offset) {
            stream.Position = offset;
            }

        stream.ReadExactly(buffer);
        return ParseSlot(buffer, index, chunkSize);
        }

    internal static byte[] ReadChunk(
        Stream stream,
        uint index,
        out ChunkHeader slot,
        ArchiveCodec? codec = null,
        int expectedPlainLength = -1,
        int chunkSize = ChunkSize
        ) {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        Span<byte> chunkBytes = buffer.AsSpan(0, chunkSize);
        try {
            long offset = checked((long)index * chunkSize);
            if (stream.Position != offset) {
                stream.Position = offset;
                }

            stream.ReadExactly(chunkBytes);
            if (!TryParseHeader(chunkBytes, index, chunkSize, out slot)) {
                throw new InvalidDataException($"Chunk {index} is empty.");
                }
            int payloadStart = slot.Type == NodeType.Continuation ? NextHeaderSize : FirstHeaderSize;
            ReadOnlySpan<byte> stored = buffer.AsSpan(payloadStart, checked((int)slot.DataLength));
            if (codec is not null) {
                return codec.DecodeChunk( in slot, stored, expectedPlainLength);
                }

            byte[] payload = stored.ToArray();
            Span<byte> payloadHash = stackalloc byte[32];
            SHA256.HashData(payload, payloadHash);
            NodeFlags dataFlags = NodeFlags.Compressed | NodeFlags.Encrypted | NodeFlags.Streamed;
            if ((slot.Flags & dataFlags) != NodeFlags.None || !slot.Hash.Matches(payloadHash)) {
                throw new InvalidDataException($"Chunk {index} payload checksum does not match.");
                }

            return payload;
            }
        finally {
            ArrayPool<byte>.Shared.Return(buffer);
            }
        }

    // Nullable adapter for standalone header inspection; the chunk loop uses an out value.
    private static ChunkHeader? ParseSlot(ReadOnlySpan<byte> buffer, uint index, int chunkSize) {
        return TryParseHeader(buffer, index, chunkSize, out ChunkHeader header) ? header : null;
        }

    private static bool TryParseHeader(ReadOnlySpan<byte> buffer, uint index, int chunkSize, out ChunkHeader slot) {
        slot = default;
        if (buffer.Length < NextHeaderSize) {
            throw new InvalidDataException($"Chunk {index} header is incomplete.");
            }

        if (buffer[0] == 0) {
            return false;
            }

        RawChunkHeader raw = MemoryMarshal.Read<RawChunkHeader>(buffer);
        if (!BitConverter.IsLittleEndian) {
            ReverseNumericByteOrder(ref raw);
            }

        if (raw.Magic != ChunkMagic) {
            throw new InvalidDataException($"Chunk {index} has invalid magic.");
            }

        if (raw.Type is not (NodeType.File or NodeType.Directory or NodeType.Continuation)) {
            throw new InvalidDataException($"Chunk {index} has an invalid type.");
            }

        Span<byte> headerHash = stackalloc byte[32];
        HashHeader(buffer, raw.Type, headerHash);
        if (!headerHash.Slice(0, 8).SequenceEqual(buffer.Slice(120, 8))) {
            throw new InvalidDataException($"Chunk {index} header checksum does not match.");
            }

        slot = new ChunkHeader {
            Type = raw.Type,
            Flags = raw.Flags,
            NameInIndex = (raw.Flags & NodeFlags.IndexName) != 0,
            WideName = (raw.Flags & NodeFlags.DoubleNameLength) != 0,
            Id = GuidTools.GuidFromLongs(raw.IdFirst, raw.IdSecond),
            Parent = GuidTools.GuidFromLongs(raw.ParentFirst, raw.ParentSecond),
            Next = raw.Next,
            Sequence = raw.Sequence,
            DataLength = raw.DataLength,
            Size = raw.Size,
            CreatedUtc = ReadTimestamp(raw.CreatedTicks, index),
            ModifiedUtc = ReadTimestamp(raw.ModifiedTicks, index),
            AccessedUtc = ReadTimestamp(raw.AccessedTicks, index),
            Attributes = raw.Attributes,
            Hash = raw.Fingerprint
            };

        ValidateMetadata(slot, raw.Reserved, raw.NameLength, index, chunkSize);
        if (slot.Type != NodeType.Continuation) {
            if (buffer.Length < NextHeaderSize + raw.NameLength) {
                throw new InvalidDataException($"Chunk {index} name is incomplete.");
                }

            try {
                slot.Name = Utf8.GetString(buffer.Slice(NextHeaderSize, raw.NameLength));
                if ((slot.Flags & NodeFlags.Encrypted) == 0 && !slot.NameInIndex) {
                    ValidateName(slot.Name);
                    }
                else if (raw.NameLength != 0) {
                    throw new ArgumentException("Encrypted chunk exposes a name.");
                    }
                }
            catch (ArgumentException error) {
                throw new InvalidDataException($"Chunk {index} has an invalid name.", error);
                }
            }

        return true;
        }

    private static DateTime ReadTimestamp(long ticks, uint index) {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) {
            throw new InvalidDataException($"Chunk {index} has an invalid timestamp.");
            }

        return new DateTime(ticks, DateTimeKind.Utc);
        }

    private static void ValidateMetadata( in ChunkHeader slot, ushort reserved, ushort nameLength, uint index, int chunkSize) {
        NodeFlags allowedFlags = NodeFlags.Compressed | NodeFlags.Encrypted | NodeFlags.Streamed | NodeFlags.DoubleNameLength | NodeFlags.IndexName;
        int nameCapacity = slot.WideName ? InlineNameBytes * 2 : InlineNameBytes;
        int headerSize = slot.Type == NodeType.Continuation ? NextHeaderSize : FirstHeaderSize;
        bool continuation = slot.Type == NodeType.Continuation;
        bool directory = slot.Type == NodeType.Directory;
        bool streamed = (slot.Flags & NodeFlags.Streamed) != 0;

        if (slot.Id == Guid.Empty || reserved != 0 || (slot.Flags & ~allowedFlags) != 0) {
            throw new InvalidDataException($"Chunk {index} has invalid identifiers or flags.");
            }

        if (slot.NameInIndex && (nameLength != 0 || continuation || slot.WideName)) {
            throw new InvalidDataException($"Chunk {index} has invalid indexed-name metadata.");
            }

        if (nameLength > nameCapacity || (continuation && (nameLength != 0 || slot.WideName))) {
            throw new InvalidDataException($"Chunk {index} has invalid name length.");
            }

        if (slot.DataLength > chunkSize - headerSize || (directory && (slot.DataLength != 0 || slot.Next != 0 || streamed))) {
            throw new InvalidDataException($"Chunk {index} has invalid payload metadata.");
            }

        if ((continuation && slot.Sequence == 0) || (!continuation && slot.Sequence != 0)) {
            throw new InvalidDataException($"Chunk {index} has invalid sequence metadata.");
            }

        if (streamed && (slot.Flags & NodeFlags.Compressed) != 0) {
            throw new InvalidDataException($"Chunk {index} mixes stream and independent compression.");
            }
        }

    internal static void WriteSlot(
        Stream stream,
        uint index,
        ref ChunkHeader slot,
        ReadOnlySpan<byte> payload,
        ArchiveCodec? codec = null,
        byte[]? preparedCompressed = null,
        bool compressionChecked = false,
        int chunkSize = ChunkSize
        ) {
        int headerSize = slot.Type == NodeType.Continuation ? NextHeaderSize : FirstHeaderSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        Span<byte> chunkBytes = buffer.AsSpan(0, chunkSize);
        try {
            slot.Flags = (slot.NameInIndex ? NodeFlags.IndexName : NodeFlags.None) | (slot.WideName ? NodeFlags.DoubleNameLength : NodeFlags.None);
            byte[] stored;
            if (codec is null) {
                stored = payload.ToArray();
                slot.DataLength = (uint)stored.Length;
                slot.Hash = ChunkFingerprint.Compute(stored);
                }
            else {
                stored = codec.EncodeChunk(ref slot, payload, preparedCompressed, compressionChecked);
                }

            if (stored.Length > chunkSize - headerSize) {
                throw new ArgumentOutOfRangeException(nameof(payload), "Encoded data does not fit in a chunk.");
                }

            // Clear only bytes not overwritten by the fixed header and payload.
        // Full continuation chunks need no separate zero-fill pass.
        if (headerSize > NextHeaderSize) {
            buffer.AsSpan(NextHeaderSize, headerSize - NextHeaderSize).Clear();
            }

        buffer.AsSpan(headerSize + stored.Length, chunkSize - headerSize - stored.Length).Clear();
        int nameLength = 0;
            if (slot.Type != NodeType.Continuation && !slot.NameInIndex) {
                int nameCapacity = slot.WideName ? InlineNameBytes * 2 : InlineNameBytes;
                nameLength = Utf8.GetByteCount(slot.Name);
                if (nameLength > nameCapacity) {
                    throw new ArgumentException("Archive name is too long.");
                    }

                Utf8.GetBytes(slot.Name, buffer.AsSpan(NextHeaderSize, nameCapacity));
                }

            (long idFirst, long idSecond) = slot.Id.ToLongs();
            (long parentFirst, long parentSecond) = slot.Parent.ToLongs();
            RawChunkHeader raw = new RawChunkHeader {
                Magic = ChunkMagic,
                Type = slot.Type,
                Flags = slot.Flags,
                IdFirst = idFirst,
                IdSecond = idSecond,
                ParentFirst = parentFirst,
                ParentSecond = parentSecond,
                Next = slot.Next,
                Sequence = slot.Sequence,
                DataLength = slot.DataLength,
                Size = slot.Size,
                CreatedTicks = slot.CreatedUtc.Ticks,
                ModifiedTicks = slot.ModifiedUtc.Ticks,
                AccessedTicks = slot.AccessedUtc.Ticks,
                Attributes = slot.Attributes,
                NameLength = checked((ushort)nameLength),
                Fingerprint = slot.Hash
                };
            if (!BitConverter.IsLittleEndian) {
                ReverseNumericByteOrder(ref raw);
                }

            MemoryMarshal.Write(buffer, in raw);
            stored.CopyTo(buffer.AsSpan(headerSize));
            Span<byte> headerHash = stackalloc byte[32];
            HashHeader(buffer, slot.Type, headerHash);
            headerHash.Slice(0, 8).CopyTo(buffer.AsSpan(120, 8));
            long offset = checked((long)index * chunkSize);
            if (stream.Position != offset) {
                stream.Position = offset;
                }

            stream.Write(chunkBytes);
            }
        finally {
            ArrayPool<byte>.Shared.Return(buffer);
            }
        }

    internal static void RewriteNext(Stream stream, uint index, uint next, int chunkSize = ChunkSize) {
        Span<byte> header = stackalloc byte[FirstHeaderSize];
        stream.Position = checked((long)index * chunkSize);
        stream.ReadExactly(header);
        NodeType kind = (NodeType)header[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), next);
        Span<byte> headerHash = stackalloc byte[32];
        HashHeader(header, kind, headerHash);
        headerHash.Slice(0, 8).CopyTo(header.Slice(120, 8));
        stream.Position = checked((long)index * chunkSize);
        stream.Write(header.Slice(0, kind == NodeType.Continuation ? NextHeaderSize : FirstHeaderSize));
        }

    private static void HashHeader(ReadOnlySpan<byte> header, NodeType kind, Span<byte> destination) {
        int nameBytes = ((NodeFlags)header[5] & NodeFlags.DoubleNameLength) != 0 ? InlineNameBytes * 2 : InlineNameBytes;
        int coveredLength = kind == NodeType.Continuation ? 120 : 120 + nameBytes;
        Span<byte> covered = stackalloc byte[120 + InlineNameBytes * 2];
        header.Slice(0, 120).CopyTo(covered);
        if (kind != NodeType.Continuation) {
            if (header.Length < NextHeaderSize + nameBytes) {
                throw new InvalidDataException("Chunk name region is incomplete.");
                }

            header.Slice(NextHeaderSize, nameBytes).CopyTo(covered.Slice(120));
            }

        SHA256.HashData(covered.Slice(0, coveredLength), destination);
        }

    // Fixed header layout: ushort, uint and ulong numeric field positions.
    private static readonly int[] ShortOffsets = [6, 84, 86];
    private static readonly int[] IntegerOffsets = [0, 40, 44, 48];
    private static readonly int[] LongOffsets = [8, 16, 24, 32, 52, 60, 68, 76];

    private static void ReverseNumericByteOrder(ref RawChunkHeader raw) {
        // Only numeric fields are reversed; fingerprint and checksum bytes stay opaque.
        Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref raw, 1));
        foreach (int offset in ShortOffsets) {
            bytes.Slice(offset, sizeof(ushort)).Reverse();
            }
        foreach (int offset in IntegerOffsets) {
            bytes.Slice(offset, sizeof(uint)).Reverse();
            }
        foreach (int offset in LongOffsets) {
            bytes.Slice(offset, sizeof(ulong)).Reverse();
            }
        }

    internal static void ClearSlot(Stream stream, uint index, int chunkSize = ChunkSize) {
        stream.Position = checked((long)index * chunkSize);
        stream.Write(new byte[chunkSize]);
        }
    }
