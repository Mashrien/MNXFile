using System.Text;
using MNX.Codecs;
using MNX.Storage;
using MNX.Tools;

namespace MNX;

internal readonly record struct IndexDescriptor(Guid Id, Guid Owner, ulong Offset, ulong Length, bool Directory) {
    internal const int Bytes = 56;
    internal static IndexDescriptor Read(BinaryReader reader) {
        Guid id = xGuid.ReadGuid(reader);
        Guid owner = xGuid.ReadGuid(reader);
        ulong offset = reader.ReadUInt64();
        ulong length = reader.ReadUInt64();
        uint flags = reader.ReadUInt32();
        uint reserved = reader.ReadUInt32();
        if (flags > 1 || reserved != 0) {
            throw new InvalidDataException("Archive index descriptor flags are invalid.");
            }
        return new(id, owner, offset, length, flags == 1);
        }
    internal void Write(BinaryWriter writer) {
        xGuid.WriteGuid(writer, Id);
        xGuid.WriteGuid(writer, Owner);
        writer.Write(Offset);
        writer.Write(Length);
        writer.Write(Directory ? 1u : 0u);
        writer.Write(0u);
        }
    internal static int CompareDirectory(IndexDescriptor left, IndexDescriptor right) {
        int owner = left.Owner.CompareTo(right.Owner);
        if (owner != 0) {
            return owner;
            }
        int directory = right.Directory.CompareTo(left.Directory);
        return directory != 0 ? directory : left.Id.CompareTo(right.Id);
        }
    }

/// <summary>A temporary disk stream with deterministic removal, including failed rebuilds.</summary>
internal sealed class IndexTemporaryFile : IDisposable {
    internal Stream Stream { get; }
    internal IndexTemporaryFile() {
        string path = Path.Combine(Path.GetTempPath(), "mnx-index-" + Guid.NewGuid().ToString("N"));
        FileStreamOptions options = new() {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.DeleteOnClose
            };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
        Stream = new EncryptedIndexScratchStream(new FileStream(path, options));
        }
    public void Dispose() => Stream.Dispose();
    internal Stream OpenReadView() => new ScratchReadView(Stream);

    private sealed class ScratchReadView(Stream source) : Stream {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => source.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

/// <summary>On-demand front index with fixed-width GUID and parent lookup tables.</summary>
internal sealed class PagedArchiveIndex : IDisposable {
    private const int HeaderBytes = 128;
    private readonly PagedIndexStream _stream;
    private readonly BinaryReader _reader;
    private readonly uint _indexChunks;
    private readonly uint _totalChunks;
    private readonly int _chunkSize;
    private readonly bool _streamed;
    private readonly Guid _root;
    private readonly ulong _idOffset;
    private readonly ulong _directoryOffset;
    private readonly ulong _recordsOffset;
    private readonly ulong _freeOffset;
    private readonly uint _freeCount;
    private readonly IDisposable? _ownedSource;
    private readonly ulong _freeChunks;
    internal uint Count { get; }
    internal uint DirectoryCount { get; }
    internal ulong PageReads => _stream.PageReads;
    internal int ResidentPages => _stream.ResidentPages;

    internal PagedArchiveIndex(Func<Stream> open, ArchiveCodec codec, ArchiveFormat.HeaderInfo header, IDisposable? ownedSource = null, uint? dataIndexChunks = null) {
        _ownedSource = ownedSource;
        _indexChunks = dataIndexChunks ?? header.indexChunks;
        _totalChunks = header.chunks;
        _chunkSize = header.chunkSize;
        _streamed = header.flags.HasFlag(NodeFlags.Streamed);
        _root = header.rootId;
        _stream = new(open, codec, header.chunkSize, header.indexBytes, header.indexHash);
        _reader = new(_stream, Encoding.UTF8, true);
        if (!_reader.ReadBytes(8).AsSpan().SequenceEqual("MNXIDX8\0"u8)) {
            throw new InvalidDataException("Unsupported paged archive index.");
            }
        Count = _reader.ReadUInt32();
        DirectoryCount = _reader.ReadUInt32();
        _freeCount = _reader.ReadUInt32();
        if (_reader.ReadUInt32() != 0) {
            throw new InvalidDataException("Archive index reserved field is invalid.");
            }
        _idOffset = _reader.ReadUInt64();
        _directoryOffset = _reader.ReadUInt64();
        _recordsOffset = _reader.ReadUInt64();
        _freeOffset = _reader.ReadUInt64();
        ulong length = _reader.ReadUInt64();
        _freeChunks = _reader.ReadUInt64();
        ulong tableBytes = (ulong)Count * IndexDescriptor.Bytes;
        ulong pages = (length + (uint)_stream.PayloadBytes - 1) / (uint)_stream.PayloadBytes;
        if (Count != header.entries || DirectoryCount > Count || Count > header.chunks - _indexChunks || _freeCount > int.MaxValue || _freeChunks < _freeCount || _freeChunks + Count > header.chunks - _indexChunks || (_freeCount == 0 && _freeChunks != 0) || _idOffset != HeaderBytes || _directoryOffset != _idOffset + tableBytes || _recordsOffset != _directoryOffset + tableBytes || _freeOffset < _recordsOffset || length != header.indexBytes || _freeOffset > length || length - _freeOffset != (ulong)_freeCount * 8 || pages > header.indexChunks) {
            throw new InvalidDataException("Archive index table bounds are invalid.");
            }
        }

    internal IndexDescriptor DescriptorAt(uint ordinal, bool directory = false) {
        if (ordinal >= Count) {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
            }
        _stream.Position = checked((long)((directory ? _directoryOffset : _idOffset) + (ulong)ordinal * IndexDescriptor.Bytes));
        IndexDescriptor descriptor = IndexDescriptor.Read(_reader);
        if (descriptor.Id == Guid.Empty || descriptor.Id == _root || descriptor.Owner == Guid.Empty || descriptor.Offset < _recordsOffset || descriptor.Offset > _freeOffset || descriptor.Length > _freeOffset - descriptor.Offset || descriptor.Length < 98) {
            throw new InvalidDataException("Archive index descriptor exceeds its metadata region.");
            }
        return descriptor;
        }

    internal VFile ReadEntry(IndexDescriptor descriptor) {
        _stream.Position = checked((long)descriptor.Offset);
        VFile file = IndexRecord.Read(_reader, descriptor.Offset + descriptor.Length, _indexChunks, _totalChunks, _chunkSize, _streamed, _root);
        if (file.FileID != descriptor.Id || file.OwnerID != descriptor.Owner || file.IsDirectory != descriptor.Directory) {
            throw new InvalidDataException("Archive lookup table does not match its metadata record.");
            }
        return file;
        }

    internal VFile? Get(Guid id) {
        IndexDescriptor? descriptor = Find(id);
        return descriptor is IndexDescriptor found ? ReadEntry(found) : null;
        }

    private IndexDescriptor? Find(Guid id) {
        ulong low = 0;
        ulong high = Count;
        while (low < high) {
            uint middle = (uint)(low + (high - low) / 2);
            IndexDescriptor descriptor = DescriptorAt(middle);
            int comparison = descriptor.Id.CompareTo(id);
            if (comparison < 0) {
                low = (ulong)middle + 1;
                }
            else if (comparison > 0) {
                high = middle;
                }
            else {
                return descriptor;
                }
            }
        return null;
        }

    internal void ValidateTables(CancellationToken cancellationToken) {
        _stream.ClearCache();
        byte[] buffer = new byte[64 * 1024];
        _stream.Position = 0;
        while (_stream.Read(buffer) > 0) {
            cancellationToken.ThrowIfCancellationRequested();
            }
        Guid previousId = Guid.Empty;
        IndexDescriptor? previousDirectory = null;
        ulong recordPosition = _recordsOffset;
        uint directories = 0;
        for (ulong ordinal = 0; ordinal < Count; ordinal++) {
            cancellationToken.ThrowIfCancellationRequested();
            IndexDescriptor id = DescriptorAt((uint)ordinal);
            if (ordinal > 0 && previousId.CompareTo(id.Id) >= 0) {
                throw new InvalidDataException("Archive GUID lookup table is duplicated or unordered.");
                }
            previousId = id.Id;
            IndexDescriptor directory = DescriptorAt((uint)ordinal, true);
            if ((previousDirectory is IndexDescriptor previous && IndexDescriptor.CompareDirectory(previous, directory) >= 0) || directory.Offset != recordPosition || Find(directory.Id) != directory) {
                throw new InvalidDataException("Archive directory lookup table does not match its ordered metadata records.");
                }
            previousDirectory = directory;
            recordPosition = checked(recordPosition + directory.Length);
            if (directory.Directory) {
                directories++;
                }
            }
        if (recordPosition != _freeOffset || directories != DirectoryCount) {
            throw new InvalidDataException("Archive metadata coverage or directory count is invalid.");
            }
        }

    private uint DirectoryStart(Guid owner) {
        ulong low = 0;
        ulong high = Count;
        while (low < high) {
            uint middle = (uint)(low + (high - low) / 2);
            if (DescriptorAt(middle, true).Owner.CompareTo(owner) < 0) {
                low = (ulong)middle + 1;
                }
            else {
                high = middle;
                }
            }
        return checked((uint)low);
        }

    internal IEnumerable<IndexDescriptor> DirectoryDescriptors(Guid owner) {
        for (ulong ordinal = DirectoryStart(owner); ordinal < Count; ordinal++) {
            IndexDescriptor descriptor = DescriptorAt((uint)ordinal, true);
            if (descriptor.Owner != owner) {
                yield break;
                }
            yield return descriptor;
            }
        }

    internal IEnumerable<VFile> Enumerate(uint offset = 0) {
        for (ulong ordinal = offset; ordinal < Count; ordinal++) {
            yield return ReadEntry(DescriptorAt((uint)ordinal));
            }
        }

    internal IEnumerable<VFile> EnumerateInDirectoryOrder() {
        for (ulong ordinal = 0; ordinal < Count; ordinal++) {
            yield return ReadEntry(DescriptorAt((uint)ordinal, true));
            }
        }

    internal FreeChunkSet ReadFreeChunks() {
        FreeChunkSet result = new();
        result.SetSource(new FreeRangeView(this), _freeChunks);
        return result;
        }

    private FreeChunkRange FreeRangeAt(int index) {
        if ((uint)index >= _freeCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
            }
        FreeChunkRange range = ReadFreeRange(index);
        if (index > 0 && ReadFreeRange(index - 1).End >= range.Start) {
            throw new InvalidDataException("Archive free chunk ranges overlap or are unordered.");
            }
        return range;
        }

    private FreeChunkRange ReadFreeRange(int index) {
        _stream.Position = checked((long)(_freeOffset + (ulong)(uint)index * 8));
        uint start = _reader.ReadUInt32();
        uint length = _reader.ReadUInt32();
        if (length == 0 || start <= _indexChunks || (ulong)start + length - 1 > _totalChunks) {
            throw new InvalidDataException("Archive free chunk range is invalid.");
            }
        return new(start, length);
        }

    private sealed class FreeRangeView(PagedArchiveIndex owner) : IReadOnlyList<FreeChunkRange> {
        public int Count => (int)owner._freeCount;
        public FreeChunkRange this[int index] => owner.FreeRangeAt(index);
        public IEnumerator<FreeChunkRange> GetEnumerator() {
            ulong previousEnd = 0;
            for (int index = 0; index < Count; index++) {
                FreeChunkRange range = this[index];
                if (range.Start <= previousEnd) {
                    throw new InvalidDataException("Archive free chunk ranges overlap or are unordered.");
                    }
                previousEnd = range.End;
                yield return range;
                }
            }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

    internal static IndexTemporaryFile Build(ArchiveEntryStore entries, FreeChunkSet free) {
        return Build(entries.ForRebuild(), entries.Count, free);
        }

    internal static IndexTemporaryFile Build(IEnumerable<VFile> files, uint expectedEntries, FreeChunkSet free) {
        IndexTemporaryFile result = new();
        try {
            Stream logical = result.Stream;
            ulong idOffset = HeaderBytes;
            ulong directoryOffset = idOffset + (ulong)expectedEntries * IndexDescriptor.Bytes;
            ulong recordsOffset = directoryOffset + (ulong)expectedEntries * IndexDescriptor.Bytes;
            logical.SetLength(checked((long)recordsOffset));
            using BinaryWriter writer = new(logical, Encoding.UTF8, true);
            using IndexTemporaryFile records = new();
            using BinaryWriter recordWriter = new(records.Stream, Encoding.UTF8, true);
            using IndexTemporaryFile directoryTable = new();
            using BinaryWriter directoryWriter = new(directoryTable.Stream, Encoding.UTF8, true);
            uint ordinal = 0;
            uint directories = 0;
            foreach (VFile file in files) {
                ulong start = (ulong)records.Stream.Position;
                IndexRecord.Write(recordWriter, file);
                ulong end = (ulong)records.Stream.Position;
                IndexDescriptor descriptor = new(file.FileID, file.OwnerID, start, end - start, file.IsDirectory);
                descriptor.Write(directoryWriter);
                ordinal = checked(ordinal + 1);
                if (file.IsDirectory) {
                    directories++;
                    }
                }
            if (ordinal != expectedEntries) {
                throw new InvalidOperationException("Archive entries changed during index serialization.");
                }
            recordWriter.Flush();
            directoryWriter.Flush();
            using IndexTemporaryFile sortedDirectory = new();
            DescriptorSort.Sort(directoryTable.Stream, sortedDirectory.Stream);
            sortedDirectory.Stream.Position = 0;
            using BinaryReader sortedReader = new(sortedDirectory.Stream, Encoding.UTF8, true);
            using IndexTemporaryFile idTable = new();
            using BinaryWriter idWriter = new(idTable.Stream, Encoding.UTF8, true);
            using IndexTemporaryFile updatedDirectory = new();
            using BinaryWriter updatedDirectoryWriter = new(updatedDirectory.Stream, Encoding.UTF8, true);
            logical.Position = checked((long)recordsOffset);
            byte[] copyBuffer = new byte[64 * 1024];
            for (uint index = 0; index < ordinal; index++) {
                IndexDescriptor sourceDescriptor = IndexDescriptor.Read(sortedReader);
                ulong recordOffset = (ulong)logical.Position;
                records.Stream.Position = checked((long)sourceDescriptor.Offset);
                ulong remaining = sourceDescriptor.Length;
                while (remaining > 0) {
                    int take = (int)Math.Min((ulong)copyBuffer.Length, remaining);
                    records.Stream.ReadExactly(copyBuffer.AsSpan(0, take));
                    logical.Write(copyBuffer, 0, take);
                    remaining -= (uint)take;
                    }
                IndexDescriptor descriptor = sourceDescriptor with { Offset = recordOffset };
                descriptor.Write(updatedDirectoryWriter);
                descriptor.Write(idWriter);
                }
            ulong freeOffset = (ulong)logical.Position;
            foreach (FreeChunkRange range in free.Ranges) {
                writer.Write(range.Start);
                writer.Write(range.Count);
                }
            ulong length = (ulong)logical.Position;
            updatedDirectoryWriter.Flush();
            updatedDirectory.Stream.Position = 0;
            logical.Position = checked((long)directoryOffset);
            updatedDirectory.Stream.CopyTo(logical, copyBuffer.Length);
            logical.Position = checked((long)idOffset);
            idWriter.Flush();
            DescriptorSort.Sort(idTable.Stream, logical, directory: false);
            logical.Position = 0;
            writer.Write("MNXIDX8\0"u8);
            writer.Write(expectedEntries);
            writer.Write(directories);
            writer.Write((uint)free.Ranges.Count);
            writer.Write(0u);
            writer.Write(idOffset);
            writer.Write(directoryOffset);
            writer.Write(recordsOffset);
            writer.Write(freeOffset);
            writer.Write(length);
            writer.Write(free.Count);
            writer.Flush();
            logical.Position = 0;
            return result;
            }
        catch {
            result.Dispose();
            throw;
            }
        }

    public void Dispose() {
        _reader.Dispose();
        _stream.Dispose();
        _ownedSource?.Dispose();
        }
    }

/// <summary>Bounded external merge sort; at most 4096 descriptors and 64 readers per merge.</summary>
internal static class DescriptorSort {
    private const int RunEntries = 4096;
    private const int FanIn = 64;
    internal static void Sort(Stream source, Stream destination, bool directory = true) {
        Comparison<IndexDescriptor> comparison = directory ? IndexDescriptor.CompareDirectory : (left, right) => left.Id.CompareTo(right.Id);
        List<List<IndexTemporaryFile>> levels = new();
        List<IndexTemporaryFile> owned = new();
        try {
            source.Position = 0;
            using BinaryReader reader = new(source, Encoding.UTF8, true);
            while (source.Position < source.Length) {
                List<IndexDescriptor> descriptors = new(RunEntries);
                while (descriptors.Count < RunEntries && source.Position < source.Length) {
                    descriptors.Add(IndexDescriptor.Read(reader));
                    }
                descriptors.Sort(comparison);
                IndexTemporaryFile run = new();
                owned.Add(run);
                using (BinaryWriter writer = new(run.Stream, Encoding.UTF8, true)) {
                    foreach (IndexDescriptor descriptor in descriptors) {
                        descriptor.Write(writer);
                        }
                    }
                int level = 0;
                while (true) {
                    if (level == levels.Count) {
                        levels.Add(new());
                        }
                    levels[level].Add(run);
                    if (levels[level].Count < FanIn) {
                        break;
                        }
                    IndexTemporaryFile merged = new();
                    owned.Add(merged);
                    Merge(levels[level], merged.Stream, comparison);
                    foreach (IndexTemporaryFile consumed in levels[level]) {
                        consumed.Dispose();
                        owned.Remove(consumed);
                        }
                    levels[level].Clear();
                    run = merged;
                    level++;
                    }
                }
            List<IndexTemporaryFile> remaining = levels.SelectMany(level => level).ToList();
            while (remaining.Count > FanIn) {
                List<IndexTemporaryFile> next = new();
                for (int start = 0; start < remaining.Count; start += FanIn) {
                    List<IndexTemporaryFile> group = remaining.Skip(start).Take(FanIn).ToList();
                    IndexTemporaryFile merged = new();
                    owned.Add(merged);
                    Merge(group, merged.Stream, comparison);
                    next.Add(merged);
                    foreach (IndexTemporaryFile consumed in group) {
                        consumed.Dispose();
                        owned.Remove(consumed);
                        }
                    }
                remaining = next;
                }
            Merge(remaining, destination, comparison);
            }
        finally {
            foreach (IndexTemporaryFile file in owned) {
                file.Dispose();
                }
            }
        }

    private static void Merge(IReadOnlyList<IndexTemporaryFile> runs, Stream destination, Comparison<IndexDescriptor> comparison) {
        List<BinaryReader> readers = new();
        PriorityQueue<(IndexDescriptor Descriptor, int Run), IndexDescriptor> queue = new(Comparer<IndexDescriptor>.Create(comparison));
        using BinaryWriter writer = new(destination, Encoding.UTF8, true);
        try {
            for (int index = 0; index < runs.Count; index++) {
                runs[index].Stream.Position = 0;
                BinaryReader reader = new(runs[index].Stream, Encoding.UTF8, true);
                readers.Add(reader);
                if (reader.BaseStream.Length > 0) {
                    IndexDescriptor descriptor = IndexDescriptor.Read(reader);
                    queue.Enqueue((descriptor, index), descriptor);
                    }
                }
            while (queue.TryDequeue(out (IndexDescriptor Descriptor, int Run) item, out _)) {
                item.Descriptor.Write(writer);
                BinaryReader reader = readers[item.Run];
                if (reader.BaseStream.Position < reader.BaseStream.Length) {
                    IndexDescriptor descriptor = IndexDescriptor.Read(reader);
                    queue.Enqueue((descriptor, item.Run), descriptor);
                    }
                }
            }
        finally {
            foreach (BinaryReader reader in readers) {
                reader.Dispose();
                }
            }
        }
    }
