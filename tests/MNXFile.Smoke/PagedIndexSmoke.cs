using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MNX;
using MNX.Codecs;
using MNX.Storage;

internal static class PagedIndexSmoke {
    internal static void Run(string work) {
        MalformedRecordBounds();
        EncryptedScratch(work);
        VirtualIndexOverFourGiB();
        LegacyReadAndUpgrade(work);
        RealPagedReopen(work, encrypted: false);
        RealPagedReopen(work, encrypted: true);
        Console.WriteLine("PASS: >4 GiB virtual paged index, bounded page/metadata caches, direct folder/GUID lookups, owner-contiguous reads, streamed rebuild, encrypted pages and wire7 compatibility.");
        }

    private static void VirtualIndexOverFourGiB() {
        GeneratedIndex generated = new(21_000_000);
        Require(generated.LogicalLength > uint.MaxValue, "Virtual test must cross both the 2 GiB array and 4 GiB index-width boundaries.");
        using ArchiveCodec codec = new(generated.ArchiveId, ArchiveCompression.None, 0, null);
        ArchiveFormat.HeaderInfo header = generated.Header();
        ArchiveEntryStore store = new();
        store.Reset(new PagedArchiveIndex(() => new GeneratedPhysicalStream(generated), codec, header));
        Require(store.Count == 21_000_000 && store.ResidentEntries == 0 && store.PageReads == 1, "Opening a large index eagerly loaded metadata or unrelated pages.");
        Guid owner = generated.Owner(12345);
        ulong before = store.PageReads;
        VFile[] children = store.Directory(owner).ToArray();
        Require(children.Length == 100 && children.All(file => file.OwnerID == owner), "Owner lookup did not return precisely one folder.");
        Require(store.PageReads - before <= 32, "One folder required scattered or full-index reads.");
        ulong warmed = store.PageReads;
        Require(store.Directory(owner).Count() == 100 && store.PageReads == warmed, "A warm folder reread its index pages.");
        VFile? far = store.GetValueOrDefault(generated.Id(20_999_999));
        Require(far is not null && far.SizeOriginal == 1, "GUID lookup above the 4 GiB logical index boundary failed.");
        Require(store.Enumerate(20_999_900, 100).Count() == 100, "Offset enumeration failed near the final descriptor.");
        for (uint index = 0; index < 800; index++) {
            Require(store.GetValueOrDefault(generated.Id(index * 25001)) is not null, "Scattered direct lookup failed.");
            }
        Require(store.ResidentEntries <= ArchiveEntryStore.MaximumCachedEntries && store.ResidentPages <= PagedIndexStream.MaximumCachedPages && store.DirtyEntries == 0, "Clean browsing accumulated resident metadata or pages.");
        Console.WriteLine($"PASS: virtual index {generated.LogicalLength:N0} bytes / {store.Count:N0} entries; folder reads {warmed - before}, warm reads 0; cache {store.ResidentPages} pages / {store.ResidentEntries} entries.");
        store.Clear();
        }

    private static void LegacyReadAndUpgrade(string work) {
        foreach (bool encrypted in new[] { false, true }) {
            string path = Path.Combine(work, "legacy7-" + encrypted + ".mnx");
            Guid archiveId = Guid.NewGuid();
            Guid root = Guid.NewGuid();
            VFile file = NewFile(Guid.NewGuid(), root, "wire7.txt", 2, 3);
            byte[] salt = encrypted ? RandomNumberGenerator.GetBytes(16) : new byte[16];
            using ArchiveCodec codec = new(archiveId, ArchiveCompression.None, 0, encrypted ? ArchiveCodec.DeriveKey("legacy password", salt, ArchiveCodec.KdfIterations) : null);
            byte[] plainIndex = ArchiveIndex.Serialize(new[] { file }, new FreeChunkSet());
            (byte[] stored, byte[] nonce, byte[] tag) = codec.EncryptIndex(plainIndex);
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite)) {
                ChunkHeader slot = new() {
                    Type = NodeType.File, Id = file.FileID, Parent = root, Name = encrypted ? "" : file.Name, Size = 3,
                    CreatedUtc = DateTime.UnixEpoch, ModifiedUtc = DateTime.UnixEpoch, AccessedUtc = DateTime.UnixEpoch
                    };
                ChunkFormat.WriteSlot(stream, 2, ref slot, "old"u8, codec, chunkSize: 4096);
                ArchiveFormat.WriteIndex(stream, 1, stored, 4096);
                ArchiveFormat.WriteHeader(stream, archiveId, root, 2, 1, 1, stored, 7, encrypted ? NodeFlags.Encrypted : NodeFlags.None, salt, encrypted ? ArchiveCodec.KdfIterations : 0, nonce, tag, chunkSize: 4096);
                }
            using (Archive archive = new Archive().Open(path, encrypted ? "legacy password" : null)) {
                Require(archive.State.Entries.DirtyEntries == 0 && archive.State.Entries.ResidentEntries == 0, "Legacy conversion retained a full dictionary in steady state.");
                Require(archive.FormatVersion == 7 && archive.SemanticFormatVersion == "0.1.7", "A wire7 archive did not retain its semantic format identity.");
                Require(archive.ReadFileRange(file.FileID, 0, 3).AsSpan().SequenceEqual("old"u8), "A real wire7 entry could not be read.");
                using MemoryStream extra = new("new"u8.ToArray());
                archive.AddStreamNow(extra, "new.txt", 3);
                Require(archive.FormatVersion == 8 && archive.VerifyArchive() == Archive.ReturnCode.Success, "Legacy write migration did not produce a valid paged wire8 archive.");
                }
            using Archive reopened = new Archive().Open(path, encrypted ? "legacy password" : null);
            Require(reopened.DirectoryList().Count == 2 && reopened.GetFile(file.FileID) is not null, "Upgraded legacy entries did not survive reopen.");
            }
        }

    private static void RealPagedReopen(string work, bool encrypted) {
        string path = Path.Combine(work, "paged-" + encrypted + ".mnx");
        Guid folder;
        Guid selected;
        using (Archive archive = new Archive()) {
            archive.Options.ChunkSize = 4096;
            archive.Create(new FileInfo(path), encrypted ? "paged password" : null);
            archive.AddDirectoryNow(Directory.CreateDirectory(Path.Combine(work, "paged-folder-" + encrypted)));
            folder = archive.DirectoryList().Single().FileID;
            bool batch = archive.BeginWriteBatch();
            for (int index = 0; index < 900; index++) {
                using MemoryStream source = new(new byte[] { (byte)index });
                archive.AddStreamNow(source, "file-" + index.ToString("D4"), 1, folder);
                }
            archive.FinishWriteBatch(batch);
            selected = archive.DirectoryList(folder)[450].FileID;
            Require(archive.VerifyArchive() == Archive.ReturnCode.Success, "Streamed index rebuild broke file chains.");
            }
        using (Archive archive = new Archive().Open(path, encrypted ? "paged password" : null)) {
            Require(archive.State.Entries.ResidentEntries == 0, "Reopen materialized all VFiles.");
            Require(archive.HasSubdirectories() && !archive.HasSubdirectories(folder), "Directory descriptor flags were not queried directly.");
            Require(archive.GetArchiveInfo().FileCount == 900 && archive.GetArchiveInfo().DirectoryCount == 1, "Archive statistics scanned or miscounted entries.");
            Require(archive.EnumerateFiles(896, 5).Count() == 5, "Bounded public enumeration did not return the requested page.");
            Require(archive.DirectoryList(folder).Count == 900 && archive.State.Entries.ResidentEntries <= 512, "A folder listing pinned the complete archive in the engine.");
            Require(archive.GetFile(selected)?.OwnerID == folder && archive.ReadFileRange(selected, 0, 1)[0] == unchecked((byte)450), "Evicted metadata could not be reloaded for reading.");
            archive.DeleteFileFromArchive(selected);
            archive.CompactArchive();
            Require(archive.GetFile(selected) is null && archive.GetArchiveInfo().FileCount == 899 && archive.VerifyArchive() == Archive.ReturnCode.Success, "Paged mutation/compaction failed.");
            }
        using Archive final = new Archive().Open(path, encrypted ? "paged password" : null);
        Require(final.GetArchiveInfo().FileCount == 899 && final.DirectoryList(folder).Count == 899, "Dirty overlay changes did not persist.");
        Require(final.VerifyArchive() == Archive.ReturnCode.Success, "Fresh verification failed before the corruption check.");
        using (FileStream damaged = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) {
            damaged.Position = final.ChunkSize + PagedIndexStream.EnvelopeBytes + 100;
            int original = damaged.ReadByte();
            damaged.Position--;
            damaged.WriteByte((byte)(original ^ 1));
            }
        Require(final.VerifyArchive() != Archive.ReturnCode.Success, "Warm page caches concealed physical index corruption.");
        }

    private static void MalformedRecordBounds() {
        VFile file = NewFile(Guid.NewGuid(), Guid.NewGuid(), "f", 2, 1);
        using MemoryStream bytes = new();
        using (BinaryWriter writer = new(bytes, Encoding.UTF8, true)) {
            IndexRecord.Write(writer, file);
            }
        bytes.Position = 0;
        using BinaryReader reader = new(bytes, Encoding.UTF8, true);
        Reject(() => IndexRecord.Read(reader, 89, 1, 3, 4096, false, Guid.NewGuid()), "A short record read into its neighbor.");
        Require(bytes.Position <= 89, "Record validation crossed the declared boundary before rejecting it.");
        file.SetExtents(new[] { new ChunkExtent(0, 2, 1), new ChunkExtent(1, 2, 1), new ChunkExtent(2, 2, 1) });
        file.SetChunkLengthGroups(new[] { new ChunkLengthGroup(0, 3, 1, 0) });
        bytes.SetLength(0);
        bytes.Position = 0;
        using (BinaryWriter writer = new(bytes, Encoding.UTF8, true)) {
            IndexRecord.Write(writer, file);
            }
        bytes.Position = 0;
        Reject(() => IndexRecord.Read(reader, (ulong)bytes.Length, 1, 3, 4096, false, Guid.NewGuid()), "An impossible extent count was accepted.");
        Require(bytes.Position <= 78, "Impossible extent counts traversed metadata before rejection.");
        }

    private static void EncryptedScratch(string work) {
        string path = Path.Combine(work, "encrypted-index-scratch.bin");
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.DeleteOnClose };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
        FileStream physical = new(path, options);
        using EncryptedIndexScratchStream scratch = new(physical);
        byte[] phrase = Encoding.UTF8.GetBytes("private archived filename and metadata");
        scratch.Position = 65530;
        scratch.Write(phrase);
        scratch.Position = 170000;
        scratch.Write(phrase);
        scratch.Flush();
        physical.Position = 0;
        byte[] stored = new byte[physical.Length];
        physical.ReadExactly(stored);
        Require(stored.AsSpan().IndexOf(phrase) < 0, "Temporary index storage exposed plaintext metadata.");
        scratch.Position = 65530;
        byte[] restored = new byte[phrase.Length];
        scratch.ReadExactly(restored);
        Require(restored.AsSpan().SequenceEqual(phrase), "Encrypted scratch seek/cross-page reads did not round-trip.");
        scratch.Position = 170000;
        scratch.ReadExactly(restored);
        Require(restored.AsSpan().SequenceEqual(phrase), "Encrypted scratch reload failed.");
        if (!OperatingSystem.IsWindows()) {
            Require((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0, "Scratch permissions expose metadata to other users.");
            }
        }

    private static void Reject(Action action, string message) {
        try {
            action();
            }
        catch (InvalidDataException) {
            return;
            }
        throw new InvalidOperationException(message);
        }

    private static VFile NewFile(Guid id, Guid owner, string name, uint first, ulong size) {
        VFile file = new(id, owner, name, false, size, DateTime.UnixEpoch, DateTime.UnixEpoch, DateTime.UnixEpoch, 0, first);
        file.AppendChunk(first);
        file.AppendLogicalLength((uint)size);
        return file;
        }
    private static void Require(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message);
            }
        }

    private sealed class GeneratedIndex {
        internal const int ChunkSize = 32768;
        private const int RecordBytes = 98;
        internal uint Count { get; }
        internal Guid ArchiveId { get; } = Guid.NewGuid();
        internal Guid Root { get; } = Guid.NewGuid();
        internal ulong DirectoryOffset => 128 + (ulong)Count * IndexDescriptor.Bytes;
        internal ulong RecordsOffset => DirectoryOffset + (ulong)Count * IndexDescriptor.Bytes;
        internal ulong LogicalLength => RecordsOffset + (ulong)Count * RecordBytes;
        internal uint Pages => (uint)((LogicalLength + ChunkSize - 65) / (ChunkSize - 64));
        internal GeneratedIndex(uint count) => Count = count;
        internal Guid Id(uint ordinal) => new((int)ordinal + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
        internal Guid Owner(uint ordinal) => new((int)ordinal + 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2);
        internal ArchiveFormat.HeaderInfo Header() => new(ArchiveId, Root, Pages + Count, Count, Pages, LogicalLength, SHA256.HashData(Page(0)), 8, NodeFlags.None, ChunkSize, ArchiveCompression.None, 0, new byte[16], 0, new byte[12], new byte[16]);
        internal byte[] Page(ulong number) {
            byte[] physical = new byte[ChunkSize];
            ulong logicalStart = number * (ChunkSize - 64);
            Span<byte> target = physical.AsSpan(64);
            int copied = 0;
            while (copied < target.Length && logicalStart + (uint)copied < LogicalLength) {
                ulong offset = logicalStart + (uint)copied;
                byte[] item;
                int within;
                if (offset < 128) {
                    item = new byte[128];
                    using BinaryWriter writer = new(new MemoryStream(item));
                    writer.Write("MNXIDX8\0"u8);
                    writer.Write(Count);
                    writer.Write(0u);
                    writer.Write(0u);
                    writer.Write(0u);
                    writer.Write(128UL);
                    writer.Write(DirectoryOffset);
                    writer.Write(RecordsOffset);
                    writer.Write(LogicalLength);
                    writer.Write(LogicalLength);
                    within = (int)offset;
                    }
                else if (offset < RecordsOffset) {
                    ulong tableStart = offset < DirectoryOffset ? 128UL : DirectoryOffset;
                    uint ordinal = (uint)((offset - tableStart) / IndexDescriptor.Bytes);
                    within = (int)((offset - tableStart) % IndexDescriptor.Bytes);
                    item = new byte[IndexDescriptor.Bytes];
                    using BinaryWriter writer = new(new MemoryStream(item));
                    new IndexDescriptor(Id(ordinal), Owner(ordinal / 100), RecordsOffset + (ulong)ordinal * RecordBytes, RecordBytes, false).Write(writer);
                    }
                else {
                    uint ordinal = (uint)((offset - RecordsOffset) / RecordBytes);
                    within = (int)((offset - RecordsOffset) % RecordBytes);
                    using MemoryStream record = new();
                    using BinaryWriter writer = new(record, Encoding.UTF8, true);
                    IndexRecord.Write(writer, NewFile(Id(ordinal), Owner(ordinal / 100), "f", Pages + ordinal + 1, 1));
                    item = record.ToArray();
                    Require(item.Length == RecordBytes, "Generated record width changed.");
                    }
                int take = Math.Min(item.Length - within, target.Length - copied);
                item.AsSpan(within, take).CopyTo(target.Slice(copied, take));
                copied += take;
                }
            SHA256.HashData(physical.AsSpan(32), physical.AsSpan(0, 32));
            return physical;
            }
        }

    private sealed class GeneratedPhysicalStream(GeneratedIndex generated) : Stream {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => ((long)generated.Pages + generated.Count + 1) * GeneratedIndex.ChunkSize;
        public override long Position { get => _position; set => _position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination) {
            int total = 0;
            while (total < destination.Length) {
                long physicalChunk = _position / GeneratedIndex.ChunkSize;
                int within = (int)(_position % GeneratedIndex.ChunkSize);
                byte[] page = generated.Page((ulong)(physicalChunk - 1));
                int take = Math.Min(destination.Length - total, page.Length - within);
                page.AsSpan(within, take).CopyTo(destination.Slice(total, take));
                _position += take;
                total += take;
                }
            return total;
            }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        }
    }
