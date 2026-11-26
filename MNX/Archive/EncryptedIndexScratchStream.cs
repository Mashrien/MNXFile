using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MNX;

/// <summary>Seekable scratch storage encrypted with a fresh key owned only by this stream.</summary>
internal sealed class EncryptedIndexScratchStream : Stream {
    private const int BlockBytes = 65536;
    private const int AuthenticationBytes = 28;
    private const int PhysicalBytes = BlockBytes + AuthenticationBytes;
    private readonly FileStream _file;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly AesGcm _cipher;
    private readonly byte[] _plain = new byte[BlockBytes];
    private readonly byte[] _physical = new byte[PhysicalBytes];
    private long _cachedBlock = -1;
    private bool _dirty;
    private long _position;
    private long _length;

    internal EncryptedIndexScratchStream(FileStream file) {
        _file = file;
        _cipher = new(_key, 16);
        }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }

    private void LoadBlock(long number) {
        if (number == _cachedBlock) {
            return;
            }
        FlushBlock();
        Array.Clear(_plain);
        long offset = checked(number * PhysicalBytes);
        if (offset < _file.Length) {
            _file.Position = offset;
            _file.ReadExactly(_physical);
            // Unwritten holes belong to this newly created, process-private scratch file.
            bool written = false;
            for (int index = 0; index < AuthenticationBytes; index++) {
                written |= _physical[index] != 0;
                }
            if (written) {
                Span<byte> associated = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(associated, number);
                _cipher.Decrypt(_physical.AsSpan(0, 12), _physical.AsSpan(AuthenticationBytes), _physical.AsSpan(12, 16), _plain, associated);
                }
            }
        _cachedBlock = number;
        }

    private void FlushBlock() {
        if (!_dirty) {
            return;
            }
        Span<byte> nonce = _physical.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> associated = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(associated, _cachedBlock);
        _cipher.Encrypt(nonce, _plain, _physical.AsSpan(AuthenticationBytes), _physical.AsSpan(12, 16), associated);
        _file.Position = checked(_cachedBlock * PhysicalBytes);
        _file.Write(_physical);
        _dirty = false;
        }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> destination) {
        int count = (int)Math.Min(destination.Length, Math.Max(0, _length - _position));
        int copied = 0;
        while (copied < count) {
            LoadBlock(_position / BlockBytes);
            int within = (int)(_position % BlockBytes);
            int take = Math.Min(count - copied, BlockBytes - within);
            _plain.AsSpan(within, take).CopyTo(destination.Slice(copied, take));
            copied += take;
            _position += take;
            }
        return count;
        }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> source) {
        while (!source.IsEmpty) {
            LoadBlock(_position / BlockBytes);
            int within = (int)(_position % BlockBytes);
            int take = Math.Min(source.Length, BlockBytes - within);
            source.Slice(0, take).CopyTo(_plain.AsSpan(within, take));
            source = source.Slice(take);
            _position += take;
            _length = Math.Max(_length, _position);
            _dirty = true;
            }
        }
    public override void Flush() {
        FlushBlock();
        _file.Flush();
        }
    public override long Seek(long offset, SeekOrigin origin) {
        Position = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
        return Position;
        }
    public override void SetLength(long value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        _length = value;
        }
    protected override void Dispose(bool disposing) {
        if (disposing) {
            _cipher.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            CryptographicOperations.ZeroMemory(_plain);
            _file.Dispose();
            }
        base.Dispose(disposing);
        }
    }
