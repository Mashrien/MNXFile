using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive.Resume;
using Mash.IO.Format.MNX.Interop;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX.Native;

public static unsafe partial class Exports {
    [UnmanagedCallersOnly(EntryPoint = "mnx_client_protocol_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ClientProtocolVersion() {
        return 1;
        }

    // Each request executes once. The caller releases the returned allocation,
    // including error responses. No "probe buffer length" call repeats a mutation.
    [UnmanagedCallersOnly(EntryPoint = "mnx_client_request", CallConvs = [typeof(CallConvCdecl)])]
    public static int ClientRequest(byte* request, ulong requestLength, nint callback, nint user, byte** response, ulong* responseLength) {
        if (response == null || responseLength == null) {
            return InvalidArgument;
            }

        *response = null;
        *responseLength = 0;
        try {
            Required(request != null && requestLength <= int.MaxValue, "Invalid request buffer.");
            ClientRequest input = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(request, (int)requestLength), ClientJson.Default.ClientRequest)
                ?? throw new ArgumentException("Request is empty.");
            ClientResponse output = new ClientResponse();
            try {
                DispatchClientRequest(input, output, callback, user);
                }
            catch (Exception error) {
                output.Error = error.Message;
                output.ErrorType = error.GetType().Name;
                if (error.Data["MNXReturnCode"] is ulong code) {
                    output.ReturnCode = code;
                    }
                else if (input.Handle != 0 && Archives.TryGetValue(input.Handle, out Context? context)) {
                    output.ReturnCode = (ulong)context.Archive.ErrorStatus;
                    }
                else {
                    output.ReturnCode = (ulong)ArchiveFile.ReturnCode.Failed;
                    }
                }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(output, ClientJson.Default.ClientResponse);
            byte* allocation = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(allocation, bytes.Length));
            *response = allocation;
            *responseLength = (ulong)bytes.Length;
            // Responses can include an explicitly requested recovery password.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            return 0;
            }
        catch {
            return Unexpected;
            }
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_client_release", CallConvs = [typeof(CallConvCdecl)])]
    public static void ClientRelease(byte* response, ulong length) {
        if (response != null) {
            if (length <= int.MaxValue) {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(new Span<byte>(response, (int)length));
                }
            NativeMemory.Free(response);
            }
        }

    // Cancellation must not wait for Context.Gate: the worker holds that gate
    // throughout the operation. CancellationTokenSource.Cancel is thread safe.
    [UnmanagedCallersOnly(EntryPoint = "mnx_client_cancel", CallConvs = [typeof(CallConvCdecl)])]
    public static int ClientCancel(ulong handle) {
        try {
            if (!Archives.TryGetValue(handle, out Context? context)) {
                return InvalidHandle;
                }
            context.Cancellation.Cancel();
            return 0;
            }
        catch (ObjectDisposedException) {
            return InvalidHandle;
            }
        catch {
            return Unexpected;
            }
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_client_prepare", CallConvs = [typeof(CallConvCdecl)])]
    public static int ClientPrepare(ulong handle) {
        return Run(handle, context => {
            if (context.Cancellation.IsCancellationRequested) {
                context.Cancellation.Dispose();
                context.Cancellation = new CancellationTokenSource();
                }
            context.Archive.Options.CancellationToken = context.Cancellation.Token;
            });
        }

    private static void DispatchClientRequest(ClientRequest input, ClientResponse output, nint callback, nint user) {
        switch (input.Operation) {
            case "new":
            case "create" when input.Handle == 0:
            case "open" when input.Handle == 0:
                ArchiveFile archive = new ArchiveFile();
                try {
                    archive.Options.Compression = (ArchiveCompression)input.Compression;
                    archive.Options.CompressionLevel = input.Level;
                    archive.Options.CompressionLayout = (ArchiveCompressionLayout)input.Layout;
                    archive.Options.ChunkSize = input.ChunkSize;
                    if (input.Operation == "create") {
                        archive.Create(new FileInfo(input.Path!), input.Password);
                        }
                    else if (input.Operation == "open") {
                        archive.Open(input.Path!, input.Password);
                        }
                    ulong handle = 0;
                    Register(archive, &handle);
                    output.Handle = handle;
                    FillClientInfo(archive, output);
                    return;
                    }
                catch (Exception error) {
                    error.Data["MNXReturnCode"] = (ulong)archive.ErrorStatus;
                    archive.Dispose();
                    throw;
                    }
            case "archive-is":
                output.Value = ArchiveFile.IsArchive(new FileInfo(input.Path!));
                return;
            case "validate-name":
                ArchiveFile.ValidateDestinationName(input.Name!);
                return;
            case "queue-is":
                output.Value = RemainingQueue.IsQueue(new FileInfo(input.Path!));
                return;
            case "queue-read":
                output.Resume = RemainingQueue.Read(new FileInfo(input.Path!));
                return;
            case "queue-locate":
                output.Text = RemainingQueue.LocateArchive(new FileInfo(input.Path!), input.Resume!);
                return;
            case "queue-mark":
                output.Text = RemainingQueue.MarkCompleted(new FileInfo(input.Path!));
                return;
            case "queue-discard":
                RemainingQueue.Discard(new FileInfo(input.Path!));
                return;
            case "queue-save":
                output.Text = RemainingQueue.Save(input.Path!, input.Queue.Select(FromSnapshot), input.Password);
                return;
            case "queue-password":
                output.Text = ResumePassword.Read(new FileInfo(input.Path!), input.Name!);
                return;
            }

        if (!Archives.TryGetValue(input.Handle, out Context? context)) {
            throw new ObjectDisposedException("MNX", "Invalid or closed archive handle.");
            }
        lock (context.Gate) {
            if (context.Active || context.Closed) {
                throw new InvalidOperationException("The archive is busy or closed.");
                }
            if (input.Operation == "prepare") {
                context.Cancellation.Dispose();
                context.Cancellation = new CancellationTokenSource();
                return;
                }
            context.Active = true;
            ArchiveFile target = context.Archive;
            target.Options.CancellationToken = context.Cancellation.Token;
            target.Options.OverrideByDefault = input.Overwrite;
            target.Options.ProgressReportFrequency = Math.Max(1, input.Interval);
            long lastReport = Environment.TickCount64 - target.Options.ProgressReportFrequency;
            void Report(object? sender, OperationProgressArgs progress) {
                long now = Environment.TickCount64;
                if (now - lastReport < target.Options.ProgressReportFrequency && progress.BytesComplete < progress.BytesMax) {
                    return;
                    }
                lastReport = now;
                if (NotifyClient(callback, user, 0, progress.FileName, progress.BytesComplete, progress.BytesMax, 0) != 0) {
                    throw new OperationCanceledException("The caller stopped the operation.");
                    }
                }
            target.ProgressReport += Report;
            try {
                switch (input.Operation) {
                    case "create":
                    case "open":
                        target.Options.Compression = (ArchiveCompression)input.Compression;
                        target.Options.CompressionLevel = input.Level;
                        target.Options.CompressionLayout = (ArchiveCompressionLayout)input.Layout;
                        target.Options.ChunkSize = input.ChunkSize;
                        try {
                            if (input.Operation == "create") {
                                target.Create(new FileInfo(input.Path!), input.Password);
                                }
                            else {
                                target.Open(input.Path!, input.Password);
                                }
                            }
                        catch (Exception error) {
                            error.Data["MNXReturnCode"] = (ulong)target.ErrorStatus;
                            throw;
                            }
                        output.Handle = input.Handle;
                        FillClientInfo(target, output);
                        break;
                    case "archive-close":
                        foreach (Stream openedStream in context.Streams.Values) {
                            openedStream.Dispose();
                            }
                        context.Streams.Clear();
                        target.Close();
                        break;
                    case "info":
                        FillClientInfo(target, output);
                        break;
                    case "files":
                        output.Entries = target.Files.Select(ToSnapshot).ToArray();
                        break;
                    case "queue":
                        output.Queue = target.FileQueue.Select(ToSnapshot).ToArray();
                        break;
                    case "queue-file":
                        output.ReturnCode = (ulong)target.Queue_AddFile(new FileInfo(input.Path!), input.Parent);
                        break;
                    case "queue-directory":
                        output.ReturnCode = (ulong)target.Queue_AddDirectory(new DirectoryInfo(input.Path!), input.Parent);
                        break;
                    case "queue-remove":
                        output.ReturnCode = (ulong)target.Queue_RemoveFromQueue(input.Id);
                        break;
                    case "process":
                        output.ReturnCode = (ulong)target.ProcessAddQueue((current, total, phase, name) => {
                            return NotifyClient(callback, user, 1, name, current, total, phase) != 0;
                            });
                        break;
                    case "add-file":
                        output.ReturnCode = (ulong)target.AddFileNow(new FileInfo(input.Path!), input.Parent);
                        break;
                    case "add-directory":
                        output.ReturnCode = (ulong)target.AddDirectoryNow(new DirectoryInfo(input.Path!), input.Parent);
                        break;
                    case "add-stream":
                        Required(input.SourceCallback != 0, "Source callback is missing.");
                        using (ClientInputStream source = new ClientInputStream((nint)input.SourceCallback, (nint)input.SourceContext)) {
                            output.ReturnCode = (ulong)target.AddStreamNow(source, input.Name!, input.Length, input.Parent);
                            }
                        break;
                    case "extract-file":
                        output.ReturnCode = (ulong)target.ExtractFile(input.Id, new FileInfo(input.Path!), input.Overwrite);
                        break;
                    case "extract-directory":
                        output.ReturnCode = (ulong)target.ExtractDirectory(input.Id, new DirectoryInfo(input.Path!), input.Overwrite);
                        break;
                    case "delete":
                        output.ReturnCode = (ulong)target.DeleteFileFromArchive(input.Id);
                        break;
                    case "compact":
                        output.ReturnCode = (ulong)target.CompactArchive();
                        break;
                    case "verify":
                        output.ReturnCode = (ulong)target.VerifyArchive();
                        break;
                    case "load-queue":
                        output.ReturnCode = (ulong)target.LoadRemainingQueue(new FileInfo(input.Path!));
                        break;
                    case "save-queue":
                        output.Text = target.SaveRemainingQueue(input.ExportKey);
                        break;
                    case "consume-key":
                        target.ConsumeResumeKey(new FileInfo(input.Path!));
                        break;
                    case "stream-open":
                        Stream stream = target.OpenRead(input.Id);
                        long streamId = ++context.NextStream;
                        context.Streams.Add(streamId, stream);
                        output.StreamId = streamId;
                        output.Length = stream.Length;
                        break;
                    case "stream-close":
                        if (context.Streams.Remove(input.StreamId, out Stream? closedStream)) {
                            closedStream.Dispose();
                            }
                        break;
                    case "close":
                        foreach (Stream openedStream in context.Streams.Values) {
                            openedStream.Dispose();
                            }
                        context.Streams.Clear();
                        context.Closed = true;
                        Archives.TryRemove(input.Handle, out _);
                        target.Dispose();
                        context.Cancellation.Dispose();
                        break;
                    default:
                        throw new NotSupportedException("Unknown MNX client operation: " + input.Operation);
                    }
                }
            finally {
                target.ProgressReport -= Report;
                context.Active = false;
                }
            }
        }

    private static int NotifyClient(nint callback, nint user, int kind, string name, long current, long total, int phase) {
        if (callback == 0) {
            return 0;
            }
        byte[] bytes = Utf8.GetBytes(name + '\0');
        fixed (byte* namePointer = bytes) {
            return ((delegate* unmanaged[Cdecl]<nint, int, byte*, long, long, int, int>)callback)(user, kind, namePointer, current, total, phase);
            }
        }

    private static void FillClientInfo(ArchiveFile archive, ClientResponse output) {
        output.ArchiveId = archive.ArchiveGuid;
        output.RootId = archive.RootDirectoryId;
        output.FormatVersion = archive.FormatVersion;
        if (!archive.IsOpen) {
            output.ChunkSize = archive.Options.ChunkSize;
            return;
            }
        Mash.IO.Format.MNX.Archive.ArchiveInfo info = archive.GetArchiveInfo();
        output.ArchiveId = info.ArchiveGuid;
        output.RootId = info.RootDirectoryId;
        output.Text = archive.ArchivePath;
        output.FormatVersion = archive.FormatVersion;
        output.ChunkSize = archive.ChunkSize;
        output.Compression = (int)archive.Compression;
        output.Level = archive.CompressionLevel;
        output.Layout = (int)archive.CompressionLayout;
        output.Encrypted = archive.IsEncrypted;
        output.SizeOnDisk = info.SizeOnDisk;
        output.ChunkCount = info.ChunkCount;
        output.IndexChunks = info.IndexChunkCount;
        output.FreeChunks = info.FreeChunkCount;
        output.FreeRanges = info.FreeRangeCount;
        output.FileCount = info.FileCount;
        output.DirectoryCount = info.DirectoryCount;
        }

    private static EntrySnapshot ToSnapshot(VirtualFile entry) {
        return new EntrySnapshot {
            Id = entry.FileID,
            Parent = entry.OwnerID,
            Name = entry.Name,
            Directory = entry.IsDirectory,
            Size = entry.SizeOriginal,
            Chunks = entry.ChunkCount,
            Extents = entry.ChunkExtents.Count,
            Attributes = (short)entry.Attributes,
            Created = entry.CreationDateUtc,
            Modified = entry.ModificationDateUtc,
            Accessed = entry.AccessDateUtc
            };
        }

    private static QueueSnapshot ToSnapshot(FileQueueItem item) {
        return new QueueSnapshot {
            Id = item.ID,
            Parent = item.Parent,
            Name = item.Name,
            Path = item.SourcePath,
            Directory = item.IsDirectory,
            Length = item.Length
            };
        }

    private static FileQueueItem FromSnapshot(QueueSnapshot item) {
        return new FileQueueItem(item.Path, item.Name, item.Parent, item.Directory, item.Id);
        }

    [UnmanagedCallersOnly(EntryPoint = "mnx_client_stream_read", CallConvs = [typeof(CallConvCdecl)])]
    public static int ClientStreamRead(ulong handle, long streamId, long offset, byte* buffer, int capacity, int* count) {
        if (count == null) {
            return InvalidArgument;
            }
        *count = 0;
        return Run(handle, context => {
            Required(capacity >= 0 && capacity <= 4 * 1024 * 1024 && (buffer != null || capacity == 0), "Read buffer must be 0..4 MiB.");
            Required(context.Streams.TryGetValue(streamId, out Stream? stream), "Invalid stream handle.");
            context.Archive.Options.CancellationToken = context.Cancellation.Token;
            stream!.Position = offset;
            *count = stream.Read(new Span<byte>(buffer, capacity));
            });
        }

    private sealed class ClientInputStream(nint callback, nint user) : Stream {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) {
            return Read(buffer.AsSpan(offset, count));
            }
        public override int Read(Span<byte> buffer) {
            fixed (byte* pointer = buffer) {
                int count = ((delegate* unmanaged[Cdecl]<nint, byte*, int, int>)callback)(user, pointer, buffer.Length);
                if (count < 0 || count > buffer.Length) {
                    throw new IOException("The caller's input stream failed.");
                    }
                return count;
                }
            }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
