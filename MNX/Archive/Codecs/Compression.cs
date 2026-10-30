using System.Diagnostics;
using System.IO.Compression;
using K4os.Compression.LZ4.Streams;
using K4os.Compression.LZ4;
using Snappier;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX.Archive.Codecs;

/// <summary>Shared codec stream construction for file streams and independent chunks.</summary>
internal static class CompressionStreams {
    internal static Stream CreateEncoder(Stream destination, ArchiveCompression compression, int level) {
        switch (compression) {
            case ArchiveCompression.Brotli:
                int quality = 1 + (level - 1) * 10 / 8;
                BrotliCompressionOptions options = new BrotliCompressionOptions {
                    Quality = quality
                    };
                return new BrotliStream(destination, options, leaveOpen: true);
            case ArchiveCompression.Snappy:
                return new SnappyStream(destination, CompressionMode.Compress, leaveOpen: true);
            case ArchiveCompression.LZ4:
                LZ4EncoderSettings settings = new LZ4EncoderSettings {
                    CompressionLevel = LZ4Level.L00_FAST,
                    ChainBlocks = true,
                    BlockSize = 65536
                    };
                return LZ4Stream.Encode(destination, settings, leaveOpen: true);
            default:
                throw new NotSupportedException("Unsupported compression method.");
            }
        }

    internal static Stream CreateDecoder(Stream source, ArchiveCompression compression) {
        switch (compression) {
            case ArchiveCompression.Brotli:
                return new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
            case ArchiveCompression.Snappy:
                return new SnappyStream(source, CompressionMode.Decompress, leaveOpen: true);
            case ArchiveCompression.LZ4:
                return LZ4Stream.Decode(source, leaveOpen: true);
            default:
                throw new InvalidDataException("Unsupported compression method.");
            }
        }
    }

/// <summary>Independent, bounded compression for one physical chunk.</summary>
internal static class ChunkCompression {
    internal static byte[] Compress(ReadOnlySpan<byte> input, ArchiveCompression method, int level) {
        if (method == ArchiveCompression.None) {
            return input.ToArray();
            }

        if (method == ArchiveCompression.Snappy) {
            return Snappy.CompressToArray(input);
            }

        using MemoryStream destination = new MemoryStream();
        using (Stream encoder = CompressionStreams.CreateEncoder(destination, method, level)) {
            encoder.Write(input);
            }

        return destination.ToArray();
        }

    internal static byte[] Decompress(ReadOnlySpan<byte> stored, ArchiveCompression method, int expectedLength) {
        if (expectedLength < 0 || expectedLength > 65536) {
            throw new InvalidDataException("Invalid logical chunk length.");
            }

        if (method == ArchiveCompression.Snappy) {
            if (Snappy.GetUncompressedLength(stored) != expectedLength) {
                throw new InvalidDataException("Snappy chunk length does not match the index.");
                }

            byte[] result = new byte[expectedLength];
            if (Snappy.Decompress(stored, result) != expectedLength) {
                throw new InvalidDataException("Snappy chunk decompressed to the wrong length.");
                }

            return result;
            }

        using MemoryStream source = new MemoryStream(stored.ToArray(), writable: false);
        using Stream decoder = CompressionStreams.CreateDecoder(source, method);
        byte[] output = new byte[expectedLength];
        try {
            decoder.ReadExactly(output);
            if (decoder.ReadByte() != -1) {
                throw new InvalidDataException("Compressed chunk exceeds its indexed length.");
                }
            }
        catch (EndOfStreamException ex) {
            throw new InvalidDataException("Compressed chunk is shorter than its indexed length.", ex);
            }

        return output;
        }
    }


/// <summary>Compression codecs available when creating an archive.</summary>
/// <summary>Stream whole-file compression into bounded physical chunks.</summary>
internal static class ArchiveCompressionIO {
    internal static void Write(ArchiveFile owner, Stream input, long sourceLength, Guid parent, Guid id, string name, ArchiveFile.EntryMetadata metadata) {
        using FileStream archive = owner.OpenWrite();
        uint first = owner.Allocate();
        VirtualFile file = ArchiveFile.FromMetadata(id, parent, name, metadata, false, (ulong)sourceLength, first);
        long bytesCompleted = 0;
        Stopwatch? timer = Stopwatch.StartNew();
        try {
            using CompressedChunkSink sink = new CompressedChunkSink(owner, archive, file, metadata);
            if (sourceLength > 0) {
                using (Stream encoder = CompressionStreams.CreateEncoder(sink, owner.Compression, owner.CompressionLevel)) {
                    byte[] buffer = new byte[64 * 1024];
                    while (bytesCompleted < sourceLength) {
                        owner.Options.CancellationToken.ThrowIfCancellationRequested();
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, sourceLength - bytesCompleted));
                        if (read == 0) {
                            throw new EndOfStreamException("Source file was shortened while being archived.");
                            }

                        encoder.Write(buffer, 0, read);
                        bytesCompleted += read;
                        if (timer.ElapsedMilliseconds >= owner.Options.ProgressReportFrequency) {
                            owner.RaiseProgress(name, bytesCompleted, sourceLength);
                            timer.Restart();
                            }
                        }
                    }
                }

            owner.Options.CancellationToken.ThrowIfCancellationRequested();
            sink.Complete();
            owner.AddEntry(file);
            owner.Commit(archive);
            owner.RaiseProgress(name, bytesCompleted, sourceLength);
            }
        catch {
            foreach (uint index in file.PhysicalChunks()) {
                ChunkFormat.ClearSlot(archive, index, owner.State.ChunkSize);
                owner.State.FreeChunks.Add(index);
                }

            owner.RemoveEntry(id);
            owner.TrimFreeTail(archive);
            owner.Commit(archive);
            throw;
            }
        }

    private sealed class CompressedChunkSink(ArchiveFile owner, Stream archive, VirtualFile file, ArchiveFile.EntryMetadata metadata) : Stream {
        private readonly byte[] _buffer = new byte[owner.State.ChunkSize - ArchiveFormat.NextHeaderSize];
        private uint _chunkSequence;
        private uint _physicalChunkIndex = file.FirstChunk;
        private int _bufferBytesUsed;
        private bool _completed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_completed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
            }

        public override void Write(ReadOnlySpan<byte> data) {
            if (_completed) {
                throw new ObjectDisposedException(nameof(CompressedChunkSink));
                }

            while (!data.IsEmpty) {
                int capacity = owner.State.ChunkSize - (_chunkSequence == 0 ? ArchiveFormat.FirstHeaderSize : ArchiveFormat.NextHeaderSize);
                if (_bufferBytesUsed == capacity) {
                    uint next = owner.Allocate();
                    file.AppendChunk(next);
                    FlushChunk(next);
                    _physicalChunkIndex = next;
                    _chunkSequence++;
                    _bufferBytesUsed = 0;
                    }

                int take = Math.Min(capacity - _bufferBytesUsed, data.Length);
                data[..take].CopyTo(_buffer.AsSpan(_bufferBytesUsed));
                _bufferBytesUsed += take;
                data = data[take..];
                }
            }

        internal void Complete() {
            if (_completed) {
                return;
                }

            if (_bufferBytesUsed == 0 && file.SizeOriginal != 0) {
                throw new InvalidDataException("Compressor emitted an empty stream.");
                }

            FlushChunk(0);
            _completed = true;
            }

        private void FlushChunk(uint next) {
            ChunkHeader slot = owner.NewSlot(
                file.FileID,
                file.OwnerID,
                _chunkSequence == 0 ? file.Name : string.Empty,
                _chunkSequence == 0 ? NodeType.File : NodeType.Continuation,
                metadata
                );
            slot.Sequence = _chunkSequence;
            slot.Next = next;
            slot.Size = _chunkSequence == 0 && !owner.IsEncrypted ? file.SizeOriginal : 0;
            ChunkFormat.WriteSlot(
                archive,
                _physicalChunkIndex,
                ref slot,
                _buffer.AsSpan(0, _bufferBytesUsed),
                owner.State.Codec,
                chunkSize: owner.State.ChunkSize
                );
            file.AppendLogicalLength((uint)_bufferBytesUsed);
            }

        public override void Flush() {
            }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        }

    }

internal sealed class StoredPayloadStream(ArchiveFile owner, VirtualFile file) : Stream {
    private readonly FileStream _input = DiskFile.OpenRead(owner.State.Path!);
    private uint _chunkSequence;
    private byte[] _payload = Array.Empty<byte>();
    private int _offset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
        }

    public override int Read(Span<byte> output) {
        if (output.IsEmpty) {
            return 0;
            }

        while (_offset == _payload.Length) {
            if (_chunkSequence == file.ChunkCount) {
                return 0;
                }

            uint physical = file.PhysicalChunk(_chunkSequence);
            byte[] payload = ChunkFormat.ReadChunk(
                _input,
                physical,
                out ChunkHeader slot,
                owner.State.Codec,
                file.LogicalLength(_chunkSequence),
                owner.State.ChunkSize
                );
            uint expectedNext = _chunkSequence + 1 < file.ChunkCount ? file.PhysicalChunk(_chunkSequence + 1) : 0;
            if (slot.Id != file.FileID || slot.Sequence != _chunkSequence || slot.Next != expectedNext || slot.Type != (_chunkSequence == 0 ? NodeType.File : NodeType.Continuation) || payload.Length == 0) {
                throw new InvalidDataException("Compressed file chunk chain is invalid.");
                }

            _payload = payload;
            _offset = 0;
            _chunkSequence++;
            }

        int take = Math.Min(output.Length, _payload.Length - _offset);
        _payload.AsSpan(_offset, take).CopyTo(output);
        _offset += take;
        return take;
        }

    public override void Flush() {
        }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _input.Dispose();
            }

        base.Dispose(disposing);
        }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

internal sealed class ContinuousArchiveReadStream : Stream {
    private readonly ArchiveFile _owner;
    private readonly VirtualFile _file;
    private StoredPayloadStream _payload;
    private Stream _decoder;
    private long _position;
    internal ContinuousArchiveReadStream(ArchiveFile owner, VirtualFile file) {
        _owner = owner;
        _file = file;
        _payload = new StoredPayloadStream(owner, file);
        _decoder = file.SizeOriginal == 0 ? Stream.Null : CompressionStreams.CreateDecoder(_payload, owner.Compression);
        }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => checked((long)_file.SizeOriginal);
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override int Read(byte[] buffer, int offset, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
        }

    public override int Read(Span<byte> output) {
        lock (_owner.State.SyncRoot) {
            _owner.CheckOpen();
            if (!_owner.State.Entries.TryGetValue(_file.FileID, out VirtualFile? current) || !ReferenceEquals(current, _file)) {
                throw new InvalidOperationException("The archived file changed while this stream was open.");
                }

            if (_position >= Length || output.IsEmpty) {
                return 0;
                }

            int wanted = (int)Math.Min(output.Length, Length - _position);
            int read = _decoder.Read(output[..wanted]);
            if (read == 0) {
                throw new InvalidDataException("Compressed file ended before its indexed length.");
                }

            _position += read;
            return read;
            }
        }

    internal void ValidateEnd() {
        if (_position != Length || _decoder.ReadByte() != -1) {
            throw new InvalidDataException("Compressed file exceeds its indexed length.");
            }
        }

    public override long Seek(long offset, SeekOrigin origin) {
        long target = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))};
        if (target < 0) {
            throw new ArgumentOutOfRangeException(nameof(offset));
            }

        if (target < _position) {
            _decoder.Dispose();
            _payload.Dispose();
            _payload = new StoredPayloadStream(_owner, _file);
            _decoder = _file.SizeOriginal == 0 ? Stream.Null : CompressionStreams.CreateDecoder(_payload, _owner.Compression);
            _position = 0;
            }

        byte[] discard = new byte[64 * 1024];
        while (_position < Math.Min(target, Length)) {
            int read = Read(discard, 0, (int)Math.Min(discard.Length, target - _position));
            if (read == 0) {
                throw new InvalidDataException("Compressed file ended before the seek target.");
                }
            }

        _position = target;
        return target;
        }

    public override void Flush() {
        }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _decoder.Dispose();
            _payload.Dispose();
            }

        base.Dispose(disposing);
        }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
