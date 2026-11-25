using System.Security.Cryptography;
using MNX;
using static MNX.Archive;

if (args.Length != 0 && args[0] is "--missing" or "--incompatible") {
    try {
        using Archive missing = new Archive().Open("missing.mnx");
        throw new Exception("A missing native library was accepted.");
        }
    catch (NotSupportedException error) when (args[0] == "--incompatible") {
        Console.WriteLine("PASS: incompatible native library reported: " + error.Message);
        return;
        }
    catch (DllNotFoundException error) when (args[0] == "--missing") {
        Console.WriteLine("PASS: missing library reported: " + error.Message);
        return;
        }
    }

string engineVersion = LibraryInfo.Version;
Check(engineVersion.StartsWith("1.3.1-alpha.", StringComparison.Ordinal), "native engine reports its release version without opening an archive");
Check(LibraryInfo.FormatVersion == "0.1.8", "native engine reports the semantic storage format without opening an archive");
Check(Archive.MaximumArchiveBytes == 140_737_488_322_560UL, "client reports the exact physical archive capacity");
Console.WriteLine("PASS: installed MNXFile version " + engineVersion);
string root = Path.Combine(Path.GetTempPath(), "mnx-client-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    if (args.Contains("--precreate")) {
        string sourcePath = Path.Combine(root, "queued.txt");
        File.WriteAllText(sourcePath, "queued before creation");
        using Archive pending = new Archive();
        Guid originalRoot = pending.RootDirectoryId;
        pending.Queue_AddFile(new FileInfo(sourcePath), originalRoot);
        Check(!pending.IsOpen && pending.FileQueue.Count == 1, "queue before creation");
        pending.Create(new FileInfo(Path.Combine(root, "precreate.mnx")));
        Check(pending.RootDirectoryId == originalRoot, "creation preserves queued parent IDs");
        pending.Close();
        Check(!pending.IsOpen && pending.FileQueue.Count == 1, "close preserves remaining queue");
        Console.WriteLine("PASS: precreation queue and close preserve metadata");
        return;
        }
    if (args.Contains("--overwrite")) {
        string sourcePath = Path.Combine(root, "same.txt");
        File.WriteAllText(sourcePath, "original");
        using Archive guarded = new Archive();
        guarded.Create(new FileInfo(Path.Combine(root, "overwrite.mnx")));
        guarded.AddFileNow(new FileInfo(sourcePath));
        Guid originalId = guarded.Files.Single().FileID;
        guarded.Options.OverrideByDefault = false;
        File.WriteAllText(sourcePath, "replacement");
        guarded.Queue_AddFile(new FileInfo(sourcePath));
        try {
            guarded.ProcessAddQueue();
            }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        Check(guarded.GetFile(originalId) != null, "queue must honor overwrite disabled");
        Check(System.Text.Encoding.UTF8.GetString(guarded.ReadFileRange(originalId, 0, 32)) == "original", "original data preserved");
        Console.WriteLine("PASS: queue processing respects overwrite disabled");
        return;
        }
    byte[] contents = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 137);
    string source = Path.Combine(root, "あいうえお with.dots.é.txt");
    File.WriteAllBytes(source, contents);
    foreach (ArchiveCompression compression in new[] { ArchiveCompression.None, ArchiveCompression.Brotli, ArchiveCompression.Snappy, ArchiveCompression.LZ4 }) {
        string path = Path.Combine(root, compression + ".mnx");
        using Archive archive = new Archive();
        archive.Options.Compression = compression;
        archive.Options.CompressionLevel = compression == ArchiveCompression.Brotli ? 1 : 0;
        archive.Create(new FileInfo(path), "ひみつのことば");
        archive.Options.OverrideByDefault = false;
        Check(archive.Queue_AddFile(new FileInfo(source)) == ReturnCode.Success, "queue source");
        Check(archive.FileQueue.Count == 1 && archive.FileQueue[0].Length == (ulong)contents.Length, "queue metadata");
        List<int> phases = new List<int>();
        Check(archive.ProcessAddQueue((_, _, phase, _) => { phases.Add(phase); return false; }) == ReturnCode.Success, "process");
        Check(phases.SequenceEqual(new[] { 0, 1 }), "file boundary callbacks");
        Guid fileId = archive.Files.Single().FileID;
        Check(archive.GetEntry(fileId)?.FileID == fileId && archive.GetEntry(Guid.NewGuid()) is null, "direct native GUID metadata lookup");
        Check(archive.DirectoryList().Single().FileID == fileId && !archive.HasSubdirectories(), "native directory descriptor lookup");
        Check(archive.EnumerateFiles(0, 1).Single().FileID == fileId && !archive.EnumerateFiles(1, 1).Any(), "bounded native entry enumeration");
        Check(archive.SemanticFormatVersion == "0.1.8", "archive semantic format version");
        Check(archive.ReadFileRange(fileId, 127, 4097).SequenceEqual(contents.AsSpan(127, 4097).ToArray()), "bounded range");
        using (Stream stored = archive.OpenRead(fileId)) {
            using SHA256 hash = SHA256.Create();
            Check(hash.ComputeHash(stored).SequenceEqual(SHA256.HashData(contents)), "stream comparison");
            stored.Seek(17, SeekOrigin.Begin);
            byte[] prefix = new byte[83];
            stored.ReadExactly(prefix);
            Check(prefix.SequenceEqual(contents.AsSpan(17, 83).ToArray()), "stream backwards seek");
            }
        string output = Path.Combine(root, compression + ".out");
        archive.ExtractFile(fileId, new FileInfo(output), false);
        Check(File.ReadAllBytes(output).SequenceEqual(contents), "extract bytes");
        archive.AddStreamNow(new MemoryStream([1, 2, 3, 4]), "stream.bin", 4);
        string later = Path.Combine(root, "later.txt");
        File.WriteAllText(later, "remaining work");
        archive.Queue_AddFile(new FileInfo(later), archive.RootDirectoryId);
        string queue = archive.SaveRemainingQueue(exportKeyFile: true);
        Check(RemainingQueue.IsQueue(new FileInfo(queue)), "queue identification");
        Check(ResumePassword.Read(new FileInfo(queue), path) == "ひみつのことば", "exported key read");
        archive.Queue_RemoveFromQueue(archive.FileQueue.Single().ID);
        archive.LoadRemainingQueue(new FileInfo(queue));
        archive.ConsumeResumeKey(new FileInfo(queue));
        Check(!File.Exists(Path.ChangeExtension(queue, ".mnx-key")), "key consumed");
        archive.ProcessAddQueue();
        RemainingQueue.MarkCompleted(new FileInfo(queue));
        archive.DeleteFileFromArchive(archive.Files.Single(entry => entry.Name == "later.txt").FileID);
        archive.DeleteFileFromArchive(archive.Files.Single(entry => entry.Name == "stream.bin").FileID);
        archive.CompactArchive();
        Check(archive.VerifyArchive() == ReturnCode.Success, "verify after compact");
        archive.Close();
        archive.Open(path, "ひみつのことば");
        Check(archive.Compression == compression && archive.IsEncrypted && archive.Files.Single().Name == Path.GetFileName(source), "reopen metadata");
        Check(archive.VerifyArchive() == ReturnCode.Success, "reopen verify");
        Console.WriteLine("PASS: native client " + compression + " encryption/queue/callbacks/stream/range/extract/key/compact/reopen");
        }

    string pagedSource = Path.Combine(root, "paged-source");
    Directory.CreateDirectory(pagedSource);
    for (int index = 0; index < 300; index++) {
        File.WriteAllText(Path.Combine(pagedSource, "entry-" + index.ToString("D3") + ".txt"), index.ToString());
        }
    using (Archive paged = new Archive().Create(new FileInfo(Path.Combine(root, "paged-client.mnx")))) {
        paged.Queue_AddDirectory(new DirectoryInfo(pagedSource));
        paged.ProcessAddQueue();
        Guid folder = paged.DirectoryList().Single().FileID;
        Check(paged.EnumerateFiles().Count() == 301 && paged.EnumerateFiles(255, 4).Count() == 4 && !paged.EnumerateFiles(301, 1).Any(), "native enumeration crosses its 256-entry response boundary");
        Check(paged.DirectoryList(folder).Count == 300 && paged.HasSubdirectories() && !paged.HasSubdirectories(folder), "native folder lookup remains independent of whole-index snapshots");
        Check(paged.GetArchiveInfo().FileCount == 300 && paged.GetArchiveInfo().DirectoryCount == 1, "native occupancy counts after paged mutation");
        }
    Console.WriteLine("PASS: native client paged enumeration, GUID lookup, directory lookup, subdirectory flags and semantic format identity");

    using Archive cancelled = new Archive();
    string cancelledPath = Path.Combine(root, "cancelled.mnx");
    cancelled.Create(new FileInfo(cancelledPath));
    cancelled.Queue_AddFile(new FileInfo(source));
    try {
        cancelled.ProcessAddQueue((_, _, _, _) => throw new InvalidOperationException("boundary-test"));
        throw new Exception("Callback exception was swallowed.");
        }
    catch (InvalidOperationException error) when (error.Message == "boundary-test") {
        Check(cancelled.FileQueue.Count == 1 && cancelled.Files.Count == 0, "callback failure keeps queued work");
        }
    cancelled.ProcessAddQueue();
    Check(cancelled.VerifyArchive() == ReturnCode.Success, "recover after callback failure");
    using CancellationTokenSource cancellation = new CancellationTokenSource();
    cancelled.Options.ProgressReportFrequency = 1;
    cancelled.Options.CancellationToken = cancellation.Token;
    cancelled.ProgressReport += (_, _) => cancellation.Cancel();
    string partial = Path.Combine(root, "partial.out");
    try {
        cancelled.ExtractFile(cancelled.Files.Single().FileID, new FileInfo(partial));
        throw new Exception("Extraction cancellation was ignored.");
        }
    catch (OperationCanceledException) {
        Check(!File.Exists(partial), "cancelled extraction removes unfinished output");
        }
    cancelled.Options.CancellationToken = CancellationToken.None;
    Check(cancelled.VerifyArchive() == ReturnCode.Success, "recover after cancellation");
    Console.WriteLine("PASS: callback exceptions and cancellation stay inside ABI and preserve valid archive");
    }
finally {
    Directory.Delete(root, recursive: true);
    }

static void Check(bool condition, string message) {
    if (!condition) {
        throw new InvalidOperationException("FAIL: " + message);
        }
    }
