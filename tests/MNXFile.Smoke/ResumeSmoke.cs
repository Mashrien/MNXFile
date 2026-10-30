using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive.Resume;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

internal static class ResumeSmoke {
    internal static void Run(string work) {
        string source = Path.Combine(work, "resume.input");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        string name = new string ('é', 100) + ".with.dots.txt";
        string filePath = Path.Combine(source, name);
        byte[] contents = new byte[333_333];
        new Random(31).NextBytes(contents);
        DiskFile.WriteAllBytes(filePath, contents);
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
                string path = Path.Combine(work, $"resume-{codec}-{encrypted}.mnx");
                string? password = encrypted ? "resume-password" : null;
                string queuePath;
                using (ArchiveFile archive = new ArchiveFile()) {
                    archive.Options.Compression = codec;
                    archive.Options.CompressionLevel = codec == ArchiveCompression.Brotli ? 2 : 0;
                    archive.Create(new FileInfo(path), password);
                    archive.AddStreamNow(new MemoryStream([1, 6, 1, 8]), "already.complete", 4);
                    archive.Queue_AddDirectory(new DirectoryInfo(source));
                    queuePath = RemainingQueue.Save(path, archive.FileQueue);
                    }

                byte[] queueBytes = DiskFile.ReadAllBytes(queuePath);
                // A changed source is rejected before any queue or archive mutation.
                byte[] originalArchive = DiskFile.ReadAllBytes(path);
                DateTime originalTime = DiskFile.GetLastWriteTimeUtc(filePath);
                DiskFile.AppendAllText(filePath, "changed");
                using (ArchiveFile archive = new ArchiveFile().Open(path, password)) {
                    bool rejected = false;
                    try {
                        archive.LoadRemainingQueue(new FileInfo(queuePath));
                        }
                    catch (IOException) {
                        rejected = true;
                        }

                    Check(
                        rejected && archive.FileQueue.Count == 0 && archive.Files.Count == 1,
                        "Changed source preflight writes nothing"
                        );
                    }

                DiskFile.WriteAllBytes(filePath, contents);
                DiskFile.SetLastWriteTimeUtc(filePath, originalTime);
                Check(DiskFile.ReadAllBytes(path).SequenceEqual(originalArchive), "Rejected resume preserves archive bytes");
                string damaged = Path.Combine(work, "damaged.mnq");
                string text = System.Text.Encoding.UTF8.GetString(queueBytes).Replace("resume.input", "resume.other");
                DiskFile.WriteAllText(damaged, text);
                bool badQueue = false;
                try {
                    RemainingQueue.Read(new FileInfo(damaged));
                    }
                catch (InvalidDataException) {
                    badQueue = true;
                    }

                Check(badQueue, "Queue payload checksum rejects edited metadata");
                using (ArchiveFile archive = new ArchiveFile().Open(path, password)) {
                    archive.AddStreamNow(new MemoryStream([9]), "changed.archive", 1);
                    bool rejected = false;
                    try {
                        archive.LoadRemainingQueue(new FileInfo(queuePath));
                        }
                    catch (InvalidDataException) {
                        rejected = true;
                        }

                    Check(rejected && archive.FileQueue.Count == 0, "Stale archive checksum rejects old queue");
                    }

                DiskFile.WriteAllBytes(path, originalArchive);
                QueuePayload? payload = RemainingQueue.Read(new FileInfo(queuePath));
                string moved = Path.Combine(work, "moved-" + codec + "-" + encrypted);
                Directory.CreateDirectory(moved);
                string movedPath = Path.Combine(moved, Path.GetFileName(path));
                string movedQueue = Path.Combine(moved, "remaining_queue.mnq");
                DiskFile.Move(path, movedPath);
                DiskFile.Move(queuePath, movedQueue);
                Check(
                    RemainingQueue.LocateArchive(new FileInfo(movedQueue), payload) == movedPath,
                    "Queue and archive can move together"
                    );
                using (ArchiveFile archive = new ArchiveFile().Open(movedPath, password)) {
                    int level = archive.CompressionLevel;
                    archive.LoadRemainingQueue(new FileInfo(movedQueue));
                    Check(archive.ProcessAddQueue() == ArchiveFile.ReturnCode.Success, "Remaining entries process successfully");
                    Check(
                        archive.Compression == codec && archive.CompressionLevel == level && archive.IsEncrypted == encrypted,
                        "Resume inherits stored compression, effort and encryption"
                        );
                    VirtualFile? file = archive.Files.Single(entry => entry.Name == name);
                    Check(
                        SHA256.HashData(archive.ReadFileRange(file.FileID, 0, contents.Length)).SequenceEqual(SHA256.HashData(contents)) && archive.Files.Any(entry => entry.Name == "empty" && entry.IsDirectory) && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success,
                        "Resume preserves names, nested/empty folders and payload bytes"
                        );
                    }

                string completed = RemainingQueue.MarkCompleted(new FileInfo(movedQueue));
                Check(
                    !DiskFile.Exists(movedQueue) && DiskFile.Exists(completed),
                    "Completed queue is retained under a completed filename"
                    );
                }
            }

        Console.WriteLine("PASS: resume validates queues, archive fingerprints and sources; moved archives, Unicode folders/files, all current codecs and encryption");
        }

    private static void Check(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException("FAILED: " + message);
            }
        }
    }
