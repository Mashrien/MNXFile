using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

internal static class FilenameSmoke {
    internal static void Run(string work) {
        string source = Path.Combine(work, "names.with.periods");
        Directory.CreateDirectory(source);
        List<string> names = new List<string> {
            "multi.part.name.txt",
            ".hidden",
            "#hash %percent &quote' \"name.txt",
            new string ('a', 251) + ".txt",
            new string ('é', 100) + ".txt"
            };
        if (OperatingSystem.IsWindows()) {
            names.RemoveAt(2);
            }
        else {
            names.AddRange(["back\\slash.txt", "   ", "colon:star*question?pipe|angle<>.txt", "line\nbreak\t.txt"]);
            }

        for (int i = 0; i < names.Count; i++) {
            DiskFile.WriteAllText(Path.Combine(source, names[i]), $"unique payload {i}");
            }

        foreach (bool encrypted in new[] {
            false,
            true
            }

            ) {
            string archivePath = Path.Combine(work, "names-" + encrypted + ".mnx");
            string? password = encrypted ? "name-test" : null;
            using (ArchiveFile archive = new ArchiveFile()) {
                archive.Options.Compression = ArchiveCompression.Snappy;
                archive.Create(new FileInfo(archivePath), password);
                archive.AddDirectoryNow(new DirectoryInfo(source));
                archive.CompactArchive();
                if (archive.VerifyArchive() != ArchiveFile.ReturnCode.Success) {
                    throw new InvalidOperationException("Filename archive failed verification.");
                    }
                }

            using (ArchiveFile archive = new ArchiveFile().Open(archivePath, password)) {
                VirtualFile? folder = archive.DirectoryList().Single();
                string output = Path.Combine(work, "name-output-" + encrypted);
                archive.ExtractDirectory(folder.FileID, new DirectoryInfo(output));
                for (int i = 0; i < names.Count; i++) {
                    if (DiskFile.ReadAllText(Path.Combine(output, names[i])) != $"unique payload {i}") {
                        throw new InvalidOperationException("Filename or contents changed: " + names[i]);
                        }
                    }

                // Windows permits 255 UTF-16 code units, potentially over 255 UTF-8 bytes.
                string wide = new string ('界', 250) + ".txt";
                archive.AddStreamNow(new MemoryStream([1, 2, 3]), wide, 3);
                }

            using (ArchiveFile archive = new ArchiveFile().Open(archivePath, password)) {
                if (!archive.Files.Any(file => file.Name == new string ('界', 250) + ".txt")) {
                    throw new InvalidOperationException("Wide Unicode name did not round-trip.");
                    }
                }
            }

        Console.WriteLine("PASS: special, long, whitespace and Unicode names preserve exact bytes through reopen/extract/compact/encryption");
        }
    }
