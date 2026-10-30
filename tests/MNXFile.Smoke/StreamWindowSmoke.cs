using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

internal static class StreamWindowSmoke {
    internal static void Run(string work) {
        byte[] source = new byte[200_123];
        for (int position = 0; position < source.Length; position++) {
            source[position] = (byte)((position * 37 + (position >> 11) * 19) & 255);
            }

        foreach (ArchiveCompression compression in new[] { ArchiveCompression.None, ArchiveCompression.Brotli, ArchiveCompression.Snappy }) {
            foreach (bool encrypted in new[] { false, true }) {
                string path = Path.Combine(work, $"stream-window-{compression}-{encrypted}.mnx");
                using ArchiveFile archive = new ArchiveFile();
                archive.Options.Compression = compression;
                archive.Options.CompressionLayout = ArchiveCompressionLayout.IndependentChunks;
                archive.Options.CompressionLevel = compression == ArchiveCompression.Snappy ? 0 : 1;
                archive.Create(new FileInfo(path), encrypted ? "sliding-window-check" : null);
                archive.AddStreamNow(new MemoryStream(source), "small-reads.bin", source.Length);
                VirtualFile file = archive.Files.Single();
                using Stream input = archive.OpenRead(file.FileID);
                byte[] output = new byte[source.Length];
                int completed = 0;
                while (completed < output.Length) {
                    int requested = Math.Min(997, output.Length - completed);
                    int count = input.Read(output.AsSpan(completed, requested));
                    if (count == 0) {
                        throw new InvalidOperationException("Small stream reads ended early.");
                        }

                    completed += count;
                    }

                if (!output.AsSpan().SequenceEqual(source) || input.Read(output, 0, 1) != 0) {
                    throw new InvalidOperationException("Small stream reads changed data or missed EOF.");
                    }

                foreach (int offset in new[] { 0, 65_537, 32_127, 131_000, source.Length - 121 }) {
                    input.Seek(offset, SeekOrigin.Begin);
                    int count = Math.Min(121, source.Length - offset);
                    input.ReadExactly(output.AsSpan(0, count));
                    if (!output.AsSpan(0, count).SequenceEqual(source.AsSpan(offset, count))) {
                        throw new InvalidOperationException("Sliding cache returned the wrong data after seeking.");
                        }
                    }

                input.Seek(0, SeekOrigin.Begin);
                if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(source))) {
                    throw new InvalidOperationException("Stream hash changed after rewinding.");
                    }

                input.Seek(source.Length + 100, SeekOrigin.Begin);
                if (input.Read(output, 0, 1) != 0) {
                    throw new InvalidOperationException("Seek past EOF returned data.");
                    }
                }
            }

        Console.WriteLine("PASS: bounded sliding stream reads, boundary crossings, backward seeks, EOF and encryption");
        }
    }
