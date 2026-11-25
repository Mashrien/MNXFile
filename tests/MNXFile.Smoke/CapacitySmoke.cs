using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using MNX;
using MNX.Storage;

internal static class CapacitySmoke {
    private const ulong MaximumBytes = 140_737_488_322_560UL;

    internal static void Run() {
        using (Archive archive = new Archive()) {
            archive.State.ChunkSize = 32768;
            archive.State.ChunkCount = uint.MaxValue - 2;
            Require(archive.Allocate() == uint.MaxValue - 1, "The last physical chunk below the capacity limit must remain usable.");
            Reject(() => archive.Allocate(), "Allocation must include the header chunk when enforcing the archive limit.");
            Require(archive.State.ChunkCount == uint.MaxValue - 1, "Rejected allocation changed archive state.");
            }

        using (VirtualArchiveStream stream = new VirtualArchiveStream()) {
            ArchiveFormat.WriteHeader(stream, Guid.NewGuid(), Guid.NewGuid(), uint.MaxValue - 1, 0, 1,
                new byte[16], 7, NodeFlags.None, chunkSize: 32768);
            stream.SetLength((long)MaximumBytes);
            Require(ArchiveFormat.ReadHeader(stream).chunks == uint.MaxValue - 1, "The exact archive limit must be readable.");
            stream.Position = 48;
            Span<byte> count = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(count, uint.MaxValue);
            stream.Write(count);
            stream.Position = 0;
            byte[] header = new byte[144];
            stream.ReadExactly(header);
            stream.Position = 144;
            stream.Write(SHA256.HashData(header));
            stream.SetLength((long)MaximumBytes + 32768);
            Reject(() => ArchiveFormat.ReadHeader(stream), "An otherwise valid header above the byte limit must be rejected.");
            }

        uint chunks = uint.MaxValue - 2;
        VFile large = MakeFile(2, (ulong)chunks * 32768);
        large.SetExtents(new ChunkExtent[] { new ChunkExtent(0, 2, chunks) });
        large.SetChunkLengthGroups(new ChunkLengthGroup[] { new ChunkLengthGroup(0, chunks, 32768, 0) });
        foreach (ulong offset in new ulong[] { 2_147_483_647, 2_147_483_648, 4_294_967_295, 4_294_967_296, large.SizeOriginal - 1 }) {
            (uint sequence, int withinChunk) = large.LocateByte(offset);
            Require((ulong)sequence * 32768 + (uint)withinChunk == offset && large.PhysicalChunk(sequence) == (ulong)sequence + 2,
                "File byte-offset lookup narrowed a value above 2/4 GiB.");
            }

        VFile last = MakeFile(uint.MaxValue, 1);
        last.SetExtents(new ChunkExtent[] { new ChunkExtent(0, uint.MaxValue, 1) });
        last.SetChunkLengthGroups(new ChunkLengthGroup[] { new ChunkLengthGroup(0, 1, 1, 0) });
        FreeChunkSet free = new FreeChunkSet();
        free.AddRange(2, uint.MaxValue - 2);
        byte[] index = ArchiveIndex.Serialize(new VFile[] { last }, free);
        ArchiveIndex.Deserialize(index, 1, 1, uint.MaxValue, last.OwnerID, 4096);

        using (Archive archive = new Archive())
        using (VirtualArchiveStream stream = new VirtualArchiveStream()) {
            archive.State.ChunkSize = 32768;
            archive.State.IndexChunks = 1;
            archive.State.ChunkCount = uint.MaxValue - 1;
            VFile moved = MakeFile(2, 1);
            moved.AppendChunk(2);
            moved.AppendLogicalLength(1);
            archive.State.Entries.Add(moved.FileID, moved);
            ChunkHeader slot = new ChunkHeader {
                Type = NodeType.File, Id = moved.FileID, Parent = moved.OwnerID, Name = moved.Name, Size = 1,
                CreatedUtc = DateTime.UnixEpoch, ModifiedUtc = DateTime.UnixEpoch, AccessedUtc = DateTime.UnixEpoch
                };
            ChunkFormat.WriteSlot(stream, 2, ref slot, new byte[] { 42 }, chunkSize: 32768);
            stream.SetLength((long)MaximumBytes);
            int writes = stream.WriteCalls;
            MethodInfo expand = typeof(Archive).GetMethod("ExpandIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Reject(() => {
                try {
                    expand.Invoke(archive, new object[] { stream, 32769 });
                    }
                catch (TargetInvocationException error) when (error.InnerException is IOException) {
                    throw error.InnerException;
                    }
                }, "Index growth must preflight relocation space before changing the archive.");
            Require(stream.WriteCalls == writes && archive.State.ChunkCount == uint.MaxValue - 1 && archive.State.IndexChunks == 1,
                "Failed index growth changed metadata or wrote physical data.");
            }
        Console.WriteLine("PASS: archive capacity, 64-bit offsets, final uint extent and atomic index-growth boundary checks.");
        }

    private static VFile MakeFile(uint first, ulong size) {
        return new VFile(Guid.NewGuid(), Guid.NewGuid(), "capacity.bin", false, size,
            DateTime.UnixEpoch, DateTime.UnixEpoch, DateTime.UnixEpoch, (Archive.Attribs)0, first);
        }

    private static void Reject(Action action, string message) {
        try {
            action();
            }
        catch (Exception error) when (error is IOException || error is InvalidDataException) {
            return;
            }
        throw new Exception(message);
        }

    private static void Require(bool condition, string message) {
        if (!condition) {
            throw new Exception(message);
            }
        }

    // Sparse virtual addresses only: the largest tests store a handful of 32 KiB
    // pages in memory and never create or resize a physical multi-terabyte file.
    private sealed class VirtualArchiveStream : Stream {
        private readonly Dictionary<long, byte[]> _pages = new Dictionary<long, byte[]>();
        private long _length;
        private long _position;
        private const int PageSize = 32768;
        internal int WriteCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination) {
            int count = (int)Math.Min(destination.Length, Math.Max(0, _length - _position));
            int copied = 0;
            while (copied < count) {
                long pageNumber = _position / PageSize;
                int pageOffset = (int)(_position % PageSize);
                int take = Math.Min(count - copied, PageSize - pageOffset);
                Span<byte> target = destination.Slice(copied, take);
                if (_pages.TryGetValue(pageNumber, out byte[]? page)) {
                    page.AsSpan(pageOffset, take).CopyTo(target);
                    }
                else {
                    target.Clear();
                    }
                copied += take;
                _position += take;
                }
            return count;
            }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> source) {
            WriteCalls++;
            while (!source.IsEmpty) {
                long pageNumber = _position / PageSize;
                int pageOffset = (int)(_position % PageSize);
                int take = Math.Min(source.Length, PageSize - pageOffset);
                if (!_pages.TryGetValue(pageNumber, out byte[]? page)) {
                    page = new byte[PageSize];
                    _pages.Add(pageNumber, page);
                    }
                source.Slice(0, take).CopyTo(page.AsSpan(pageOffset, take));
                source = source.Slice(take);
                _position += take;
                _length = Math.Max(_length, _position);
                }
            }
        public override long Seek(long offset, SeekOrigin origin) {
            _position = origin switch {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
            return _position;
            }
        public override void SetLength(long value) {
            _length = value;
            }
        }
    }
