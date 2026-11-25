using System.Buffers.Binary;
using System.Security.Cryptography;
using MNX.Codecs;

namespace MNX;

/// <summary>Maps a logical index stream onto independently authenticated physical chunks.</summary>
internal sealed class PagedIndexStream : Stream {
    internal const int EnvelopeBytes = 64;
    internal const int MaximumCachedPages = 64;
    private readonly Func<Stream> _open;
    private readonly ArchiveCodec _codec;
    private readonly int _chunkSize;
    private readonly long _length;
    private readonly byte[] _rootHash;
    private readonly Dictionary<ulong, LinkedListNode<(ulong Number, byte[] Data)>> _pages = new();
    private readonly LinkedList<(ulong Number, byte[] Data)> _recent = new();
    private long _position;
    internal ulong PageReads { get; private set; }
    internal int ResidentPages => _pages.Count;
    internal int PayloadBytes => _chunkSize - EnvelopeBytes;
    internal void ClearCache() {
        foreach ((ulong _, byte[] data) in _recent) {
            CryptographicOperations.ZeroMemory(data);
            }
        _pages.Clear();
        _recent.Clear();
        }

    internal PagedIndexStream(Func<Stream> open, ArchiveCodec codec, int chunkSize, ulong length, byte[] rootHash) {
        _open = open;
        _codec = codec;
        _chunkSize = chunkSize;
        _length = checked((long)length);
        _rootHash = rootHash;
        }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> destination) {
        int count = checked((int)Math.Min(destination.Length, Math.Max(0, _length - _position)));
        int copied = 0;
        while (copied < count) {
            ulong number = (ulong)(_position / PayloadBytes);
            int within = (int)(_position % PayloadBytes);
            byte[] page = LoadPage(number);
            int take = Math.Min(count - copied, PayloadBytes - within);
            page.AsSpan(within, take).CopyTo(destination.Slice(copied, take));
            copied += take;
            _position += take;
            }
        return count;
        }

    private byte[] LoadPage(ulong number) {
        if (_pages.TryGetValue(number, out LinkedListNode<(ulong Number, byte[] Data)>? cached)) {
            _recent.Remove(cached);
            _recent.AddFirst(cached);
            return cached.Value.Data;
            }
        byte[] physical = new byte[_chunkSize];
        using (Stream source = _open()) {
            source.Position = checked((long)(number + 1) * _chunkSize);
            source.ReadExactly(physical);
            }
        PageReads++;
        if (number == 0 && !SHA256.HashData(physical).AsSpan().SequenceEqual(_rootHash)) {
            throw new InvalidDataException("Archive index root checksum does not match.");
            }
        Span<byte> checksum = stackalloc byte[32];
        SHA256.HashData(physical.AsSpan(32), checksum);
        if (!checksum.SequenceEqual(physical.AsSpan(0, 32))) {
            throw new InvalidDataException("Archive index page checksum does not match.");
            }
        byte[] plain = new byte[PayloadBytes];
        _codec.DecodeIndexPage(physical.AsSpan(EnvelopeBytes), plain, number, physical.AsSpan(32, 12), physical.AsSpan(44, 16));
        if (_pages.Count == MaximumCachedPages) {
            LinkedListNode<(ulong Number, byte[] Data)> oldest = _recent.Last!;
            _pages.Remove(oldest.Value.Number);
            _recent.RemoveLast();
            CryptographicOperations.ZeroMemory(oldest.Value.Data);
            }
        LinkedListNode<(ulong Number, byte[] Data)> node = _recent.AddFirst((number, plain));
        _pages.Add(number, node);
        return plain;
        }

    internal static (ulong Bytes, uint Pages, byte[] RootHash) Write(Stream destination, Stream logical, int chunkSize, ArchiveCodec codec) {
        int capacity = chunkSize - EnvelopeBytes;
        ulong bytes = checked((ulong)logical.Length);
        uint pages = checked((uint)((bytes + (uint)capacity - 1) / (uint)capacity));
        byte[] plain = new byte[capacity];
        byte[] physical = new byte[chunkSize];
        byte[] rootHash = Array.Empty<byte>();
        logical.Position = 0;
        destination.Position = chunkSize;
        for (uint page = 0; page < pages; page++) {
            Array.Clear(plain);
            Array.Clear(physical);
            int take = (int)Math.Min(capacity, logical.Length - logical.Position);
            logical.ReadExactly(plain.AsSpan(0, take));
            codec.EncodeIndexPage(plain, physical.AsSpan(EnvelopeBytes), page, physical.AsSpan(32, 12), physical.AsSpan(44, 16));
            SHA256.HashData(physical.AsSpan(32), physical.AsSpan(0, 32));
            if (page == 0) {
                rootHash = SHA256.HashData(physical);
                }
            destination.Write(physical);
            }
        CryptographicOperations.ZeroMemory(plain);
        return (bytes, pages, rootHash);
        }

    public override long Seek(long offset, SeekOrigin origin) {
        Position = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
        return _position;
        }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) {
        ClearCache();
        base.Dispose(disposing);
        }
    }
