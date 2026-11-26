using System.Text;
using MNX.Storage;
using MNX.Tools;

namespace MNX;

/// <summary>Streaming variable-length metadata records shared by the two paged lookup tables.</summary>
internal static class IndexRecord {
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static void Write(BinaryWriter writer, VFile file) {
        ArchiveFormat.ValidateName(file.Name);
        byte[] name = Utf8.GetBytes(file.Name);
        xGuid.WriteGuid(writer, file.FileID);
        xGuid.WriteGuid(writer, file.OwnerID);
        writer.Write(file.FirstChunk);
        writer.Write(file.SizeOriginal);
        writer.Write(file.CreationDateUtc.Ticks);
        writer.Write(file.ModificationDateUtc.Ticks);
        writer.Write(file.AccessDateUtc.Ticks);
        writer.Write((ushort)file.Attributes);
        writer.Write((byte)(file.IsDirectory ? 1 : 0));
        writer.Write((ushort)name.Length);
        writer.Write(name);
        writer.Write((uint)file.ChunkExtents.Count);
        foreach (ChunkExtent extent in file.ChunkExtents) {
            writer.Write(extent.FirstChunk);
            writer.Write(extent.Count);
            }
        writer.Write((uint)file.ChunkLengthGroups.Count);
        foreach (ChunkLengthGroup run in file.ChunkLengthGroups) {
            writer.Write(run.Count);
            writer.Write(run.BytesPerChunk);
            }
        }

    internal static VFile Read(BinaryReader reader, ulong end, uint indexChunks, uint totalChunks, int chunkSize, bool streamed, Guid root, bool allowTrailing = false) {
        try {
            RequireAvailable(reader, end, 73);
            Guid id = xGuid.ReadGuid(reader);
            Guid owner = xGuid.ReadGuid(reader);
            uint first = reader.ReadUInt32();
            ulong size = reader.ReadUInt64();
            DateTime created = new(reader.ReadInt64(), DateTimeKind.Utc);
            DateTime modified = new(reader.ReadInt64(), DateTimeKind.Utc);
            DateTime accessed = new(reader.ReadInt64(), DateTimeKind.Utc);
            Archive.Attribs attributes = (Archive.Attribs)reader.ReadUInt16();
            byte directory = reader.ReadByte();
            ushort nameLength = reader.ReadUInt16();
            if (id == Guid.Empty || id == root || owner == Guid.Empty || owner == id || first <= indexChunks || first > totalChunks || directory > 1 || nameLength == 0) {
                throw new InvalidDataException("Archive entry identifiers or metadata are invalid.");
                }
            RequireAvailable(reader, end, (ulong)nameLength + 4);
            byte[] nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength) {
                throw new EndOfStreamException();
                }
            string name = Utf8.GetString(nameBytes);
            ArchiveFormat.ValidateName(name);
            VFile file = new(id, owner, name, directory == 1, size, created, modified, accessed, attributes, first);
            uint extentCount = reader.ReadUInt32();
            if (extentCount == 0 || extentCount > totalChunks - indexChunks) {
                throw new InvalidDataException("Archive extent count is invalid.");
                }
            RequireAvailable(reader, end, (ulong)extentCount * 8 + 4);
            List<ChunkExtent> extents = new();
            ulong sequence = 0;
            for (uint index = 0; index < extentCount; index++) {
                uint physical = reader.ReadUInt32();
                uint count = reader.ReadUInt32();
                if (count == 0 || physical <= indexChunks || (ulong)physical + count - 1 > totalChunks || sequence + count > totalChunks - indexChunks) {
                    throw new InvalidDataException("Archive extent exceeds its physical or logical bounds.");
                    }
                extents.Add(new((uint)sequence, physical, count));
                sequence += count;
                }
            if (extents[0].FirstChunk != first || (directory == 1 && (sequence != 1 || size != 0))) {
                throw new InvalidDataException("Archive extent metadata does not match the entry.");
                }
            file.SetExtents(extents);
            uint runCount = reader.ReadUInt32();
            if (runCount == 0 || runCount > sequence) {
                throw new InvalidDataException("Archive logical run count is invalid.");
                }
            RequireAvailable(reader, end, (ulong)runCount * 8);
            List<ChunkLengthGroup> runs = new();
            ulong logicalSequence = 0;
            ulong bytes = 0;
            for (uint index = 0; index < runCount; index++) {
                uint count = reader.ReadUInt32();
                uint length = reader.ReadUInt32();
                if (count == 0 || logicalSequence + count > sequence || length > (streamed ? chunkSize - ArchiveFormat.NextHeaderSize : 65536) || (length == 0 && (size != 0 || sequence != 1))) {
                    throw new InvalidDataException("Archive logical run is invalid.");
                    }
                runs.Add(new((uint)logicalSequence, count, length, bytes));
                logicalSequence += count;
                bytes = checked(bytes + (ulong)count * length);
                }
            if (logicalSequence != sequence || (!streamed && bytes != size) || (streamed && (size == 0 ? sequence != 1 || bytes != 0 : bytes == 0)) || (streamed && runs[0].BytesPerChunk > chunkSize - ArchiveFormat.FirstHeaderSize) || (directory == 1 && (runs.Count != 1 || bytes != 0)) || (!allowTrailing && (ulong)reader.BaseStream.Position != end)) {
                throw new InvalidDataException("Archive logical lengths do not match the entry.");
                }
            file.SetChunkLengthGroups(runs);
            return file;
            }
        catch (Exception error) when (error is EndOfStreamException || error is ArgumentException || error is OverflowException) {
            throw new InvalidDataException("Archive metadata record is invalid or truncated.", error);
            }
        }

    private static void RequireAvailable(BinaryReader reader, ulong end, ulong bytes) {
        ulong position = checked((ulong)reader.BaseStream.Position);
        if (position > end || bytes > end - position) {
            throw new InvalidDataException("Archive metadata record crosses its declared boundary.");
            }
        }
    }
