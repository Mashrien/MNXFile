using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Interop;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX;

/// <summary>Thin native client. The installed MNXFile library owns archive processing.</summary>
public sealed unsafe partial class MNX : IDisposable {
    private readonly object _gate = new object();
    private ulong _handle;
    private bool _disposed;
    private bool _opened;
    private ClientResponse? _info;
    private IReadOnlyCollection<VirtualFile>? _files;
    private IReadOnlyList<FileQueueItem>? _queue;
    public Configuration Options { get; } = new Configuration();
    public ReturnCode ErrorStatus { get; private set; } = ReturnCode.None;
    public bool IsOpen => _opened;
    public string? ArchivePath => IsOpen ? Info.Text : null;
    public Guid ArchiveGuid => Info.ArchiveId;
    public Guid RootDirectoryId => Info.RootId;
    public ushort FormatVersion => checked((ushort)Info.FormatVersion);
    public int ChunkSize => IsOpen ? Info.ChunkSize : Options.ChunkSize;
    public uint IndexChunkCount => Info.IndexChunks;
    public bool IsEncrypted => IsOpen && Info.Encrypted;
    public ArchiveCompression Compression => IsOpen ? (ArchiveCompression)Info.Compression : Options.Compression;
    public int CompressionLevel => IsOpen ? Info.Level : Options.CompressionLevel;
    public ArchiveCompressionLayout CompressionLayout => IsOpen ? (ArchiveCompressionLayout)Info.Layout : Options.CompressionLayout;
    public event EventHandler<OperationProgressArgs>? ProgressReport;
    private ClientResponse Info => _info ??= Call(new ClientRequest { Operation = "info" });

    public IReadOnlyCollection<VirtualFile> Files {
        get {
            lock (_gate) {
                if (!IsOpen) {
                    return Array.Empty<VirtualFile>();
                    }
                return _files ??= Array.AsReadOnly(Call(new ClientRequest { Operation = "files" }).Entries.Select(entry => new VirtualFile(entry)).ToArray());
                }
            }
        }

    public IReadOnlyList<FileQueueItem> FileQueue {
        get {
            lock (_gate) {
                if (_handle == 0) {
                    return Array.Empty<FileQueueItem>();
                    }
                return _queue ??= Array.AsReadOnly(Call(new ClientRequest { Operation = "queue" }).Queue.Select(item => new FileQueueItem(item)).ToArray());
                }
            }
        }

    public MNX Create(FileInfo archiveFile, string? password = null) {
        return OpenOrCreate(archiveFile.FullName, password, true);
        }
    public MNX Open(FileInfo archiveFile, string? password = null) {
        return OpenOrCreate(archiveFile.FullName, password, false);
        }
    public MNX Open(string path, string? password = null) {
        return OpenOrCreate(path, password, false);
        }
    private MNX OpenOrCreate(string path, string? password, bool create) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsOpen) {
                throw new InvalidOperationException("Close the current archive first.");
                }
            ClientResponse response = NativeApi.Instance.Request(new ClientRequest {
                Operation = create ? "create" : "open",
                Handle = _handle,
                Path = Path.GetFullPath(path),
                Password = password,
                Compression = (int)Options.Compression,
                Level = Options.CompressionLevel,
                Layout = (int)Options.CompressionLayout,
                ChunkSize = Options.ChunkSize
                });
            ErrorStatus = (ReturnCode)response.ReturnCode;
            NativeApi.ThrowIfError(response);
            _handle = response.Handle;
            _opened = true;
            _info = response;
            _files = null;
            _queue = null;
            return this;
            }
        }

    public ArchiveInfo GetArchiveInfo() {
        ClientResponse response = Call(new ClientRequest { Operation = "info" });
        _info = response;
        return new ArchiveInfo(response.ArchiveId, response.RootId, response.Text!, response.SizeOnDisk, response.ChunkCount,
            response.IndexChunks, response.FreeChunks, response.FreeRanges, response.FileCount, response.DirectoryCount
            );
        }

    public VirtualFile? GetFile(Guid id) {
        return Files.FirstOrDefault(entry => entry.FileID == id);
        }
    public IReadOnlyList<VirtualFile> DirectoryList(Guid parent = default) {
        Guid owner = parent == Guid.Empty ? RootDirectoryId : parent;
        if (owner != RootDirectoryId && GetFile(owner)?.IsDirectory != true) {
            throw new DirectoryNotFoundException("Archive directory was not found.");
            }
        return Files.Where(entry => entry.OwnerID == owner).OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        }
    public ReturnCode Queue_AddFile(FileInfo file, Guid parent = default) {
        return Change("queue-file", file.FullName, parent: parent);
        }
    public ReturnCode Queue_AddDirectory(DirectoryInfo directory, Guid parent = default) {
        return Change("queue-directory", directory.FullName, parent: parent);
        }
    public ReturnCode Queue_RemoveFromQueue(Guid id) {
        return Change("queue-remove", id: id);
        }
    public ReturnCode ProcessAddQueue(Func<int, int, int, string, bool>? callback = null) {
        try {
            return (ReturnCode)Call(new ClientRequest { Operation = "process", Overwrite = Options.OverrideByDefault }, callback).ReturnCode;
            }
        finally {
            Invalidate();
            }
        }
    public ReturnCode AddFileNow(FileInfo file, Guid parent = default) {
        return Change("add-file", file.FullName, parent: parent);
        }
    public ReturnCode AddDirectoryNow(DirectoryInfo directory, Guid parent = default) {
        return Change("add-directory", directory.FullName, parent: parent);
        }
    public ReturnCode ExtractFile(Guid id, FileInfo destination, bool? overwrite = null) {
        return (ReturnCode)Call(new ClientRequest { Operation = "extract-file", Id = id, Path = destination.FullName, Overwrite = overwrite ?? Options.OverrideByDefault }).ReturnCode;
        }
    public ReturnCode ExtractDirectory(Guid id, DirectoryInfo destination, bool? overwrite = null) {
        return (ReturnCode)Call(new ClientRequest { Operation = "extract-directory", Id = id, Path = destination.FullName, Overwrite = overwrite ?? Options.OverrideByDefault }).ReturnCode;
        }
    public ReturnCode DeleteFileFromArchive(Guid id) {
        return Change("delete", id: id);
        }
    public ReturnCode CompactArchive() {
        return Change("compact");
        }
    public ReturnCode VerifyArchive() {
        return (ReturnCode)Call(new ClientRequest { Operation = "verify" }).ReturnCode;
        }
    public ReturnCode LoadRemainingQueue(FileInfo file) {
        return Change("load-queue", file.FullName);
        }
    public string SaveRemainingQueue(bool exportKeyFile = false) {
        return Call(new ClientRequest { Operation = "save-queue", ExportKey = exportKeyFile }).Text!;
        }
    public void ConsumeResumeKey(FileInfo file) {
        Call(new ClientRequest { Operation = "consume-key", Path = file.FullName });
        }
    public static bool IsArchive(FileInfo source) {
        ClientResponse response = NativeApi.Instance.Request(new ClientRequest { Operation = "archive-is", Path = source.FullName });
        NativeApi.ThrowIfError(response);
        return response.Value;
        }
    public static void ValidateDestinationName(string name) {
        ClientResponse response = NativeApi.Instance.Request(new ClientRequest { Operation = "validate-name", Name = name });
        NativeApi.ThrowIfError(response);
        }

    private ReturnCode Change(string operation, string? path = null, Guid id = default, Guid parent = default) {
        try {
            return (ReturnCode)Call(new ClientRequest { Operation = operation, Path = path, Id = id, Parent = parent, Overwrite = Options.OverrideByDefault }).ReturnCode;
            }
        finally {
            Invalidate();
            }
        }
    private void Invalidate() {
        _info = null;
        _files = null;
        _queue = null;
        }

    private sealed class CallbackState(MNX owner, Func<int, int, int, string, bool>? boundary) {
        internal readonly MNX Owner = owner;
        internal readonly Func<int, int, int, string, bool>? Boundary = boundary;
        internal ExceptionDispatchInfo? Error;
        internal Stream? Source;
        }

    internal ClientResponse Call(ClientRequest request, Func<int, int, int, string, bool>? boundary = null, Stream? source = null) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool permitsClosed = request.Operation is "info" or "files" or "queue" or "queue-file" or "queue-directory" or "queue-remove";
            if (!IsOpen && !permitsClosed) {
                throw new InvalidOperationException("Create or open an archive first.");
                }
            Options.CancellationToken.ThrowIfCancellationRequested();
            if (_handle == 0) {
                ClientResponse session = NativeApi.Instance.Request(new ClientRequest { Operation = "new" });
                NativeApi.ThrowIfError(session);
                _handle = session.Handle;
                }

            request.Handle = _handle;
            request.Interval = Options.ProgressReportFrequency;
            // Reset cancellation before registering the new token, so cancellation
            // between registration and entry into native code is not lost.
            NativeApi.Instance.Prepare(_handle);
            CallbackState state = new CallbackState(this, boundary) { Source = source };
            GCHandle context = GCHandle.Alloc(state);
            try {
                if (source != null) {
                    request.SourceCallback = (long)(nint)(delegate* unmanaged[Cdecl]<nint, byte*, int, int>)&ReadSource;
                    request.SourceContext = (long)GCHandle.ToIntPtr(context);
                    }
                ulong handle = _handle;
                using CancellationTokenRegistration registration = Options.CancellationToken.Register(() => NativeApi.Instance.Cancel(handle));
                ClientResponse response = NativeApi.Instance.Request(request,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, byte*, long, long, int, int>)&Notify,
                    GCHandle.ToIntPtr(context)
                    );
                ErrorStatus = (ReturnCode)response.ReturnCode;
                state.Error?.Throw();
                NativeApi.ThrowIfError(response);
                return response;
                }
            finally {
                context.Free();
                }
            }
        }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Notify(nint context, int kind, byte* name, long current, long total, int phase) {
        CallbackState state = (CallbackState)GCHandle.FromIntPtr(context).Target!;
        try {
            string fileName = Marshal.PtrToStringUTF8((nint)name) ?? "";
            if (kind == 1) {
                return state.Boundary?.Invoke(checked((int)current), checked((int)total), phase, fileName) == true ? 1 : 0;
                }
            state.Owner.ProgressReport?.Invoke(state.Owner, new OperationProgressArgs {
                ErrorLevel = ReturnCode.Success,
                FileName = fileName,
                BytesComplete = current,
                BytesMax = total,
                Progress = total == 0 ? 1 : (float)current / total
                });
            return 0;
            }
        catch (Exception error) {
            state.Error ??= ExceptionDispatchInfo.Capture(error);
            return 1;
            }
        }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadSource(nint context, byte* buffer, int capacity) {
        CallbackState state = (CallbackState)GCHandle.FromIntPtr(context).Target!;
        try {
            return state.Source!.Read(new Span<byte>(buffer, capacity));
            }
        catch (Exception error) {
            state.Error ??= ExceptionDispatchInfo.Capture(error);
            return -1;
            }
        }

    public ReturnCode AddStreamNow(Stream source, string name, long length, Guid parent = default) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        try {
            return (ReturnCode)Call(new ClientRequest {
                Operation = "add-stream", Name = name, Length = length, Parent = parent, Overwrite = Options.OverrideByDefault
                }, source: source).ReturnCode;
            }
        finally {
            Invalidate();
            }
        }

    public void Close() {
        lock (_gate) {
            if (_handle == 0) {
                return;
                }
            // Closing always works even when the preceding operation was cancelled.
            ClientResponse response = NativeApi.Instance.Request(new ClientRequest { Operation = "archive-close", Handle = _handle });
            NativeApi.ThrowIfError(response);
            _opened = false;
            Invalidate();
            }
        }
    public void Dispose() {
        lock (_gate) {
            if (_handle != 0) {
                ClientResponse response = NativeApi.Instance.Request(new ClientRequest { Operation = "close", Handle = _handle });
                NativeApi.ThrowIfError(response);
                _handle = 0;
                }
            _opened = false;
            _disposed = true;
            Invalidate();
            }
        }
    }
