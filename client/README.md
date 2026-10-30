# MNXFile native client

C# bindings for the installed MNXFile shared library, using the existing
Mash.IO.Format.MNX.MNX API for frontend operations. This package contains no
archive format parser, compressor, encryption engine, or native engine binary.
It requires client protocol 1 from libmnxfile.so.1 on Linux or
MNXFile.Native.dll on Windows. Install the architecture-matching mnxfile runtime.
MNXFILE_NATIVE_LIBRARY can select an absolute library path for development.
The library is loaded for the process lifetime; restart after updating it.

The managed engine package Mash.MNXFile and this client are alternative API
providers. Reference one in a given application, not both. The client exposes
frontend operations and metadata, not the engine's physical chunk internals.

Windows clients also discover MNXFile.Native.dll under `%CommonProgramFiles%\MNXFile` or `%LOCALAPPDATA%\Programs\MNXFile`. Use `../scripts/install-runtime.ps1` to install the shared engine once. An explicit absolute `MNXFILE_NATIVE_LIBRARY` takes priority. The runtime is never embedded in the client package.
