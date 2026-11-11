using System.Collections.ObjectModel;
using MNX.Storage;

namespace MNX;


/// <summary>A source file or directory waiting to be written.</summary>
public sealed class FileQueueItem {
    internal FileQueueItem(string sourcePath, string name, Guid parent, bool directory, Guid? id = null) {
        ID = id ?? Guid.NewGuid();
        SourcePath = sourcePath;
        Name = name;
        Parent = parent;
        IsDirectory = directory;
        }

    public Guid ID { get; }
    public Guid Parent { get; internal set; }
    public string Name { get; }
    public string FullPath => SourcePath;
    public string SourcePath { get; }
    public bool IsDirectory { get; }
    public ulong Length => IsDirectory ? 0 : (ulong)new FileInfo(SourcePath).Length;

    public Archive.ReturnCode ChangeOwnerGuid(Guid parent) {
        Parent = parent;
        return Archive.ReturnCode.Success;
        }
    }
