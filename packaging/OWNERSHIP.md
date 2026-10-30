# Engine package ownership

The runtime package owns one installed native engine: `libmnxfile.so.1` on Linux and `MNXFile.Native.dll` on Windows. It must carry the matching C ABI/application protocol and produce the SONAME `libmnxfile.so.1`. Its codec implementations are part of the engine. The managed client package contains protocol/interop code and no engine or codec binaries.

An RPM runtime package should expose `libmnxfile.so.1()(64bit)` and record the protocol version supported by the engine. Native runtime dependencies should be derived from the actual binary with RPM tooling; do not guess them from the NuGet dependency list. Check the built SONAME with `readelf -d` before packaging. Put `mnxfile.h` and the unversioned linker symlink into a matching devel package. The native runtime package owns no CLI executable, GUI executable, launcher, MIME definition, or Dolphin service menu.

The managed NuGet engine and client packages go to `MNX_Builds/mnxfile/packages`; neither is automatically uploaded. The apps pin client version `1.2.0-alpha.3`. Independent package sources can be supplied using `MNXPackageFeed` to the app build scripts.

The project is GPL-3.0-only. Linux release packaging is implemented in `package_linux.py`; see `README.md` for commands and verification. Signed metadata and installation/dependency validation on a clean machine remain separate release gates. No installation is performed by the build scripts.

Linux encryption/hashing uses the platform OpenSSL backend. On Fedora/Nobara, declare openssl-libs explicitly: it is loaded at runtime and may not appear in ELF automatic dependency scanning. Native C runtime/glibc requirements still depend on the build baseline.
