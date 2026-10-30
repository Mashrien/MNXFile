using System.Diagnostics;
using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive.Resume;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using Mash.IO.Format.MNX.Tools;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using DiskNodeFlags = Mash.IO.Format.MNX.Chunk.NodeFlags;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX;
/// <summary>A seekable MNX archive with a fixed chunk size declared by its header.</summary>
public sealed partial class MNX : IDisposable {
    private readonly ArchiveState _state = new();
    internal ArchiveState State => _state;
    public ArchiveQueue Queue { get; }
    public ArchiveRecovery Recovery { get; }
    public MNX() {
        ArchiveGuid = Guid.NewGuid();
        RootDirectoryId = Guid.NewGuid();
        Queue = new ArchiveQueue(this);
        Recovery = new ArchiveRecovery(this);
        }

    public Configuration Options => _state.Options;
    public Guid ArchiveGuid { get => _state.ArchiveGuid; private set => _state.ArchiveGuid = value; }
    public Guid RootDirectoryId { get => _state.RootDirectoryId; private set => _state.RootDirectoryId = value; }
    public string? ArchivePath => _state.Path;
    public bool IsOpen => _state.Path is not null;
    public ReturnCode ErrorStatus { get => _state.ErrorStatus; private set => _state.ErrorStatus = value; }
    public uint ChunkRangeStart => _state.ChunkCount <= _state.IndexChunks ? 0u : _state.IndexChunks + 1;
    public uint ChunkRangeEnd => _state.ChunkCount;
    public uint IndexChunkCount => _state.IndexChunks;
    public ushort FormatVersion => ArchiveFormat.CurrentVersion;
    public int ChunkSize => _state.ChunkSize;
    public bool IsEncrypted => _state.StorageFlags.HasFlag(DiskNodeFlags.Encrypted);
    public ArchiveCompression Compression => _state.Codec?.Compression ?? ArchiveCompression.None;
    /// <summary>How compressed file data is divided across physical chunks.</summary>
    public ArchiveCompressionLayout CompressionLayout => _state.StorageFlags.HasFlag(DiskNodeFlags.Streamed) ? ArchiveCompressionLayout.Continuous : ArchiveCompressionLayout.IndependentChunks;
    private bool UsesStreamCompression => Compression != ArchiveCompression.None && CompressionLayout == ArchiveCompressionLayout.Continuous;
    /// <summary>Stored compression effort, 1-9; Snappy and uncompressed archives report 0.</summary>
    public int CompressionLevel => _state.Codec?.CompressionLevel ?? 0;

    public IReadOnlyList<FileQueueItem> FileQueue => Queue.Items;

    public IReadOnlyCollection<VirtualFile> Files {
        get {
            lock (_state.SyncRoot) {
                return _state.Entries.Values.ToArray();
                }
            }
        }

    /// <summary>Return live size, occupancy, and entry counts for the open archive.</summary>
    public ArchiveInfo GetArchiveInfo() {
        lock (_state.SyncRoot) {
            CheckOpen();
            return new ArchiveInfo(
                ArchiveGuid,
                RootDirectoryId,
                _state.Path!,
                ((long)_state.ChunkCount + 1) * _state.ChunkSize,
                _state.ChunkCount,
                _state.IndexChunks,
                (uint)_state.FreeChunks.Count,
                (uint)_state.FreeChunks.Ranges.Count,
                (uint)_state.Entries.Values.Count(f => !f.IsDirectory),
                (uint)_state.Entries.Values.Count(f => f.IsDirectory)
                );
            }
        }

    public event EventHandler<FileOpenedEventArgs>? FileOpened;
    public event EventHandler<OperationProgressArgs>? ProgressReport;
    /// <summary>Create a new archive at the given path. Existing files are never overwritten.</summary>
    public ArchiveFile Create(FileInfo archiveFile, string? password = null) {
        ArgumentNullException.ThrowIfNull(archiveFile);
        lock (_state.SyncRoot) {
            CheckUsable();
            if (IsOpen) {
                throw new InvalidOperationException("Close the current archive first.");
                }

            if (password is not null && password.Length == 0) {
                throw new ArgumentException("A nonempty passphrase is required.", nameof(password));
                }

            if (!Enum.IsDefined(Options.Compression)) {
                throw new ArgumentOutOfRangeException(nameof(Options.Compression));
                }

            if (!Enum.IsDefined(Options.CompressionLayout)) {
                throw new ArgumentOutOfRangeException(nameof(Options.CompressionLayout));
                }

            if (Options.ChunkSize is not (ArchiveFormat.ChunkSize or ArchiveFormat.LargeChunkSize)) {
                throw new ArgumentOutOfRangeException(nameof(Options.ChunkSize));
                }

            if (Options.Compression == ArchiveCompression.LZ4 && Options.CompressionLayout != ArchiveCompressionLayout.Continuous) {
                throw new NotSupportedException("LZ4 requires the continuous file stream layout.");
                }

            int compressionLevel = Options.Compression is ArchiveCompression.Snappy or ArchiveCompression.LZ4 && Options.CompressionLevel == 4 ? 0 : Options.CompressionLevel;
            if (Options.Compression is ArchiveCompression.Snappy or ArchiveCompression.LZ4 && compressionLevel != 0) {
                throw new ArgumentOutOfRangeException(
                    nameof(Options.CompressionLevel),
                    "Snappy and LZ4 have fixed fast compression effort; use level 0."
                    );
                }

            if (Options.Compression == ArchiveCompression.Brotli && compressionLevel is < 1 or > 9) {
                throw new ArgumentOutOfRangeException(nameof(Options.CompressionLevel));
                }

            _state.ChunkSize = Options.ChunkSize;
            _state.StorageFlags = (Options.Compression != ArchiveCompression.None ? DiskNodeFlags.Compressed : DiskNodeFlags.None) | (password is not null ? DiskNodeFlags.Encrypted : DiskNodeFlags.None) | (Options.Compression != ArchiveCompression.None && Options.CompressionLayout == ArchiveCompressionLayout.Continuous ? DiskNodeFlags.Streamed : DiskNodeFlags.None);
            _state.Salt = password is null ? new byte[ArchiveCodec.SaltLength] : RandomNumberGenerator.GetBytes(ArchiveCodec.SaltLength);
            _state.KdfIterations = password is null ? 0 : ArchiveCodec.KdfIterations;
            using FileStream stream = new FileStream(archiveFile.FullName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try {
                _state.Codec = new ArchiveCodec(
                    ArchiveGuid,
                    Options.Compression,
                    Options.Compression == ArchiveCompression.None ? 0 : compressionLevel,
                    password is null ? null : ArchiveCodec.DeriveKey(password, _state.Salt, _state.KdfIterations),
                    Options.Compression != ArchiveCompression.None && Options.CompressionLayout == ArchiveCompressionLayout.Continuous
                    );
                _state.IndexChunks = _state.ChunkSize == ArchiveFormat.LargeChunkSize ? 1u : ArchiveFormat.InitialIndexChunks;
                _state.ChunkCount = _state.IndexChunks;
                byte[] index = ArchiveIndex.Serialize(Array.Empty<VirtualFile>(), _state.FreeChunks);
                WriteIndexAndHeader(stream, index);
                _state.Path = archiveFile.FullName;
                _state.ResumePassword = password is null ? null : System.Text.Encoding.UTF8.GetBytes(password);
                _state.Entries.Clear();
                _state.FreeChunks.Clear();
                ErrorStatus = ReturnCode.Success;
                return this;
                }
            catch {
                Close();
                stream.Dispose();
                DiskFile.Delete(archiveFile.FullName);
                throw;
                }
            }
        }

    /// <summary>Open an existing archive by reading only the front index.</summary>
    public ArchiveFile Open(FileInfo archiveFile, string? password = null) {
        ArgumentNullException.ThrowIfNull(archiveFile);
        lock (_state.SyncRoot) {
            CheckUsable();
            if (IsOpen) {
                throw new InvalidOperationException("Close the current archive first.");
                }

            ArchiveCodec? openedCodec = null;
            try {
                using FileStream stream = new FileStream(archiveFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                ArchiveFormat.HeaderInfo? header = ArchiveFormat.ReadHeader(stream);
                bool encrypted = header.flags.HasFlag(DiskNodeFlags.Encrypted);
                if (encrypted && string.IsNullOrEmpty(password)) {
                    throw new CryptographicException("A passphrase is required to open this archive.");
                    }

                if (!encrypted && password is not null) {
                    throw new ArgumentException("This archive is not encrypted.", nameof(password));
                    }

                openedCodec = new ArchiveCodec(
                    header.archiveId,
                    header.compression,
                    header.compressionLevel,
                    encrypted ? ArchiveCodec.DeriveKey(password!, header.salt, header.iterations) : null,
                    header.flags.HasFlag(DiskNodeFlags.Streamed)
                    );
                byte[] indexData = ArchiveFormat.ReadIndex(stream, header.indexBytes, header.indexHash, header.chunkSize);
                if (encrypted) {
                    indexData = openedCodec!.DecryptIndex(indexData, header.indexNonce, header.indexTag);
                    }

                (Dictionary<Guid, VirtualFile> entries, FreeChunkSet free) = ArchiveIndex.Deserialize(
                    indexData,
                    header.entries,
                    header.indexChunks,
                    header.chunks,
                    header.rootId,
                    header.chunkSize,
                    header.flags.HasFlag(DiskNodeFlags.Streamed)
                    );
                foreach (VirtualFile file in entries.Values) {
                    if (file.OwnerID != header.rootId && (!entries.TryGetValue(file.OwnerID, out VirtualFile? parent) || !parent.IsDirectory)) {
                        throw new InvalidDataException($"Invalid parent directory for {file.Name}.");
                        }
                    }

                // Parent chains must terminate at the archive root.
                foreach (VirtualFile file in entries.Values) {
                    HashSet<Guid> ancestors = new HashSet<Guid> {
                        file.FileID
                        };
                    Guid parent = file.OwnerID;
                    while (parent != header.rootId) {
                        if (!ancestors.Add(parent)) {
                            throw new InvalidDataException("Directory cycle detected.");
                            }

                        parent = entries[parent].OwnerID;
                        }
                    }

                ArchiveGuid = header.archiveId;
                RootDirectoryId = header.rootId;
                _state.Path = archiveFile.FullName;
                _state.ChunkCount = header.chunks;
                _state.IndexChunks = header.indexChunks;
                _state.ChunkSize = header.chunkSize;
                _state.StorageFlags = header.flags;
                _state.Salt = header.salt;
                _state.KdfIterations = header.iterations;
                _state.Codec = openedCodec;
                _state.ResumePassword = password is null ? null : System.Text.Encoding.UTF8.GetBytes(password);
                openedCodec = null;
                _state.Entries.Clear();
                foreach (KeyValuePair<Guid, VirtualFile> item in entries) {
                    _state.Entries.Add(item.Key, item.Value);
                    }

                _state.FreeChunks.Clear();
                foreach (FreeChunkRange range in free.Ranges) {
                    _state.FreeChunks.AddRange(range.Start, range.Count);
                    }

                ErrorStatus = ReturnCode.Success;
                FileOpenedEventArgs args = new FileOpenedEventArgs {
                    ErrorLevel = ReturnCode.Success
                    };
                FileOpened?.Invoke(this, args);
                if (args.Cancel) {
                    Close();
                    }

                return this;
                }
            catch (Exception ex) {
                openedCodec?.Dispose();
                Close();
                ErrorStatus = ex is FileNotFoundException ? ReturnCode.Failed | ReturnCode.ArchiveNonExistant : ReturnCode.Failed | ReturnCode.ParseFailed;
                FileOpened?.Invoke(
                    this,
                    new FileOpenedEventArgs { ErrorLevel = ErrorStatus, ErrorMessage = ex.Message, ExceptionText = ex.ToString() }
                    );
                throw;
                }
            }
        }

    public ArchiveFile Open(string path, string? password = null) => Open(new FileInfo(path), password);
    /// <summary>Forget the open archive while keeping the queue for a later save.</summary>
    public void Close() {
        lock (_state.SyncRoot) {
            _state.Path = null;
            _state.ChunkCount = 0;
            _state.IndexChunks = 0;
            _state.ChunkSize = ArchiveFormat.ChunkSize;
            _state.Codec?.Dispose();
            _state.Codec = null;
            if (_state.ResumePassword is not null) {
                CryptographicOperations.ZeroMemory(_state.ResumePassword);
                _state.ResumePassword = null;
                }

            _state.ValidatedResumeQueue = null;
            _state.ValidatedResumeQueueHash = null;
            _state.StorageFlags = DiskNodeFlags.None;
            _state.Salt = new byte[ArchiveCodec.SaltLength];
            _state.KdfIterations = 0;
            _state.Entries.Clear();
            _state.FreeChunks.Clear();
            ErrorStatus = ReturnCode.None;
            }
        }

    public VirtualFile GetRootVFile() => new(
        RootDirectoryId,
        Guid.Empty,
        "/",
        true,
        0,
        DateTime.UnixEpoch,
        DateTime.UnixEpoch,
        DateTime.UnixEpoch,
        0,
        0
        );
    public VirtualFile? GetFile(Guid id) {
        lock (_state.SyncRoot) {
            return _state.Entries.GetValueOrDefault(id);
            }
        }

    public IReadOnlyList<VirtualFile> DirectoryList(Guid parent = default) {
        lock (_state.SyncRoot) {
            Guid owner = parent == Guid.Empty ? RootDirectoryId : parent;
            RequireDirectory(owner);
            return _state.Entries.Values.Where(f => f.OwnerID == owner).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
            }
        }

    /// <summary>Queue a file. Links are silently ignored.</summary>
    public ReturnCode Queue_AddFile(FileInfo file, Guid parent = default) => Queue.AddFile(file, parent);
    /// <summary>Queue a folder tree, preserving empty folders and skipping links.</summary>
    public ReturnCode Queue_AddDirectory(DirectoryInfo directory, Guid parent = default) => Queue.AddDirectory(directory, parent);
    public Dictionary<string, ReturnCode> AddFileDropList(IEnumerable<string> paths, Guid parent = default) => Queue.AddDropList(paths, parent);
    public ReturnCode Queue_RemoveFromQueue(Guid id) => Queue.Remove(id);
    public ReturnCode ProcessAddQueue() => Queue.Process();
    public ReturnCode ProcessAddQueue(Func<int, int, int, string, bool>? callback) => Queue.Process(callback);

    public ReturnCode LoadRemainingQueue(FileInfo queueFile) => Recovery.LoadRemainingQueue(queueFile);
    public string SaveRemainingQueue(bool exportKeyFile = false) => Recovery.SaveRemainingQueue(exportKeyFile);
    public void ConsumeResumeKey(FileInfo queueFile) => Recovery.ConsumeResumeKey(queueFile);

    /// <summary>Process queued changes in an open archive.</summary>
    public ReturnCode SaveArchive() => ProcessAddQueue();
    /// <summary>Create an archive if needed, then process its queue.</summary>
    public ReturnCode SaveArchive(FileInfo archiveFile, string? password = null) {
        ArgumentNullException.ThrowIfNull(archiveFile);
        lock (_state.SyncRoot) {
            if (!IsOpen) {
                Create(archiveFile, password);
                }
            else if (!Path.GetFullPath(_state.Path!).Equals(archiveFile.FullName, StringComparison.Ordinal)) {
                throw new InvalidOperationException("Use the current archive path or close before creating another archive.");
                }

            return ProcessAddQueue();
            }
        }

    public Task<ReturnCode> AddFileImmediate(FileInfo file) => Task.Run(() => AddFileNow(file));
    public Task<ReturnCode> AddFile(FileInfo file) => AddFileImmediate(file);
    public ReturnCode AddFileNow(FileInfo file, Guid parent = default) {
        ArgumentNullException.ThrowIfNull(file);
        lock (_state.SyncRoot) {
            CheckOpen();
            if (FileSystemTools.IsLink(file)) {
                return SetError(ReturnCode.Success);
                }

            if (!file.Exists) {
                return SetError(ReturnCode.Failed | ReturnCode.ArchiveNonExistant);
                }

            WriteFile(file, NormalizeParent(parent), Guid.NewGuid(), file.Name);
            return SetError(ReturnCode.Success);
            }
        }

    /// <summary>Add exactly length bytes from a readable stream without taking ownership of it.</summary>
    public ReturnCode AddStreamNow(Stream source, string name, long length, Guid parent = default) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!source.CanRead) {
            throw new ArgumentException("Source must be readable.", nameof(source));
            }

        ValidateInputName(name);
        lock (_state.SyncRoot) {
            CheckOpen();
            if (source is FileStream fileStream && fileStream.Name == _state.Path) {
                throw new InvalidOperationException("An archive cannot contain itself.");
                }

            DateTime now = DateTime.UtcNow;
            WriteContent(
                source,
                length,
                NormalizeParent(parent),
                Guid.NewGuid(),
                name,
                new EntryMetadata(now, now, now, 0)
                );
            return SetError(ReturnCode.Success);
            }
        }

    public ReturnCode AddDirectoryNow(DirectoryInfo directory, Guid parent = default) {
        ArgumentNullException.ThrowIfNull(directory);
        lock (_state.SyncRoot) {
            CheckOpen();
            int start = _state.QueueItems.Count;
            ReturnCode result = Queue_AddDirectory(directory, parent);
            if (result != ReturnCode.Success) {
                return result;
                }

            bool ownsBatch = BeginWriteBatch();
            try {
                foreach (FileQueueItem item in _state.QueueItems.Skip(start).ToArray()) {
                    if (item.IsDirectory) {
                        WriteDirectory(item);
                        }
                    else {
                        WriteFile(new FileInfo(item.SourcePath), item.Parent, item.ID, item.Name);
                        }

                    _state.QueueItems.Remove(item);
                    }

                return SetError(ReturnCode.Success);
                }
            finally {
                FinishWriteBatch(ownsBatch);
                }
            }
        }

    internal void WriteDirectory(FileQueueItem item) {
        RequireDirectory(item.Parent);
        ReplaceIfNeeded(item.Parent, item.Name);
        DirectoryInfo source = new DirectoryInfo(item.SourcePath);
        if (!source.Exists) {
            throw new DirectoryNotFoundException(source.FullName);
            }

        uint index = Allocate();
        using FileStream archive = OpenWrite();
        try {
            ChunkHeader slot = NewSlot(item.ID, item.Parent, item.Name, NodeType.Directory, source);
            ChunkFormat.WriteSlot(
                archive,
                index,
                ref slot,
                ReadOnlySpan<byte>.Empty,
                _state.Codec,
                chunkSize: _state.ChunkSize
                );
            VirtualFile? file = FromSource(item.ID, item.Parent, item.Name, source, true, 0, index);
            file.AppendLogicalLength(0);
            AddEntry(file);
            Commit(archive);
            RaiseProgress(item.Name, 0, 0);
            }
        catch {
            ChunkFormat.ClearSlot(archive, index, _state.ChunkSize);
            _state.FreeChunks.Add(index);
            RemoveEntry(item.ID);
            TrimFreeTail(archive);
            Commit(archive);
            throw;
            }
        }

    internal void WriteFile(FileInfo source, Guid parent, Guid id, string name) {
        if (source.FullName == _state.Path) {
            throw new InvalidOperationException("An archive cannot contain itself.");
            }

        if (!source.Exists) {
            throw new FileNotFoundException("Source file is missing.", source.FullName);
            }

        using FileStream input = source.OpenRead();
        WriteContent(input, input.Length, parent, id, name, SourceMetadata(source));
        }

    private void WriteContent(Stream input, long sourceLength, Guid parent, Guid id, string name, EntryMetadata metadata) {
        RequireDirectory(parent);
        Options.CancellationToken.ThrowIfCancellationRequested();
        ValidateInputName(name);
        ReplaceIfNeeded(parent, name);
        if (UsesStreamCompression) {
            ArchiveCompressionIO.Write(this, input, sourceLength, parent, id, name, metadata);
            return;
            }

        using FileStream archive = OpenWrite();
        VirtualFile? writtenFile = null;
        uint pendingIndex = 0;
        uint nextAllocated = 0;
        byte[] buffer = new byte[Compression != ArchiveCompression.None ? 65536 : _state.ChunkSize - ArchiveFormat.NextHeaderSize];
        int buffered = 0;
        long complete = 0;
        Stopwatch? timer = Stopwatch.StartNew();
        try {
            do {
                Options.CancellationToken.ThrowIfCancellationRequested();
                bool first = writtenFile is null;
                int capacity = _state.ChunkSize - (first ? ArchiveFormat.FirstHeaderSize : ArchiveFormat.NextHeaderSize);
                while (buffered < buffer.Length && complete + buffered < sourceLength) {
                    int read = input.Read(buffer, buffered, (int)Math.Min(buffer.Length - buffered, sourceLength - complete - buffered));
                    if (read == 0) {
                        break;
                        }

                    buffered += read;
                    }

                if (buffered == 0 && !first) {
                    throw new EndOfStreamException("Source file was shortened while being archived.");
                    }

                int bytes = Math.Min(buffered, capacity);
                byte[]? preparedCompressed = null;
                bool compressionChecked = Compression != ArchiveCompression.None && bytes > 0;
                // Probe one physical payload first. Incompressible input avoids
                // repeated large compression attempts as the window slides forward.
                if (compressionChecked) {
                    byte[] probe = ChunkCompression.Compress(buffer.AsSpan(0, bytes), Compression, CompressionLevel);
                    if (probe.Length < bytes) {
                        preparedCompressed = probe;
                        }

                    if (buffered > capacity && preparedCompressed is not null) {
                        // A small probe gives a useful size estimate. Starting at the
                        // full 64 KiB window and repeatedly halving it is especially
                        // expensive for data that compresses only a little.
                        long estimate = (long)(capacity - 64) * bytes / probe.Length;
                        int candidate = (int)Math.Min(buffered, estimate);
                        if (candidate > capacity + 64) {
                            for (int attempt = 0; attempt < 3 && candidate > capacity; attempt++) {
                                byte[] compressed = ChunkCompression.Compress(buffer.AsSpan(0, candidate), Compression, CompressionLevel);
                                if (compressed.Length <= capacity && compressed.Length < candidate) {
                                    bytes = candidate;
                                    preparedCompressed = compressed;
                                    break;
                                    }

                                if (candidate <= capacity * 2) {
                                    break;
                                    }

                                candidate = capacity + (candidate - capacity) / 2;
                                }
                            }
                        }
                    }

                uint index = nextAllocated == 0 ? Allocate() : nextAllocated;
                pendingIndex = index;
                nextAllocated = 0;
                uint next = complete + bytes < sourceLength ? Allocate() : 0;
                nextAllocated = next;
                ChunkHeader slot = NewSlot(
                    id,
                    parent,
                    first ? name : string.Empty,
                    first ? NodeType.File : NodeType.Continuation,
                    metadata
                    );
                slot.Sequence = first ? 0 : writtenFile!.ChunkCount;
                slot.Size = first && !IsEncrypted ? (ulong)sourceLength : 0;
                slot.Next = next;
                ChunkFormat.WriteSlot(
                    archive,
                    index,
                    ref slot,
                    buffer.AsSpan(0, bytes),
                    _state.Codec,
                    preparedCompressed,
                    compressionChecked,
                    _state.ChunkSize
                    );
                if (first) {
                    writtenFile = FromMetadata(id, parent, name, metadata, false, (ulong)sourceLength, index);
                    }
                else {
                    writtenFile!.AppendChunk(index);
                    }

                pendingIndex = 0;
                writtenFile!.AppendLogicalLength((uint)bytes);
                complete += bytes;
                buffered -= bytes;
                if (buffered > 0) {
                    Buffer.BlockCopy(buffer, bytes, buffer, 0, buffered);
                    }

                if (timer.ElapsedMilliseconds >= Options.ProgressReportFrequency) {
                    RaiseProgress(name, complete, sourceLength);
                    timer.Restart();
                    }
                }
            while (complete < sourceLength);
            Options.CancellationToken.ThrowIfCancellationRequested();
            AddEntry(writtenFile!);
            Commit(archive);
            RaiseProgress(name, complete, sourceLength);
            }
        catch {
            if (pendingIndex != 0) {
                ChunkFormat.ClearSlot(archive, pendingIndex, _state.ChunkSize);
                _state.FreeChunks.Add(pendingIndex);
                }

            if (nextAllocated != 0) {
                ChunkFormat.ClearSlot(archive, nextAllocated, _state.ChunkSize);
                _state.FreeChunks.Add(nextAllocated);
                }

            if (writtenFile is not null) {
                foreach (uint index in writtenFile.PhysicalChunks()) {
                    ChunkFormat.ClearSlot(archive, index, _state.ChunkSize);
                    _state.FreeChunks.Add(index);
                    }
                }

            RemoveEntry(id);
            TrimFreeTail(archive);
            Commit(archive);
            throw;
            }
        }

    /// <summary>Read a byte range. Continuous compression decodes from the file's beginning.</summary>
    public byte[] ReadFileRange(Guid id, long offset, int count) {
        if (offset < 0) {
            throw new ArgumentOutOfRangeException(nameof(offset));
            }

        if (count < 0) {
            throw new ArgumentOutOfRangeException(nameof(count));
            }

        lock (_state.SyncRoot) {
            CheckOpen();
            VirtualFile? file = RequireFile(id);
            if (UsesStreamCompression) {
                if ((ulong)offset >= file.SizeOriginal || count == 0) {
                    return Array.Empty<byte>();
                    }

                int length = (int)Math.Min((ulong)count, file.SizeOriginal - (ulong)offset);
                byte[] data = new byte[length];
                using ContinuousArchiveReadStream decoded = new ContinuousArchiveReadStream(this, file);
                decoded.Seek(offset, SeekOrigin.Begin);
                decoded.ReadExactly(data);
                return data;
                }

            using FileStream archive = DiskFile.OpenRead(_state.Path!);
            return ReadFileRangeCore(archive, file, offset, count);
            }
        }

    private byte[] ReadFileRangeCore(Stream archive, VirtualFile file, long offset, int count) {
        if ((ulong)offset >= file.SizeOriginal || count == 0) {
            return Array.Empty<byte>();
            }

        int length = (int)Math.Min((ulong)count, file.SizeOriginal - (ulong)offset);
        byte[] output = new byte[length];
        int copied = 0;
        long cursor = offset;
        while (copied < length) {
            uint sequence;
            int within;
            (sequence, within) = file.LocateByte((ulong)cursor);
            uint index = file.PhysicalChunk(sequence);
            byte[] payload = ChunkFormat.ReadChunk(
                archive,
                index,
                out ChunkHeader slot,
                _state.Codec,
                LogicalChunkLength(file, sequence),
                _state.ChunkSize
                );
            if (slot.Id != file.FileID || slot.Sequence != sequence) {
                throw new InvalidDataException("Chunk does not match the file index.");
                }

            if (within >= payload.Length) {
                throw new InvalidDataException("Chunk length does not cover the requested offset.");
                }

            int take = Math.Min(length - copied, payload.Length - within);
            payload.AsSpan(within, take).CopyTo(output.AsSpan(copied));
            copied += take;
            cursor += take;
            }

        return output;
        }

    /// <summary>Read a chunk payload; continuous compression returns a slice of the compressed file stream.</summary>
    public byte[] GetFileChunk(Guid id, uint sequence) {
        lock (_state.SyncRoot) {
            CheckOpen();
            VirtualFile? file = RequireFile(id);
            uint index = file.PhysicalChunk(sequence);
            using FileStream archive = DiskFile.OpenRead(_state.Path!);
            byte[] payload = ChunkFormat.ReadChunk(
                archive,
                index,
                out ChunkHeader slot,
                _state.Codec,
                LogicalChunkLength(file, sequence),
                _state.ChunkSize
                );
            if (slot.Id != id || slot.Sequence != sequence) {
                throw new InvalidDataException("Chunk does not match the file index.");
                }

            return payload;
            }
        }

    /// <summary>Verify every occupied chunk without extracting any files.</summary>
    public ReturnCode VerifyArchive() {
        lock (_state.SyncRoot) {
            CheckOpen();
            try {
                using FileStream archive = DiskFile.OpenRead(_state.Path!);
                ArchiveFormat.HeaderInfo? header = ArchiveFormat.ReadHeader(archive);
                ArchiveFormat.ReadIndex(archive, header.indexBytes, header.indexHash, _state.ChunkSize);
                long totalBytes = _state.Entries.Values.Sum(file => (long)file.ChunkCount) * _state.ChunkSize;
                if (UsesStreamCompression) {
                    totalBytes = checked(totalBytes + _state.Entries.Values.Where(file => !file.IsDirectory).Sum(file => checked((long)file.SizeOriginal)));
                    }

                long checkedBytes = 0;
                Stopwatch? progressTimer = Stopwatch.StartNew();
                foreach (VirtualFile file in _state.Entries.Values) {
                    ulong size = 0;
                    for (uint sequence = 0; sequence < file.ChunkCount; sequence++) {
                        uint index = file.PhysicalChunk(sequence);
                        byte[] payload = ChunkFormat.ReadChunk(
                            archive,
                            index,
                            out ChunkHeader slot,
                            _state.Codec,
                            LogicalChunkLength(file, sequence),
                            _state.ChunkSize
                            );
                        uint expectedNext = sequence + 1 < file.ChunkCount ? file.PhysicalChunk(sequence + 1) : 0;
                        if (payload.Length != LogicalChunkLength(file, sequence) || slot.Id != file.FileID || slot.Sequence != sequence || slot.Next != expectedNext || (sequence == 0 && slot.Type != (file.IsDirectory ? NodeType.Directory : NodeType.File)) || (sequence > 0 && slot.Type != NodeType.Continuation)) {
                            throw new InvalidDataException($"Chunk chain mismatch for {file.Name}.");
                            }

                        size += (uint)LogicalChunkLength(file, sequence);
                        checkedBytes += _state.ChunkSize;
                        if (progressTimer.ElapsedMilliseconds >= Options.ProgressReportFrequency) {
                            RaiseProgress("verify", checkedBytes, totalBytes);
                            progressTimer.Restart();
                            }
                        }

                    if (!UsesStreamCompression && size != file.SizeOriginal) {
                        throw new InvalidDataException($"Size mismatch for {file.Name}.");
                        }

                    if (UsesStreamCompression && !file.IsDirectory) {
                        using ContinuousArchiveReadStream decoded = new ContinuousArchiveReadStream(this, file);
                        byte[] buffer = new byte[64 * 1024];
                        long remaining = checked((long)file.SizeOriginal);
                        while (remaining > 0) {
                            int read = decoded.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read == 0) {
                                throw new InvalidDataException($"Compressed file {file.Name} ended early.");
                                }

                            remaining -= read;
                            checkedBytes += read;
                            if (progressTimer.ElapsedMilliseconds >= Options.ProgressReportFrequency) {
                                RaiseProgress("verify", checkedBytes, totalBytes);
                                progressTimer.Restart();
                                }
                            }

                        decoded.ValidateEnd();
                        }
                    }

                RaiseProgress("verify", checkedBytes, totalBytes);
                return SetError(ReturnCode.Success);
                }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException) {
                return SetError(ReturnCode.Failed | ReturnCode.HashMismatch);
                }
            }
        }

    public Stream OpenRead(Guid id) {
        lock (_state.SyncRoot) {
            CheckOpen();
            VirtualFile? file = RequireFile(id);
            return UsesStreamCompression ? new ContinuousArchiveReadStream(this, file) : new ArchiveReadStream(this, file);
            }
        }

    /// <summary>Copy a file range through a fixed-size buffer without materializing the range.</summary>
    public long CopyFileRange(Guid id, Stream destination, long offset, long length, int bufferSize = 64 * 1024) {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) {
            throw new ArgumentException("Destination must be writable.", nameof(destination));
            }

        if (offset < 0) {
            throw new ArgumentOutOfRangeException(nameof(offset));
            }

        if (length < 0) {
            throw new ArgumentOutOfRangeException(nameof(length));
            }

        if (bufferSize <= 0) {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
            }

        using Stream source = OpenRead(id);
        if (offset >= source.Length || length == 0) {
            return 0;
            }

        source.Seek(offset, SeekOrigin.Begin);
        long remaining = Math.Min(length, source.Length - offset);
        byte[] buffer = new byte[(int)Math.Min(bufferSize, remaining)];
        long copied = 0;
        while (remaining > 0) {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, wanted);
            if (read == 0) {
                throw new EndOfStreamException("Archive file ended before its indexed length.");
                }

            destination.Write(buffer, 0, read);
            copied += read;
            remaining -= read;
            }

        return copied;
        }

    public ReturnCode ExtractFile(Guid id, FileInfo destination, bool? overwrite = null) {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_state.SyncRoot) {
            VirtualFile? file = RequireFile(id);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(destination.FullName, _state.Path, comparison)) {
                throw new InvalidOperationException("The extraction destination cannot be the archive itself.");
                }

            bool outputOpened = false;
            try {
                Options.CancellationToken.ThrowIfCancellationRequested();
                using (Stream? source = OpenRead(id)) {
                    FileMode mode = overwrite ?? Options.OverrideByDefault ? FileMode.Create : FileMode.CreateNew;
                    using (FileStream output = new FileStream(destination.FullName, mode, FileAccess.Write, FileShare.None)) {
                        outputOpened = true;
                        byte[] buffer = new byte[64 * 1024];
                        long completed = 0;
                        while (true) {
                            Options.CancellationToken.ThrowIfCancellationRequested();
                            int count = source.Read(buffer, 0, buffer.Length);
                            if (count == 0) {
                                break;
                                }

                            output.Write(buffer, 0, count);
                            completed += count;
                            RaiseProgress(file.Name, completed, (long)file.SizeOriginal);
                            }
                        }
                    }

                Options.CancellationToken.ThrowIfCancellationRequested();
                DiskFile.SetCreationTimeUtc(destination.FullName, file.CreationDateUtc);
                DiskFile.SetLastWriteTimeUtc(destination.FullName, file.ModificationDateUtc);
                DiskFile.SetLastAccessTimeUtc(destination.FullName, file.AccessDateUtc);
                return SetError(ReturnCode.Success);
                }
            catch {
                // Output is written directly. Remove only the file this call
                // actually opened; never remove a target that failed to open.
                if (outputOpened) {
                    DiskFile.Delete(destination.FullName);
                    }

                throw;
                }
            }
        }

    public ReturnCode ExtractDirectory(Guid id, DirectoryInfo destination, bool? overwrite = null) {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_state.SyncRoot) {
            RequireDirectory(id);
            destination.Create();
            foreach (VirtualFile child in DirectoryList(id)) {
                ValidateDestinationName(child.Name);
                string path = Path.Combine(destination.FullName, child.Name);
                if (child.IsDirectory) {
                    ExtractDirectory(child.FileID, new DirectoryInfo(path), overwrite);
                    }
                else {
                    ExtractFile(child.FileID, new FileInfo(path), overwrite);
                    }
                }

            return SetError(ReturnCode.Success);
            }
        }

    /// <summary>Remove a file or a directory and all its descendants, leaving reusable chunk slots.</summary>
    public ReturnCode DeleteFileFromArchive(Guid id) {
        lock (_state.SyncRoot) {
            CheckOpen();
            if (!_state.Entries.ContainsKey(id)) {
                return SetError(ReturnCode.Failed | ReturnCode.GuidNotFound);
                }

            using FileStream archive = OpenWrite();
            DeleteRecursive(id, archive);
            Commit(archive);
            return SetError(ReturnCode.Success);
            }
        }

    private void DeleteRecursive(Guid id, Stream archive) {
        foreach (VirtualFile child in _state.Entries.Values.Where(f => f.OwnerID == id).ToArray()) {
            DeleteRecursive(child.FileID, archive);
            }

        VirtualFile? file = _state.Entries[id];
        foreach (ChunkExtent extent in file.ChunkExtents) {
            _state.FreeChunks.AddRange(extent.FirstChunk, extent.Count);
            }

        RemoveEntry(id);
        }

    /// <summary>Fill low holes from the tail, fix chain links, then truncate trailing free chunks.</summary>
    public ReturnCode CompactArchive() {
        lock (_state.SyncRoot) {
            CheckOpen();
            using FileStream archive = OpenWrite();
            TrimFreeTail(archive);
            while (_state.FreeChunks.Count > 0) {
                uint hole = _state.FreeChunks.Min;
                uint runLength = _state.FreeChunks.FirstRunLength;
                VirtualFile? candidate = _state.Entries.Values.Where(f => f.ChunkExtents.Count == 1 && f.ChunkCount <= runLength && f.ChunkExtents[0].FirstChunk > (ulong)hole + runLength - 1).OrderByDescending(f => f.ChunkCount).ThenByDescending(f => f.FirstChunk).FirstOrDefault();
                if (candidate is not null) {
                    MoveWholeFile(archive, candidate, hole);
                    continue;
                    }

                uint tail = _state.ChunkCount;
                while (tail > _state.IndexChunks && _state.FreeChunks.Contains(tail)) {
                    tail--;
                    }

                if (tail <= hole) {
                    break;
                    }

                byte[] raw = new byte[_state.ChunkSize];
                archive.Position = (long)tail * _state.ChunkSize;
                archive.ReadExactly(raw);
                ChunkHeader moved = ChunkFormat.ReadSlot(archive, tail, _state.ChunkSize) ?? throw new InvalidDataException("Compaction found an empty source chunk.");
                archive.Position = (long)hole * _state.ChunkSize;
                archive.Write(raw);
                VirtualFile? file = _state.Entries[moved.Id];
                if (file.PhysicalChunk(moved.Sequence) != tail) {
                    throw new InvalidDataException("Moved chunk is missing from its file index.");
                    }

                if (moved.Sequence > 0) {
                    ChunkFormat.RewriteNext(
                        archive,
                        file.PhysicalChunk(moved.Sequence - 1),
                        hole,
                        _state.ChunkSize
                        );
                    }

                file.RelocateChunk(moved.Sequence, hole);
                ChunkFormat.ClearSlot(archive, tail, _state.ChunkSize);
                _state.FreeChunks.Remove(hole);
                _state.FreeChunks.Add(tail);
                }

            TrimFreeTail(archive);
            Commit(archive);
            return SetError(ReturnCode.Success);
            }
        }

    private void MoveWholeFile(Stream archive, VirtualFile file, uint destination) {
        uint source = file.ChunkExtents[0].FirstChunk;
        uint count = file.ChunkCount;
        byte[] raw = new byte[_state.ChunkSize];
        for (uint i = 0; i < count; i++) {
            archive.Position = ((long)source + i) * _state.ChunkSize;
            archive.ReadExactly(raw);
            archive.Position = ((long)destination + i) * _state.ChunkSize;
            archive.Write(raw);
            ChunkFormat.RewriteNext(
                archive,
                destination + i,
                i + 1 < count ? destination + i + 1 : 0,
                _state.ChunkSize
                );
            }

        for (uint i = 0; i < count; i++) {
            ChunkFormat.ClearSlot(archive, source + i, _state.ChunkSize);
            _state.FreeChunks.Add(source + i);
            _state.FreeChunks.Remove(destination + i);
            }

        file.FirstChunk = destination;
        file.SetExtents(new[] { new ChunkExtent(0, destination, count) });
        }

    internal void TrimFreeTail(Stream archive) {
        _state.FreeChunks.TrimTail(ref _state.ChunkCount, _state.IndexChunks);
        archive.SetLength(((long)_state.ChunkCount + 1) * _state.ChunkSize);
        }

    private void ReplaceIfNeeded(Guid parent, string name) {
        VirtualFile? existing;
        if (_state.CurrentBatch is not null) {
            existing = _state.CurrentBatch.Names.TryGetValue((parent, name), out Guid id) ? _state.Entries[id] : null;
            }
        else {
            existing = _state.Entries.Values.FirstOrDefault(file => file.OwnerID == parent && file.Name == name);
            }
        if (existing is null) {
            return;
            }

        if (!Options.OverrideByDefault) {
            throw new IOException($"{name} already exists in this directory.");
            }

        DeleteFileFromArchive(existing.FileID);
        }

    internal readonly record struct EntryMetadata(DateTime Created, DateTime Modified, DateTime Accessed, Attribs Attributes);
    private static EntryMetadata SourceMetadata(FileSystemInfo source) => new(
        source.CreationTimeUtc,
        source.LastWriteTimeUtc,
        source.LastAccessTimeUtc,
        MapAttributes(source.Attributes)
        );
    internal ChunkHeader NewSlot(Guid id, Guid parent, string name, NodeType kind, FileSystemInfo source) => NewSlot(id, parent, name, kind, SourceMetadata(source));
    internal ChunkHeader NewSlot(Guid id, Guid parent, string name, NodeType kind, EntryMetadata metadata) => IsEncrypted ? new ChunkHeader {
        Type = kind,
        Id = id,
        Parent = Guid.Empty,
        Name = string.Empty,
        NameInIndex = kind != NodeType.Continuation,
        CreatedUtc = DateTime.MinValue,
        ModifiedUtc = DateTime.MinValue,
        AccessedUtc = DateTime.MinValue,
        Attributes = 0
        }

    : new ChunkHeader {
        Type = kind,
        Id = id,
        Parent = parent,
        Name = name,
        NameInIndex = kind != NodeType.Continuation && System.Text.Encoding.UTF8.GetByteCount(name) > ArchiveFormat.InlineNameBytes * 2,
        WideName = kind != NodeType.Continuation && System.Text.Encoding.UTF8.GetByteCount(name) is > ArchiveFormat.InlineNameBytes and <= ArchiveFormat.InlineNameBytes * 2,
        CreatedUtc = metadata.Created,
        ModifiedUtc = metadata.Modified,
        AccessedUtc = metadata.Accessed,
        Attributes = (ushort)metadata.Attributes
        };
    private static Attribs MapAttributes(FileAttributes attributes) {
        Attribs result = 0;
        if (attributes.HasFlag(FileAttributes.ReadOnly)) {
            result |= Attribs.ReadOnly;
            }

        if (attributes.HasFlag(FileAttributes.Archive)) {
            result |= Attribs.Archive;
            }

        if (attributes.HasFlag(FileAttributes.System)) {
            result |= Attribs.System;
            }

        if (attributes.HasFlag(FileAttributes.Hidden)) {
            result |= Attribs.Hidden;
            }

        return result;
        }

    private int LogicalChunkLength(VirtualFile file, uint sequence) {
        return file.LogicalLength(sequence);
        }

    private static VirtualFile FromSource(Guid id, Guid parent, string name, FileSystemInfo source, bool directory, ulong size, uint first) => FromMetadata(id, parent, name, SourceMetadata(source), directory, size, first);
    internal static VirtualFile FromMetadata(Guid id, Guid parent, string name, EntryMetadata metadata, bool directory, ulong size, uint first) {
        VirtualFile file = new VirtualFile(
            id,
            parent,
            name,
            directory,
            size,
            metadata.Created,
            metadata.Modified,
            metadata.Accessed,
            metadata.Attributes,
            first
            );
        file.AppendChunk(first);
        return file;
        }

    internal uint Allocate() {
        if (_state.FreeChunks.Count > 0) {
            uint result = _state.FreeChunks.Min;
            _state.FreeChunks.Remove(result);
            return result;
            }

        if (_state.ChunkCount == uint.MaxValue) {
            throw new IOException("Archive has reached its chunk limit.");
            }

        return ++_state.ChunkCount;
        }

    internal void Commit(Stream archive) {
        if (_state.CurrentBatch is not null) {
            long indexBytes = checked(16 + _state.CurrentBatch.EntryBytes + 8L * _state.FreeChunks.Ranges.Count);
            if (indexBytes <= (long)_state.IndexChunks * _state.ChunkSize) {
                return;
                }
            }

        byte[] index = ArchiveIndex.Serialize(_state.Entries.Values, _state.FreeChunks);
        while ((ulong)index.Length > (ulong)_state.IndexChunks * (ulong)_state.ChunkSize) {
            ExpandIndex(archive, index.Length);
            index = ArchiveIndex.Serialize(_state.Entries.Values, _state.FreeChunks);
            }

        WriteIndexAndHeader(archive, index);
        archive.Flush();
        _state.CurrentBatch?.Reset(_state.Entries.Values);
        }

    internal void AddEntry(VirtualFile entry) {
        _state.Entries.Add(entry.FileID, entry);
        _state.CurrentBatch?.Add(entry);
        }

    internal void RemoveEntry(Guid id) {
        if (_state.Entries.Remove(id, out VirtualFile? entry)) {
            _state.CurrentBatch?.Remove(entry);
            }
        }

    internal bool BeginWriteBatch() {
        if (_state.CurrentBatch is not null) {
            return false;
            }

        _state.CurrentBatch = new WriteBatch(_state.Entries.Values);
        return true;
        }

    internal void FinishWriteBatch(bool ownsBatch) {
        if (ownsBatch) {
            _state.CurrentBatch = null;
            using FileStream archive = OpenWrite();
            Commit(archive);
            }
        }

    private void WriteIndexAndHeader(Stream archive, byte[] plainIndex) {
        (byte[]? stored, byte[]? nonce, byte[]? tag) = _state.Codec?.EncryptIndex(plainIndex) ?? (plainIndex, new byte[12], new byte[16]);
        ArchiveFormat.WriteIndex(archive, _state.IndexChunks, stored, _state.ChunkSize);
        ArchiveFormat.WriteHeader(
            archive,
            ArchiveGuid,
            RootDirectoryId,
            _state.ChunkCount,
            (uint)_state.Entries.Count,
            _state.IndexChunks,
            stored,
            ArchiveFormat.CurrentVersion,
            _state.StorageFlags,
            _state.Salt,
            _state.KdfIterations,
            nonce,
            tag,
            Compression,
            CompressionLevel,
            _state.ChunkSize
            );
        }

    private void ExpandIndex(Stream archive, int requiredBytes) {
        uint oldCount = _state.IndexChunks;
        uint required = checked((uint)((requiredBytes + _state.ChunkSize - 1) / _state.ChunkSize));
        uint newCount = Math.Max(required, checked(oldCount * 2));
        if (newCount >= uint.MaxValue) {
            throw new IOException("Archive index is too large.");
            }

        uint originalCount = _state.ChunkCount;
        if (_state.ChunkCount < newCount) {
            _state.ChunkCount = newCount;
            archive.SetLength(((long)_state.ChunkCount + 1) * _state.ChunkSize);
            }

        for (uint index = oldCount + 1; index <= newCount; index++) {
            if (index > originalCount) {
                continue;
                }

            if (_state.FreeChunks.Remove(index)) {
                continue;
                }

            ChunkHeader slot = ChunkFormat.ReadSlot(archive, index, _state.ChunkSize) ?? throw new InvalidDataException($"Index expansion found an untracked free chunk at {index}.");
            VirtualFile? file = _state.Entries[slot.Id];
            if (file.PhysicalChunk(slot.Sequence) != index) {
                throw new InvalidDataException("Index expansion cannot locate a data chunk.");
                }

            uint target = ++_state.ChunkCount;
            byte[] raw = new byte[_state.ChunkSize];
            archive.Position = (long)index * _state.ChunkSize;
            archive.ReadExactly(raw);
            archive.Position = (long)target * _state.ChunkSize;
            archive.Write(raw);
            if (slot.Sequence > 0) {
                ChunkFormat.RewriteNext(
                    archive,
                    file.PhysicalChunk(slot.Sequence - 1),
                    target,
                    _state.ChunkSize
                    );
                }

            file.RelocateChunk(slot.Sequence, target);
            }

        _state.IndexChunks = newCount;
        }

    internal FileStream OpenWrite() => new(_state.Path!, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 256 * 1024);
    internal ReturnCode SetError(ReturnCode code) {
        ErrorStatus = code;
        return code;
        }

    internal void CheckUsable() {
        if (_state.Disposed) {
            throw new ObjectDisposedException(nameof(ArchiveFile));
            }
        }

    internal void CheckOpen() {
        CheckUsable();
        if (!IsOpen) {
            throw new InvalidOperationException("Create or open an archive first.");
            }
        }

    internal Guid NormalizeParent(Guid id) {
        Guid owner = id == Guid.Empty ? RootDirectoryId : id;
        if (owner != RootDirectoryId && !_state.QueueItems.Any(x => x.ID == owner && x.IsDirectory)) {
            RequireDirectory(owner);
            }

        return owner;
        }

    private void RequireDirectory(Guid id) {
        if (id != RootDirectoryId && (!_state.Entries.TryGetValue(id, out VirtualFile? file) || !file.IsDirectory)) {
            throw new DirectoryNotFoundException($"Directory {id} was not found in this archive.");
            }
        }

    private VirtualFile RequireFile(Guid id) {
        if (!_state.Entries.TryGetValue(id, out VirtualFile? file) || file.IsDirectory) {
            throw new FileNotFoundException($"File {id} was not found in this archive.");
            }

        return file;
        }

    public static bool IsArchive(FileInfo source) {
        return ArchiveFormat.HasMagic(source);
        }

    public static void ValidateDestinationName(string name) {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
            throw new IOException($"This operating system cannot extract the preserved filename: {name}");
            }

        if (OperatingSystem.IsWindows()) {
            string stem = name.Split('.')[0].ToUpperInvariant();
            bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
            if (reserved || name.EndsWith(' ') || name.EndsWith('.')) {
                throw new IOException($"Windows cannot extract this name without changing it: {name}");
                }
            }
        }

    internal void ValidateInputName(string name) {
        ArchiveFormat.ValidateName(name);
        }

    internal void RaiseProgress(string name, long completed, long total) => ProgressReport?.Invoke(
        this,
        new OperationProgressArgs { ErrorLevel = ReturnCode.Success, FileName = name, BytesMin = 0, BytesMax = total, BytesComplete = completed, Progress = total == 0 ? 1 : (float)completed / total }
        );
    public void Dispose() {
        Close();
        _state.Disposed = true;
        }

    }
