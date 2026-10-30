using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive.Resume;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX.Native;
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CreateOptions {
    public uint StructSize;
    public int Compression, CompressionLevel;
    public uint ChunkSize;
    public int CompressionLayout, Reserved;
    }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ArchiveInfo {
    public uint StructSize, FormatVersion, ChunkSize;
    public int Compression, CompressionLevel, CompressionLayout;
    public uint Encrypted, Reserved;
    public long SizeOnDisk;
    public uint ChunkCount, IndexChunkCount, FreeChunkCount, FreeRangeCount, FileCount, DirectoryCount;
    public fixed byte ArchiveId[37];
    public fixed byte RootId[37];
    }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct EntryInfo {
    public uint StructSize, IsDirectory;
    public ulong OriginalSize;
    public long CreatedUnixMs, ModifiedUnixMs, AccessedUnixMs;
    public fixed byte Id[37];
    public fixed byte ParentId[37];
    public fixed byte Name[129];
    }

// No archive payloads cross this ABI. Paths and metadata are UTF-8, IDs are GUIDs.
public static unsafe partial class Exports {
    private const int InvalidArgument = -1, InvalidHandle = -2, BufferTooSmall = -3, IoError = -4, Corrupt = -5, AuthError = -6, Unsupported = -7, Busy = -8, Unexpected = -9, Failed = -10, NotFound = -11, OutOfMemory = -12, AccessDenied = -13;
    private static readonly ConcurrentDictionary<ulong, Context> Archives = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static long _nextHandle;
    [ThreadStatic]
    private static string? _lastError;
    [ThreadStatic]
    private static ulong _lastReturnCode;
    private sealed class NativeError(int status, string message) : Exception(message) {
        public int Status { get; } = status;
        }

    private sealed class Context(ArchiveFile archive) {
        public readonly ArchiveFile Archive = archive;
        public readonly object Gate = new();
        public bool Active, Closed;
        public CancellationTokenSource Cancellation = new();
        public readonly Dictionary<long, Stream> Streams = new();
        public long NextStream;
        public nint Callback, CallbackContext;
        public void Progress(object? sender, OperationProgressArgs report) {
            if (Callback == 0) {
                return;
                }

            byte[] name = Utf8.GetBytes(report.FileName + '\0');
            fixed (byte* pointer = name)
                ((delegate* unmanaged[Cdecl]<nint, byte*, long, long, void> )Callback)(CallbackContext, pointer, report.BytesComplete, report.BytesMax);
            }
        }

    private static int Guard(Action action) {
        _lastError = null;
        _lastReturnCode = 0;
        try {
            action();
            // A rejected reentrant notification call must not poison a successful outer call.
            _lastError = null;
            return 0;
            }
        catch (Exception error) {
            _lastError = error.Message;
            return error switch {
                NativeError native => native.Status,
                ArgumentException or FormatException or OverflowException => InvalidArgument,
                FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => NotFound,
                UnauthorizedAccessException => AccessDenied,
                CryptographicException => AuthError,
                InvalidDataException or EndOfStreamException => Corrupt,
                NotSupportedException => Unsupported,
                IOException => IoError,
                OutOfMemoryException => OutOfMemory,
                _ => Unexpected
                };
            }
        }

    private static int Run(ulong handle, Action<Context> action) => Guard(() => {
        if (!Archives.TryGetValue(handle, out Context? context)) {
            throw new NativeError(InvalidHandle, "Invalid or closed archive handle.");
            }

        lock (context.Gate) {
            if (context.Closed) {
                throw new NativeError(InvalidHandle, "Archive is closed.");
                }

            if (context.Active) {
                throw new NativeError(Busy, "An operation is already running on this archive.");
                }

            context.Active = true;
            try {
                action(context);
                }
            finally {
                context.Active = false;
                }
            }
        });
    private static void Required(bool condition, string message) {
        if (!condition) {
            throw new NativeError(InvalidArgument, message);
            }
        }

    private static string Text(byte* value) {
        Required(value != null, "A required string is NULL.");
        const int maxBytes = 1024 * 1024;
        int length = 0;
        while (length < maxBytes && value[length] != 0) {
            length++;
            }

        Required(length < maxBytes, "String exceeds the 1 MiB ABI limit.");
        return Utf8.GetString(new ReadOnlySpan<byte>(value, length));
        }

    private static Guid Id(byte* value, bool root = false) {
        if (value == null && root) {
            return Guid.Empty;
            }

        Required(Guid.TryParseExact(Text(value), "D", out Guid id), "ID must be a canonical GUID string.");
        return id;
        }

    private static bool Boolean(int value) {
        Required(value is 0 or 1, "Boolean must be 0 or 1.");
        return value == 1;
        }

    private static void Copy(string value, byte* destination, int capacity) {
        Span<byte> span = new Span<byte>(destination, capacity);
        span.Clear();
        int length = Utf8.GetBytes(value, span[..(capacity - 1)]);
        span[length] = 0;
        }

    private static void Result(ArchiveFile.ReturnCode code, bool verify = false) {
        _lastReturnCode = (ulong)code;
        if (code == ArchiveFile.ReturnCode.Success) {
            return;
            }

        int status = verify ? Corrupt : (code & (ArchiveFile.ReturnCode.GuidNotFound | ArchiveFile.ReturnCode.ArchiveNonExistant)) != 0 ? NotFound : Failed;
        throw new NativeError(status, $"MNX operation returned {code}.");
        }

    private static void Fill(VirtualFile file, EntryInfo* output) {
        *output = default;
        output->StructSize = (uint)sizeof(EntryInfo);
        output->IsDirectory = file.IsDirectory ? 1u : 0u;
        output->OriginalSize = file.SizeOriginal;
        output->CreatedUnixMs = new DateTimeOffset(file.CreationDateUtc).ToUnixTimeMilliseconds();
        output->ModifiedUnixMs = new DateTimeOffset(file.ModificationDateUtc).ToUnixTimeMilliseconds();
        output->AccessedUnixMs = new DateTimeOffset(file.AccessDateUtc).ToUnixTimeMilliseconds();
        Copy(file.FileID.ToString("D"), output->Id, 37);
        Copy(file.OwnerID.ToString("D"), output->ParentId, 37);
        // Preserve the ABI1 struct size. Full names are available by ID below.
        int end = file.Name.Length;
        while (Utf8.GetByteCount(file.Name.AsSpan(0, end)) > 128) {
            end -= char.IsLowSurrogate(file.Name[end - 1]) ? 2 : 1;
            }

        Copy(file.Name[..end], output->Name, 129);
        }

    private static void List(IReadOnlyCollection<VirtualFile> files, EntryInfo* entries, ulong capacity, ulong* count) {
        Required(count != null, "Count output is NULL.");
        *count = (ulong)files.Count;
        if (entries == null && capacity == 0) {
            return;
            }

        Required(entries != null, "Metadata buffer is NULL.");
        if (capacity < (ulong)files.Count) {
            throw new NativeError(BufferTooSmall, "Metadata buffer is too small.");
            }

        int index = 0;
        foreach (VirtualFile file in files) {
            Fill(file, entries + index++);
            }
        }

    private static void Register(ArchiveFile archive, ulong* handle) {
        Context context = new Context(archive);
        archive.ProgressReport += context.Progress;
        ulong id = checked((ulong)Interlocked.Increment(ref _nextHandle));
        if (!Archives.TryAdd(id, context)) {
            throw new InvalidOperationException("Handle collision.");
            }

        *handle = id;
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersion() => 1;
    [UnmanagedCallersOnly(EntryPoint = "mnx_options_init", CallConvs = [typeof(CallConvCdecl)])]
    public static int OptionsInit(CreateOptions* options) => Guard(() => {
        Required(options != null, "Options output is NULL.");
        *options = new CreateOptions {
            StructSize = (uint)sizeof(CreateOptions),
            CompressionLevel = 4,
            ChunkSize = 32768,
            CompressionLayout = 1
            };
        });
    [UnmanagedCallersOnly(EntryPoint = "mnx_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int Create(byte* path, byte* password, CreateOptions* options, ulong* handle) {
        if (handle != null) {
            *handle = 0;
            }

        return Guard(() => {
            Required(handle != null, "Handle output is NULL.");
            ArchiveFile archive = new ArchiveFile();
            try {
                if (options != null) {
                    Required(
                        options->StructSize == sizeof(CreateOptions) && options->Reserved == 0,
                        "Unsupported options struct size or reserved field."
                        );
                    Required(options->Compression is 0 or 1 or 4 or 5, "Unsupported compression choice.");
                    Required(options->CompressionLayout is 0 or 1, "Unsupported compression layout.");
                    archive.Options.Compression = (ArchiveCompression)options->Compression;
                    archive.Options.CompressionLevel = options->CompressionLevel;
                    archive.Options.ChunkSize = checked((int)options->ChunkSize);
                    archive.Options.CompressionLayout = (ArchiveCompressionLayout)options->CompressionLayout;
                    }

                archive.Create(new FileInfo(Text(path)), password == null ? null : Text(password));
                Register(archive, handle);
                }
            catch {
                archive.Dispose();
                throw;
                }
            });
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_open", CallConvs = [typeof(CallConvCdecl)])]
    public static int Open(byte* path, byte* password, ulong* handle) {
        if (handle != null) {
            *handle = 0;
            }

        return Guard(() => {
            Required(handle != null, "Handle output is NULL.");
            ArchiveFile archive = new ArchiveFile();
            try {
                archive.Open(Text(path), password == null ? null : Text(password));
                Register(archive, handle);
                }
            catch {
                archive.Dispose();
                throw;
                }
            });
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_close", CallConvs = [typeof(CallConvCdecl)])]
    public static int Close(ulong handle) => Run(handle, context => {
        Archives.TryRemove(handle, out _);
        context.Closed = true;
        foreach (Stream stream in context.Streams.Values) {
            stream.Dispose();
            }
        context.Streams.Clear();
        context.Cancellation.Dispose();
        context.Archive.Dispose();
        });
    [UnmanagedCallersOnly(EntryPoint = "mnx_set_progress", CallConvs = [typeof(CallConvCdecl)])]
    public static int SetProgress(ulong handle, nint callback, nint user, int interval) => Run(handle, context => {
        Required(interval >= 0, "Progress interval cannot be negative.");
        context.Callback = callback;
        context.CallbackContext = user;
        context.Archive.Options.ProgressReportFrequency = interval;
        });
    [UnmanagedCallersOnly(EntryPoint = "mnx_set_overwrite", CallConvs = [typeof(CallConvCdecl)])]
    public static int SetOverwrite(ulong handle, int overwrite) => Run(handle, context => context.Archive.Options.OverrideByDefault = Boolean(overwrite));
    [UnmanagedCallersOnly(EntryPoint = "mnx_add_file", CallConvs = [typeof(CallConvCdecl)])]
    public static int AddFile(ulong handle, byte* source, byte* parent) => Run(
        handle,
        context => Result(context.Archive.AddFileNow(new FileInfo(Text(source)), Id(parent, true)))
        );
    [UnmanagedCallersOnly(EntryPoint = "mnx_add_directory", CallConvs = [typeof(CallConvCdecl)])]
    public static int AddDirectory(ulong handle, byte* source, byte* parent) => Run(
        handle,
        context => Result(context.Archive.AddDirectoryNow(new DirectoryInfo(Text(source)), Id(parent, true)))
        );
    [UnmanagedCallersOnly(EntryPoint = "mnx_list", CallConvs = [typeof(CallConvCdecl)])]
    public static int ListAll(ulong handle, EntryInfo* entries, ulong capacity, ulong* count) {
        if (count != null) {
            *count = 0;
            }

        return Run(handle, context => List(context.Archive.Files, entries, capacity, count));
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_list_directory", CallConvs = [typeof(CallConvCdecl)])]
    public static int ListDirectory(ulong handle, byte* parent, EntryInfo* entries, ulong capacity, ulong* count) {
        if (count != null) {
            *count = 0;
            }

        return Run(
            handle,
            context => List(context.Archive.DirectoryList(Id(parent, true)), entries, capacity, count)
            );
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_get_entry", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetEntry(ulong handle, byte* id, EntryInfo* output) => Run(handle, context => {
        Required(output != null && output->StructSize == sizeof(EntryInfo), "Unsupported entry output struct.");
        VirtualFile? file = context.Archive.GetFile(Id(id)) ?? throw new NativeError(NotFound, "Entry was not found.");
        Fill(file, output);
        });
    [UnmanagedCallersOnly(EntryPoint = "mnx_get_entry_name", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetEntryName(ulong handle, byte* id, byte* buffer, ulong capacity, ulong* required) => Run(handle, context => {
        Required(required != null, "A required-size pointer is needed.");
        VirtualFile? file = context.Archive.GetFile(Id(id)) ?? throw new NativeError(NotFound, "Entry was not found.");
        int bytes = Utf8.GetByteCount(file.Name) + 1;
        *required = (ulong)bytes;
        if (buffer == null) {
            Required(capacity == 0, "Null buffer requires zero capacity.");
            return;
            }

        if (capacity < (ulong)bytes) {
            throw new NativeError(BufferTooSmall, "Name buffer is too small.");
            }

        Copy(file.Name, buffer, bytes);
        });
    [UnmanagedCallersOnly(EntryPoint = "mnx_resume", CallConvs = [typeof(CallConvCdecl)])]
    public static int Resume(ulong handle, byte* queuePath) => Run(handle, context => {
        ArchiveFile? archive = context.Archive;
        FileInfo queue = new FileInfo(Text(queuePath));
        bool overwrite = archive.Options.OverrideByDefault;
        archive.Options.OverrideByDefault = false;
        try {
            archive.LoadRemainingQueue(queue);
            archive.ConsumeResumeKey(queue);
            Result(archive.ProcessAddQueue());
            RemainingQueue.MarkCompleted(queue);
            }
        finally {
            archive.Options.OverrideByDefault = overwrite;
            foreach (FileQueueItem item in archive.FileQueue) {
                archive.Queue_RemoveFromQueue(item.ID);
                }
            }
        });
    [UnmanagedCallersOnly(EntryPoint = "mnx_get_info", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetInfo(ulong handle, ArchiveInfo* output) => Run(handle, context => {
        Required(output != null && output->StructSize == sizeof(ArchiveInfo), "Unsupported archive output struct.");
        ArchiveFile? archive = context.Archive;
        Mash.IO.Format.MNX.Archive.ArchiveInfo? info = archive.GetArchiveInfo();
        *output = default;
        output->StructSize = (uint)sizeof(ArchiveInfo);
        output->FormatVersion = archive.FormatVersion;
        output->ChunkSize = (uint)archive.ChunkSize;
        output->Compression = (int)archive.Compression;
        output->CompressionLevel = archive.CompressionLevel;
        output->CompressionLayout = (int)archive.CompressionLayout;
        output->Encrypted = archive.IsEncrypted ? 1u : 0u;
        output->SizeOnDisk = info.SizeOnDisk;
        output->ChunkCount = info.ChunkCount;
        output->IndexChunkCount = info.IndexChunkCount;
        output->FreeChunkCount = info.FreeChunkCount;
        output->FreeRangeCount = info.FreeRangeCount;
        output->FileCount = info.FileCount;
        output->DirectoryCount = info.DirectoryCount;
        Copy(info.ArchiveGuid.ToString("D"), output->ArchiveId, 37);
        Copy(info.RootDirectoryId.ToString("D"), output->RootId, 37);
        });
    [UnmanagedCallersOnly(EntryPoint = "mnx_extract_file", CallConvs = [typeof(CallConvCdecl)])]
    public static int ExtractFile(ulong handle, byte* id, byte* destination, int overwrite) => Run(
        handle,
        context => Result(context.Archive.ExtractFile(Id(id), new FileInfo(Text(destination)), Boolean(overwrite)))
        );
    [UnmanagedCallersOnly(EntryPoint = "mnx_extract_directory", CallConvs = [typeof(CallConvCdecl)])]
    public static int ExtractDirectory(ulong handle, byte* id, byte* destination, int overwrite) => Run(
        handle,
        context => Result(context.Archive.ExtractDirectory(Id(id), new DirectoryInfo(Text(destination)), Boolean(overwrite)))
        );
    [UnmanagedCallersOnly(EntryPoint = "mnx_delete", CallConvs = [typeof(CallConvCdecl)])]
    public static int Delete(ulong handle, byte* id) => Run(handle, context => Result(context.Archive.DeleteFileFromArchive(Id(id))));
    [UnmanagedCallersOnly(EntryPoint = "mnx_compact", CallConvs = [typeof(CallConvCdecl)])]
    public static int Compact(ulong handle) => Run(handle, context => Result(context.Archive.CompactArchive()));
    [UnmanagedCallersOnly(EntryPoint = "mnx_verify", CallConvs = [typeof(CallConvCdecl)])]
    public static int Verify(ulong handle) => Run(handle, context => Result(context.Archive.VerifyArchive(), true));
    [UnmanagedCallersOnly(EntryPoint = "mnx_last_error", CallConvs = [typeof(CallConvCdecl)])]
    public static int LastError(byte* buffer, ulong capacity, ulong* required) {
        // Diagnostic access must preserve the error it describes, including on a short buffer.
        if (required == null) {
            return InvalidArgument;
            }

        string error = _lastError ?? string.Empty;
        int bytes = Utf8.GetByteCount(error) + 1;
        *required = (ulong)bytes;
        if (buffer == null) {
            return capacity == 0 ? 0 : InvalidArgument;
            }

        if (capacity < (ulong)bytes) {
            return BufferTooSmall;
            }

        Copy(error, buffer, bytes);
        return 0;
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_last_return_code", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong LastReturnCode() => _lastReturnCode;
    }
