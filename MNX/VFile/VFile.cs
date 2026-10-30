using System.Collections.ObjectModel;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using Mash.IO.Format.MNX.Tools;
using ArchiveFile = Mash.IO.Format.MNX.MNX;

namespace Mash.IO.Format.MNX.VFile;


/// <summary>Metadata for an archived file or directory.</summary>
public sealed class VFile {
    private readonly List<ChunkExtent> _extents = new();
    private readonly List<ChunkLengthGroup> _chunkLengthGroups = new();
    private IReadOnlyDictionary<uint, uint>? _orderedChunksView;
    internal VFile(
        Guid id,
        Guid owner,
        string name,
        bool directory,
        ulong size,
        DateTime created,
        DateTime modified,
        DateTime accessed,
        ArchiveFile.Attribs attributes,
        uint first
        ) {
        FileID = id;
        OwnerID = owner;
        Name = name;
        IsDirectory = directory;
        SizeOriginal = size;
        CreationDateUtc = created;
        ModificationDateUtc = modified;
        AccessDateUtc = accessed;
        Attributes = attributes;
        FirstChunk = first;
        }

    public Guid FileID { get; }
    public Guid OwnerID { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public ArchiveFile.NodeFlags Flags => IsDirectory ? ArchiveFile.NodeFlags.IsDirectory : ArchiveFile.NodeFlags.None;
    public ArchiveFile.Attribs Attributes { get; }
    public ulong SizeOriginal { get; }
    public DateTime CreationDateUtc { get; }
    public DateTime ModificationDateUtc { get; }
    public DateTime AccessDateUtc { get; }
    /// <summary>Compact physical runs; use these for large files.</summary>
    public IReadOnlyList<ChunkExtent> ChunkExtents => _extents.AsReadOnly();
    /// <summary>Indexed byte lengths grouped by adjacent chunks of equal size.</summary>
    public IReadOnlyList<ChunkLengthGroup> ChunkLengthGroups => _chunkLengthGroups.AsReadOnly();

    /// <summary>Optional view. Materializes one mapping per chunk when first accessed; prefer ChunkExtents for large files.</summary>
    public IReadOnlyDictionary<uint, uint> OrderedChunks {
        get {
            if (_orderedChunksView is null) {
                Dictionary<uint, uint> map = new Dictionary<uint, uint>();
                foreach (ChunkExtent extent in _extents) {
                    for (uint i = 0; i < extent.Count; i++) {
                        map.Add(extent.FirstSequence + i, extent.FirstChunk + i);
                        }
                    }

                _orderedChunksView = new ReadOnlyDictionary<uint, uint>(map);
                }

            return _orderedChunksView;
            }
        }

    public uint ChunkCount => _extents.Count == 0 ? 0 : checked(_extents[_extents.Count - 1].FirstSequence + _extents[_extents.Count - 1].Count);
    public (long FirstPart, long SecondPart) UUIDAsLongs => FileID.ToLongs();
    internal uint FirstChunk { get; set; }

    internal uint PhysicalChunk(uint sequence) {
        int position = FindExtent(sequence);
        ChunkExtent extent = _extents[position];
        return extent.FirstChunk + (sequence - extent.FirstSequence);
        }

    private int FindExtent(uint sequence) {
        int low = 0;
        int high = _extents.Count - 1;
        while (low <= high) {
            int middle = low + (high - low) / 2;
            ChunkExtent extent = _extents[middle];
            if (sequence < extent.FirstSequence) {
                high = middle - 1;
                }
            else if (sequence - extent.FirstSequence >= extent.Count) {
                low = middle + 1;
                }
            else {
                return middle;
                }
            }

        throw new ArgumentOutOfRangeException(nameof(sequence));
        }

    internal IEnumerable<uint> PhysicalChunks() {
        foreach (ChunkExtent extent in _extents) {
            for (uint i = 0; i < extent.Count; i++) {
                yield return extent.FirstChunk + i;
                }
            }
        }

    internal void SetExtents(IEnumerable<ChunkExtent> extents) {
        _extents.Clear();
        _extents.AddRange(extents);
        _orderedChunksView = null;
        }

    internal void SetChunkLengthGroups(IEnumerable<ChunkLengthGroup> chunkGroups) {
        _chunkLengthGroups.Clear();
        _chunkLengthGroups.AddRange(chunkGroups);
        }

    internal void AppendLogicalLength(uint chunkLength) {
        if (_chunkLengthGroups.Count == 0) {
            _chunkLengthGroups.Add(new ChunkLengthGroup(0, 1, chunkLength, 0));
            return;
            }

        int lastGroupIndex = _chunkLengthGroups.Count - 1;
        ChunkLengthGroup lastGroup = _chunkLengthGroups[lastGroupIndex];
        if (lastGroup.BytesPerChunk == chunkLength) {
            uint extendedCount = checked(lastGroup.Count + 1);
            _chunkLengthGroups[lastGroupIndex] = new ChunkLengthGroup(
                lastGroup.FirstSequence,
                extendedCount,
                lastGroup.BytesPerChunk,
                lastGroup.FirstByteOffset);
            return;
            }

        uint nextSequence = checked(lastGroup.FirstSequence + lastGroup.Count);
        ulong groupByteLength = (ulong)lastGroup.Count * lastGroup.BytesPerChunk;
        ulong nextByteOffset = lastGroup.FirstByteOffset + groupByteLength;
        _chunkLengthGroups.Add(new ChunkLengthGroup(nextSequence, 1, chunkLength, nextByteOffset));
        }

    internal int LogicalLength(uint sequence) {
        int low = 0;
        int high = _chunkLengthGroups.Count - 1;
        while (low <= high) {
            int middle = low + (high - low) / 2;
            ChunkLengthGroup chunkGroup = _chunkLengthGroups[middle];
            if (sequence < chunkGroup.FirstSequence) {
                high = middle - 1;
                }
            else if (sequence - chunkGroup.FirstSequence >= chunkGroup.Count) {
                low = middle + 1;
                }
            else {
                return checked((int)chunkGroup.BytesPerChunk);
                }
            }

        throw new InvalidDataException("Logical chunk length is missing from the index.");
        }

    internal (uint sequence, int within) LocateByte(ulong offset) {
        int low = 0;
        int high = _chunkLengthGroups.Count - 1;
        while (low <= high) {
            int middle = low + (high - low) / 2;
            ChunkLengthGroup chunkGroup = _chunkLengthGroups[middle];
            ulong end = chunkGroup.FirstByteOffset + (ulong)chunkGroup.Count * chunkGroup.BytesPerChunk;
            if (offset < chunkGroup.FirstByteOffset) {
                high = middle - 1;
                }
            else if (offset >= end) {
                low = middle + 1;
                }
            else {
                ulong offsetInGroup = offset - chunkGroup.FirstByteOffset;
                return (checked(chunkGroup.FirstSequence + (uint)(offsetInGroup / chunkGroup.BytesPerChunk)), checked((int)(offsetInGroup % chunkGroup.BytesPerChunk)));
                }
            }

        throw new ArgumentOutOfRangeException(nameof(offset));
        }

    internal void SetChunks(IEnumerable<uint> chunks) {
        _extents.Clear();
        _orderedChunksView = null;
        foreach (uint chunk in chunks) {
            AppendChunk(chunk);
            }
        }

    internal void AppendChunk(uint chunk) {
        uint sequence = ChunkCount;
        if (_extents.Count > 0) {
            ChunkExtent last = _extents[_extents.Count - 1];
            if ((ulong)last.FirstChunk + last.Count == chunk) {
                _extents[_extents.Count - 1] = new ChunkExtent(last.FirstSequence, last.FirstChunk, last.Count + 1);
                _orderedChunksView = null;
                return;
                }
            }

        _extents.Add(new ChunkExtent(sequence, chunk, 1));
        _orderedChunksView = null;
        }

    internal void RelocateChunk(uint sequence, uint destination) {
        int position = FindExtent(sequence);
        ChunkExtent extent = _extents[position];
        uint before = sequence - extent.FirstSequence;
        uint after = extent.Count - before - 1;
        _extents.RemoveAt(position);
        if (after > 0) {
            _extents.Insert(position, new ChunkExtent(sequence + 1, extent.FirstChunk + before + 1, after));
            }

        _extents.Insert(position, new ChunkExtent(sequence, destination, 1));
        if (before > 0) {
            _extents.Insert(position, new ChunkExtent(extent.FirstSequence, extent.FirstChunk, before));
            }

        for (int i = Math.Max(0, position - 1); i < _extents.Count - 1;) {
            ChunkExtent left = _extents[i];
            ChunkExtent right = _extents[i + 1];
            if ((ulong)left.FirstSequence + left.Count == right.FirstSequence && (ulong)left.FirstChunk + left.Count == right.FirstChunk) {
                _extents[i] = new ChunkExtent(left.FirstSequence, left.FirstChunk, left.Count + right.Count);
                _extents.RemoveAt(i + 1);
                }
            else {
                i++;
                }
            }

        if (sequence == 0) {
            FirstChunk = destination;
            }

        _orderedChunksView = null;
        }
    }

