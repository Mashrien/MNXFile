using System.Text;

namespace MNX;

/// <summary>Track index growth and names during a serial batch without repeatedly scanning all entries.</summary>
internal sealed class WriteBatch {
    internal long EntryBytes { get; private set; }
    internal Dictionary<(Guid Parent, string Name), Guid> Names { get; } = new();
    private readonly Dictionary<Guid, long> _recordSizes = new();

    internal WriteBatch(IEnumerable<VFile> entries) {
        Reset(entries);
        }

    internal void Reset(IEnumerable<VFile> entries) {
        EntryBytes = 0;
        Names.Clear();
        _recordSizes.Clear();
        foreach (VFile entry in entries) {
            Add(entry);
            }
        }

    internal void Add(VFile entry) {
        long bytes = checked(81L + Encoding.UTF8.GetByteCount(entry.Name) + 8L * entry.ChunkExtents.Count + 8L * entry.ChunkLengthGroups.Count);
        _recordSizes.Add(entry.FileID, bytes);
        EntryBytes = checked(EntryBytes + bytes);
        Names.Add((entry.OwnerID, entry.Name), entry.FileID);
        }

    internal void Remove(VFile entry) {
        if (_recordSizes.Remove(entry.FileID, out long bytes)) {
            EntryBytes -= bytes;
            Names.Remove((entry.OwnerID, entry.Name));
            }
        }
    }
