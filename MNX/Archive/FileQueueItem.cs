using System.Collections.ObjectModel;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Chunk;
using Mash.IO.Format.MNX.Tools;
using ArchiveFile = Mash.IO.Format.MNX.MNX;

namespace Mash.IO.Format.MNX.Archive;


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

    public ArchiveFile.ReturnCode ChangeOwnerGuid(Guid parent) {
        Parent = parent;
        return ArchiveFile.ReturnCode.Success;
        }
    }
