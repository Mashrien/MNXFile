using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

internal static class ExtractionCancellationSmoke {
    internal static void Run(string work) {
        byte[] data = new byte[333_333];
        new Random(318).NextBytes(data);
        foreach (ArchiveCompression codec in new[] {
            ArchiveCompression.None,
            ArchiveCompression.Brotli,
            ArchiveCompression.Snappy,
            ArchiveCompression.LZ4
            }

            ) {
            foreach (bool encrypted in new[] {
                false,
                true
                }

                ) {
                string path = Path.Combine(work, $"extract-cancel-{codec}-{encrypted}.mnx");
                using ArchiveFile archive = new ArchiveFile();
                archive.Options.Compression = codec;
                archive.Options.CompressionLevel = codec == ArchiveCompression.Brotli ? 1 : 0;
                archive.Create(new FileInfo(path), encrypted ? "extraction smoke password" : null);
                archive.AddStreamNow(new MemoryStream(data), "payload.bin", data.Length);
                VirtualFile? file = archive.Files.Single();
                foreach (bool overwrite in new[] {
                    false,
                    true
                    }

                    ) {
                    string output = path + (overwrite ? ".existing.bin" : ".new.bin");
                    byte[] old = [3, 1, 4, 1, 5];
                    if (overwrite) {
                        DiskFile.WriteAllBytes(output, old);
                        }

                    using CancellationTokenSource cancellation = new CancellationTokenSource();
                    archive.Options.CancellationToken = cancellation.Token;
                    void Stop(object? sender, OperationProgressArgs report) {
                        if (report.BytesComplete > 0) {
                            cancellation.Cancel();
                            }
                        }

                    archive.ProgressReport += Stop;
                    bool cancelled = false;
                    try {
                        archive.ExtractFile(file.FileID, new FileInfo(output), overwrite);
                        }
                    catch (OperationCanceledException) {
                        cancelled = true;
                        }
                    finally {
                        archive.ProgressReport -= Stop;
                        archive.Options.CancellationToken = default;
                        }

                    Check(cancelled, "Extraction cooperatively stops during a file");
                    Check(!DiskFile.Exists(output), "Stop-now deletes the current direct output file");
                    Check(!Directory.EnumerateFiles(work, ".mnx-extract-*").Any(), "Temporary extraction output is removed");
                    archive.ExtractFile(file.FileID, new FileInfo(output), overwrite);
                    Check(
                        DiskFile.ReadAllBytes(output).SequenceEqual(data) && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
                        "A subsequent extraction succeeds without changing archive contents"
                        );
                    }
                }
            }

        Console.WriteLine("PASS: bounded extraction cancellation and direct output deletion, all codecs and encryption");
        }

    private static void Check(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException("FAILED: " + message);
            }
        }
    }
