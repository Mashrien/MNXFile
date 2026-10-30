using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Tools;
using DiskNodeFlags = Mash.IO.Format.MNX.Chunk.NodeFlags;

namespace Mash.IO.Format.MNX.Archive;

/// <summary>The MNX disk layout. Each archive declares its fixed physical chunk size.</summary>
internal static class ArchiveFormat {
    internal const int ChunkSize = 4096;
    internal const int LargeChunkSize = 32768;
    internal const int FirstHeaderSize = 512;
    internal const int NextHeaderSize = 128;
    internal const int InlineNameBytes = 128;
    internal const int MaxNameBytes = ushort.MaxValue;
    internal const uint InitialIndexChunks = 4;
    internal const ushort CurrentVersion = 7;
    private static readonly byte[] CurrentMagic = "MNX7"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal sealed record HeaderInfo(
        Guid archiveId,
        Guid rootId,
        uint chunks,
        uint entries,
        uint indexChunks,
        uint indexBytes,
        byte[] indexHash,
        ushort version,
        DiskNodeFlags flags,
        int chunkSize,
        ArchiveCompression compression,
        int compressionLevel,
        byte[] salt,
        int iterations,
        byte[] indexNonce,
        byte[] indexTag
        );

    internal static bool HasMagic(FileInfo source) {
        if (!source.Exists) {
            return false;
            }
        using FileStream input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> magic = stackalloc byte[4];
        int count = input.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        return count == CurrentMagic.Length && magic.SequenceEqual(CurrentMagic);
        }

    // Archive names are opaque Unicode segments. Backslash is a legal Linux
    // character; destination-OS restrictions are checked when extracting.
    internal static void ValidateName(string name) {
        int limit = MaxNameBytes;
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOf('/') >= 0 || name.IndexOf('\0') >= 0 || Utf8.GetByteCount(name) > limit) {
            throw new ArgumentException($"An archive name must be one nonempty segment of at most {limit:N0} UTF-8 bytes.", nameof(name));
            }
        }

    internal static void WriteHeader(
        Stream stream,
        Guid archiveId,
        Guid rootId,
        uint chunks,
        uint entries,
        uint indexChunks,
        byte[] indexData,
        ushort version,
        DiskNodeFlags flags,
        byte[]? salt = null,
        int iterations = 0,
        byte[]? indexNonce = null,
        byte[]? indexTag = null,
        ArchiveCompression compression = ArchiveCompression.None,
        int compressionLevel = 0,
        int chunkSize = ChunkSize
        ) {
        if (version != CurrentVersion) {
            throw new ArgumentOutOfRangeException(nameof(version));
            }

        if (chunkSize is not (ChunkSize or LargeChunkSize)) {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

        byte[] data = new byte[chunkSize];
        using (BinaryWriter writer = new BinaryWriter(new MemoryStream(data, true), Encoding.UTF8, false)) {
            writer.Write(CurrentMagic);
            writer.Write(version);
            writer.Write((ushort)chunkSize);
            writer.Write(chunkSize);
            GuidTools.WriteGuid(writer, archiveId);
            GuidTools.WriteGuid(writer, rootId);
            writer.Write(ShortPacking.PackBytes((byte)flags, 0));
            writer.Write(ShortPacking.PackBytes((byte)compression, checked((byte)compressionLevel)));
            writer.Write(chunks);
            writer.Write(entries);
            writer.Write(indexChunks);
            writer.Write((uint)indexData.Length);
            }

        SHA256.HashData(indexData).CopyTo(data, 64);
        if (salt is { Length: 16 }) {
            salt.CopyTo(data, 96);
            }

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(112, 4), iterations);
        if (indexNonce is { Length: 12 }) {
            indexNonce.CopyTo(data, 116);
            }

        if (indexTag is { Length: 16 }) {
            indexTag.CopyTo(data, 128);
            }

        SHA256.HashData(data.AsSpan(0, 144)).CopyTo(data, 144);
        stream.Position = 0;
        stream.Write(data);
        stream.Flush();
        }

    internal static HeaderInfo ReadHeader(Stream stream) {
        if (stream.Length < ChunkSize) {
            throw new InvalidDataException("Archive is shorter than its header.");
            }

        byte[] data = new byte[ChunkSize];
        stream.Position = 0;
        stream.ReadExactly(data);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        if (version != CurrentVersion || !data.AsSpan(0, 4).SequenceEqual(CurrentMagic)) {
            throw new InvalidDataException("Unsupported MNX archive version or magic.");
            }

        if (!SHA256.HashData(data.AsSpan(0, 144)).AsSpan().SequenceEqual(data.AsSpan(144, 32))) {
            throw new InvalidDataException("Archive header checksum does not match.");
            }

        using BinaryReader reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8, false);
        reader.ReadBytes(4);
        int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
        if (chunkSize is not (ChunkSize or LargeChunkSize)) {
            throw new InvalidDataException("Unsupported physical chunk size.");
            }

        if (reader.ReadUInt16() != version || reader.ReadUInt16() != chunkSize || reader.ReadInt32() != chunkSize) {
            throw new InvalidDataException("Unsupported MNX archive version or chunk size.");
            }

        if (stream.Length % chunkSize != 0) {
            throw new InvalidDataException("Archive length is not aligned to its chunk size.");
            }

        Guid archiveId = GuidTools.ReadGuid(reader);
        Guid rootId = GuidTools.ReadGuid(reader);
        if (archiveId == Guid.Empty || rootId == Guid.Empty) {
            throw new InvalidDataException("Archive identifiers are missing.");
            }

        (byte storedFlags, byte reservedFlags) = ShortPacking.UnpackBytes(reader.ReadUInt16());
        if (reservedFlags != 0) {
            throw new InvalidDataException("Archive uses unsupported reserved flags.");
            }

        DiskNodeFlags flags = (DiskNodeFlags)storedFlags;
        ushort codecSettings = reader.ReadUInt16();
        uint chunks = reader.ReadUInt32();
        uint entries = reader.ReadUInt32();
        uint indexChunks = reader.ReadUInt32();
        uint indexBytes = reader.ReadUInt32();
        byte[] indexHash = reader.ReadBytes(32);
        if ((ulong)chunks + 1 != (ulong)stream.Length / (ulong)chunkSize) {
            throw new InvalidDataException("Archive chunk count does not match file length.");
            }

        if (indexChunks == 0 || indexChunks > chunks || indexBytes > (ulong)indexChunks * (ulong)chunkSize) {
            throw new InvalidDataException("Archive index region is invalid.");
            }

        if ((flags & ~(DiskNodeFlags.Compressed | DiskNodeFlags.Encrypted | DiskNodeFlags.Streamed)) != 0 || (flags.HasFlag(DiskNodeFlags.Streamed) && !flags.HasFlag(DiskNodeFlags.Compressed))) {
            throw new InvalidDataException("Archive uses unsupported storage flags.");
            }

        (byte codec, byte effort) = ShortPacking.UnpackBytes(codecSettings);
        ArchiveCompression compression = (ArchiveCompression)codec;
        int compressionLevel = effort;
        bool validCodec;
        if (compression == ArchiveCompression.None) {
            validCodec = codecSettings == 0 && !flags.HasFlag(DiskNodeFlags.Compressed);
            }
        else {
            validCodec = Enum.IsDefined(compression) && flags.HasFlag(DiskNodeFlags.Compressed) && (compression != ArchiveCompression.LZ4 || flags.HasFlag(DiskNodeFlags.Streamed)) && (compression is ArchiveCompression.Snappy or ArchiveCompression.LZ4 ? compressionLevel == 0 : compressionLevel is >= 1 and <= 9);
            }

        if (!validCodec) {
            throw new InvalidDataException("Archive compression settings are invalid.");
            }

        byte[] salt = data.AsSpan(96, 16).ToArray();
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(112, 4));
        byte[] nonce = data.AsSpan(116, 12).ToArray();
        byte[] tag = data.AsSpan(128, 16).ToArray();
        if (flags.HasFlag(DiskNodeFlags.Encrypted) && (iterations < 100_000 || iterations > 10_000_000)) {
            throw new InvalidDataException("Archive key derivation settings are invalid.");
            }

        return new HeaderInfo(
            archiveId,
            rootId,
            chunks,
            entries,
            indexChunks,
            indexBytes,
            indexHash,
            version,
            flags,
            chunkSize,
            compression,
            compressionLevel,
            salt,
            iterations,
            nonce,
            tag
            );
        }

    internal static byte[] ReadIndex(Stream stream, uint byteCount, byte[] expectedHash, int chunkSize = ChunkSize) {
        byte[] data = new byte[byteCount];
        stream.Position = chunkSize;
        stream.ReadExactly(data);
        if (!SHA256.HashData(data).AsSpan().SequenceEqual(expectedHash)) {
            throw new InvalidDataException("Archive index checksum does not match.");
            }

        return data;
        }

    internal static void WriteIndex(Stream stream, uint indexChunks, byte[] data, int chunkSize = ChunkSize) {
        if ((ulong)data.Length > (ulong)indexChunks * (ulong)chunkSize) {
            throw new ArgumentOutOfRangeException(nameof(data));
            }

        stream.Position = chunkSize;
        stream.Write(data);
        long padding = (long)indexChunks * chunkSize - data.Length;
        byte[] zeros = new byte[chunkSize];
        while (padding > 0) {
            int take = (int)Math.Min(padding, zeros.Length);
            stream.Write(zeros, 0, take);
            padding -= take;
            }
        }
    }
