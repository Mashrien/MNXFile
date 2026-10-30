# MNXFile

**An archive format and library created for large network archives and NAS backups.**

MNX keeps the complete file/folder index in contiguous chunks at the front of the
archive. Browsing and searching read that index without scanning file payloads;
file contents are streamed through bounded buffers. The primary design target is hundreds-of-gigabytes or terabytes of
backups/archives on NAS or other network storage, where seeks, network reads and
memory use matter. Local archives use the same format and API.

## The three projects

| Repository | Responsibility |
| --- | --- |
| **[MNXFile](https://github.com/Mashrien/MNXFile)** | Archive engine, format, native shared library, C API and managed packages |
| [mnx-cli](https://github.com/Mashrien/mnx-cli) | `mnx` command line tool and `mnxi` interactive shell |
| [MNXArchiver](https://github.com/Mashrien/MNXArchiver) | Windows and Linux desktop archive manager |

The applications use the separately installed native engine. A compatible engine
update changes archive handling without rebuilding either interface. This repository
also supplies a full managed library for callers that prefer an in-process engine.

## Features

- Front index with folder relationships, sizes, timestamps, flags and chunk extents.
- Fixed, aligned chunks: **32 KiB by default**, with a 4 KiB option at creation.
- Add, list, search, extract, delete, verify and compact. Compaction fills free space
  with data from the archive's tail and truncates unused trailing chunks.
- Optional **Brotli, LZ4 or Snappy** compression. Continuous per-file compression is
  the default; compressed output is chunked as it is produced. Brotli exposes effort
  levels 1–9; LZ4 and Snappy use their fixed-speed settings.
- Optional **AES-256-GCM** encryption of file contents and the complete name/index
  data, with a password-derived key. Compression and encryption choices are fixed
  when creating an archive.
- UTF-8 names and parent-ID paths; names are preserved, and links are skipped on add.
- Controlled cancellation and checksummed `.mnq` queues for later resume. An optional
  explicitly exported `.mnx-key` is convenience recovery material; see the queue spec.
- Native AOT shared library, versioned C ABI/application protocol and thin C# client.

Current disk schema: **MNX7**. It replaces the old MSX prototype; older prototype
archives are not supported. Controlled cancel/resume is not a power-loss journal.

## Downloads and installation

The [initial releases](https://github.com/Mashrien/MNXFile/releases) provide Linux
x86-64 RPM/DEB/portable tar packages, a Windows x64 ZIP, and managed engine/client
NuGet packages. Linux binaries require glibc2.39 or newer and OpenSSL3; Ubuntu24.04
is the compilation baseline. The native engine needs no installed .NET runtime.
Install **mnxfile** before the CLI or GUI. Download all required packages first:

```sh
sudo dnf install ./mnxfile-*.x86_64.rpm ./mnx-cli-*.x86_64.rpm
# Debian/Ubuntu alternative:
sudo apt install ./mnxfile_*_amd64.deb ./mnx-cli_*_amd64.deb
```

On Windows, extract the engine ZIP and run `install-runtime.ps1` in PowerShell;
then extract either application ZIP. The installer defaults to the current user
and clients discover its installation automatically. The engine ZIP includes the
C header/import library. NuGet packages are release downloads, not NuGet.org uploads.
See [packaging instructions](packaging/README.md) for reproducing packages.

## Build from source

Requires the **.NET 10 SDK**. Linux Native AOT also needs `clang`, a C toolchain,
Python 3 and zlib development headers. Windows native builds require Visual Studio
C++ build tools and a Windows SDK. Build Windows artifacts on Windows.

```sh
git clone https://github.com/Mashrien/MNXFile.git
cd MNXFile
./build.sh --test
```

```powershell
.\build.cmd -Clean -Test -Runtime win-x64
```

Each script reserves a build number automatically. The library/client version is
`1.2.0-alpha.N`. **Both applications currently pin client `1.2.0-alpha.3`**, so use
this command when preparing their dependency feed:

```sh
MNXBuildNumber=3 ./build.sh --test
# Managed NuGet packages only, without building the native library:
MNXBuildNumber=3 ./build.sh --packages-only
```

PowerShell equivalent: `$env:MNXBuildNumber='3'; .\build.ps1 -Test`.
The package IDs are `Mash.MNXFile` (full managed engine) and
`Mash.MNXFile.Client` (thin native client). Packages are generated locally; these
versions have not been published to NuGet.org.

### Build outputs

| Location, relative to the checkout | Contents |
| --- | --- |
| `../MNX_Builds/mnxfile/packages/` | Engine and client NuGet packages |
| `../MNX_Builds/mnxfile/linux-x64/` | `libmnxfile.so.1`, linker aliases and C header |
| `../MNX_Builds/mnxfile/win-x64/` | `MNXFile.Native.dll`, import library and C header |
| `../MNX_Builds/binaries-final/` | Flat staging directory; this build updates the engine |
| `../mnx_clutter/build/mnxfile/` | Intermediates, symbols and test output |

Linux arm64 selects the host architecture. Override `MNXBuildRoot`, `MNXReleaseRoot`
or `MNXFinalRoot` to choose other destinations. Builds do not install the engine.

## Use the engine

Choose **one** managed API provider in an application:

- `Mash.MNXFile`: full managed implementation, including codecs.
- `Mash.MNXFile.Client`: frontend API and metadata that call the installed native
  library; it embeds neither the engine nor its codecs.

Both expose the master class `Mash.IO.Format.MNX.MNX`. For example:

```csharp
using System.IO;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using Mash.IO.Format.MNX.Archive.Codecs;

using ArchiveFile archive = new ArchiveFile();
archive.Options.Compression = ArchiveCompression.Snappy;
archive.Create(new FileInfo("backup.mnx"));
archive.AddFileNow(new FileInfo("document.txt"));
ArchiveFile.ReturnCode result = archive.VerifyArchive();
```

The C API is in [native/include/mnxfile.h](native/include/mnxfile.h).
Read the [native interface guide](native/README.md) for ownership, callbacks,
threading, errors and ABI limits, and the [client guide](client/README.md) for C#
client deployment. Independent handles must not modify the same archive concurrently.

### Native runtime installation

Linux uses `libmnxfile.so.1`; Windows uses `MNXFile.Native.dll`. The native engine
requires no installed .NET runtime. System libraries, including OpenSSL on Linux,
still apply. The native build uses invariant globalization and does not require ICU.

For development, set an absolute path without installing anything:

```sh
export MNXFILE_NATIVE_LIBRARY="$(realpath ../MNX_Builds/mnxfile/linux-x64/libmnxfile.so.1)"
```

[scripts/install-runtime.sh](scripts/install-runtime.sh) installs/stages the Linux
runtime; for example `DESTDIR=/tmp/mnx-stage scripts/install-runtime.sh
../MNX_Builds/mnxfile/linux-x64 /usr /usr/lib64`. Choose the library directory for
your distribution, and refresh its loader cache after a real installation.
On Windows, `scripts/install-runtime.ps1 -Library PATH\MNXFile.Native.dll` installs
per user; `-AllUsers` selects the shared installation. See [package ownership](packaging/OWNERSHIP.md).
[Packaging scripts](packaging/README.md) turn validated build outputs into release
archives and RPM/DEB packages; compilation alone does not install them.

## Source map and format references

| Path | Purpose |
| --- | --- |
| `MNX/File.cs`, `MNX/Configuration.cs` | Public archive interface and creation options |
| `MNX/Archive/` | Index, storage, queue/resume, codecs and encryption |
| `MNX/Chunk/`, `MNX/VFile/` | Chunk structures/maps/readers and virtual entries |
| `native/`, `interop/` | Native exports, C header and versioned protocol |
| `client/` | Thin C# native client |
| `tests/` | Managed, client/protocol and native C smoke tests |

- [FORMAT.md](FORMAT.md): byte layout, checksums, names and index/chunk records.
- [MNQ_FORMAT.md](MNQ_FORMAT.md): controlled cancellation, resume and key sidecars.
- [docs/client-protocol.md](docs/client-protocol.md): application protocol.

Linux x64 is the current primary test target. Windows x64 has been built/tested;
architecture and toolkit-specific limits are documented with each component.

## License

Copyright Mashrien. This project's original code is licensed under
[GNU GPL version 3](LICENSE) (`GPL-3.0-only`). Bundled and downloaded dependencies
retain their own copyright and license notices.
