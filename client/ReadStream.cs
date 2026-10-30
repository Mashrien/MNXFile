using Mash.IO.Format.MNX.Interop;

namespace Mash.IO.Format.MNX;

public sealed unsafe partial class MNX {
    public Stream OpenRead(Guid id) {
        ClientResponse response = Call(new ClientRequest { Operation = "stream-open", Id = id });
        return new NativeReadStream(this, _handle, response.StreamId, response.Length);
        }

    public byte[] ReadFileRange(Guid id, long offset, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        using Stream source = OpenRead(id);
        source.Position = offset;
        int length = checked((int)Math.Min(count, Math.Max(0, source.Length - offset)));
        byte[] bytes = new byte[length];
        source.ReadExactly(bytes);
        return bytes;
        }

    public long CopyFileRange(Guid id, Stream destination, long offset, long length, int bufferSize = 65536) {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        using Stream source = OpenRead(id);
        source.Position = offset;
        long remaining = Math.Min(length, Math.Max(0, source.Length - offset));
        long copied = 0;
        byte[] buffer = new byte[Math.Min(bufferSize, 4 * 1024 * 1024)];
        while (remaining > 0) {
            int read = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read == 0) {
                throw new EndOfStreamException();
                }
            destination.Write(buffer, 0, read);
            copied += read;
            remaining -= read;
            }
        return copied;
        }

    private sealed class NativeReadStream(MNX owner, ulong archiveHandle, long streamId, long length) : Stream {
        private long _position;
        private bool _closed;
        public override bool CanRead => !_closed;
        public override bool CanSeek => !_closed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position {
            get => _position;
            set {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
                }
            }
        public override int Read(byte[] buffer, int offset, int count) {
            return Read(buffer.AsSpan(offset, count));
            }
        public override int Read(Span<byte> destination) {
            lock (owner._gate) {
                ObjectDisposedException.ThrowIf(_closed || owner._handle != archiveHandle, this);
                CancellationToken cancellation = owner.Options.CancellationToken;
                cancellation.ThrowIfCancellationRequested();
                NativeApi.Instance.Prepare(archiveHandle);
                using CancellationTokenRegistration registration = cancellation.Register(() => NativeApi.Instance.Cancel(archiveHandle));
                int count;
                try {
                    count = NativeApi.Instance.Read(archiveHandle, streamId, _position, destination[..Math.Min(destination.Length, 4 * 1024 * 1024)]);
                    }
                catch {
                    cancellation.ThrowIfCancellationRequested();
                    throw;
                    }
                _position += count;
                return count;
                }
            }
        public override long Seek(long offset, SeekOrigin origin) {
            Position = origin switch {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
            return Position;
            }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) {
            if (!_closed && disposing) {
                lock (owner._gate) {
                    if (owner._handle == archiveHandle) {
                        NativeApi.ThrowIfError(NativeApi.Instance.Request(new ClientRequest { Operation = "stream-close", Handle = archiveHandle, StreamId = streamId }));
                        }
                    _closed = true;
                    }
                }
            base.Dispose(disposing);
            }
        }
    }
