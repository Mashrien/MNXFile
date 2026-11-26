using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using MNX.Codecs;

namespace MNX.Storage;

/// <summary>Reads adjacent disk chunks in batches and delivers validated payloads in file order.</summary>
internal sealed class ChunkReadWindow(Archive owner, VFile file, FileStream input) : IDisposable {
    internal const int BytesPerWorker = 256 * 1024;
    private readonly bool _ownsInput;
    private byte[][] _payloads = [];
    private uint _first;
    private bool _singleChunkOnly;

    internal ChunkReadWindow(Archive owner, VFile file)
        : this(owner, file, OpenInput(owner)) {
        _ownsInput = true;
        }

    internal static FileStream OpenInput(Archive owner) {
        return new FileStream(owner.State.Path!, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        }

    // The coordinator holds the archive gate until every worker has completed.
    // Workers use positional I/O and never acquire the gate or invoke a user callback.
    internal byte[] Read(uint sequence, uint requestedChunks) {
        CancellationToken cancellation = owner.Options.CancellationToken;
        cancellation.ThrowIfCancellationRequested();
        if (sequence >= _first && sequence - _first < _payloads.Length) {
            return _payloads[sequence - _first];
            }

        int workers = owner.Options.ReadWorkers;
        int chunkSize = owner.State.ChunkSize;
        int capacity = BytesPerWorker / chunkSize * workers;
        uint available = file.ChunkCount - sequence;
        int count = (int)Math.Min(Math.Min(requestedChunks, available), (uint)capacity);
        _payloads = [];
        _first = sequence;
        if (workers > 1 && count > 1 && !_singleChunkOnly) {
            try {
                _payloads = FillWindowAsync(sequence, count, workers, chunkSize, cancellation).GetAwaiter().GetResult();
                return _payloads[0];
                }
            catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException) {
                // A decoder can request bytes ahead of the caller. Deliver good earlier chunks
                // and report damage when the caller actually reaches the affected sequence.
                _singleChunkOnly = true;
                }
            }

        uint physical = file.PhysicalChunk(sequence);
        byte[] payload = ChunkFormat.ReadChunk(input, physical, out ChunkHeader header,
            owner.State.Codec, file.LogicalLength(sequence), chunkSize);
        ValidateChain(sequence, header, payload.Length);
        _payloads = [payload];
        return payload;
        }

    private async Task<byte[][]> FillWindowAsync(uint first, int count, int workers, int chunkSize, CancellationToken cancellation) {
        int groups = Math.Min(workers, count);
        byte[][] ordered = new byte[count][];
        Task[] pending = new Task[groups];
        SafeFileHandle handle = input.SafeFileHandle;
        for (int worker = 0; worker < groups; worker++) {
            int begin = count * worker / groups;
            int end = count * (worker + 1) / groups;
            pending[worker] = ReadGroupAsync(handle, first, begin, end, chunkSize, ordered, cancellation);
            }
        await Task.WhenAll(pending).ConfigureAwait(false);
        return ordered;
        }

    private async Task ReadGroupAsync(SafeFileHandle handle, uint first, int begin, int end, int chunkSize,
        byte[][] ordered, CancellationToken cancellation) {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((end - begin) * chunkSize);
        try {
            ArchiveCodec? codec = owner.State.Codec;
            using AesGcm? cipher = codec?.CreateReadCipher();
            int cursor = begin;
            while (cursor < end) {
                cancellation.ThrowIfCancellationRequested();
                uint physical = file.PhysicalChunk(first + (uint)cursor);
                int run = 1;
                while (cursor + run < end && (ulong)physical + (uint)run == file.PhysicalChunk(first + (uint)(cursor + run))) {
                    run++;
                    }

                long offset = (long)physical * chunkSize;
                Memory<byte> remaining = buffer.AsMemory(0, run * chunkSize);
                while (!remaining.IsEmpty) {
                    int read = await RandomAccess.ReadAsync(handle, remaining, offset, cancellation).ConfigureAwait(false);
                    if (read == 0) {
                        throw new EndOfStreamException("The archive ended inside a chunk.");
                        }
                    offset += read;
                    remaining = remaining.Slice(read);
                    }

                for (int slot = 0; slot < run; slot++) {
                    cancellation.ThrowIfCancellationRequested();
                    uint sequence = first + (uint)cursor;
                    ordered[cursor] = ChunkFormat.DecodeChunk(buffer.AsSpan(slot * chunkSize, chunkSize),
                        physical + (uint)slot, out ChunkHeader header, codec, file.LogicalLength(sequence), cipher);
                    ValidateChain(sequence, header, ordered[cursor].Length);
                    cursor++;
                    }
                }
            }
        finally {
            ArrayPool<byte>.Shared.Return(buffer);
            }
        }

    private void ValidateChain(uint sequence, in ChunkHeader header, int payloadLength) {
        if (payloadLength != file.LogicalLength(sequence)) {
            throw new InvalidDataException($"Chunk length mismatch for {file.Name} at sequence {sequence}.");
            }
        NodeType expectedType = NodeType.Continuation;
        if (sequence == 0) {
            expectedType = file.IsDirectory ? NodeType.Directory : NodeType.File;
            }
        uint next = sequence + 1 < file.ChunkCount ? file.PhysicalChunk(sequence + 1) : 0;
        if (header.Id != file.FileID || header.Sequence != sequence || header.Type != expectedType || header.Next != next) {
            throw new InvalidDataException($"Chunk chain mismatch for {file.Name} at sequence {sequence}.");
            }
        }

    public void Dispose() {
        _payloads = [];
        if (_ownsInput) {
            input.Dispose();
            }
        }
    }
