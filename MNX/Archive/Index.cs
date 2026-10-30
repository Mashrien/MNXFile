using System.Text;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using Mash.IO.Format.MNX.Tools;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.IO.Format.MNX.Archive;

internal static class ArchiveIndex {
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Serialize(IEnumerable<VirtualFile> files, FreeChunkSet free) {
        VirtualFile[] entries = files.OrderBy(f => f.FileID).ToArray();
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("MNXI"u8);
        writer.Write(ArchiveFormat.CurrentVersion);
        writer.Write((ushort)0);
        writer.Write((uint)entries.Length);
        writer.Write((uint)free.Ranges.Count);
        foreach (VirtualFile file in entries) {
            byte[] name = Utf8.GetBytes(file.Name);
            ArchiveFormat.ValidateName(file.Name);
            GuidTools.WriteGuid(writer, file.FileID);
            GuidTools.WriteGuid(writer, file.OwnerID);
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

        foreach (FreeChunkRange range in free.Ranges) {
            writer.Write(range.Start);
            writer.Write(range.Count);
            }

        return stream.ToArray();
        }

    internal static (Dictionary<Guid, VirtualFile> entries, FreeChunkSet free) Deserialize(
        byte[] data,
        uint expectedEntries,
        uint indexChunks,
        uint totalChunks,
        Guid rootId,
        int chunkSize = ArchiveFormat.ChunkSize,
        bool streamCompressed = false
        ) {
        using BinaryReader reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8, false);
        try {
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual("MNXI"u8) || reader.ReadUInt16() != ArchiveFormat.CurrentVersion || reader.ReadUInt16() != 0) {
                throw new InvalidDataException("Unsupported archive index.");
                }

            uint count = reader.ReadUInt32();
            uint freeCount = reader.ReadUInt32();
            if (count != expectedEntries || count > totalChunks - indexChunks || freeCount > totalChunks - indexChunks) {
                throw new InvalidDataException("Archive index counts are invalid.");
                }

            Dictionary<Guid, VirtualFile> entries = new Dictionary<Guid, VirtualFile>();
            HashSet<uint> firstChunks = new HashSet<uint>();
            List<(uint start, uint end)> spans = new List<(uint start, uint end)>();
            ulong occupied = 0;
            for (uint i = 0; i < count; i++) {
                Guid id = GuidTools.ReadGuid(reader);
                Guid parent = GuidTools.ReadGuid(reader);
                uint first = reader.ReadUInt32();
                ulong size = reader.ReadUInt64();
                DateTime created = new(reader.ReadInt64(), DateTimeKind.Utc);
                DateTime modified = new(reader.ReadInt64(), DateTimeKind.Utc);
                DateTime accessed = new(reader.ReadInt64(), DateTimeKind.Utc);
                ArchiveFile.Attribs attributes = (ArchiveFile.Attribs)reader.ReadUInt16();
                byte directory = reader.ReadByte();
                int nameLength = reader.ReadUInt16();
                if (nameLength == 0 || nameLength > ArchiveFormat.MaxNameBytes || directory > 1 || first <= indexChunks || first > totalChunks || id == Guid.Empty || id == rootId || !firstChunks.Add(first)) {
                    throw new InvalidDataException("Archive index entry is invalid.");
                    }

                string name = Utf8.GetString(reader.ReadBytes(nameLength));
                ArchiveFormat.ValidateName(name);
                uint extentCount = reader.ReadUInt32();
                if (extentCount == 0 || extentCount > totalChunks - indexChunks) {
                    throw new InvalidDataException("Archive file has an invalid extent count.");
                    }

                List<ChunkExtent> extents = new List<ChunkExtent>();
                uint sequence = 0;
                for (uint extentNumber = 0; extentNumber < extentCount; extentNumber++) {
                    uint physical = reader.ReadUInt32();
                    uint length = reader.ReadUInt32();
                    if (length == 0 || physical <= indexChunks || (ulong)physical + length - 1 > totalChunks) {
                        throw new InvalidDataException("Archive file has an invalid extent.");
                        }

                    extents.Add(new ChunkExtent(sequence, physical, length));
                    spans.Add((physical, checked(physical + length - 1)));
                    occupied += length;
                    sequence = checked(sequence + length);
                    }

                if (extents[0].FirstChunk != first || (directory == 1 && (sequence != 1 || size != 0))) {
                    throw new InvalidDataException("Archive file extent metadata does not match its entry.");
                    }

                VirtualFile file = new VirtualFile(id, parent, name, directory == 1, size, created, modified, accessed, attributes, first);
                file.SetExtents(extents);
                uint runCount = reader.ReadUInt32();
                if (runCount == 0 || runCount > sequence) {
                    throw new InvalidDataException("Archive file has an invalid logical run count.");
                    }

                List<ChunkLengthGroup> runs = new List<ChunkLengthGroup>();
                uint logicalSequence = 0;
                ulong logicalBytes = 0;
                for (uint runNumber = 0; runNumber < runCount; runNumber++) {
                    uint chunks = reader.ReadUInt32();
                    uint bytesPerChunk = reader.ReadUInt32();
                    if (chunks == 0 || bytesPerChunk > (streamCompressed ? chunkSize - ArchiveFormat.NextHeaderSize : 65536) || (!streamCompressed && ((bytesPerChunk == 0 && (size != 0 || sequence != 1)) || (bytesPerChunk != 0 && size == 0))) || (streamCompressed && bytesPerChunk == 0 && (size != 0 || sequence != 1))) {
                        throw new InvalidDataException("Archive file has an invalid logical run.");
                        }

                    runs.Add(new ChunkLengthGroup(logicalSequence, chunks, bytesPerChunk, logicalBytes));
                    logicalSequence = checked(logicalSequence + chunks);
                    logicalBytes = checked(logicalBytes + (ulong)chunks * bytesPerChunk);
                    }

                if (logicalSequence != sequence || (!streamCompressed && logicalBytes != size) || (streamCompressed && directory == 0 && (size == 0 ? (sequence != 1 || logicalBytes != 0) : logicalBytes == 0)) || (streamCompressed && directory == 0 && runs[0].BytesPerChunk > chunkSize - ArchiveFormat.FirstHeaderSize) || (directory == 1 && (runs.Count != 1 || runs[0].BytesPerChunk != 0))) {
                    throw new InvalidDataException("Archive logical lengths do not match the file.");
                    }

                file.SetChunkLengthGroups(runs);
                if (!entries.TryAdd(id, file)) {
                    throw new InvalidDataException("Archive index contains duplicate IDs.");
                    }
                }

            FreeChunkSet free = new FreeChunkSet();
            for (uint i = 0; i < freeCount; i++) {
                uint start = reader.ReadUInt32();
                uint length = reader.ReadUInt32();
                if (start <= indexChunks || length == 0 || (ulong)start + length - 1 > totalChunks || firstChunks.Contains(start) || (free.Ranges.Count > 0 && start <= free.Ranges[free.Ranges.Count - 1].End)) {
                    throw new InvalidDataException("Archive index free list is invalid.");
                    }

                free.AddRange(start, length);
                }

            spans.Sort((a, b) => a.start.CompareTo(b.start));
            for (int i = 1; i < spans.Count; i++) {
                if (spans[i].start <= spans[i - 1].end) {
                    throw new InvalidDataException("Archive index has overlapping file extents.");
                    }
                }

            int spanPosition = 0;
            foreach (FreeChunkRange range in free.Ranges) {
                while (spanPosition < spans.Count && spans[spanPosition].end < range.Start) {
                    spanPosition++;
                    }

                if (spanPosition < spans.Count && spans[spanPosition].start <= range.End) {
                    throw new InvalidDataException("Archive index marks an occupied chunk as free.");
                    }
                }

            if (occupied + free.Count != totalChunks - indexChunks) {
                throw new InvalidDataException("Archive index leaves untracked data chunks.");
                }

            if (reader.BaseStream.Position != reader.BaseStream.Length) {
                throw new InvalidDataException("Archive index has trailing data.");
                }

            return (entries, free);
            }
        catch (EndOfStreamException ex) {
            throw new InvalidDataException("Archive index is truncated.", ex);
            }
        catch (ArgumentException ex) {
            throw new InvalidDataException("Archive index contains invalid metadata.", ex);
            }
        }
    }
