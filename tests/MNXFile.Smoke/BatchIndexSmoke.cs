using System.Buffers.Binary;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;

internal static class BatchIndexSmoke {
    internal static void Run(string work) {
        string directory = Path.Combine(work, "batch-index-input");
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 500; index++) {
            DiskFile.WriteAllText(Path.Combine(directory, index.ToString("D4") + "-small-file.txt"), "unique " + index);
            }

        string path = Path.Combine(work, "batch-index.mnx");
        using (ArchiveFile archive = new ArchiveFile()) {
            archive.Options.ChunkSize = 4096;
            archive.Create(new FileInfo(path));
            byte[] initial = Header(path);
            archive.Queue_AddDirectory(new DirectoryInfo(directory));
            int finished = 0;
            archive.ProcessAddQueue((_, _, phase, _) => {
                if (phase == 1) {
                    finished++;
                    if (finished == 10 && !Header(path).SequenceEqual(initial)) {
                        throw new InvalidOperationException("Front index was rewritten before the reserved chunks filled.");
                        }
                    }
                return finished == 350;
                });
            }

        using (ArchiveFile archive = new ArchiveFile().Open(path)) {
            if (archive.Files.Count != 350 || archive.IndexChunkCount <= 4 || archive.VerifyArchive() != ArchiveFile.ReturnCode.Success) {
                throw new InvalidOperationException("Batch growth or cancellation failed to persist the completed entries.");
                }
            }

        Console.WriteLine("PASS: index stays in memory until growth and cancellation persists all complete entries");
        }

    private static byte[] Header(string path) {
        byte[] header = new byte[176];
        using FileStream stream = DiskFile.OpenRead(path);
        stream.ReadExactly(header);
        return header;
        }
    }
