using System.Buffers.Binary;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;

namespace Mash.IO.Format.MNX.Tools;

/// <summary>Converts GUIDs to fixed-width integer parts using the archive's little-endian byte order.</summary>
public static class GuidTools {
    public static Guid GuidFromLongs(long firstPart, long secondPart) {
        Span<byte> guidBytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(guidBytes, firstPart);
        BinaryPrimitives.WriteInt64LittleEndian(guidBytes.Slice(8), secondPart);
        return new Guid(guidBytes);
        }

    public static (long FirstPart, long SecondPart) ToLongs(this Guid guid) {
        Span<byte> guidBytes = stackalloc byte[16];
        guid.TryWriteBytes(guidBytes);
        long firstPart = BinaryPrimitives.ReadInt64LittleEndian(guidBytes);
        long secondPart = BinaryPrimitives.ReadInt64LittleEndian(guidBytes.Slice(8));
        return (firstPart, secondPart);
        }

    public static Guid GuidFromULongs(ulong firstPart, ulong secondPart) {
        return GuidFromLongs(unchecked((long)firstPart), unchecked((long)secondPart));
        }

    public static (ulong FirstPart, ulong SecondPart) ToULongs(this Guid guid) {
        (long firstPart, long secondPart) = guid.ToLongs();
        return (unchecked((ulong)firstPart), unchecked((ulong)secondPart));
        }

    public static Guid GuidFromInts(int firstPart, int secondPart, int thirdPart, int fourthPart) {
        Span<byte> guidBytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(guidBytes, firstPart);
        BinaryPrimitives.WriteInt32LittleEndian(guidBytes.Slice(4), secondPart);
        BinaryPrimitives.WriteInt32LittleEndian(guidBytes.Slice(8), thirdPart);
        BinaryPrimitives.WriteInt32LittleEndian(guidBytes.Slice(12), fourthPart);
        return new Guid(guidBytes);
        }

    public static (int FirstPart, int SecondPart, int ThirdPart, int FourthPart) ToInts(this Guid guid) {
        Span<byte> guidBytes = stackalloc byte[16];
        guid.TryWriteBytes(guidBytes);
        return (
            BinaryPrimitives.ReadInt32LittleEndian(guidBytes),
            BinaryPrimitives.ReadInt32LittleEndian(guidBytes.Slice(4)),
            BinaryPrimitives.ReadInt32LittleEndian(guidBytes.Slice(8)),
            BinaryPrimitives.ReadInt32LittleEndian(guidBytes.Slice(12))
            );
        }

    public static Guid GuidFromUInts(uint firstPart, uint secondPart, uint thirdPart, uint fourthPart) {
        return GuidFromInts(
            unchecked((int)firstPart),
            unchecked((int)secondPart),
            unchecked((int)thirdPart),
            unchecked((int)fourthPart)
            );
        }

    public static (uint FirstPart, uint SecondPart, uint ThirdPart, uint FourthPart) ToUInts(this Guid guid) {
        (int firstPart, int secondPart, int thirdPart, int fourthPart) = guid.ToInts();
        return (unchecked((uint)firstPart), unchecked((uint)secondPart), unchecked((uint)thirdPart), unchecked((uint)fourthPart));
        }

    /// <summary>Writes two 64-bit parts: exactly 16 bytes, with no temporary byte array.</summary>
    public static void WriteGuid(BinaryWriter writer, Guid guid) {
        (long firstPart, long secondPart) = guid.ToLongs();
        writer.Write(firstPart);
        writer.Write(secondPart);
        }

    /// <summary>Reads the archive's two 64-bit GUID parts without allocating a byte array.</summary>
    public static Guid ReadGuid(BinaryReader reader) {
        long firstPart = reader.ReadInt64();
        long secondPart = reader.ReadInt64();
        return GuidFromLongs(firstPart, secondPart);
        }
    }
