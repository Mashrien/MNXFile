using System.Security.Cryptography;
using System.Text;
using MNX.Storage;

namespace MNX;

/// <summary>Bounded two-pass conversion of an unencrypted wire7 front index.</summary>
internal sealed class LegacyIndexReader {
    private readonly Stream _stream;
    private readonly ArchiveFormat.HeaderInfo _header;
    private readonly ulong _end;
    private readonly ulong _freeOffset;
    private readonly uint _freeCount;
    private readonly ulong _freeChunks;

    internal LegacyIndexReader(Stream stream, ArchiveFormat.HeaderInfo header) {
        _stream = stream;
        _header = header;
        _end = (ulong)header.chunkSize + header.indexBytes;
        stream.Position = header.chunkSize;
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
            byte[] buffer = new byte[64 * 1024];
            ulong remaining = header.indexBytes;
            while (remaining > 0) {
                int take = (int)Math.Min((ulong)buffer.Length, remaining);
                stream.ReadExactly(buffer.AsSpan(0, take));
                hash.AppendData(buffer, 0, take);
                remaining -= (uint)take;
                }
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(header.indexHash)) {
                throw new InvalidDataException("Legacy archive index checksum does not match.");
                }
            }
        stream.Position = header.chunkSize;
        using BinaryReader reader = new(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("MNXI"u8) || reader.ReadUInt16() != 7 || reader.ReadUInt16() != 0 || reader.ReadUInt32() != header.entries) {
            throw new InvalidDataException("Legacy archive index header is invalid.");
            }
        _freeCount = reader.ReadUInt32();
        if (header.entries > header.chunks - header.indexChunks || _freeCount > int.MaxValue) {
            throw new InvalidDataException("Legacy archive index counts are invalid.");
            }
        foreach (VFile _ in Enumerate()) {
            }
        _freeOffset = (ulong)stream.Position;
        if (_freeOffset > _end || _end - _freeOffset != (ulong)_freeCount * 8) {
            throw new InvalidDataException("Legacy archive index length is invalid.");
            }
        ulong previousEnd = 0;
        ulong count = 0;
        for (int index = 0; index < (int)_freeCount; index++) {
            FreeChunkRange range = ReadRange(index);
            if (range.Start <= previousEnd) {
                throw new InvalidDataException("Legacy archive free ranges overlap or are unordered.");
                }
            previousEnd = range.End;
            count = checked(count + range.Count);
            }
        _freeChunks = count;
        }

    internal IEnumerable<VFile> Enumerate() {
        _stream.Position = _header.chunkSize + 16;
        using BinaryReader reader = new(_stream, Encoding.UTF8, true);
        for (uint ordinal = 0; ordinal < _header.entries; ordinal++) {
            yield return IndexRecord.Read(reader, _end, _header.indexChunks, _header.chunks, _header.chunkSize, _header.flags.HasFlag(NodeFlags.Streamed), _header.rootId, allowTrailing: true);
            }
        }

    internal FreeChunkSet ReadFreeChunks() {
        FreeChunkSet free = new();
        free.SetSource(new LegacyRangeView(this), _freeChunks);
        return free;
        }

    private FreeChunkRange ReadRange(int index) {
        if ((uint)index >= _freeCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
            }
        _stream.Position = checked((long)(_freeOffset + (ulong)(uint)index * 8));
        Span<byte> bytes = stackalloc byte[8];
        _stream.ReadExactly(bytes);
        uint start = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4));
        if (count == 0 || start <= _header.indexChunks || (ulong)start + count - 1 > _header.chunks) {
            throw new InvalidDataException("Legacy archive free range is invalid.");
            }
        return new(start, count);
        }

    private sealed class LegacyRangeView(LegacyIndexReader owner) : IReadOnlyList<FreeChunkRange> {
        public int Count => (int)owner._freeCount;
        public FreeChunkRange this[int index] => owner.ReadRange(index);
        public IEnumerator<FreeChunkRange> GetEnumerator() {
            for (int index = 0; index < Count; index++) {
                yield return this[index];
                }
            }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
