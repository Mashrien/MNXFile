using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive.Resume;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

string work = Path.Combine(Path.GetTempPath(), "mnx-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try {
    BatchIndexSmoke.Run(work);
    if (args.Contains("--batch-only")) {
        return;
        }

    LinkSmoke.Run(work);
    if (args.Contains("--links-only")) {
        return;
        }

    StorageLayoutSmoke.Run();
    StreamWindowSmoke.Run(work);
    FilenameSmoke.Run(work);
    ExtractionCancellationSmoke.Run(work);
    CancellationSmoke.Run(work);
    ResumeSmoke.Run(work);
    ResumeKeySmoke.Run(work);
    if (args.Contains("--names-only")) {
        return;
        }

    string sourceDir = Path.Combine(work, "source");
    Directory.CreateDirectory(sourceDir);
    Directory.CreateDirectory(Path.Combine(sourceDir, "empty-dir"));
    byte[] big = Enumerable.Range(0, 25000).Select(i => (byte)(i * 37)).ToArray();
    byte[] other = Enumerable.Range(0, 9000).Select(i => (byte)(i * 19)).ToArray();
    string bigPath = Path.Combine(sourceDir, "big.bin");
    string otherPath = Path.Combine(sourceDir, "other.bin");
    DiskFile.WriteAllBytes(bigPath, big);
    DiskFile.WriteAllBytes(otherPath, other);
    DiskFile.WriteAllBytes(Path.Combine(sourceDir, "zero.bin"), Array.Empty<byte>());
    string archivePath = Path.Combine(work, "test.mnx");
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.ChunkSize = 4096;
        Check(
            archive.Queue_AddDirectory(new DirectoryInfo(sourceDir)) == ArchiveFile.ReturnCode.Success,
            "queue directory"
            );
        Check(archive.SaveArchive(new FileInfo(archivePath)) == ArchiveFile.ReturnCode.Success, "save archive");
        Check(archive.FileQueue.Count == 0, "queue drained");
        Check(archive.Files.Count == 5, "entries saved");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(archivePath)) {
        VirtualFile rootFolder = archive.DirectoryList().Single();
        Check(rootFolder.IsDirectory, "root folder");
        Check(archive.DirectoryList(rootFolder.FileID).Count == 4, "directory listing");
        VirtualFile bigFile = archive.DirectoryList(rootFolder.FileID).Single(f => f.Name == "big.bin");
        VirtualFile otherFile = archive.DirectoryList(rootFolder.FileID).Single(f => f.Name == "other.bin");
        uint[] otherChunksBefore = otherFile.OrderedChunks.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
        Check(archive.GetArchiveInfo().FileCount == 3, "archive statistics");
        Check(
            archive.GetFileChunk(bigFile.FileID, 0).SequenceEqual(big.AsSpan(0, 3584).ToArray()),
            "direct chunk access"
            );
        Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, "whole archive verification");
        Check(
            archive.ReadFileRange(bigFile.FileID, 3500, 6000).SequenceEqual(big.AsSpan(3500, 6000).ToArray()),
            "cross-chunk read"
            );
        using (Stream? stream = archive.OpenRead(bigFile.FileID)) {
            stream.Seek(15000, SeekOrigin.Begin);
            byte[] part = new byte[5000];
            Check(stream.Read(part, 0, part.Length) == part.Length, "seek read length");
            Check(part.SequenceEqual(big.AsSpan(15000, 5000).ToArray()), "seek read data");
            }

        string extracted = Path.Combine(work, "extracted");
        Check(
            archive.ExtractDirectory(rootFolder.FileID, new DirectoryInfo(extracted)) == ArchiveFile.ReturnCode.Success,
            "extract directory"
            );
        Check(DiskFile.ReadAllBytes(Path.Combine(extracted, "big.bin")).SequenceEqual(big), "extracted big file");
        Check(
            DiskFile.ReadAllBytes(Path.Combine(extracted, "other.bin")).SequenceEqual(other),
            "extracted other file"
            );
        Check(Directory.Exists(Path.Combine(extracted, "empty-dir")), "extracted empty directory");
        Check(new FileInfo(Path.Combine(extracted, "zero.bin")).Length == 0, "extracted empty file");
        long before = new FileInfo(archivePath).Length;
        Check(archive.DeleteFileFromArchive(bigFile.FileID) == ArchiveFile.ReturnCode.Success, "delete middle file");
        Check(archive.CompactArchive() == ArchiveFile.ReturnCode.Success, "compact archive");
        long after = new FileInfo(archivePath).Length;
        Check(after < before, "compaction truncates file");
        Check(
            !otherChunksBefore.SequenceEqual(otherFile.OrderedChunks.OrderBy(p => p.Key).Select(p => p.Value)),
            "compaction relocated tail chunks"
            );
        Check(otherFile.ChunkExtents.Count == 1, "best-fit compaction preserves contiguous file");
        Check(
            archive.ReadFileRange(otherFile.FileID, 0, other.Length).SequenceEqual(other),
            "moved data after compact"
            );
        Check(
            archive.AddFileNow(new FileInfo(bigPath), rootFolder.FileID) == ArchiveFile.ReturnCode.Success,
            "add after compact"
            );
        Check(archive.DirectoryList(rootFolder.FileID).Count == 4, "readded file");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(archivePath)) {
        VirtualFile folder = archive.DirectoryList().Single();
        VirtualFile file = archive.DirectoryList(folder.FileID).Single(f => f.Name == "big.bin");
        Check(archive.ReadFileRange(file.FileID, 0, big.Length).SequenceEqual(big), "reopen after compact");
        Check(
            archive.DirectoryList(folder.FileID).Single(f => f.Name == "other.bin").SizeOriginal == (ulong)other.Length,
            "moved file persisted"
            );
        }

    // Corrupt a data byte; the affected read must reject it.
    uint corruptedChunk;
    Guid corruptedFile;
    using (ArchiveFile archive = new ArchiveFile().Open(archivePath)) {
        VirtualFile folder = archive.DirectoryList().Single();
        VirtualFile file = archive.DirectoryList(folder.FileID).Single(f => f.Name == "big.bin");
        corruptedChunk = file.OrderedChunks[0];
        corruptedFile = file.FileID;
        }

    byte[] bytes = DiskFile.ReadAllBytes(archivePath);
    bytes[corruptedChunk * 4096 + 512] ^= 0xFF;
    DiskFile.WriteAllBytes(archivePath, bytes);
    using (ArchiveFile archive = new ArchiveFile().Open(archivePath)) {
        bool rejected = false;
        try {
            archive.ReadFileRange(corruptedFile, 0, 1);
            }
        catch (InvalidDataException) {
            rejected = true;
            }

        Check(rejected, "corrupted chunk rejected");
        Check(
            archive.VerifyArchive() == (ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.HashMismatch),
            "verification detects corruption"
            );
        }

    // Force the front index to grow over its initial four chunks while a data chunk
    // occupies the new index area. Its content and identifier must survive relocation.
    string growingPath = Path.Combine(work, "growing.mnx");
    byte[] seed = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
    string seedPath = Path.Combine(work, "seed.bin");
    DiskFile.WriteAllBytes(seedPath, seed);
    Guid seedId;
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.ChunkSize = 4096;
        archive.Create(new FileInfo(growingPath));
        Check(archive.AddFileNow(new FileInfo(seedPath)) == ArchiveFile.ReturnCode.Success, "seed add");
        seedId = archive.DirectoryList().Single().FileID;
        for (int i = 0; i < 250; i++) {
            string path = Path.Combine(work, $"entry-{i:D4}.bin");
            DiskFile.WriteAllBytes(path, Array.Empty<byte>());
            Check(archive.Queue_AddFile(new FileInfo(path)) == ArchiveFile.ReturnCode.Success, "queue index entry");
            }

        Check(archive.SaveArchive() == ArchiveFile.ReturnCode.Success, "grow front index");
        Check(archive.IndexChunkCount > 4, "front index expanded");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(growingPath)) {
        Check(archive.DirectoryList().Count == 251, "expanded index lists all files");
        Check(
            archive.ReadFileRange(seedId, 0, seed.Length).SequenceEqual(seed),
            "data relocated for index growth"
            );
        Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, "expanded index verifies");
        }

    string largeSource = Path.Combine(work, "large.bin");
    string largeArchive = Path.Combine(work, "large.mnx");
    byte[] large = new byte[20 * 1024 * 1024];
    for (int i = 0; i < large.Length; i++) {
        large[i] = (byte)(i * 13);
        }

    DiskFile.WriteAllBytes(largeSource, large);
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.ChunkSize = 4096;
        archive.Create(new FileInfo(largeArchive));
        Check(archive.AddFileNow(new FileInfo(largeSource)) == ArchiveFile.ReturnCode.Success, "large file add");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(largeArchive)) {
        VirtualFile file = archive.DirectoryList().Single();
        Check(file.ChunkExtents.Count == 1, "large contiguous file has one index extent");
        Check(archive.IndexChunkCount == 4, "large file index remains four chunks");
        Check(
            archive.ReadFileRange(file.FileID, 18 * 1024 * 1024 + 321, 65536).SequenceEqual(large.AsSpan(18 * 1024 * 1024 + 321, 65536).ToArray()),
            "large far-offset read"
            );
        using (MemoryStream window = new MemoryStream()) {
            Check(
                archive.CopyFileRange(file.FileID, window, 17 * 1024 * 1024 + 73, 100_000, 4096) == 100_000,
                "windowed copy length"
                );
            Check(
                window.ToArray().SequenceEqual(large.AsSpan(17 * 1024 * 1024 + 73, 100_000).ToArray()),
                "windowed copy data"
                );
            }

        Check(archive.DeleteFileFromArchive(file.FileID) == ArchiveFile.ReturnCode.Success, "large delete");
        Check(archive.GetArchiveInfo().FreeRangeCount == 1, "large contiguous deletion uses one free range");
        Check(archive.GetArchiveInfo().FreeChunkCount > 5000, "large deletion tracks all free chunks");
        Check(archive.CompactArchive() == ArchiveFile.ReturnCode.Success, "large free tail truncated");
        Check(new FileInfo(largeArchive).Length == 5 * 4096, "large archive reduced to header and front index");
        }

    string privateSource = Path.Combine(work, "private-sample.bin");
    byte[] compressible = Enumerable.Repeat((byte)0x5A, 30_000).ToArray();
    DiskFile.WriteAllBytes(privateSource, compressible);
    string compressedPath = Path.Combine(work, "compressed.mnx");
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.ChunkSize = 4096;
        archive.Options.Compression = ArchiveCompression.Brotli;
        archive.Options.CompressionLayout = ArchiveCompressionLayout.IndependentChunks;
        archive.Create(new FileInfo(compressedPath));
        Check(archive.AddFileNow(new FileInfo(privateSource)) == ArchiveFile.ReturnCode.Success, "compressed add");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(compressedPath)) {
        VirtualFile file = archive.DirectoryList().Single();
        Check(
            archive.FormatVersion == 7 && archive.Compression == ArchiveCompression.Brotli,
            "compression format settings persisted"
            );
        Check(
            archive.ReadFileRange(file.FileID, 0, compressible.Length).SequenceEqual(compressible),
            "compressed read"
            );
        Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, "compressed verification");
        }

    Check(
        new FileInfo(compressedPath).Length < compressible.Length,
        "compression reduces repetitive file on disk"
        );
    byte[] mixed = new byte[180_000];
    new Random(412).NextBytes(mixed);
    Array.Clear(mixed, 70_000, 60_000);
    string mixedPath = Path.Combine(work, "mixed.bin");
    DiskFile.WriteAllBytes(mixedPath, mixed);
    using (ArchiveFile archive = new ArchiveFile().Open(compressedPath)) {
        Check(archive.AddFileNow(new FileInfo(mixedPath)) == ArchiveFile.ReturnCode.Success, "mixed compressed add");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(compressedPath)) {
        VirtualFile file = archive.DirectoryList().Single(f => f.Name == "mixed.bin");
        Check(
            file.ChunkLengthGroups.Count > 1 && file.ChunkExtents.Count == 1,
            "mixed file has compact logical and physical runs"
            );
        foreach (int offset in new[] {
            0,
            3500,
            69_000,
            74_000,
            129_000,
            175_000
            }

            ) {
            int length = Math.Min(5000, mixed.Length - offset);
            Check(
                archive.ReadFileRange(file.FileID, offset, length).SequenceEqual(mixed.AsSpan(offset, length).ToArray()),
                "mixed compressed range"
                );
            }

        Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, "mixed compressed verification");
        }

    const string passphrase = "correct horse battery staple for tests";
    string securePath = Path.Combine(work, "secure.mnx");
    byte[] tailData = new byte[9_000];
    new Random(19).NextBytes(tailData);
    string tailPath = Path.Combine(work, "tail.bin");
    DiskFile.WriteAllBytes(tailPath, tailData);
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.ChunkSize = 4096;
        archive.Options.Compression = ArchiveCompression.Brotli;
        archive.Options.CompressionLayout = ArchiveCompressionLayout.IndependentChunks;
        archive.Create(new FileInfo(securePath), passphrase);
        Check(
            archive.AddFileNow(new FileInfo(privateSource)) == ArchiveFile.ReturnCode.Success,
            "encrypted compressed add"
            );
        Check(archive.AddFileNow(new FileInfo(tailPath)) == ArchiveFile.ReturnCode.Success, "encrypted tail add");
        Check(archive.IsEncrypted, "encrypted flag");
        }

    Check(
        !System.Text.Encoding.UTF8.GetString(DiskFile.ReadAllBytes(securePath)).Contains("private-sample.bin"),
        "encrypted archive hides filename"
        );
    bool rejectedMissingPassword = false;
    try {
        using ArchiveFile opened = new ArchiveFile().Open(securePath);
        }
    catch (System.Security.Cryptography.CryptographicException) {
        rejectedMissingPassword = true;
        }

    Check(rejectedMissingPassword, "missing passphrase rejected");
    bool rejectedWrongPassword = false;
    try {
        using ArchiveFile opened = new ArchiveFile().Open(securePath, "wrong password");
        }
    catch (System.Security.Cryptography.CryptographicException) {
        rejectedWrongPassword = true;
        }

    Check(rejectedWrongPassword, "wrong passphrase rejected");
    using (ArchiveFile archive = new ArchiveFile().Open(securePath, passphrase)) {
        VirtualFile file = archive.DirectoryList().Single(f => f.Name == "private-sample.bin");
        VirtualFile tail = archive.DirectoryList().Single(f => f.Name == "tail.bin");
        Check(
            archive.ReadFileRange(file.FileID, 2000, 8000).SequenceEqual(compressible.AsSpan(2000, 8000).ToArray()),
            "encrypted compressed range"
            );
        Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, "encrypted verification");
        Check(archive.DeleteFileFromArchive(file.FileID) == ArchiveFile.ReturnCode.Success, "encrypted delete");
        long beforeCompact = new FileInfo(securePath).Length;
        Check(archive.CompactArchive() == ArchiveFile.ReturnCode.Success, "encrypted compact");
        Check(new FileInfo(securePath).Length < beforeCompact, "encrypted compaction truncates");
        Check(
            archive.ReadFileRange(tail.FileID, 0, tailData.Length).SequenceEqual(tailData),
            "encrypted moved tail reads"
            );
        Check(
            archive.AddFileNow(new FileInfo(privateSource)) == ArchiveFile.ReturnCode.Success,
            "encrypted add after compact"
            );
        }

    using (ArchiveFile archive = new ArchiveFile().Open(securePath, passphrase)) {
        VirtualFile file = archive.DirectoryList().Single(f => f.Name == "private-sample.bin");
        Check(archive.GetFileChunk(file.FileID, 0).SequenceEqual(compressible), "encrypted reopen");
        VirtualFile tail = archive.DirectoryList().Single(f => f.Name == "tail.bin");
        Check(
            archive.ReadFileRange(tail.FileID, 100, 5000).SequenceEqual(tailData.AsSpan(100, 5000).ToArray()),
            "encrypted relocated reopen"
            );
        Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, "encrypted relocated verification");
        }

    string encryptedIndexPath = Path.Combine(work, "encrypted-index.mnx");
    Guid encryptedSeedId;
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.ChunkSize = 4096;
        archive.Create(new FileInfo(encryptedIndexPath), passphrase);
        Check(archive.AddFileNow(new FileInfo(tailPath)) == ArchiveFile.ReturnCode.Success, "encryption-only seed add");
        encryptedSeedId = archive.DirectoryList().Single().FileID;
        for (int i = 0; i < 150; i++) {
            string path = Path.Combine(work, $"encrypted-index-entry-{i:D4}-xxxxxxxxxx.bin");
            DiskFile.WriteAllBytes(path, Array.Empty<byte>());
            Check(
                archive.Queue_AddFile(new FileInfo(path)) == ArchiveFile.ReturnCode.Success,
                "encryption-only index queue"
                );
            }

        Check(archive.SaveArchive() == ArchiveFile.ReturnCode.Success, "encryption-only index grow");
        Check(archive.IndexChunkCount > 4, "encrypted front index expanded");
        }

    Check(
        !System.Text.Encoding.UTF8.GetString(DiskFile.ReadAllBytes(encryptedIndexPath)).Contains("encrypted-index-entry-0000"),
        "expanded encrypted index hides names"
        );
    uint encryptedSeedChunk;
    using (ArchiveFile archive = new ArchiveFile().Open(encryptedIndexPath, passphrase)) {
        Check(
            archive.FormatVersion == 7 && archive.IsEncrypted && archive.Compression == ArchiveCompression.None,
            "encryption-only settings"
            );
        Check(archive.DirectoryList().Count == 151, "expanded encrypted index lists entries");
        Check(
            archive.ReadFileRange(encryptedSeedId, 0, tailData.Length).SequenceEqual(tailData),
            "encrypted seed survives index expansion"
            );
        Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, "encrypted expanded verification");
        encryptedSeedChunk = archive.GetFile(encryptedSeedId)!.OrderedChunks[0];
        }

    byte[] damaged = DiskFile.ReadAllBytes(encryptedIndexPath);
    damaged[encryptedSeedChunk * 4096 + 520] ^= 0x40;
    DiskFile.WriteAllBytes(encryptedIndexPath, damaged);
    using (ArchiveFile archive = new ArchiveFile().Open(encryptedIndexPath, passphrase)) {
        Check(
            archive.VerifyArchive() == (ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.HashMismatch),
            "encrypted corruption detected"
            );
        }

    byte[] codecSample = new byte[180_000];
    new Random(531).NextBytes(codecSample);
    Array.Clear(codecSample, 20_000, 100_000);
    string codecSource = Path.Combine(work, "codec-sample.bin");
    DiskFile.WriteAllBytes(codecSource, codecSample);
    foreach (ArchiveCompression method in new[] {
        ArchiveCompression.Brotli,
        ArchiveCompression.Snappy,
        ArchiveCompression.LZ4
        }

        ) {
        string path = Path.Combine(work, $"codec-{method}.mnx");
        int level = method is ArchiveCompression.Snappy or ArchiveCompression.LZ4 ? 0 : 9;
        using (ArchiveFile archive = new ArchiveFile()) {
            archive.Options.Compression = method;
            archive.Options.CompressionLevel = level;
            archive.Create(new FileInfo(path));
            archive.Options.Compression = ArchiveCompression.None;
            archive.Options.CompressionLevel = 1;
            Check(archive.AddFileNow(new FileInfo(codecSource)) == ArchiveFile.ReturnCode.Success, $"{method} add");
            Check(
                archive.Compression == method && archive.CompressionLevel == level,
                $"{method} immutable settings"
                );
            }

        Check(new FileInfo(path).Length < codecSample.Length, $"{method} reduces physical archive size");
        using (ArchiveFile archive = new ArchiveFile().Open(path)) {
            VirtualFile file = archive.DirectoryList().Single();
            Check(
                archive.FormatVersion == 7 && archive.ChunkSize == 32768 && archive.CompressionLayout == ArchiveCompressionLayout.Continuous && archive.Compression == method && archive.CompressionLevel == level,
                $"{method} header settings"
                );
            foreach (int offset in new[] {
                0,
                3700,
                21_000,
                90_000,
                145_000,
                175_000
                }

                ) {
                int length = Math.Min(5000, codecSample.Length - offset);
                Check(
                    archive.ReadFileRange(file.FileID, offset, length).SequenceEqual(codecSample.AsSpan(offset, length).ToArray()),
                    $"{method} range at {offset}"
                    );
                }

            Check(archive.VerifyArchive() == ArchiveFile.ReturnCode.Success, $"{method} whole archive verification");
            using (Stream? stream = archive.OpenRead(file.FileID)) {
                stream.Seek(150_000, SeekOrigin.Begin);
                byte[] tail = new byte[9000];
                stream.ReadExactly(tail);
                Check(
                    tail.SequenceEqual(codecSample.AsSpan(150_000, 9000).ToArray()),
                    $"{method} continuous stream far read"
                    );
                stream.Seek(100, SeekOrigin.Begin);
                byte[] start = new byte[9000];
                stream.ReadExactly(start);
                Check(
                    start.SequenceEqual(codecSample.AsSpan(100, 9000).ToArray()),
                    $"{method} continuous stream rewind"
                    );
                }

            Check(archive.DeleteFileFromArchive(file.FileID) == ArchiveFile.ReturnCode.Success, $"{method} delete");
            Check(archive.CompactArchive() == ArchiveFile.ReturnCode.Success, $"{method} compact");
            }

        if (method == ArchiveCompression.Brotli) {
            string fastPath = Path.Combine(work, $"codec-{method}-fast.mnx");
            using (ArchiveFile archive = new ArchiveFile()) {
                archive.Options.Compression = method;
                archive.Options.CompressionLevel = 1;
                archive.Create(new FileInfo(fastPath));
                Check(archive.AddFileNow(new FileInfo(codecSource)) == ArchiveFile.ReturnCode.Success, $"{method} fast add");
                }

            using ArchiveFile fast = new ArchiveFile().Open(fastPath);
            VirtualFile fastFile = fast.DirectoryList().Single();
            Check(
                fast.CompressionLevel == 1 && fast.ReadFileRange(fastFile.FileID, 18_000, 100_000).SequenceEqual(codecSample.AsSpan(18_000, 100_000).ToArray()),
                $"{method} fast effort round trip"
                );
            }
        }

    string secureSnappy = Path.Combine(work, "secure-snappy.mnx");
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.Compression = ArchiveCompression.Snappy;
        archive.Create(new FileInfo(secureSnappy), passphrase);
        Check(
            archive.AddFileNow(new FileInfo(privateSource)) == ArchiveFile.ReturnCode.Success,
            "secure Snappy first file"
            );
        Check(archive.AddFileNow(new FileInfo(tailPath)) == ArchiveFile.ReturnCode.Success, "secure Snappy tail file");
        VirtualFile first = archive.DirectoryList().Single(f => f.Name == "private-sample.bin");
        VirtualFile tail = archive.DirectoryList().Single(f => f.Name == "tail.bin");
        Check(archive.DeleteFileFromArchive(first.FileID) == ArchiveFile.ReturnCode.Success, "secure Snappy delete");
        Check(archive.CompactArchive() == ArchiveFile.ReturnCode.Success, "secure Snappy compaction");
        Check(
            archive.ReadFileRange(tail.FileID, 0, tailData.Length).SequenceEqual(tailData),
            "secure Snappy moved tail"
            );
        }

    Check(
        !System.Text.Encoding.UTF8.GetString(DiskFile.ReadAllBytes(secureSnappy)).Contains("tail.bin"),
        "secure Snappy hides name"
        );
    bool rejectedWrongSnappyPassword = false;
    try {
        using ArchiveFile opened = new ArchiveFile().Open(secureSnappy, "wrong password");
        }
    catch (System.Security.Cryptography.CryptographicException) {
        rejectedWrongSnappyPassword = true;
        }

    Check(rejectedWrongSnappyPassword, "secure Snappy rejects wrong passphrase");
    using (ArchiveFile archive = new ArchiveFile().Open(secureSnappy, passphrase)) {
        VirtualFile tail = archive.DirectoryList().Single();
        Check(
            archive.IsEncrypted && archive.Compression == ArchiveCompression.Snappy && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success && archive.ReadFileRange(tail.FileID, 1000, 5000).SequenceEqual(tailData.AsSpan(1000, 5000).ToArray()),
            "secure Snappy reopen"
            );
        }

    string secureLz4 = Path.Combine(work, "secure-lz4.mnx");
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.Compression = ArchiveCompression.LZ4;
        archive.Create(new FileInfo(secureLz4), passphrase);
        Check(archive.AddFileNow(new FileInfo(codecSource)) == ArchiveFile.ReturnCode.Success, "secure LZ4 add");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(secureLz4, passphrase)) {
        VirtualFile file = archive.DirectoryList().Single();
        Check(
            archive.FormatVersion == 7 && archive.ChunkSize == 32768 && archive.IsEncrypted && archive.Compression == ArchiveCompression.LZ4 && archive.ReadFileRange(file.FileID, 85_000, 30_000).SequenceEqual(codecSample.AsSpan(85_000, 30_000).ToArray()) && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
            "secure LZ4 reopen and verify"
            );
        }

    foreach ((ArchiveCompression method, int level) in new[] {
        (ArchiveCompression.Brotli, 0),
        (ArchiveCompression.Brotli, 10),
        (ArchiveCompression.Snappy, 9),
        (ArchiveCompression.LZ4, 9)
        }

        ) {
        using ArchiveFile invalid = new ArchiveFile();
        invalid.Options.Compression = method;
        invalid.Options.CompressionLevel = level;
        bool rejected = false;
        try {
            invalid.Create(new FileInfo(Path.Combine(work, $"invalid-{method}.mnx")));
            }
        catch (ArgumentOutOfRangeException) {
            rejected = true;
            }

        Check(rejected, $"{method} invalid effort rejected");
        }

    using (ArchiveFile invalid = new ArchiveFile()) {
        invalid.Options.Compression = ArchiveCompression.LZ4;
        invalid.Options.CompressionLayout = ArchiveCompressionLayout.IndependentChunks;
        bool rejected = false;
        try {
            invalid.Create(new FileInfo(Path.Combine(work, "invalid-lz4-layout.mnx")));
            }
        catch (NotSupportedException) {
            rejected = true;
            }

        Check(rejected, "LZ4 independent chunk layout rejected");
        }

    string streamArchivePath = Path.Combine(work, "stream-source.mnx");
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.Compression = ArchiveCompression.Snappy;
        archive.Create(new FileInfo(streamArchivePath));
        using NonSeekableMemoryStream source = new NonSeekableMemoryStream(codecSample);
        Check(
            archive.AddStreamNow(source, "from-stream.bin", codecSample.Length) == ArchiveFile.ReturnCode.Success,
            "nonseekable stream add"
            );
        Check(source.CanRead, "caller retains source stream ownership");
        }

    using (ArchiveFile archive = new ArchiveFile().Open(streamArchivePath)) {
        VirtualFile file = archive.DirectoryList().Single();
        Check(
            archive.ReadFileRange(file.FileID, 100_000, 50_000).SequenceEqual(codecSample.AsSpan(100_000, 50_000).ToArray()),
            "nonseekable source round trip"
            );
        }

    using (ArchiveFile archive = new ArchiveFile().Open(streamArchivePath)) {
        using MemoryStream empty = new MemoryStream();
        Check(
            archive.AddStreamNow(empty, "empty-stream.bin", 0) == ArchiveFile.ReturnCode.Success,
            "continuous empty stream add"
            );
        VirtualFile file = archive.DirectoryList().Single(f => f.Name == "empty-stream.bin");
        Check(
            file.SizeOriginal == 0 && file.ChunkCount == 1 && archive.GetFileChunk(file.FileID, 0).Length == 0 && archive.ReadFileRange(file.FileID, 0, 10).Length == 0,
            "continuous empty file stores a zero-length chunk"
            );
        using NonSeekableMemoryStream shortSource = new NonSeekableMemoryStream(new byte[5]);
        bool rejectedShortSource = false;
        try {
            archive.AddStreamNow(shortSource, "short-source.bin", 6);
            }
        catch (EndOfStreamException) {
            rejectedShortSource = true;
            }

        Check(
            rejectedShortSource && archive.DirectoryList().Count == 2 && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
            "continuous source length mismatch rolls back"
            );
        }

    using (ArchiveFile archive = new ArchiveFile().Open(streamArchivePath)) {
        Check(
            archive.DirectoryList().Count == 2 && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
            "continuous empty file survives reopening"
            );
        }

    // Version 5 must relocate a data chunk when its one-chunk front index grows.
    string widePath = Path.Combine(work, "wide.mnx");
    byte[] wideBytes = new byte[180_000];
    new Random(241).NextBytes(wideBytes);
    Guid wideId;
    using (ArchiveFile archive = new ArchiveFile().Create(new FileInfo(widePath))) {
        Check(
            archive.FormatVersion == 7 && archive.ChunkSize == 32768 && archive.IndexChunkCount == 1,
            "32 KiB format defaults"
            );
        using MemoryStream input = new MemoryStream(wideBytes);
        Check(
            archive.AddStreamNow(input, "wide.bin", wideBytes.Length) == ArchiveFile.ReturnCode.Success,
            "32 KiB seed add"
            );
        wideId = archive.DirectoryList().Single().FileID;
        for (int i = 0; i < 350; i++) {
            string entryPath = Path.Combine(work, $"wide-entry-{i:D4}-xxxxxxxxxxxx.bin");
            DiskFile.WriteAllBytes(entryPath, Array.Empty<byte>());
            Check(archive.Queue_AddFile(new FileInfo(entryPath)) == ArchiveFile.ReturnCode.Success, "32 KiB index queue");
            }

        Check(
            archive.SaveArchive() == ArchiveFile.ReturnCode.Success && archive.IndexChunkCount > 1,
            "32 KiB front index expanded"
            );
        Check(
            archive.ReadFileRange(wideId, 91_000, 70_000).SequenceEqual(wideBytes.AsSpan(91_000, 70_000).ToArray()),
            "32 KiB relocated seed reads"
            );
        VirtualFile emptyTail = archive.DirectoryList().Single(f => f.Name.StartsWith("wide-entry-0349"));
        Check(archive.DeleteFileFromArchive(wideId) == ArchiveFile.ReturnCode.Success, "32 KiB delete seed");
        long beforeWideCompact = new FileInfo(widePath).Length;
        Check(
            archive.CompactArchive() == ArchiveFile.ReturnCode.Success && new FileInfo(widePath).Length < beforeWideCompact,
            "32 KiB compaction truncates"
            );
        Check(
            archive.GetFileChunk(emptyTail.FileID, 0).Length == 0 && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
            "32 KiB compacted index and data verify"
            );
        }

    using (ArchiveFile archive = new ArchiveFile().Open(widePath)) {
        Check(
            archive.FormatVersion == 7 && archive.ChunkSize == 32768 && archive.IndexChunkCount > 1 && archive.DirectoryList().Count == 350 && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
            "32 KiB archive reopens after index growth and compaction"
            );
        }

    string wideIndependentPath = Path.Combine(work, "wide-independent.mnx");
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Options.Compression = ArchiveCompression.Brotli;
        archive.Options.CompressionLevel = 1;
        archive.Options.CompressionLayout = ArchiveCompressionLayout.IndependentChunks;
        archive.Create(new FileInfo(wideIndependentPath));
        Check(
            archive.AddFileNow(new FileInfo(codecSource)) == ArchiveFile.ReturnCode.Success,
            "32 KiB independent compression add"
            );
        }

    using (ArchiveFile archive = new ArchiveFile().Open(wideIndependentPath)) {
        VirtualFile file = archive.DirectoryList().Single();
        Check(
            archive.FormatVersion == 7 && archive.ChunkSize == 32768 && archive.CompressionLayout == ArchiveCompressionLayout.IndependentChunks && archive.ReadFileRange(file.FileID, 132_000, 40_000).SequenceEqual(codecSample.AsSpan(132_000, 40_000).ToArray()) && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
            "32 KiB independent range and verification"
            );
        }

    string wideEncryptedPath = Path.Combine(work, "wide-encrypted.mnx");
    using (ArchiveFile archive = new ArchiveFile()) {
        archive.Create(new FileInfo(wideEncryptedPath), passphrase);
        Check(
            archive.AddFileNow(new FileInfo(tailPath)) == ArchiveFile.ReturnCode.Success,
            "32 KiB encryption-only add"
            );
        }

    Check(
        !System.Text.Encoding.UTF8.GetString(DiskFile.ReadAllBytes(wideEncryptedPath)).Contains("tail.bin"),
        "32 KiB encryption hides index names"
        );
    using (ArchiveFile archive = new ArchiveFile().Open(wideEncryptedPath, passphrase)) {
        VirtualFile file = archive.DirectoryList().Single();
        Check(
            archive.FormatVersion == 7 && archive.ChunkSize == 32768 && archive.IsEncrypted && archive.Compression == ArchiveCompression.None && archive.ReadFileRange(file.FileID, 0, tailData.Length).SequenceEqual(tailData) && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
            "32 KiB encryption-only reopen and verification"
            );
        }

    Console.WriteLine("PASS: archive operations, codecs, encryption, compaction, stream input, current format");
    }
finally {
    Directory.Delete(work, true);
    }

static void Check(bool condition, string name) {
    if (!condition) {
        throw new Exception("FAILED: " + name);
        }
    }

sealed class NonSeekableMemoryStream(byte[] data) : MemoryStream(data) {
    public override bool CanSeek => false;

    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
