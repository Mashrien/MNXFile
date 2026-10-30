# Linux release packages

`package_linux.py` packages an existing native `linux-x64` output directory. It
creates a portable tar.gz, an RPM, a DEB and a component SHA256SUMS file. It never
installs the packages. The adjacent `component.json` selects this repository's
files, identity and source URL. Each repository maintains its own packaging copy.

Run with Python 3.11 or newer, `readelf`, `rpmbuild` and `dpkg-deb` installed:

```sh
python3 packaging/package_linux.py \
  --input /absolute/path/to/linux-x64 \
  --output /absolute/path/to/release-assets \
  --version RELEASE_VERSION
```

The checked-in `release.Dockerfile` records the Ubuntu 24.04 Noble/.NET 10 SDK
release build environment, with the SDK base image pinned by digest. Build it with:

```sh
docker build -f packaging/release.Dockerfile -t mnx-release-sdk:noble .
```

Run the repository build script inside that container with the repository and its
output directory mounted. The installed GTK libraries, Xvfb, D-Bus and desktop
keyring utilities support isolated GUI smoke checks. Apt packages remain resolved
from Ubuntu repositories at image build time; preserve the built image digest and
package inventory when exact environment reproduction is required.

Build the native input with the Ubuntu 24.04 build baseline. Packaging checks ELF
architecture and rejects any binary requiring a glibc symbol newer than 2.39. The
engine must carry SONAME `libmnxfile.so.1`. App inputs must already contain all
listed native files; the GUI requires its SkiaSharp and HarfBuzzSharp libraries.
Only the declared component binaries are copied, so apps do not bundle an engine.

The tar keeps the native files together and includes LICENSE, INSTALL.txt,
RELEASE-INFO.json and any notices under the input/repository THIRD-PARTY or licenses
directories. NOTICE and THIRD-PARTY-NOTICES text/Markdown files are also copied.
Before packaging, explicitly stage every notice listed in `component.json` under
`required_notices` for the target platform into the input's `THIRD-PARTY/` directory
or the repository's `THIRD-PARTY/` directory. The Linux packager rejects missing
required notices. Ordinary build scripts do not yet collect all release notices
automatically. Notice versions must match the actual native/compiler dependencies;
update the manifest when those dependency versions change.
The engine header is included in its portable bundle, but no development package
is generated. Corresponding source must accompany the release through its source
tag/archive; creating these binary packages does not publish that source.

RPM versions and DEB versions map `1.2.0-alpha.3` to `1.2.0~alpha.3`, with package
revision 1. Download filenames retain `-alpha.N`, avoiding GitHub's renaming of
`~` characters; package metadata still uses `~alpha.N` for correct version ordering.
Numeric four-part GUI versions are retained. RPM derives ELF native
requirements and adds dependencies loaded at runtime. Both formats explicitly
require glibc >= 2.39 and OpenSSL >= 3, plus engine ABI 1/protocol 1 for clients.
GUI toolkit requirements accept GTK3 >= 3.24 or GTK4 >= 4.12. NativeAOT needs no
.NET SDK or runtime. Optional password saving uses the desktop Secret Service.

The engine installs in `/usr/lib64` (RPM) or `/usr/lib/x86_64-linux-gnu` (DEB).
CLI executables install in `/usr/bin`. GUI files install in
`/usr/libexec/mnxarchiver`, with a `/usr/bin/mnx-gui` launcher that preserves all
arguments. Its desktop entry, icon, MIME definition and Dolphin service menus
install to standard `/usr/share` locations. Both creation and extraction appear
under the **MNX Archive** submenu. Package scripts refresh desktop caches after
installation/removal, and the engine updates ldconfig.

For reproducible output, use identical native inputs, packaging sources, Python,
RPM and dpkg tool versions, and a fixed timestamp. Supply `--epoch UNIX_SECONDS`
or `SOURCE_DATE_EPOCH`; otherwise the latest repository commit timestamp is used.
Pass `--source-commit FULL_SHA` to record a specific native source revision; otherwise
the current repository HEAD is recorded in RELEASE-INFO.json and INSTALL.txt. The
caller is responsible for matching that revision to the native input.

Archive ownership/modes/times, compression headers, RPM build host/time and DEB
ownership/times are normalized. Build twice to separate output directories and
compare their SHA256SUMS files. This proves package construction reproducibility;
native compilation reproducibility is a separate build concern.

Inspect packages before publication:

```sh
rpm -qpl /path/to/package.rpm
rpm -qp --requires /path/to/package.rpm
rpm -qp --scripts /path/to/package.rpm
dpkg-deb --contents /path/to/package.deb
dpkg-deb --info /path/to/package.deb
tar -tzf /path/to/package.tar.gz
```

Metadata, contents and extraction checks do not prove installation or desktop
integration on a clean target distribution. Validate those separately before
claiming a particular distribution has been tested.

## Windows ZIP

Build and validate the Windows x64 output on Windows first. The ZIP packager itself
uses only Python's standard library and may run on either OS:

```sh
python3 packaging/package_windows.py \
  --input /absolute/path/to/win-x64 \
  --output /absolute/path/to/release-assets \
  --version RELEASE_VERSION --source-commit FULL_SHA --epoch UNIX_SECONDS
```

It checks PE x64 architecture, mandatory files and the `win-x64` notice manifest,
then produces a reproducible ZIP and checksum file with installation instructions.
The engine ZIP carries its DLL, C header/import library and per-user installer;
CLI contains both programs, and GUI contains the self-contained WinForms executable.
Client ZIPs never bundle the engine. Keep the engine independently installed.
