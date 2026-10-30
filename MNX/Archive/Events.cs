using ArchiveFile = Mash.IO.Format.MNX.MNX;

namespace Mash.IO.Format.MNX.Archive;


public sealed class FileOpenedEventArgs : EventArgs {
    public ArchiveFile.ReturnCode ErrorLevel { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string ExceptionText { get; set; } = string.Empty;
    public bool Cancel { get; set; }
    }


public sealed class OperationProgressArgs : EventArgs {
    public ArchiveFile.ReturnCode ErrorLevel { get; set; }
    public string FileName { get; set; } = string.Empty;
    public float Progress { get; set; }
    public long BytesMin { get; set; }
    public long BytesMax { get; set; }
    public long BytesComplete { get; set; }
    }
