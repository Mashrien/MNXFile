using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;

namespace Mash.IO.Format.MNX.Tools;

public static class FileSystemTools {
    /// <summary>Identify symbolic links and reparse points, including links with missing targets.</summary>
    public static bool IsLink(FileSystemInfo source) {
        ArgumentNullException.ThrowIfNull(source);
        source.Refresh();
        return source.LinkTarget is not null || (source.Exists && source.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
    }
