namespace MNX;

/// <summary>Bounded clean metadata cache over a paged disk index, plus explicitly changed entries.</summary>
internal sealed class ArchiveEntryStore {
    internal const int MaximumCachedEntries = 512;
    internal const long MaximumCachedMetadataBytes = 8 * 1024 * 1024;
    private PagedArchiveIndex? _source;
    private readonly SortedDictionary<Guid, VFile?> _changes = new();
    private readonly Dictionary<Guid, LinkedListNode<VFile>> _cache = new();
    private readonly LinkedList<VFile> _recent = new();
    private readonly Dictionary<Guid, long> _cacheWeights = new();
    private long _residentBytes;
    internal uint Count { get; private set; }
    internal uint DirectoryCount { get; private set; }
    internal ulong Version { get; private set; }
    internal int ResidentEntries => _cache.Count;
    internal long ResidentMetadataBytes => _residentBytes;
    internal int DirtyEntries => _changes.Count;
    internal ulong PageReads => _source?.PageReads ?? 0;
    internal int ResidentPages => _source?.ResidentPages ?? 0;
    internal void ValidateIndex(CancellationToken cancellationToken) => _source?.ValidateTables(cancellationToken);
    internal IEnumerable<Guid> Keys => Values.Select(file => file.FileID);
    internal VFile this[Guid id] => GetValueOrDefault(id) ?? throw new KeyNotFoundException("Archive entry was not found.");

    internal VFile? GetValueOrDefault(Guid id) {
        if (_changes.TryGetValue(id, out VFile? changed)) {
            return changed;
            }
        if (_cache.TryGetValue(id, out LinkedListNode<VFile>? node)) {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value;
            }
        VFile? file = _source?.Get(id);
        return file is null ? null : Remember(file);
        }

    private VFile Remember(VFile file) {
        if (_cache.TryGetValue(file.FileID, out LinkedListNode<VFile>? existing)) {
            _recent.Remove(existing);
            _recent.AddFirst(existing);
            return existing.Value;
            }
        long bytes = MetadataBytes(file);
        if (bytes > MaximumCachedMetadataBytes) {
            return file;
            }
        while (_cache.Count >= MaximumCachedEntries || _residentBytes + bytes > MaximumCachedMetadataBytes) {
            EvictOldest();
            }
        _cache.Add(file.FileID, _recent.AddFirst(file));
        _cacheWeights.Add(file.FileID, bytes);
        _residentBytes += bytes;
        return file;
        }

    private static long MetadataBytes(VFile file) => 256L + file.Name.Length * 2L + file.ChunkExtents.Count * 32L + file.ChunkLengthGroups.Count * 64L;

    private void EvictOldest() {
        LinkedListNode<VFile> oldest = _recent.Last!;
        _residentBytes -= _cacheWeights[oldest.Value.FileID];
        _cacheWeights.Remove(oldest.Value.FileID);
        _cache.Remove(oldest.Value.FileID);
        _recent.RemoveLast();
        }

    internal bool ContainsKey(Guid id) => GetValueOrDefault(id) is not null;
    internal bool TryGetValue(Guid id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VFile? file) {
        file = GetValueOrDefault(id);
        return file is not null;
        }

    internal void Add(Guid id, VFile file) {
        if (id != file.FileID || ContainsKey(id)) {
            throw new ArgumentException("Archive entry ID already exists or does not match.");
            }
        _changes[id] = file;
        Count = checked(Count + 1);
        if (file.IsDirectory) {
            DirectoryCount = checked(DirectoryCount + 1);
            }
        Version++;
        }

    internal bool Remove(Guid id, out VFile? file) {
        file = GetValueOrDefault(id);
        if (file is null) {
            return false;
            }
        _changes[id] = null;
        if (_cache.Remove(id, out LinkedListNode<VFile>? node)) {
            _residentBytes -= _cacheWeights[id];
            _cacheWeights.Remove(id);
            _recent.Remove(node);
            }
        Count--;
        if (file.IsDirectory) {
            DirectoryCount--;
            }
        Version++;
        return true;
        }

    internal void MarkChanged(VFile file) {
        _changes[file.FileID] = file;
        if (_cache.TryGetValue(file.FileID, out LinkedListNode<VFile>? node)) {
            long bytes = MetadataBytes(file);
            _residentBytes += bytes - _cacheWeights[file.FileID];
            _cacheWeights[file.FileID] = bytes;
            node.Value = file;
            while (_residentBytes > MaximumCachedMetadataBytes) {
                EvictOldest();
                }
            }
        Version++;
        }

    internal IEnumerable<VFile> Values {
        get {
            using IEnumerator<VFile> original = (_source?.Enumerate() ?? Enumerable.Empty<VFile>()).GetEnumerator();
            using IEnumerator<KeyValuePair<Guid, VFile?>> changed = _changes.GetEnumerator();
            bool hasOriginal = original.MoveNext();
            bool hasChanged = changed.MoveNext();
            while (hasOriginal || hasChanged) {
                int order = !hasOriginal ? 1 : !hasChanged ? -1 : original.Current.FileID.CompareTo(changed.Current.Key);
                if (order < 0) {
                    yield return Remember(original.Current);
                    hasOriginal = original.MoveNext();
                    }
                else {
                    if (changed.Current.Value is VFile file) {
                        yield return file;
                        }
                    if (order == 0) {
                        hasOriginal = original.MoveNext();
                        }
                    hasChanged = changed.MoveNext();
                    }
                }
            }
        }

    internal IEnumerable<VFile> ForRebuild() {
        if (_source is not null) {
            foreach (VFile file in _source.EnumerateInDirectoryOrder()) {
                if (!_changes.ContainsKey(file.FileID)) {
                    yield return file;
                    }
                }
            }
        foreach (VFile? file in _changes.Values) {
            if (file is not null) {
                yield return file;
                }
            }
        }

    internal IEnumerable<VFile> Enumerate(uint offset, uint count) {
        if (_changes.Count == 0 && _source is not null) {
            foreach (VFile file in _source.Enumerate(offset)) {
                if (count == 0) {
                    yield break;
                    }
                yield return Remember(file);
                count--;
                }
            yield break;
            }
        foreach (VFile file in Values) {
            if (offset > 0) {
                offset--;
                continue;
                }
            if (count == 0) {
                yield break;
                }
            yield return file;
            count--;
            }
        }

    internal IEnumerable<VFile> Directory(Guid owner) {
        if (_source is not null) {
            foreach (IndexDescriptor descriptor in _source.DirectoryDescriptors(owner)) {
                if (!_changes.ContainsKey(descriptor.Id)) {
                    if (_cache.TryGetValue(descriptor.Id, out LinkedListNode<VFile>? cached)) {
                        yield return cached.Value;
                        }
                    else {
                        yield return Remember(_source.ReadEntry(descriptor));
                        }
                    }
                }
            }
        foreach (VFile? file in _changes.Values) {
            if (file is not null && file.OwnerID == owner) {
                yield return file;
                }
            }
        }

    internal bool HasSubdirectories(Guid owner) {
        foreach (VFile? file in _changes.Values) {
            if (file is not null && file.OwnerID == owner && file.IsDirectory) {
                return true;
                }
            }
        if (_source is not null) {
            foreach (IndexDescriptor descriptor in _source.DirectoryDescriptors(owner)) {
                if (!descriptor.Directory) {
                    break;
                    }
                if (!_changes.ContainsKey(descriptor.Id)) {
                    return true;
                    }
                }
            }
        return false;
        }

    internal void Reset(PagedArchiveIndex? source = null, bool retainCached = false) {
        _source?.Dispose();
        _source = source;
        if (retainCached) {
            foreach (KeyValuePair<Guid, VFile?> change in _changes) {
                if (change.Value is VFile file) {
                    Remember(file);
                    }
                else if (_cache.Remove(change.Key, out LinkedListNode<VFile>? node)) {
                    _residentBytes -= _cacheWeights[change.Key];
                    _cacheWeights.Remove(change.Key);
                    _recent.Remove(node);
                    }
                }
            }
        else {
            _cache.Clear();
            _recent.Clear();
            _cacheWeights.Clear();
            _residentBytes = 0;
            }
        _residentBytes = _recent.Sum(file => MetadataBytes(file));
        while (_residentBytes > MaximumCachedMetadataBytes) {
            EvictOldest();
            }
        _changes.Clear();
        Count = source?.Count ?? 0;
        DirectoryCount = source?.DirectoryCount ?? 0;
        Version++;
        }
    internal void Clear() => Reset();
    }
