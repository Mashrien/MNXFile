using System.Diagnostics;
using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using Mash.IO.Format.MNX.Tools;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using DiskNodeFlags = Mash.IO.Format.MNX.Chunk.NodeFlags;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX.Chunk;

internal sealed class ArchiveReadStream(ArchiveFile archive, VirtualFile file) : Stream {
    private long _position;
    private uint? _cachedSequence;
    private byte[] _cachedPayload = Array.Empty<byte>();
    private readonly FileStream _input = DiskFile.OpenRead(archive.State.Path!);
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => checked((long)file.SizeOriginal);
    public override long Position { get => _position; set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }

    public override int Read(byte[] buffer, int offset, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) {
            throw new ArgumentException("Buffer range is invalid.");
            }

        return Read(buffer.AsSpan(offset, count));
        }

    public override int Read(Span<byte> destination) {
        lock (archive.State.SyncRoot) {
            archive.CheckOpen();
            if (!archive.State.Entries.TryGetValue(file.FileID, out VirtualFile? current) || !ReferenceEquals(current, file)) {
                throw new InvalidOperationException("The archived file changed while this stream was open.");
                }

            if (_position >= Length || destination.Length == 0) {
                return 0;
                }

            int requested = (int)Math.Min(destination.Length, Length - _position);
            int copied = 0;
            while (copied < requested) {
                (uint sequence, int withinChunk) = file.LocateByte((ulong)_position);
                if (_cachedSequence != sequence) {
                    uint physicalChunk = file.PhysicalChunk(sequence);
                    byte[] payload = ChunkFormat.ReadChunk(
                        _input,
                        physicalChunk,
                        out ChunkHeader header,
                        archive.State.Codec,
                        file.LogicalLength(sequence),
                        archive.State.ChunkSize
                        );
                    uint expectedNext = sequence + 1 < file.ChunkCount ? file.PhysicalChunk(sequence + 1) : 0;
                    NodeType expectedType = sequence == 0 ? NodeType.File : NodeType.Continuation;
                    if (header.Id != file.FileID || header.Sequence != sequence || header.Type != expectedType || header.Next != expectedNext) {
                        throw new InvalidDataException("Chunk does not match the file index.");
                        }

                    _cachedPayload = payload;
                    _cachedSequence = sequence;
                    }

                int available = _cachedPayload.Length - withinChunk;
                if (available <= 0) {
                    throw new InvalidDataException("Chunk length does not cover the requested offset.");
                    }

                int take = Math.Min(requested - copied, available);
                _cachedPayload.AsSpan(withinChunk, take).CopyTo(destination.Slice(copied, take));
                copied += take;
                _position += take;
                }

            return copied;
            }
        }

    public override long Seek(long offset, SeekOrigin origin) {
        long next = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))};
        Position = next;
        return next;
        }

    public override void Flush() {
        }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _input.Dispose();
            }

        base.Dispose(disposing);
        }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
