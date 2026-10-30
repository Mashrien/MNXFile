using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;

namespace Mash.IO.Format.MNX;

public sealed partial class MNX {
    [Flags]
    public enum ReturnCode : ulong {
        Success              = 1UL << 0,
        Failed               = 1UL << 1,
        None                 = 1UL << 2,
        Unknown              = 1UL << 3,
        Info                 = 1UL << 4,
        Warning              = 1UL << 5,
        Depreciated          = 1UL << 6,
        GenericIO            = 1UL << 7,
        Permission           = 1UL << 8,
        CyclingRedundancy    = 1UL << 9,
        InvalidHash          = 1UL << 10,
        HashMismatch         = 1UL << 11,
        PasswordInvalid      = 1UL << 12,
        ChunkAlignment       = 1UL << 13,
        HeaderHashMismatch   = 1UL << 14,
        HeaderCorrupt        = 1UL << 15,
        ParseFailed          = 1UL << 16,
        NotValidMNXFile      = 1UL << 17,
        UnexpectedEndOfFile  = 1UL << 18,
        ArchiveIncomplete    = 1UL << 19,
        MissingChunk         = 1UL << 20,
        OrphanedChunk        = 1UL << 21,
        GuidNotFound         = 1UL << 22,
        ChunkAllocFailed     = 1UL << 23,
        ChunkMoveFailed      = 1UL << 24,
        ChildRebaseFailed    = 1UL << 25,
        PInvokeFailed        = 1UL << 26,
        Win32APICallFailed   = 1UL << 27,
        ArchiveTrimFailed    = 1UL << 28,
        ArchiveInvalid       = 1UL << 29,
        ArchiveNonExistant   = 1UL << 30,
        ArchiveUninitialized = 1UL << 31,
        ArchiveLengthInvalid = 1UL << 32,
        OutputAccessDenied   = 1UL << 33,
        }

    [Flags]
    public enum ArchiveFlags : short {
        none           = 0,
        Compressed     = 1 << 0,
        Encrypted      = 1 << 1,
        Multipart      = 1 << 2,
        Broken         = 1 << 3,
        Corrupted      = 1 << 4,
        Incomplete     = 1 << 5,
        BadHeaderHash  = 1 << 6,
        BadArchiveHash = 1 << 7,
        }

    /// <summary>Public file status flags. Physical storage flags are defined separately in the Chunk context.</summary>
    [Flags]
    public enum NodeFlags : short {
        None             = 0,
        Compressed       = 1 << 0,
        Encrypted        = 1 << 1,
        SplitAcrossFiles = 1 << 2,
        Broken           = 1 << 3,
        Corrupted        = 1 << 4,
        IsFolder         = 1 << 5,
        BadFileHash      = 1 << 6,
        IsDirectory      = 1 << 7,
        }

    [Flags]
    public enum Attribs : short {
        ReadOnly = 1 << 0,
        Archive  = 1 << 1,
        System   = 1 << 2,
        Hidden   = 1 << 3,
        }

    public static IReadOnlyDictionary<string, string> FileTypes { get; } = new Dictionary<string, string> {
        ["mnx"] = "Mash Seekable Archive files | *.mnx"
        };
    }
