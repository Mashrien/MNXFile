using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;

namespace Mash.IO.Format.MNX.Chunk;


internal readonly record struct FreeChunkRange(uint Start, uint Count) {
    internal ulong End => (ulong)Start + Count - 1;
    }


/// <summary>Sorted, merged ranges of reusable physical chunks.</summary>
internal sealed class FreeChunkSet {
    private readonly List<FreeChunkRange> _ranges = new();
    internal IReadOnlyList<FreeChunkRange> Ranges => _ranges;
    internal ulong Count { get; private set; }
    internal uint Min => _ranges[0].Start;
    internal uint FirstRunLength => _ranges[0].Count;

    internal void Clear() {
        _ranges.Clear();
        Count = 0;
        }

    internal bool Contains(uint value) => Find(value) >= 0;
    internal void Add(uint value) => AddRange(value, 1);
    internal void AddRange(uint start, uint count) {
        if (count == 0 || (ulong)start + count - 1 > uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(count));
            }

        ulong end = (ulong)start + count - 1;
        int i = 0;
        while (i < _ranges.Count && _ranges[i].End + 1 < start) {
            i++;
            }

        while (i < _ranges.Count && _ranges[i].Start <= end + 1) {
            FreeChunkRange old = _ranges[i];
            start = Math.Min(start, old.Start);
            end = Math.Max(end, old.End);
            Count -= old.Count;
            _ranges.RemoveAt(i);
            }

        uint merged = checked((uint)(end - start + 1));
        _ranges.Insert(i, new FreeChunkRange(start, merged));
        Count += merged;
        }

    internal bool Remove(uint value) {
        int i = Find(value);
        if (i < 0) {
            return false;
            }

        FreeChunkRange range = _ranges[i];
        _ranges.RemoveAt(i);
        if (range.Start < value) {
            _ranges.Insert(i++, new FreeChunkRange(range.Start, value - range.Start));
            }

        if ((ulong)value < range.End) {
            _ranges.Insert(i, new FreeChunkRange(value + 1, checked((uint)(range.End - value))));
            }

        Count--;
        return true;
        }

    internal void TrimTail(ref uint chunkCount, uint minimum) {
        while (_ranges.Count > 0 && _ranges[_ranges.Count - 1].End == chunkCount && chunkCount > minimum) {
            FreeChunkRange range = _ranges[_ranges.Count - 1];
            if (range.Start <= minimum) {
                uint removable = chunkCount - minimum;
                _ranges[_ranges.Count - 1] = new FreeChunkRange(range.Start, range.Count - removable);
                Count -= removable;
                chunkCount = minimum;
                break;
                }

            _ranges.RemoveAt(_ranges.Count - 1);
            Count -= range.Count;
            chunkCount = range.Start - 1;
            }
        }

    private int Find(uint value) {
        int low = 0;
        int high = _ranges.Count - 1;
        while (low <= high) {
            int middle = low + (high - low) / 2;
            FreeChunkRange range = _ranges[middle];
            if (value < range.Start) {
                high = middle - 1;
                }
            else if (value > range.End) {
                low = middle + 1;
                }
            else {
                return middle;
                }
            }

        return -1;
        }
    }


/// <summary>A contiguous run of logical file chunks at consecutive archive positions.</summary>
public readonly record struct ChunkExtent(uint FirstSequence, uint FirstChunk, uint Count);

/// <summary>Adjacent chunks with equal indexed byte lengths. Continuous compression indexes compressed bytes.</summary>
public readonly record struct ChunkLengthGroup(uint FirstSequence, uint Count, uint BytesPerChunk, ulong FirstByteOffset);
