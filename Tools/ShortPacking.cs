using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;

namespace Mash.IO.Format.MNX.Tools;

/// <summary>Two full bytes or four four-bit values packed into the same 16-bit storage.</summary>
public static class ShortPacking {
    public static ushort PackBytes(byte lowByte, byte highByte) {
        return (ushort)(lowByte | (highByte << 8));
        }

    public static (byte LowByte, byte HighByte) UnpackBytes(ushort packedBytes) {
        return ((byte)(packedBytes & 0xff), (byte)(packedBytes >> 8));
        }

    public static short PackShort(byte firstNibble, byte secondNibble, byte thirdNibble, byte fourthNibble) {
        ValidateNibble(firstNibble, nameof(firstNibble));
        ValidateNibble(secondNibble, nameof(secondNibble));
        ValidateNibble(thirdNibble, nameof(thirdNibble));
        ValidateNibble(fourthNibble, nameof(fourthNibble));
        return unchecked((short)((firstNibble << 12) | (secondNibble << 8) | (thirdNibble << 4) | fourthNibble));
        }

    public static (byte FirstNibble, byte SecondNibble, byte ThirdNibble, byte FourthNibble) UnpackShort(short packedNibbles) {
        return (
            (byte)((packedNibbles >> 12) & 0xf),
            (byte)((packedNibbles >> 8) & 0xf),
            (byte)((packedNibbles >> 4) & 0xf),
            (byte)(packedNibbles & 0xf)
            );
        }

    private static void ValidateNibble(byte nibble, string parameterName) {
        if (nibble > 15) {
            throw new ArgumentOutOfRangeException(parameterName, "A four-bit value must be between 0 and 15.");
            }
        }
    }
