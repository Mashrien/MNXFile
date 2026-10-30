using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Interop;

namespace Mash.IO.Format.MNX {
    public sealed class Configuration {
        public int ProgressReportFrequency { get; set; } = 250;
        public CancellationToken CancellationToken { get; set; }
        public bool OverrideByDefault { get; set; } = true;
        public ArchiveCompression Compression { get; set; }
        public ArchiveCompressionLayout CompressionLayout { get; set; } = ArchiveCompressionLayout.Continuous;
        public int CompressionLevel { get; set; } = 4;
        public int ChunkSize { get; set; } = 32768;
        }
    }

namespace Mash.IO.Format.MNX.VFile {
    public sealed class VFile {
        internal VFile(EntrySnapshot snapshot) {
            FileID = snapshot.Id;
            OwnerID = snapshot.Parent;
            Name = snapshot.Name;
            IsDirectory = snapshot.Directory;
            SizeOriginal = snapshot.Size;
            ChunkCount = snapshot.Chunks;
            ExtentCount = snapshot.Extents;
            Attributes = (MNX.Attribs)snapshot.Attributes;
            CreationDateUtc = snapshot.Created;
            ModificationDateUtc = snapshot.Modified;
            AccessDateUtc = snapshot.Accessed;
            }
        public Guid FileID { get; }
        public Guid OwnerID { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
        public MNX.NodeFlags Flags => IsDirectory ? MNX.NodeFlags.IsDirectory : MNX.NodeFlags.None;
        public MNX.Attribs Attributes { get; }
        public ulong SizeOriginal { get; }
        public uint ChunkCount { get; }
        public int ExtentCount { get; }
        public DateTime CreationDateUtc { get; }
        public DateTime ModificationDateUtc { get; }
        public DateTime AccessDateUtc { get; }
        }
    }

namespace Mash.IO.Format.MNX.Archive {
    public sealed class FileQueueItem {
        internal FileQueueItem(QueueSnapshot snapshot) {
            Snapshot = snapshot;
            }
        internal QueueSnapshot Snapshot { get; }
        public Guid ID => Snapshot.Id;
        public Guid Parent => Snapshot.Parent;
        public string Name => Snapshot.Name;
        public string FullPath => Snapshot.Path;
        public string SourcePath => Snapshot.Path;
        public bool IsDirectory => Snapshot.Directory;
        public ulong Length => Snapshot.Length;
        }
    }
