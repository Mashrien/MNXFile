using MNX.Interop;

namespace MNX;

/// <summary>Reports the version of the installed native engine loaded by this client.</summary>
public static class LibraryInfo {
    public static string Version {
        get {
            // Ask the engine itself. The client package can be updated independently.
            ClientResponse response = NativeApi.Instance.Request(new ClientRequest { Operation = "library-version" });
            if (response.ErrorType == "ObjectDisposedException") {
                throw new NotSupportedException("The installed MNXFile engine does not report its version. Install MNXFile 1.3.0-alpha.7 or newer.");
                }
            NativeApi.ThrowIfError(response);
            if (string.IsNullOrWhiteSpace(response.Text)) {
                throw new InvalidDataException("MNXFile returned an empty library version.");
                }
            return response.Text;
            }
        }
    }
