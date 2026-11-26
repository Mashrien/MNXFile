using MNX;

namespace MNX.Storage;


internal readonly record struct FreeChunkRange(uint Start, uint Count) {
    internal ulong End => (ulong)Start + Count - 1;
    }


/// <summary>Sorted, merged ranges of reusable physical chunks.</summary>
internal sealed class FreeChunkSet {
    private readonly List<FreeChunkRange> _ranges = new();
    private IReadOnlyList<FreeChunkRange>? _source;
    internal IReadOnlyList<FreeChunkRange> Ranges => _source ?? _ranges;
    internal ulong Count { get; private set; }
    internal uint Min => Ranges[0].Start;
    internal uint FirstRunLength => Ranges[0].Count;

    internal void SetSource(IReadOnlyList<FreeChunkRange> ranges, ulong count) {
        _ranges.Clear();
        _source = ranges;
        Count = count;
        }

    internal void ReplaceWith(FreeChunkSet source) => SetSource(source.Ranges, source.Count);

    private void MakeMutable() {
        if (_source is not null) {
            List<FreeChunkRange> validated = new(_source.Count);
            ulong count = 0;
            foreach (FreeChunkRange range in _source) {
                validated.Add(range);
                count = checked(count + range.Count);
                }
            if (count != Count) {
                throw new InvalidDataException("Archive free chunk count does not match its ranges.");
                }
            _ranges.AddRange(validated);
            _source = null;
            }
        }

    internal void Clear() {
        _ranges.Clear();
        _source = null;
        Count = 0;
        }

    internal bool Contains(uint value) => Find(value) >= 0;
    internal bool Overlaps(uint start, ulong end) {
        int low = 0;
        int high = Ranges.Count - 1;
        while (low <= high) {
            int middle = low + (high - low) / 2;
            FreeChunkRange range = Ranges[middle];
            if (range.End < start) {
                low = middle + 1;
                }
            else if (range.Start > end) {
                high = middle - 1;
                }
            else {
                return true;
                }
            }
        return false;
        }
    internal void Add(uint value) => AddRange(value, 1);
    internal void AddRange(uint start, uint count) {
        if (count == 0 || (ulong)start + count - 1 > uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(count));
            }

        MakeMutable();

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

        MakeMutable();

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
        MakeMutable();
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
        int high = Ranges.Count - 1;
        while (low <= high) {
            int middle = low + (high - low) / 2;
            FreeChunkRange range = Ranges[middle];
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
