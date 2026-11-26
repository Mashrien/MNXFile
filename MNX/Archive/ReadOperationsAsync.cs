namespace MNX;

public sealed partial class Archive {
    // Keep the existing operation gate, progress callbacks and partial-output cleanup
    // on one coordinator. The engine dispatches positional reads beneath that gate.
    public Task<ReturnCode> ExtractFileAsync(Guid id, FileInfo destination, bool? overwrite = null) {
        return Task.Run(() => ExtractFile(id, destination, overwrite), Options.CancellationToken);
        }

    public Task<ReturnCode> VerifyArchiveAsync() {
        return Task.Run(VerifyArchive, Options.CancellationToken);
        }

    public Task<byte[]> ReadFileRangeAsync(Guid id, long offset, int count) {
        return Task.Run(() => ReadFileRange(id, offset, count), Options.CancellationToken);
        }
    }
