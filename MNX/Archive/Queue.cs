using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Tools;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;

namespace Mash.IO.Format.MNX.Archive;

/// <summary>Queue and scan input paths, then write them serially as one index batch.</summary>
public sealed class ArchiveQueue {
    private readonly ArchiveFile _owner;
    private readonly ArchiveState _state;
    internal ArchiveQueue(ArchiveFile owner) {
        _owner = owner;
        _state = owner.State;
        }

    public IReadOnlyList<FileQueueItem> Items {
        get {
            lock (_state.SyncRoot) {
                return _state.QueueItems.ToArray();
                }
            }
        }

    public ArchiveFile.ReturnCode AddFile(FileInfo file, Guid parent = default) {
        ArgumentNullException.ThrowIfNull(file);
        lock (_state.SyncRoot) {
            _owner.CheckUsable();
            if (FileSystemTools.IsLink(file)) {
                return _owner.SetError(ArchiveFile.ReturnCode.Success);
                }

            if (!file.Exists) {
                return _owner.SetError(ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.ArchiveNonExistant);
                }

            _owner.ValidateInputName(file.Name);
            Guid owner = _owner.NormalizeParent(parent);
            if (_state.QueueItems.Any(item => item.SourcePath == file.FullName && item.Parent == owner)) {
                return _owner.SetError(ArchiveFile.ReturnCode.Success);
                }

            _state.QueueItems.Add(new FileQueueItem(file.FullName, file.Name, owner, false));
            return _owner.SetError(ArchiveFile.ReturnCode.Success);
            }
        }

    /// <summary>Queue a directory and descendants, including empty folders; silently skip links.</summary>
    public ArchiveFile.ReturnCode AddDirectory(DirectoryInfo directory, Guid parent = default) {
        ArgumentNullException.ThrowIfNull(directory);
        lock (_state.SyncRoot) {
            _owner.CheckUsable();
            if (FileSystemTools.IsLink(directory)) {
                return _owner.SetError(ArchiveFile.ReturnCode.Success);
                }

            if (!directory.Exists) {
                return _owner.SetError(ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.ArchiveNonExistant);
                }

            Guid owner = _owner.NormalizeParent(parent);
            int start = _state.QueueItems.Count;
            try {
                QueueDirectoryRecursive(directory, owner);
                }
            catch {
                _state.QueueItems.RemoveRange(start, _state.QueueItems.Count - start);
                throw;
                }

            return _owner.SetError(ArchiveFile.ReturnCode.Success);
            }
        }

    private void QueueDirectoryRecursive(DirectoryInfo directory, Guid parent) {
        _owner.Options.CancellationToken.ThrowIfCancellationRequested();
        if (FileSystemTools.IsLink(directory)) {
            return;
            }

        _owner.ValidateInputName(directory.Name);
        FileQueueItem item = new FileQueueItem(directory.FullName, directory.Name, parent, true);
        _state.QueueItems.Add(item);
        foreach (DirectoryInfo child in directory.EnumerateDirectories().OrderBy(x => x.Name, StringComparer.Ordinal)) {
            QueueDirectoryRecursive(child, item.ID);
            }

        foreach (FileInfo file in directory.EnumerateFiles().OrderBy(x => x.Name, StringComparer.Ordinal)) {
            _owner.Options.CancellationToken.ThrowIfCancellationRequested();
            if (FileSystemTools.IsLink(file)) {
                continue;
                }

            _owner.ValidateInputName(file.Name);
            _state.QueueItems.Add(new FileQueueItem(file.FullName, file.Name, item.ID, false));
            }
        }

    /// <summary>Queue paths supplied by any drag and drop UI without depending on UI frameworks.</summary>
    public Dictionary<string, ArchiveFile.ReturnCode> AddDropList(IEnumerable<string> paths, Guid parent = default) {
        ArgumentNullException.ThrowIfNull(paths);
        Dictionary<string, ArchiveFile.ReturnCode> results = new Dictionary<string, ArchiveFile.ReturnCode>(StringComparer.Ordinal);
        foreach (string path in paths) {
            if (FileSystemTools.IsLink(new FileInfo(path))) {
                results[path] = ArchiveFile.ReturnCode.Success;
                }
            else if (DiskFile.Exists(path)) {
                results[path] = AddFile(new FileInfo(path), parent);
                }
            else if (Directory.Exists(path)) {
                results[path] = AddDirectory(new DirectoryInfo(path), parent);
                }
            else {
                results[path] = ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.ArchiveNonExistant;
                }
            }

        return results;
        }

    public ArchiveFile.ReturnCode Remove(Guid id) {
        lock (_state.SyncRoot) {
            HashSet<Guid> descendants = new HashSet<Guid> {
                id
                };
            bool changed;
            do {
                changed = false;
                foreach (FileQueueItem item in _state.QueueItems) {
                    if (descendants.Contains(item.Parent)) {
                        changed |= descendants.Add(item.ID);
                        }
                    }
                }
            while (changed);
            int removed = _state.QueueItems.RemoveAll(item => descendants.Contains(item.ID));
            return _owner.SetError(removed == 0 ? ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.GuidNotFound : ArchiveFile.ReturnCode.Success);
            }
        }

    public ArchiveFile.ReturnCode Process() => Process(null);
    /// <summary>Write queued items in parent-before-child order. Returning true from callback cancels.</summary>
    public ArchiveFile.ReturnCode Process(Func<int, int, int, string, bool>? callback) {
        lock (_state.SyncRoot) {
            _owner.CheckOpen();
            bool ownsBatch = _owner.BeginWriteBatch();
            try {
                foreach (FileQueueItem item in _state.QueueItems.ToArray()) {
                    _owner.Options.CancellationToken.ThrowIfCancellationRequested();
                    if (callback?.Invoke(0, 1, 0, item.Name) == true) {
                        return _owner.SetError(ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.Warning);
                        }

                    if (item.IsDirectory) {
                        _owner.WriteDirectory(item);
                        }
                    else {
                        _owner.WriteFile(new FileInfo(item.SourcePath), item.Parent, item.ID, item.Name);
                        }

                    _state.QueueItems.Remove(item);
                    if (callback?.Invoke(0, 1, 1, item.Name) == true) {
                        return _owner.SetError(ArchiveFile.ReturnCode.Failed | ArchiveFile.ReturnCode.Warning);
                        }
                    }

                return _owner.SetError(ArchiveFile.ReturnCode.Success);
                }
            finally {
                _owner.FinishWriteBatch(ownsBatch);
                }
            }
        }

    }
