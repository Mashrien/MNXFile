using System.Diagnostics;
using System.Security.Cryptography;
using MNX;
using MNX.Storage;
using DiskFile = System.IO.File;
using DiskNodeFlags = MNX.Storage.NodeFlags;

namespace MNX.Storage;

internal sealed class ArchiveReadStream(Archive archive, VFile file) : Stream {
    private long _position;
    private readonly ChunkReadWindow _chunks = new ChunkReadWindow(archive, file);
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
            if (!archive.State.Entries.ContainsKey(file.FileID)) {
                throw new InvalidOperationException("The archived file changed while this stream was open.");
                }

            if (_position >= Length || destination.Length == 0) {
                return 0;
                }

            int requested = (int)Math.Min(destination.Length, Length - _position);
            (uint lastSequence, _) = file.LocateByte((ulong)(_position + requested - 1));
            int copied = 0;
            while (copied < requested) {
                (uint sequence, int withinChunk) = file.LocateByte((ulong)_position);
                byte[] payload = _chunks.Read(sequence, lastSequence - sequence + 1);
                int available = payload.Length - withinChunk;
                if (available <= 0) {
                    throw new InvalidDataException("Chunk length does not cover the requested offset.");
                    }

                int take = Math.Min(requested - copied, available);
                payload.AsSpan(withinChunk, take).CopyTo(destination.Slice(copied, take));
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
            _chunks.Dispose();
            }

        base.Dispose(disposing);
        }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
