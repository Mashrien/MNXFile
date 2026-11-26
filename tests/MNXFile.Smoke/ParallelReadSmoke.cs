using MNX;

internal static class ParallelReadSmoke {
    internal static async Task Run(string directory) {
        byte[] source = new byte[2 * 1024 * 1024 + 137];
        new Random(3741).NextBytes(source);
        foreach (int chunkSize in new[] { 4096, 32768 }) {
            foreach (ArchiveCompression compression in Enum.GetValues<ArchiveCompression>()) {
                foreach (ArchiveCompressionLayout layout in Enum.GetValues<ArchiveCompressionLayout>()) {
                    if (compression == ArchiveCompression.LZ4 && layout == ArchiveCompressionLayout.IndependentChunks) {
                        continue;
                        }
                    string path = Path.Combine(directory, $"parallel-{chunkSize}-{compression}-{layout}.mnx");
                    using Archive archive = new Archive();
                    archive.Options.ChunkSize = chunkSize;
                    archive.Options.Compression = compression;
                    archive.Options.CompressionLayout = layout;
                    archive.Options.CompressionLevel = compression == ArchiveCompression.Brotli ? 1 : 0;
                    archive.Create(new FileInfo(path), layout == ArchiveCompressionLayout.Continuous ? "read-smoke" : null);
                    archive.AddStreamNow(new MemoryStream(source), "payload", source.Length);
                    VFile file = archive.Files.Single();
                    for (int workers = 1; workers <= 4; workers++) {
                        archive.Options.ReadWorkers = workers;
                        const int offset = 32749;
                        byte[] range = await archive.ReadFileRangeAsync(file.FileID, offset, source.Length - offset);
                        if (!range.AsSpan().SequenceEqual(source.AsSpan(offset))) {
                            throw new InvalidDataException($"Worker count {workers} returned incorrect bytes.");
                            }
                        if (await archive.VerifyArchiveAsync() != Archive.ReturnCode.Success) {
                            throw new InvalidDataException("Parallel verification failed.");
                            }
                        }
                    string output = path + ".extracted";
                    await archive.ExtractFileAsync(file.FileID, new FileInfo(output));
                    if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(source)) {
                        throw new InvalidDataException("Parallel extraction changed the entry.");
                        }
                    archive.Close();
                    File.Delete(output);
                    File.Delete(path);
                    }
                }
            }
        CheckLimits();
        await CheckFragmentedReads(directory, source);
        Console.WriteLine("PASS: 1–4 readers, both chunk sizes and layouts, all codecs, encrypted reads, fragmented and damaged extents");
        }

    private static void CheckLimits() {
        Configuration options = new Configuration();
        foreach (int invalid in new[] { -1, 0, 5 }) {
            try {
                options.ReadWorkers = invalid;
                }
            catch (ArgumentOutOfRangeException) {
                continue;
                }
            throw new Exception("Invalid read worker count accepted.");
            }
        }

    private static async Task CheckFragmentedReads(string directory, byte[] source) {
        string path = Path.Combine(directory, "parallel-fragmented.mnx");
        using Archive archive = new Archive();
        archive.Create(new FileInfo(path));
        archive.Options.ReadWorkers = 4;
        archive.AddStreamNow(new MemoryStream(source, 0, 140000), "hole", 140000);
        archive.AddStreamNow(new MemoryStream(source, 0, 90000), "separator", 90000);
        archive.DeleteFileFromArchive(archive.Files.Single(node => node.Name == "hole").FileID);
        archive.AddStreamNow(new MemoryStream(source), "fragmented", source.Length);
        VFile entry = archive.Files.Single(node => node.Name == "fragmented");
        if (entry.ChunkExtents.Count < 2 || !(await archive.ReadFileRangeAsync(entry.FileID, 0, source.Length)).AsSpan().SequenceEqual(source)) {
            throw new Exception("Fragmented file did not round-trip.");
            }
        long damagedOffset = (long)entry.PhysicalChunk(3) * archive.ChunkSize + 140;
        using (FileStream output = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) {
            output.Position = damagedOffset;
            int original = output.ReadByte();
            output.Position = damagedOffset;
            output.WriteByte((byte)(original ^ 1));
            }
        if (await archive.VerifyArchiveAsync() == Archive.ReturnCode.Success) {
            throw new Exception("Verification accepted a damaged worker chunk.");
            }
        if (!(await archive.ReadFileRangeAsync(entry.FileID, 0, 128)).AsSpan().SequenceEqual(source.AsSpan(0, 128))) {
            throw new Exception("Damage outside a requested range prevented reading an intact chunk.");
            }
        FileInfo partial = new FileInfo(path + ".out");
        try {
            await archive.ExtractFileAsync(entry.FileID, partial);
            }
        catch (InvalidDataException) when (!File.Exists(partial.FullName)) {
            return;
            }
        throw new Exception("Corruption did not fail extraction and remove partial output.");
        }
    }
