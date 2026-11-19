# MNXFile native client

C# bindings for the installed MNXFile shared library, using the existing
MNX.Archive API for frontend operations. This package contains no
archive format parser, compressor, encryption engine, or native engine binary.
`MNX.LibraryInfo.Version` queries the version of the loaded native engine without
opening an archive. This query requires engine `1.3.0-alpha.7` or newer and reports
the engine's release version independently of the client package's version.

It requires client protocol 1 from libmnxfile.so.1 on Linux or
MNXFile.Native.dll on Windows. Install the architecture-matching mnxfile runtime.
MNXFILE_NATIVE_LIBRARY can select an absolute library path for development.
The library is loaded for the process lifetime; restart after updating it.

The managed engine package Mash.MNXFile and this client are alternative API
providers. Reference one in a given application, not both. The client exposes
frontend operations and metadata, not the engine's physical chunk internals.

Windows clients also discover MNXFile.Native.dll under `%CommonProgramFiles%\MNXFile` or `%LOCALAPPDATA%\Programs\MNXFile`. Use `../scripts/install-runtime.ps1` to install the shared engine once. An explicit absolute `MNXFILE_NATIVE_LIBRARY` takes priority. The runtime is never embedded in the client package.
