using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mash.IO.Format.MNX.Interop;

namespace Mash.IO.Format.MNX;

internal sealed unsafe class NativeApi {
    private static readonly Lazy<NativeApi> Loaded = new Lazy<NativeApi>(() => new NativeApi());
    internal static NativeApi Instance => Loaded.Value;
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<byte*, ulong, nint, nint, byte**, ulong*, int> _request;
    private readonly delegate* unmanaged[Cdecl]<byte*, ulong, void> _release;
    private readonly delegate* unmanaged[Cdecl]<ulong, int> _cancel;
    private readonly delegate* unmanaged[Cdecl]<ulong, int> _prepare;
    private readonly delegate* unmanaged[Cdecl]<ulong, long, long, byte*, int, int*, int> _read;
    private readonly delegate* unmanaged[Cdecl]<byte*, ulong, ulong*, int> _lastError;

    private NativeApi() {
        string? selected = Environment.GetEnvironmentVariable("MNXFILE_NATIVE_LIBRARY");
        string name = OperatingSystem.IsWindows() ? "MNXFile.Native.dll" : "libmnxfile.so.1";
        if (!string.IsNullOrEmpty(selected)) {
            if (!Path.IsPathFullyQualified(selected)) {
                throw new DllNotFoundException("MNXFILE_NATIVE_LIBRARY must be an absolute path.");
                }
            _library = NativeLibrary.Load(selected);
            }
        else {
            string? installedPath = FindWindowsInstallation(name);
            if (installedPath != null) {
                _library = NativeLibrary.Load(installedPath);
                }
            else if (!NativeLibrary.TryLoad(name, typeof(NativeApi).Assembly, DllImportSearchPath.SafeDirectories, out _library)) {
                throw new DllNotFoundException("MNXFile library is not installed or cannot be loaded. Install the architecture-matching mnxfile package (" + name + ").");
                }
            }

        try {
            if (!NativeLibrary.TryGetExport(_library, "mnx_client_protocol_version", out nint versionExport)) {
                throw new NotSupportedException("Installed MNXFile library is too old: client protocol 1 is required.");
                }
            uint version = ((delegate* unmanaged[Cdecl]<uint>)versionExport)();
            if (version != 1) {
                throw new NotSupportedException("Installed MNXFile client protocol " + version + " is incompatible; this application requires protocol 1.");
                }
            _request = (delegate* unmanaged[Cdecl]<byte*, ulong, nint, nint, byte**, ulong*, int>)NativeLibrary.GetExport(_library, "mnx_client_request");
            _release = (delegate* unmanaged[Cdecl]<byte*, ulong, void>)NativeLibrary.GetExport(_library, "mnx_client_release");
            _cancel = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(_library, "mnx_client_cancel");
            _prepare = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(_library, "mnx_client_prepare");
            _read = (delegate* unmanaged[Cdecl]<ulong, long, long, byte*, int, int*, int>)NativeLibrary.GetExport(_library, "mnx_client_stream_read");
            _lastError = (delegate* unmanaged[Cdecl]<byte*, ulong, ulong*, int>)NativeLibrary.GetExport(_library, "mnx_last_error");
            }
        catch {
            // Even an incompatible NativeAOT runtime cannot safely be unloaded.
            // Keep its mapping alive until process exit and report the mismatch.
            throw;
            }
        // NativeAOT libraries stay loaded: unloading is not supported by .NET.
        }

    internal ClientResponse Request(ClientRequest request, nint callback = 0, nint context = 0) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request, ClientJson.Default.ClientRequest);
        byte* result = null;
        ulong length = 0;
        try {
            fixed (byte* pointer = bytes) {
                int status = _request(pointer, (ulong)bytes.Length, callback, context, &result, &length);
                if (status != 0 || result == null || length > int.MaxValue) {
                    throw new IOException("MNXFile native request failed (status " + status + ").");
                    }
                }
            return JsonSerializer.Deserialize(new ReadOnlySpan<byte>(result, (int)length), ClientJson.Default.ClientResponse)
                ?? throw new InvalidDataException("MNXFile returned an empty response.");
            }
        finally {
            CryptographicOperations.ZeroMemory(bytes);
            if (result != null) {
                _release(result, length);
                }
            }
        }

    internal void Prepare(ulong handle) {
        if (_prepare(handle) != 0) {
            throw new InvalidOperationException("MNXFile archive is busy or closed.");
            }
        }

    private static string? FindWindowsInstallation(string libraryName) {
        if (!OperatingSystem.IsWindows()) {
            return null;
            }

        string[] installationDirectories = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "MNXFile"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MNXFile")
            };

        foreach (string directory in installationDirectories) {
            string candidate = Path.Combine(directory, libraryName);
            if (System.IO.File.Exists(candidate)) {
                return candidate;
                }
            }

        return null;
        }

    internal void Cancel(ulong handle) {
        _cancel(handle);
        }

    internal int Read(ulong handle, long stream, long offset, Span<byte> destination) {
        int count = 0;
        fixed (byte* pointer = destination) {
            int status = _read(handle, stream, offset, pointer, destination.Length, &count);
            if (status != 0) {
                ulong required = 0;
                _lastError(null, 0, &required);
                byte[] message = new byte[checked((int)required)];
                fixed (byte* messagePointer = message) {
                    _lastError(messagePointer, required, &required);
                    throw new IOException(Marshal.PtrToStringUTF8((nint)messagePointer) ?? "MNXFile read failed.");
                    }
                }
            }
        return count;
        }

    internal static void ThrowIfError(ClientResponse response) {
        if (response.Error == null) {
            return;
            }
        throw response.ErrorType switch {
            "OperationCanceledException" => new OperationCanceledException(response.Error),
            "CryptographicException" => new CryptographicException(response.Error),
            "FileNotFoundException" => new FileNotFoundException(response.Error),
            "DirectoryNotFoundException" => new DirectoryNotFoundException(response.Error),
            "UnauthorizedAccessException" => new UnauthorizedAccessException(response.Error),
            "ArgumentOutOfRangeException" => new ArgumentOutOfRangeException(null, response.Error),
            "ArgumentException" => new ArgumentException(response.Error),
            "InvalidDataException" => new InvalidDataException(response.Error),
            "EndOfStreamException" => new EndOfStreamException(response.Error),
            "NotSupportedException" => new NotSupportedException(response.Error),
            "InvalidOperationException" => new InvalidOperationException(response.Error),
            "ObjectDisposedException" => new ObjectDisposedException("MNX", response.Error),
            _ => new IOException(response.Error)
            };
        }
    }
