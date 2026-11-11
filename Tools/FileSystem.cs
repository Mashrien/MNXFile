namespace MNX.Tools;

public static class FileSystem {
    /// <summary>Identify symbolic links and reparse points, including links with missing targets.</summary>
    public static bool IsLink(FileSystemInfo source) {
        ArgumentNullException.ThrowIfNull(source);
        source.Refresh();
        return source.LinkTarget is not null || (source.Exists && source.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
    }
