using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive.Resume;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;

internal static class ResumeKeySmoke {
    internal static void Run(string work) {
        string folder = Path.Combine(work, "temporary-keys");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "my.backup.mnx");
        string first = Path.Combine(folder, "first.input.txt");
        string second = Path.Combine(folder, "second.input.txt");
        DiskFile.WriteAllText(first, "First resumed payload");
        DiskFile.WriteAllText(second, "Second resumed payload");
        const string password = "A long é🐈 password that is never written literally";
        string queuePath;
        using (ArchiveFile archive = new ArchiveFile()) {
            archive.Options.Compression = ArchiveCompression.LZ4;
            archive.Create(new FileInfo(path), password);
            archive.Queue_AddFile(new FileInfo(first));
            archive.Queue_AddFile(new FileInfo(second));
            queuePath = archive.SaveRemainingQueue();
            Check(
                queuePath == Path.ChangeExtension(path, ".mnq") && !DiskFile.Exists(Path.ChangeExtension(path, ".mnx-key")),
                "Matching queue basename and no password export by default"
                );
            archive.SaveRemainingQueue(exportKeyFile: true);
            }

        FileInfo queue = new FileInfo(queuePath);
        string keyPath = Path.ChangeExtension(queuePath, ".mnx-key");
        byte[] key = DiskFile.ReadAllBytes(keyPath);
        byte[] original = DiskFile.ReadAllBytes(path);
        byte[] queueBytes = DiskFile.ReadAllBytes(queuePath);
        Check(
            !Encoding.UTF8.GetString(key).Contains(password) && ResumePassword.Read(queue, path) == password,
            "Exported AES key round-trips the exact UTF-8 password without storing plaintext"
            );
        if (!OperatingSystem.IsWindows()) {
            Check(
                DiskFile.GetUnixFileMode(keyPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "Key is created with 0600 permissions"
                );
            DiskFile.SetUnixFileMode(keyPath, UnixFileMode.UserRead);
            try {
                Reject(() => RemainingQueue.Discard(queue), "An overwrite failure prevents deletion");
                Check(
                    DiskFile.Exists(queuePath) && DiskFile.ReadAllBytes(keyPath).SequenceEqual(key),
                    "Failed overwrite retains the queue and key"
                    );
                }
            finally {
                DiskFile.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

            string savedKey = keyPath + ".test-original";
            string unrelated = Path.Combine(folder, "unrelated-file.txt");
            DiskFile.WriteAllText(unrelated, "Do not overwrite this link target.");
            DiskFile.Move(keyPath, savedKey);
            try {
                DiskFile.CreateSymbolicLink(keyPath, unrelated);
                Reject(() => ResumePassword.Read(queue, path), "Linked key files are refused");
                Reject(() => RemainingQueue.Discard(queue), "Linked key files cannot overwrite their targets");
                Check(DiskFile.ReadAllText(unrelated) == "Do not overwrite this link target.", "Link target is preserved");
                }
            finally {
                DiskFile.Delete(keyPath);
                DiskFile.Move(savedKey, keyPath);
                }
            }

        byte[] damaged = original.ToArray();
        damaged[12] ^= 1;
        DiskFile.WriteAllBytes(path, damaged);
        Reject(() => ResumePassword.Read(queue, path), "Corrupt archive header is rejected");
        SHA256.HashData(damaged.AsSpan(0, 144)).CopyTo(damaged, 144);
        DiskFile.WriteAllBytes(path, damaged);
        Reject(() => ResumePassword.Read(queue, path), "Changed header with recomputed checksum is rejected");
        DiskFile.WriteAllBytes(path, original);
        int chunk = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(8));
        damaged = original.ToArray();
        damaged[chunk] ^= 1;
        DiskFile.WriteAllBytes(path, damaged);
        Reject(() => ResumePassword.Read(queue, path), "Changed front index is rejected");
        DiskFile.WriteAllBytes(path, original);
        byte[] badKey = key.ToArray();
        badKey[badKey.Length - 1] ^= 1;
        DiskFile.WriteAllBytes(keyPath, badKey);
        Reject(() => ResumePassword.Read(queue, path), "Altered password ciphertext is rejected by AES-GCM");
        Check(DiskFile.Exists(keyPath), "Rejected key is preserved");
        DiskFile.WriteAllBytes(keyPath, key);
        // Whitespace outside the payload leaves MNQ's own checksum valid,
        // but the temporary password tag still binds the entire saved queue.
        DiskFile.WriteAllBytes(queuePath, queueBytes.Concat(new byte[] { 32 }).ToArray());
        Reject(
            () => ResumePassword.Read(queue, path),
            "Queue bytes are authenticated beyond its payload checksum"
            );
        DiskFile.WriteAllBytes(queuePath, queueBytes);
        Reject(() => RemainingQueue.MarkCompleted(queue), "Completion refuses to leave an unconsumed key");
        using (ArchiveFile archive = new ArchiveFile().Open(path, ResumePassword.Read(queue, path))) {
            Reject(() => archive.ConsumeResumeKey(queue), "Key cannot be consumed before source preflight");
            DateTime time = DiskFile.GetLastWriteTimeUtc(second);
            DiskFile.AppendAllText(second, "changed");
            Reject(() => archive.LoadRemainingQueue(queue), "Changed source prevents resume");
            Check(DiskFile.ReadAllBytes(keyPath).SequenceEqual(key), "Failed preflight preserves the usable key");
            DiskFile.WriteAllText(second, "Second resumed payload");
            DiskFile.SetLastWriteTimeUtc(second, time);
            archive.LoadRemainingQueue(queue);
            int fd = OperatingSystem.IsLinux() ? OpenInode(keyPath, 0) : -1;
            try {
                if (OperatingSystem.IsLinux()) {
                    Check(fd >= 0, "Retain an inode reader for overwrite verification");
                    }

                archive.ConsumeResumeKey(queue);
                Check(!DiskFile.Exists(keyPath), "Successful preflight consumes the key before payload writes");
                if (fd >= 0) {
                    byte[] overwritten = new byte[key.Length];
                    Check(
                        ReadInode(fd, overwritten, (nuint)overwritten.Length) == overwritten.Length && !overwritten.SequenceEqual(key) && overwritten.Any(value => value != 0),
                        "Unlinked inode actually contains random overwrites"
                        );
                    }
                }
            finally {
                if (fd >= 0) {
                    CloseInode(fd);
                    }
                }

            archive.ProcessAddQueue((_, _, phase, _) => phase == 1);
            Check(
                archive.Files.Count == 1 && archive.FileQueue.Count == 1,
                "Second interruption leaves remaining work"
                );
            archive.SaveRemainingQueue(exportKeyFile: true);
            Check(
                DiskFile.Exists(keyPath) && !DiskFile.ReadAllBytes(keyPath).SequenceEqual(key),
                "Repeated interruption exports a fresh bound key"
                );
            }

        string moved = Path.Combine(folder, "moved");
        Directory.CreateDirectory(moved);
        string movedArchive = Path.Combine(moved, Path.GetFileName(path));
        string movedQueue = Path.ChangeExtension(movedArchive, ".mnq");
        string movedKey = Path.ChangeExtension(movedArchive, ".mnx-key");
        DiskFile.Move(path, movedArchive);
        DiskFile.Move(queuePath, movedQueue);
        DiskFile.Move(keyPath, movedKey);
        FileInfo movedQueueFile = new FileInfo(movedQueue);
        using (ArchiveFile archive = new ArchiveFile().Open(movedArchive, ResumePassword.Read(movedQueueFile, movedArchive))) {
            archive.LoadRemainingQueue(movedQueueFile);
            archive.ConsumeResumeKey(movedQueueFile);
            Check(
                archive.ProcessAddQueue() == ArchiveFile.ReturnCode.Success && archive.VerifyArchive() == ArchiveFile.ReturnCode.Success && archive.IsEncrypted && archive.Compression == ArchiveCompression.LZ4 && archive.Files.Count == 2,
                "Moved recovery trio resumes with the original encryption and codec"
                );
            string third = Path.Combine(folder, "third.input.txt");
            DiskFile.WriteAllText(third, "Another pending file");
            archive.Queue_AddFile(new FileInfo(third));
            archive.SaveRemainingQueue(exportKeyFile: true);
            archive.SaveRemainingQueue();
            Check(!DiskFile.Exists(movedKey), "Saving without export overwrites and removes any old key");
            archive.SaveRemainingQueue(exportKeyFile: true);
            }

        RemainingQueue.Discard(movedQueueFile);
        Check(
            !DiskFile.Exists(movedKey) && !DiskFile.Exists(movedQueue) && DiskFile.Exists(movedArchive),
            "Discard removes both recovery sidecars"
            );
        Console.WriteLine("PASS: AES-256-GCM resume keys, archive/queue binding, preflight, random overwrite/delete, repeated cancellation and relocation");
        }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenInode(string path, int flags);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadInode(int fd, byte[] buffer, nuint count);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseInode(int fd);
    private static void Reject(Action action, string message) {
        bool rejected = false;
        try {
            action();
            }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or InvalidOperationException or UnauthorizedAccessException) {
            rejected = true;
            }

        Check(rejected, message);
        }

    private static void Check(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException("FAILED: " + message);
            }
        }
    }
