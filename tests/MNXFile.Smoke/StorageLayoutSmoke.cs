using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using Mash.IO.Format.MNX.Tools;

internal static class StorageLayoutSmoke {
    internal static void Run() {
        if (Unsafe.SizeOf<RawChunkHeader>() != 128 || RuntimeHelpers.IsReferenceOrContainsReferences<RawChunkHeader>()) {
            throw new InvalidOperationException("Raw header must be exactly 128 unmanaged bytes.");
            }

        Dictionary<string, int> offsets = new Dictionary<string, int> {
            [nameof(RawChunkHeader.Magic)] = 0,
            [nameof(RawChunkHeader.Type)] = 4,
            [nameof(RawChunkHeader.Flags)] = 5,
            [nameof(RawChunkHeader.Reserved)] = 6,
            [nameof(RawChunkHeader.IdFirst)] = 8,
            [nameof(RawChunkHeader.IdSecond)] = 16,
            [nameof(RawChunkHeader.ParentFirst)] = 24,
            [nameof(RawChunkHeader.ParentSecond)] = 32,
            [nameof(RawChunkHeader.Next)] = 40,
            [nameof(RawChunkHeader.Sequence)] = 44,
            [nameof(RawChunkHeader.DataLength)] = 48,
            [nameof(RawChunkHeader.Size)] = 52,
            [nameof(RawChunkHeader.CreatedTicks)] = 60,
            [nameof(RawChunkHeader.ModifiedTicks)] = 68,
            [nameof(RawChunkHeader.AccessedTicks)] = 76,
            [nameof(RawChunkHeader.Attributes)] = 84,
            [nameof(RawChunkHeader.NameLength)] = 86,
            [nameof(RawChunkHeader.Fingerprint)] = 88,
            [nameof(RawChunkHeader.HeaderChecksum)] = 120
            };
        foreach (KeyValuePair<string, int> field in offsets) {
            if (Marshal.OffsetOf<RawChunkHeader>(field.Key).ToInt32() != field.Value) {
                throw new InvalidOperationException("Wrong disk offset for " + field.Key);
                }
            }

        Guid original = Guid.Parse("ffeeddcc-bbaa-9988-8070-605040302010");
        (long firstLong, long secondLong) = original.ToLongs();
        (ulong firstULong, ulong secondULong) = original.ToULongs();
        (int firstInt, int secondInt, int thirdInt, int fourthInt) = original.ToInts();
        (uint firstUInt, uint secondUInt, uint thirdUInt, uint fourthUInt) = original.ToUInts();
        if (GuidTools.GuidFromLongs(firstLong, secondLong) != original ||
            GuidTools.GuidFromULongs(firstULong, secondULong) != original ||
            GuidTools.GuidFromInts(firstInt, secondInt, thirdInt, fourthInt) != original ||
            GuidTools.GuidFromUInts(firstUInt, secondUInt, thirdUInt, fourthUInt) != original) {
            throw new InvalidOperationException("GUID integer conversions changed the identifier.");
            }

        using MemoryStream guidStream = new MemoryStream();
        using BinaryWriter guidWriter = new BinaryWriter(guidStream, System.Text.Encoding.UTF8, leaveOpen: true);
        GuidTools.WriteGuid(guidWriter, original);
        if (!guidStream.ToArray().AsSpan().SequenceEqual(original.ToByteArray())) {
            throw new InvalidOperationException("GUID disk order changed.");
            }

        guidStream.Position = 0;
        using BinaryReader guidReader = new BinaryReader(guidStream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (GuidTools.ReadGuid(guidReader) != original || guidStream.Position != 16) {
            throw new InvalidOperationException("GUID reader did not consume exactly 16 bytes.");
            }

        (byte lowByte, byte highByte) = ShortPacking.UnpackBytes(ShortPacking.PackBytes(0x81, 0xfe));
        if (lowByte != 0x81 || highByte != 0xfe || ShortPacking.UnpackShort(ShortPacking.PackShort(15, 8, 1, 0)) != ((byte)15, (byte)8, (byte)1, (byte)0)) {
            throw new InvalidOperationException("Short packing lost flag bits.");
            }

        try {
            ShortPacking.PackShort(16, 0, 0, 0);
            throw new InvalidOperationException("Out-of-range nibble was accepted.");
            }
        catch (ArgumentOutOfRangeException) {
            }

        using MemoryStream archive = new MemoryStream();
        ChunkHeader header = new ChunkHeader {
            Type = NodeType.Continuation,
            Id = original,
            Sequence = 1,
            Size = 0,
            CreatedUtc = DateTime.UnixEpoch,
            ModifiedUtc = DateTime.UnixEpoch,
            AccessedUtc = DateTime.UnixEpoch
            };
        ChunkFormat.WriteSlot(archive, 1, ref header, new byte[] { 3, 4, 5 }, chunkSize: 32768);
        byte[] bytes = archive.ToArray();
        ReadOnlySpan<byte> physical = bytes.AsSpan(32768, 32768);
        if (!physical.Slice(0, 4).SequenceEqual("MNXC"u8) || physical[4] != 3 || physical[5] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(physical.Slice(44, 4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(physical.Slice(48, 4)) != 3 ||
            !physical.Slice(128, 3).SequenceEqual(new byte[] { 3, 4, 5 })) {
            throw new InvalidOperationException("Packed writer disagrees with the documented wire layout.");
            }

        ChunkHeader parsed = ChunkFormat.ReadSlot(archive, 1, 32768) ?? throw new InvalidOperationException("Written header was empty.");
        if (parsed.Id != original || parsed.Sequence != 1 || parsed.DataLength != 3 || parsed.CreatedUtc != DateTime.UnixEpoch) {
            throw new InvalidOperationException("Packed header failed to round-trip.");
            }

        for (int warmup = 0; warmup < 1000; warmup++) {
            ChunkFormat.ReadSlot(archive, 1, 32768);
            }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int repetition = 0; repetition < 10_000; repetition++) {
            parsed = ChunkFormat.ReadSlot(archive, 1, 32768).GetValueOrDefault();
            }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (allocatedBytes != 0 || parsed.Id != original) {
            throw new InvalidOperationException($"Continuation header reads allocated {allocatedBytes} bytes.");
            }

        bytes[32768 + 48] ^= 1;
        using MemoryStream damaged = new MemoryStream(bytes);
        try {
            ChunkFormat.ReadSlot(damaged, 1, 32768);
            throw new InvalidOperationException("Damaged header checksum was accepted.");
            }
        catch (InvalidDataException) {
            }

        Console.WriteLine("PASS: packed header offsets, GUID/flag packing, corruption checks and zero-allocation continuation reads");
        }
    }
