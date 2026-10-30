using System.Buffers.Binary;
using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using DiskNodeFlags = Mash.IO.Format.MNX.Chunk.NodeFlags;

namespace Mash.IO.Format.MNX.Archive.Codecs;

/// <summary>Archive chunk authentication and encryption, with independent chunk compression when selected.</summary>
internal sealed class ArchiveCodec : IDisposable {
    internal const int KdfIterations = 600_000;
    internal const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[]? _key;
    private readonly AesGcm? _aes;
    private readonly Guid _archiveId;
    private readonly bool _streamCompression;
    internal ArchiveCodec(
        Guid archiveId,
        ArchiveCompression compression,
        int compressionLevel,
        byte[]? key,
        bool streamCompression = false
        ) {
        _archiveId = archiveId;
        _streamCompression = streamCompression;
        Compression = compression;
        CompressionLevel = compressionLevel;
        _key = key;
        _aes = key is null ? null : new AesGcm(key, TagLength);
        }

    internal ArchiveCompression Compression { get; }
    internal int CompressionLevel { get; }
    internal bool Encrypted => _aes is not null;

    internal static byte[] DeriveKey(string password, byte[] salt, int iterations) {
        if (string.IsNullOrEmpty(password)) {
            throw new ArgumentException("A nonempty passphrase is required.", nameof(password));
            }

        if (salt.Length != SaltLength || iterations < 100_000 || iterations > 10_000_000) {
            throw new InvalidDataException("Archive key derivation parameters are invalid.");
            }

        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        }

    internal byte[] EncodeChunk(
        ref ChunkHeader slot,
        ReadOnlySpan<byte> plain,
        byte[]? preparedCompressed = null,
        bool compressionChecked = false
        ) {
        byte[] stored;
        slot.Flags &= DiskNodeFlags.DoubleNameLength | DiskNodeFlags.IndexName;
        if (preparedCompressed is not null) {
            if (Compression == ArchiveCompression.None || preparedCompressed.Length >= plain.Length) {
                throw new ArgumentException("Prepared compression is invalid.", nameof(preparedCompressed));
                }

            stored = preparedCompressed;
            slot.Flags |= DiskNodeFlags.Compressed;
            }
        else {
            stored = plain.ToArray();
            if (!compressionChecked && !_streamCompression && Compression != ArchiveCompression.None && plain.Length > 0) {
                byte[] compressed = ChunkCompression.Compress(plain, Compression, CompressionLevel);
                if (compressed.Length < plain.Length) {
                    stored = compressed;
                    slot.Flags |= DiskNodeFlags.Compressed;
                    }
                }
            }

        if (_streamCompression && slot.Type != NodeType.Directory) {
            slot.Flags |= DiskNodeFlags.Streamed;
            }

        slot.DataLength = (uint)stored.Length;
        if (_aes is null) {
            slot.Hash = ChunkFingerprint.Compute(stored);
            return stored;
            }

        slot.Flags |= DiskNodeFlags.Encrypted;
        Span<byte> authentication = stackalloc byte[32];
        authentication.Clear();
        Span<byte> nonce = authentication.Slice(0, NonceLength);
        Span<byte> tag = authentication.Slice(NonceLength, TagLength);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> associatedData = stackalloc byte[42];
        WriteChunkAad(slot, associatedData);
        byte[] ciphertext = new byte[stored.Length];
        _aes.Encrypt(nonce, stored, ciphertext, tag, associatedData);
        slot.Hash = ChunkFingerprint.FromBytes(authentication);
        return ciphertext;
        }

    internal byte[] DecodeChunk( in ChunkHeader slot, ReadOnlySpan<byte> stored, int expectedLength) {
        bool streamed = (slot.Flags & DiskNodeFlags.Streamed) != 0;
        bool expectedStream = _streamCompression && slot.Type != NodeType.Directory;
        if (streamed != expectedStream) {
            throw new InvalidDataException("Chunk compression layout does not match the archive.");
            }

        byte[] encoded;
        if (slot.Flags.HasFlag(DiskNodeFlags.Encrypted)) {
            if (_aes is null) {
                throw new CryptographicException("A passphrase is required for this archive.");
                }

            encoded = new byte[stored.Length];
            Span<byte> authentication = stackalloc byte[32];
            slot.Hash.CopyTo(authentication);
            Span<byte> associatedData = stackalloc byte[42];
            WriteChunkAad(slot, associatedData);
            _aes.Decrypt(
                authentication.Slice(0, NonceLength),
                stored,
                authentication.Slice(NonceLength, TagLength),
                encoded,
                associatedData
                );
            }
        else {
            if (_aes is not null) {
                throw new InvalidDataException("Encrypted archive has an unencrypted data chunk.");
                }

            encoded = stored.ToArray();
            Span<byte> checksum = stackalloc byte[32];
            SHA256.HashData(encoded, checksum);
            if (!slot.Hash.Matches(checksum)) {
                throw new InvalidDataException("Chunk payload checksum does not match.");
                }

            }

        if (_streamCompression && slot.Flags.HasFlag(DiskNodeFlags.Compressed)) {
            throw new InvalidDataException("A stream-compressed chunk has an invalid compression flag.");
            }

        if (slot.Flags.HasFlag(DiskNodeFlags.Compressed)) {
            if (Compression == ArchiveCompression.None) {
                throw new InvalidDataException("Archive chunk uses an unsupported compression method.");
                }

            return ChunkCompression.Decompress(encoded, Compression, expectedLength);
            }

        if (encoded.Length != expectedLength) {
            throw new InvalidDataException("Chunk length does not match the file index.");
            }

        return encoded;
        }

    internal (byte[] data, byte[] nonce, byte[] tag) EncryptIndex(byte[] plain) {
        if (_aes is null) {
            return (plain, new byte[NonceLength], new byte[TagLength]);
            }

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] tag = new byte[TagLength];
        byte[] ciphertext = new byte[plain.Length];
        _aes.Encrypt(nonce, plain, ciphertext, tag, IndexAad());
        return (ciphertext, nonce, tag);
        }

    internal byte[] DecryptIndex(byte[] stored, byte[] nonce, byte[] tag) {
        if (_aes is null) {
            return stored;
            }

        byte[] plain = new byte[stored.Length];
        _aes.Decrypt(nonce, stored, tag, plain, IndexAad());
        return plain;
        }

    private byte[] IndexAad() {
        byte[] result = new byte[21];
        "MNXI7"u8.CopyTo(result);
        _archiveId.TryWriteBytes(result.AsSpan(5, 16));
        return result;
        }

    private void WriteChunkAad( in ChunkHeader slot, Span<byte> destination) {
        _archiveId.TryWriteBytes(destination.Slice(0, 16));
        slot.Id.TryWriteBytes(destination.Slice(16, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(32, 4), slot.Sequence);
        destination[36] = (byte)slot.Type;
        destination[37] = (byte)slot.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(38, 4), slot.DataLength);
        }

    public void Dispose() {
        _aes?.Dispose();
        if (_key is not null) {
            CryptographicOperations.ZeroMemory(_key);
            }
        }
    }
