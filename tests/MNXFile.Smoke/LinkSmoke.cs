using static MNX.Archive;
using MNX;
using DiskFile = System.IO.File;

internal static class LinkSmoke {
    internal static void Run(string work) {
        string source = Path.Combine(work, "linked-input");
        string nested = Path.Combine(source, "nested");
        string outside = Path.Combine(work, "outside-links");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);
        string regular = Path.Combine(nested, "real.txt");
        DiskFile.WriteAllText(regular, "Only this actual file belongs in the archive.");
        DiskFile.WriteAllText(Path.Combine(outside, "outside.txt"), "Never follow the directory link.");
        string fileLink = Path.Combine(nested, "file-link");
        string directoryLink = Path.Combine(nested, "directory-link");
        string dangling = Path.Combine(nested, "dangling-link");
        DiskFile.CreateSymbolicLink(fileLink, regular);
        Directory.CreateSymbolicLink(directoryLink, outside);
        DiskFile.CreateSymbolicLink(dangling, Path.Combine(work, "does-not-exist"));
        Directory.CreateSymbolicLink(Path.Combine(nested, "cycle"), source);

        string archivePath = Path.Combine(work, "ignore-links.mnx");
        using (Archive archive = new Archive()) {
            archive.Create(new FileInfo(archivePath));
            Check(archive.Queue_AddDirectory(new DirectoryInfo(source)) == ReturnCode.Success, "Recursive scan continues past links");
            Check(archive.FileQueue.Count == 3, "Queue contains only real folders and files");
            Check(archive.ProcessAddQueue() == ReturnCode.Success, "Regular entries are committed");
            Check(archive.Queue_AddFile(new FileInfo(fileLink)) == ReturnCode.Success, "Direct file link is ignored");
            Check(archive.Queue_AddFile(new FileInfo(dangling)) == ReturnCode.Success, "Dangling file link is ignored");
            Check(archive.Queue_AddDirectory(new DirectoryInfo(directoryLink)) == ReturnCode.Success, "Direct directory link is ignored");
            Check(archive.AddFileNow(new FileInfo(fileLink)) == ReturnCode.Success, "Immediate add ignores a file link");
            Check(archive.AddDirectoryNow(new DirectoryInfo(directoryLink)) == ReturnCode.Success, "Immediate add ignores a directory link");
            Check(archive.AddFileDropList([dangling, directoryLink, fileLink]).Values.All(result => result == ReturnCode.Success), "Drop-list links are ignored");
            Check(archive.FileQueue.Count == 0 && archive.Files.Count == 3, "Ignoring links changes neither queue nor contents");
            }

        using (Archive archive = new Archive().Open(archivePath)) {
            Check(archive.Files.Count == 3 && archive.VerifyArchive() == ReturnCode.Success, "Reopened archive contains only valid real entries");
            VFile file = archive.Files.Single(entry => !entry.IsDirectory);
            using Stream data = archive.OpenRead(file.FileID);
            using StreamReader reader = new StreamReader(data);
            Check(reader.ReadToEnd() == "Only this actual file belongs in the archive.", "Original contents are preserved");
            }

        Console.WriteLine("PASS: silent link skipping, dangling targets, directory cycles, direct adds and recursive scans");
        }

    private static void Check(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message);
            }
        }
    }
