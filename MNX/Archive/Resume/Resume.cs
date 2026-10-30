using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using DiskNodeFlags = Mash.IO.Format.MNX.Chunk.NodeFlags;

namespace Mash.IO.Format.MNX.Archive.Resume;


internal sealed record QueueEnvelope(string Format, string ChecksumAlgorithm, string PayloadSha256, QueuePayload Payload);

[JsonSerializable(typeof(QueuePayload))]
[JsonSerializable(typeof(QueueEnvelope))]
internal partial class QueueJson : JsonSerializerContext {
    }


/// <summary>Read and write checksummed archive resume sidecars.</summary>
public static class RemainingQueue {
    /// <summary>Identify our JSON queue signature without reading the entire file.</summary>
    public static bool IsQueue(FileInfo file) {
        using FileStream input = file.OpenRead();
        byte[] prefix = new byte[512];
        int count = input.Read(prefix);
        try {
            Utf8JsonReader reader = new Utf8JsonReader(prefix.AsSpan(0, count), isFinalBlock: false, state: default);
            while (reader.Read()) {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("Format")) {
                    return reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("MNQ1");
                    }
                }
            }
        catch (JsonException) {
            return false;
            }

        return false;
        }

    /// <summary>Read and checksum a queue without opening its archive or reading file payloads.</summary>
    public static QueuePayload Read(FileInfo queueFile) {
        using FileStream input = queueFile.OpenRead();
        using JsonDocument json = JsonDocument.Parse(input);
        JsonElement envelope = json.RootElement;
        if (!envelope.TryGetProperty("Format", out JsonElement format) || format.GetString() != "MNQ1" || !envelope.TryGetProperty("ChecksumAlgorithm", out JsonElement algorithm) || algorithm.GetString() != "SHA256" || !envelope.TryGetProperty("Payload", out JsonElement payload) || !envelope.TryGetProperty("PayloadSha256", out JsonElement checksum)) {
            throw new InvalidDataException("This is not an MNQ1 remaining queue.");
            }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload.GetRawText());
        if (!string.Equals(
            Convert.ToHexString(SHA256.HashData(bytes)),
            checksum.GetString(),
            StringComparison.OrdinalIgnoreCase
            )) {
            throw new InvalidDataException("Remaining queue checksum does not match. No files were added.");
            }

        QueuePayload? result = payload.Deserialize(QueueJson.Default.QueuePayload);
        if (result?.Archive is null || result.Entries is null || result.Entries.Any(entry => entry is null)) {
            throw new InvalidDataException("Remaining queue metadata is incomplete.");
            }

        return result;
        }

    /// <summary>Locate the original archive, or its named sibling when both files were moved together.</summary>
    public static string LocateArchive(FileInfo queueFile, QueuePayload queue) {
        string name = queue.Archive.Name;
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
            throw new InvalidDataException("Queue archive name is invalid.");
            }

        string sibling = Path.Combine(queueFile.DirectoryName!, name);
        if (DiskFile.Exists(sibling)) {
            return sibling;
            }

        if (!Path.IsPathFullyQualified(queue.Archive.FullPath) || !DiskFile.Exists(queue.Archive.FullPath)) {
            throw new FileNotFoundException(
                "The queue's archive was not found. Move the queue beside its archive, or supply its current path.",
                queue.Archive.FullPath
                );
            }

        return queue.Archive.FullPath;
        }

    internal static void ValidateArchive(string path, QueueArchive expected) {
        QueueArchive? actual = Fingerprint(path);
        if (actual.ArchiveId != expected.ArchiveId || actual.RootDirectoryId != expected.RootDirectoryId || actual.FormatVersion != expected.FormatVersion || actual.ChunkSize != expected.ChunkSize || actual.Length != expected.Length || actual.IndexBytes != expected.IndexBytes || actual.IndexChunks != expected.IndexChunks || !string.Equals(actual.HeaderSha256, expected.HeaderSha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(actual.IndexSha256, expected.IndexSha256, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("The archive has changed since this queue was saved, or this queue belongs to a different archive. No files were added.");
            }
        }

    /// <summary>Retain a completed queue as a record, removing its active filename.</summary>
    public static string MarkCompleted(FileInfo queueFile) {
        if (ResumeKey.Exists(queueFile)) {
            throw new InvalidOperationException("Consume the validated queue's temporary key before marking it completed.");
            }

        string path = queueFile.FullName + ".completed-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        DiskFile.Move(queueFile.FullName, path);
        return path;
        }

    /// <summary>Discard cancelled recovery work, overwriting any temporary password key before deleting it.</summary>
    public static void Discard(FileInfo queueFile) {
        ResumeKey.Consume(queueFile);
        DiskFile.Delete(queueFile.FullName);
        }

    /// <summary>Save remaining work; an explicitly supplied password also exports a temporary key sidecar.</summary>
    public static string Save(string archivePath, IEnumerable<FileQueueItem> remaining) {
        return Save(archivePath, remaining, exportPassword: null);
        }

    /// <summary>Save remaining work with an explicitly requested temporary password export.</summary>
    public static string Save(string archivePath, IEnumerable<FileQueueItem> remaining, string? exportPassword) {
        byte[]? password = exportPassword is null ? null : System.Text.Encoding.UTF8.GetBytes(exportPassword);
        try {
            return SaveWithPassword(archivePath, remaining, password);
            }
        finally {
            if (password is not null) {
                CryptographicOperations.ZeroMemory(password);
                }
            }
        }

    internal static string SaveWithPassword(string archivePath, IEnumerable<FileQueueItem> remaining, byte[]? password) {
        archivePath = Path.GetFullPath(archivePath);
        QueueArchive identity = Fingerprint(archivePath);
        RemainingEntry[] entries = remaining.Select(Describe).ToArray();
        QueuePayload payload = new QueuePayload(DateTime.UtcNow, identity, entries);
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, QueueJson.Default.QueuePayload);
        QueueEnvelope envelope = new QueueEnvelope("MNQ1", "SHA256", Convert.ToHexString(SHA256.HashData(payloadBytes)), payload);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, QueueJson.Default.QueueEnvelope);
        string path = Path.ChangeExtension(archivePath, ".mnq");
        string keyPath = Path.ChangeExtension(path, ".mnx-key");
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (entries.Any(entry => string.Equals(entry.SourcePath, path, comparison) || string.Equals(entry.SourcePath, keyPath, comparison))) {
            throw new IOException("A remaining input file would be overwritten by this archive's recovery sidecars.");
            }

        string temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try {
            FileStreamOptions options = new FileStreamOptions {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write
                };
            if (!OperatingSystem.IsWindows()) {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

            using (FileStream output = new FileStream(temporary, options)) {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
                }

            // Retain old work lists, but never back up an old password sidecar.
            if (DiskFile.Exists(path)) {
                string backup = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileNameWithoutExtension(path) + ".previous-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".mnq"
                    );
                DiskFile.Copy(path, backup);
                if (!OperatingSystem.IsWindows()) {
                    DiskFile.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }

            ResumeKey.RemoveIfPresent(new FileInfo(Path.ChangeExtension(path, ".mnx-key")));
            DiskFile.Move(temporary, path, overwrite: true);
            if (password is not null) {
                ResumeKey.Export(new FileInfo(path), archivePath, password);
                }

            return path;
            }
        finally {
            if (DiskFile.Exists(temporary)) {
                DiskFile.Delete(temporary);
                }
            }
        }

    private static QueueArchive Fingerprint(string archivePath) {
        using (FileStream? input = DiskFile.OpenRead(archivePath)) {
            ArchiveFormat.HeaderInfo? validatedHeader = ArchiveFormat.ReadHeader(input);
            int chunkSize = validatedHeader.chunkSize;
            byte[] header = new byte[chunkSize];
            input.Position = 0;
            input.ReadExactly(header);
            uint indexChunks = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(56));
            uint indexBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(60));
            if (indexBytes > (ulong)indexChunks * (uint)chunkSize || chunkSize + (long)indexBytes > input.Length) {
                throw new InvalidDataException("Archive front-index length is invalid.");
                }

            using IncrementalHash indexHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long left = indexBytes;
            while (left > 0) {
                int count = (int)Math.Min(left, buffer.Length);
                input.ReadExactly(buffer.AsSpan(0, count));
                indexHash.AppendData(buffer, 0, count);
                left -= count;
                }

            byte[] indexDigest = indexHash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(indexDigest, validatedHeader.indexHash)) {
                throw new InvalidDataException("Archive front-index checksum does not match.");
                }

            return new QueueArchive(
                Path.GetFileName(archivePath),
                archivePath,
                new Guid(header.AsSpan(12, 16)),
                new Guid(header.AsSpan(28, 16)),
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)),
                chunkSize,
                input.Length,
                indexChunks,
                indexBytes,
                Convert.ToHexString(SHA256.HashData(header)),
                Convert.ToHexString(indexDigest)
                );
            }
        }

    private static RemainingEntry Describe(FileQueueItem item) {
        long? size = null;
        DateTime? modified = null;
        try {
            FileSystemInfo source = item.IsDirectory ? new DirectoryInfo(item.SourcePath) : new FileInfo(item.SourcePath);
            if (source.Exists) {
                modified = source.LastWriteTimeUtc;
                size = source is FileInfo file ? file.Length : 0;
                }
            }
        catch (IOException) {
        // Missing or disconnected sources remain in the queue; resume can
        // report them without losing their original paths and destination IDs.
            }
        catch (UnauthorizedAccessException) {
            }

        return new RemainingEntry(item.ID, item.Parent, item.Name, item.SourcePath, item.IsDirectory, size, modified);
        }
    }

/// <summary>A temporary convenience wrapper bound to a saved archive and work queue.</summary>
internal static class ResumeKey {
    // These inputs are public. This wrapper authenticates the saved state and
    // avoids storing a literal password; it is not an independent secret vault.
    private static readonly byte[] Domain = "MNX temporary resume password v1"u8.ToArray();
    private const int PrefixSize = 40;
    private const int MaxPasswordBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static FileInfo KeyFile(FileInfo queue) => new(Path.ChangeExtension(queue.FullName, ".mnx-key"));
    internal static bool Exists(FileInfo queueFile) => Present(KeyFile(queueFile));
    internal static void Export(FileInfo queueFile, string archivePath, byte[] password) {
        if (password.Length == 0 || password.Length > MaxPasswordBytes) {
            throw new ArgumentException("Temporary password export supports 1 to 1,048,576 UTF-8 bytes.");
            }

        (byte[] Salt, byte[] Aad) state = Binding(queueFile, archivePath);
        byte[] key = Derive(state.Salt);
        byte[] bytes = new byte[PrefixSize + password.Length];
        "MNXKEY1\0"u8.CopyTo(bytes);
        RandomNumberGenerator.Fill(bytes.AsSpan(8, 12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36, 4), password.Length);
        FileInfo? file = KeyFile(queueFile);
        string temporary = file.FullName + ".pending-" + Guid.NewGuid().ToString("N");
        try {
            using AesGcm aes = new AesGcm(key, 16);
            aes.Encrypt(bytes.AsSpan(8, 12), password, bytes.AsSpan(PrefixSize), bytes.AsSpan(20, 16), state.Aad);
            FileStreamOptions options = new FileStreamOptions {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
                };
            if (!OperatingSystem.IsWindows()) {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

            using (FileStream output = new FileStream(temporary, options)) {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
                }

            DiskFile.Move(temporary, file.FullName, overwrite: false);
            }
        finally {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(bytes);
            RemoveIfPresent(new FileInfo(temporary));
            }
        }

    internal static byte[]? Read(FileInfo queueFile, string archivePath) {
        FileInfo? file = KeyFile(queueFile);
        if (!Present(file)) {
            return null;
            }

        RequireRegular(file);
        (byte[] Salt, byte[] Aad) state = Binding(queueFile, archivePath);
        byte[] key = Derive(state.Salt);
        byte[]? password = null;
        try {
            using FileStream input = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length <= PrefixSize || input.Length > PrefixSize + MaxPasswordBytes) {
                throw new InvalidDataException("Temporary MNX key file has an invalid length.");
                }

            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (!bytes.AsSpan(0, 8).SequenceEqual("MNXKEY1\0"u8) || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(36, 4)) != bytes.Length - PrefixSize) {
                throw new InvalidDataException("Temporary MNX key file has an invalid signature or password length.");
                }

            password = new byte[bytes.Length - PrefixSize];
            using AesGcm aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(8, 12), bytes.AsSpan(PrefixSize), bytes.AsSpan(20, 16), password, state.Aad);
            // Refuse malformed UTF-8 instead of substituting password characters.
            _ = Utf8.GetCharCount(password);
            return password;
            }
        catch {
            if (password is not null) {
                CryptographicOperations.ZeroMemory(password);
                }

            throw;
            }
        finally {
            CryptographicOperations.ZeroMemory(key);
            }
        }

    internal static string Decode(byte[] password) => Utf8.GetString(password);
    private static (byte[] Salt, byte[] Aad) Binding(FileInfo queueFile, string archivePath) {
        QueuePayload queue = RemainingQueue.Read(queueFile);
        RemainingQueue.ValidateArchive(archivePath, queue.Archive);
        using FileStream archive = DiskFile.OpenRead(archivePath);
        ArchiveFormat.HeaderInfo? header = ArchiveFormat.ReadHeader(archive);
        if (!header.flags.HasFlag(DiskNodeFlags.Encrypted)) {
            throw new InvalidDataException("Temporary password keys require an encrypted archive.");
            }

        byte[] salt = new byte[32];
        archive.Position = 144;
        archive.ReadExactly(salt);
        byte[] aad = new byte[Domain.Length + 16 + 32 + 32];
        Domain.CopyTo(aad, 0);
        header.archiveId.TryWriteBytes(aad.AsSpan(Domain.Length, 16));
        Convert.FromHexString(queue.Archive.HeaderSha256).CopyTo(aad, Domain.Length + 16);
        using FileStream input = queueFile.OpenRead();
        SHA256.HashData(input).CopyTo(aad, Domain.Length + 48);
        return (salt, aad);
        }

    private static byte[] Derive(byte[] salt) {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Domain, 32, salt, "MNXKEY1 AES-256-GCM"u8.ToArray());
        }

    private static bool Present(FileInfo file) {
        file.Refresh();
        return file.Exists || file.LinkTarget is not null;
        }

    private static void RequireRegular(FileInfo file) {
        if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || file.LinkTarget is not null) {
            throw new IOException("Refusing a linked or non-file temporary MNX key: " + file.FullName);
            }
        }

    internal static void RemoveIfPresent(FileInfo file) {
        if (!Present(file)) {
            return;
            }

        RequireRegular(file);
        using (FileStream? output = file.Open(FileMode.Open, FileAccess.Write, FileShare.None)) {
            byte[] buffer = new byte[64 * 1024];
            long remaining = output.Length;
            while (remaining > 0) {
                int count = (int)Math.Min(remaining, buffer.Length);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                output.Write(buffer, 0, count);
                remaining -= count;
                }

            output.Flush(flushToDisk: true);
            }

        DiskFile.Delete(file.FullName);
        }

    internal static void Consume(FileInfo queueFile) => RemoveIfPresent(KeyFile(queueFile));
    }


/// <summary>Read an explicitly exported convenience password for interrupted archive work.</summary>
public static class ResumePassword {
    /// <summary>Validate the saved archive/queue and decrypt its optional temporary password without deleting it.</summary>
    public static string? Read(FileInfo queueFile, string archivePath) {
        byte[]? password = ResumeKey.Read(queueFile, archivePath);
        if (password is null) {
            return null;
            }

        try {
            return ResumeKey.Decode(password);
            }
        finally {
            CryptographicOperations.ZeroMemory(password);
            }
        }
    }


/// <summary>Validate, save and consume remaining queues and temporary keys for one archive.</summary>
public sealed class ArchiveRecovery {
    private readonly ArchiveFile _owner;
    private readonly ArchiveState _state;
    internal ArchiveRecovery(ArchiveFile owner) {
        _owner = owner;
        _state = owner.State;
        }

    /// <summary>Validate the entire saved batch, then restore its IDs and queue order without writing archive data.</summary>
    public ArchiveFile.ReturnCode LoadRemainingQueue(FileInfo queueFile) {
        lock (_state.SyncRoot) {
            _owner.CheckOpen();
            if (_state.QueueItems.Count > 0) {
                throw new InvalidOperationException("Finish or clear the existing queue before loading a remaining queue.");
                }

            QueuePayload saved = RemainingQueue.Read(queueFile);
            RemainingQueue.ValidateArchive(_state.Path!, saved.Archive);
            HashSet<Guid>? ids = _state.Entries.Keys.Append(_owner.RootDirectoryId).ToHashSet();
            HashSet<Guid>? folders = _state.Entries.Values.Where(entry => entry.IsDirectory).Select(entry => entry.FileID).Append(_owner.RootDirectoryId).ToHashSet();
            HashSet<(Guid OwnerID, string Name)>? names = _state.Entries.Values.Select(entry => (entry.OwnerID, entry.Name)).ToHashSet();
            List<FileQueueItem> restored = new List<FileQueueItem>();
            foreach (RemainingEntry entry in saved.Entries) {
                _owner.Options.CancellationToken.ThrowIfCancellationRequested();
                ArchiveFormat.ValidateName(entry.Name);
                if (entry.EntryId == Guid.Empty || !ids.Add(entry.EntryId) || !folders.Contains(entry.ParentId) || !names.Add((entry.ParentId, entry.Name))) {
                    throw new InvalidDataException("Queue contains conflicting IDs/names or an unknown parent folder. No files were added.");
                    }

                if (!Path.IsPathFullyQualified(entry.SourcePath)) {
                    throw new InvalidDataException("Queue source paths must be absolute.");
                    }

                FileAttributes attributes = DiskFile.GetAttributes(entry.SourcePath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != entry.IsDirectory) {
                    throw new IOException("Queued source type changed or is a link: " + entry.SourcePath);
                    }

                if (entry.IsDirectory) {
                    folders.Add(entry.EntryId);
                    }
                else {
                    using FileStream source = DiskFile.Open(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if ((entry.SourceLength is long size && source.Length != size) || (entry.SourceModifiedUtc is DateTime modified && DiskFile.GetLastWriteTimeUtc(entry.SourcePath) != modified)) {
                        throw new IOException("Queued source changed since cancellation: " + entry.SourcePath);
                        }
                    }

                restored.Add(new FileQueueItem(entry.SourcePath, entry.Name, entry.ParentId, entry.IsDirectory, entry.EntryId));
                }

            _state.QueueItems.AddRange(restored);
            _state.ValidatedResumeQueue = queueFile.FullName;
            _state.ValidatedResumeQueueHash = SHA256.HashData(DiskFile.ReadAllBytes(queueFile.FullName));
            return _owner.SetError(ArchiveFile.ReturnCode.Success);
            }
        }

    /// <summary>Save unfinished entries and optionally export this encrypted archive's temporary password key.</summary>
    public string SaveRemainingQueue(bool exportKeyFile = false) {
        lock (_state.SyncRoot) {
            _owner.CheckOpen();
            if (exportKeyFile && _state.ResumePassword is null) {
                throw new InvalidOperationException("Only encrypted archives can export a temporary password key.");
                }

            return RemainingQueue.SaveWithPassword(_state.Path!, _state.QueueItems, exportKeyFile ? _state.ResumePassword : null);
            }
        }

    /// <summary>After successful queue preflight, authenticate, randomly overwrite, flush and delete its optional key.</summary>
    public void ConsumeResumeKey(FileInfo queueFile) {
        lock (_state.SyncRoot) {
            _owner.CheckOpen();
            using FileStream input = queueFile.OpenRead();
            if (_state.ValidatedResumeQueue != queueFile.FullName || _state.ValidatedResumeQueueHash is null || !CryptographicOperations.FixedTimeEquals(_state.ValidatedResumeQueueHash, SHA256.HashData(input))) {
                throw new InvalidOperationException("Load and validate this remaining queue before consuming its key.");
                }

            byte[]? password = ResumeKey.Read(queueFile, _state.Path!);
            if (password is null) {
                return;
                }

            try {
                if (_state.ResumePassword is null || !CryptographicOperations.FixedTimeEquals(password, _state.ResumePassword)) {
                    throw new CryptographicException("Temporary key password does not match the open archive.");
                    }

                ResumeKey.Consume(queueFile);
                }
            finally {
                CryptographicOperations.ZeroMemory(password);
                }
            }
        }
    }
