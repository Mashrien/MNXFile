using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Interop;

namespace Mash.IO.Format.MNX.Archive.Resume;

/// <summary>Recovery operations are performed by the installed engine.</summary>
public static class RemainingQueue {
    private static ClientResponse Request(ClientRequest request) {
        ClientResponse response = NativeApi.Instance.Request(request);
        NativeApi.ThrowIfError(response);
        return response;
        }
    public static bool IsQueue(FileInfo file) {
        return Request(new ClientRequest { Operation = "queue-is", Path = file.FullName }).Value;
        }
    public static QueuePayload Read(FileInfo file) {
        return Request(new ClientRequest { Operation = "queue-read", Path = file.FullName }).Resume!;
        }
    public static string LocateArchive(FileInfo file, QueuePayload payload) {
        return Request(new ClientRequest { Operation = "queue-locate", Path = file.FullName, Resume = payload }).Text!;
        }
    public static string MarkCompleted(FileInfo file) {
        return Request(new ClientRequest { Operation = "queue-mark", Path = file.FullName }).Text!;
        }
    public static void Discard(FileInfo file) {
        Request(new ClientRequest { Operation = "queue-discard", Path = file.FullName });
        }
    public static string Save(string archivePath, IEnumerable<FileQueueItem> remaining, string? exportPassword = null) {
        return Request(new ClientRequest {
            Operation = "queue-save",
            Path = archivePath,
            Password = exportPassword,
            Queue = remaining.Select(item => item.Snapshot).ToArray()
            }).Text!;
        }
    }

public static class ResumePassword {
    public static string? Read(FileInfo queueFile, string archivePath) {
        ClientResponse response = NativeApi.Instance.Request(new ClientRequest {
            Operation = "queue-password", Path = queueFile.FullName, Name = archivePath
            });
        NativeApi.ThrowIfError(response);
        return response.Text;
        }
    }
