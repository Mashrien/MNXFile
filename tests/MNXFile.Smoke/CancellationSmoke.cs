using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

internal static class CancellationSmoke {
    internal static void Run(string work) {
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
                string path = Path.Combine(work, $"cancel-{codec}-{encrypted}.mnx");
                string? password = encrypted ? "cancel-test" : null;
                using (ArchiveFile archive = new ArchiveFile()) {
                    archive.Options.Compression = codec;
                    archive.Create(new FileInfo(path), password);
                    archive.AddStreamNow(new MemoryStream([4, 5, 6]), "completed.txt", 3);
                    using CancellationTokenSource stop = new CancellationTokenSource();
                    archive.Options.CancellationToken = stop.Token;
                    using CancelDuringRead input = new CancelDuringRead(stop);
                    bool cancelled = false;
                    try {
                        archive.AddStreamNow(input, "unfinished.txt", input.Length);
                        }
                    catch (OperationCanceledException) {
                        cancelled = true;
                        }

                    archive.Options.CancellationToken = default;
                    Check(cancelled && input.Position < input.Length, "Cancel stops before all input is read");
                    Check(
                        archive.Files.Count == 1 && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
                        "Current file rolls back and previous file remains valid"
                        );
                    archive.AddStreamNow(new MemoryStream([7]), "after-cancel.txt", 1);
                    }

                using ArchiveFile reopened = new ArchiveFile().Open(path, password);
                Check(
                    reopened.Files.Count == 2 && reopened.VerifyArchive() == ArchiveFile.ReturnCode.Success,
                    "Cancelled archive reopens and accepts subsequent work"
                    );
                VirtualFile? file = reopened.Files.Single(entry => entry.Name == "completed.txt");
                Check(
                    reopened.ReadFileRange(file.FileID, 0, 3).SequenceEqual(new byte[] { 4, 5, 6 }),
                    "Completed file bytes survive cancellation"
                    );
                }
            }

        Console.WriteLine("PASS: mid-file cancellation rolls back only the unfinished file for all current codecs, with and without encryption");
        }

    private sealed class CancelDuringRead(CancellationTokenSource stop) : MemoryStream(new byte[2 * 1024 * 1024]) {
        private int _reads;
        public override int Read(byte[] buffer, int offset, int count) {
            int read = base.Read(buffer, offset, count);
            if (++_reads == 3) {
                stop.Cancel();
                }

            return read;
            }
        }

    private static void Check(bool condition, string description) {
        if (!condition) {
            throw new InvalidOperationException("FAILED: " + description);
            }
        }
    }
