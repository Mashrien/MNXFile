# MNXFile native interface (ABI 1)

This repository builds `libmnxfile.so.1` on Linux and `MNXFile.Native.dll` on
Windows: a Native AOT shared archive engine with C ABI1 and application protocol1.
The `mnx`/`mnxi` applications live in [mnx-cli](https://github.com/Mashrien/mnx-cli)
and call this shared engine; neither embeds a private engine.

The library owns archive and file I/O, buffering, compression and encryption.
Callers supply operations, paths, options and progress callbacks. No exported chunk
pointers or caller-supplied payload streams are needed. Native AOT includes the .NET
runtime; system dependencies still apply, including glibc/OpenSSL on Linux. Native
builds use invariant globalization (no ICU requirement); names remain UTF-8.

## Build and deployment

From the repository root, `MNXBuildNumber=3 ./build.sh --test` builds the matching
engine/client packages and native library on Linux. On Windows use
`$env:MNXBuildNumber='3'; .\build.ps1 -Runtime win-x64 -Test` with the .NET10 SDK,
Visual Studio C++ tools and Windows SDK. Native AOT is built on the target OS.
The compatibility wrappers `scripts/publish-native.sh` / `.ps1` call those builds.

Outputs are under `../MNX_Builds/mnxfile/{linux-x64,win-x64}`; debug symbols stay
under `../mnx_clutter/build/mnxfile/symbols`. The release includes the C header and
Windows import library, plus Linux development aliases. See the
[root README](../README.md) for installation/staging helpers and feed setup.
Linux x64 is the primary validated target; Windows x64 has been built and tested.
ARM64 targets have not had equivalent runtime validation.

## Calling conventions and lifetime

Include `native/include/mnxfile.h` in C or C++. Other languages can bind the same symbols through their FFI. Functions use `cdecl`; all strings are NUL-terminated UTF-8. Archive/entry identifiers are canonical GUID strings, and timestamps are UTC Unix milliseconds. Handles are opaque 64-bit integers; they are not pointers. Call `mnx_close` once for each successful create/open. Failed create/open calls return handle zero. Closing a stale handle reports an error rather than accessing freed memory.

The ABI is deliberately synchronous. Each operation finishes before its call returns; successful mutations are committed without a separate save call. Calls on the same handle serialize; separate handles must not modify the same archive concurrently. There is no background I/O scheduler. Progress callbacks run on the thread making the call, so keep them short. Calling back into the same archive from its notification callback returns `MNX_BUSY`; do not wait in a callback for another thread to operate on that archive. Callback/context pointers must remain valid until unsubscribed or the archive closes. A callback must not throw across the C boundary.

Load the native library once and retain it for the application's lifetime. Native AOT does not support unloading the library with `dlclose` or `FreeLibrary`. Release archive handles with `mnx_close` instead.

Caller-owned metadata structs and diagnostic buffers remain owned by the caller. No memory needs freeing through MNXFile. Callback name strings are temporary and must be copied if retained. Pointer arguments must designate valid memory of the advertised size; the library checks NULL, sizes, enums, and IDs, but cannot validate arbitrary foreign addresses. Input strings are limited to 1 MiB of UTF-8 bytes. Version 7 entry names can occupy up to 65535 UTF-8 bytes. The ABI-1 `mnx_entry_info.name[129]` field remains a UTF-8-safe 128-byte prefix to keep metadata structs small. Use `mnx_get_entry_name` for the complete name: query with NULL buffer and zero capacity, then allocate the reported byte count (including NUL) and call again. Listing and the complete-name accessor use only the front index.

Creation settings are immutable: codec, effort, layout, chunk size, and encryption are chosen by `mnx_create`. Defaults are no compression, 32768-byte chunks, and continuous layout for compressed files. Brotli uses effort 1–9; Snappy and LZ4 use fixed effort 0. A NULL password creates/opens an unencrypted archive; an encrypted archive requires its password when opening. The entire file/folder index is encrypted alongside file contents. Metadata listing still uses only the front index, without reading file payloads.

Additions replace duplicate names by default, matching the managed library. Call `mnx_set_overwrite(handle, 0)` to refuse replacements. Extraction has its own explicit overwrite argument; zero protects existing destination files. `mnx_extract_directory` extracts the selected directory's children directly into the supplied destination.

## Example

```c
#include "mnxfile.h"

int make_backup(void) {
    mnx_create_options options;
    mnx_handle archive = 0;
    int32_t status = mnx_options_init(&options);
    if (status != MNX_OK) return status;
    options.compression = MNX_COMPRESSION_SNAPPY;
    options.compression_level = 0;
    status = mnx_create("backup.mnx", NULL, &options, &archive);
    if (status != MNX_OK) return status;
    status = mnx_set_overwrite(archive, 0);
    if (status == MNX_OK) status = mnx_add_directory(archive, "photos", NULL);
    if (status == MNX_OK) status = mnx_verify(archive);
    /* Save the diagnostic before close if status failed: close is another call. */
    mnx_close(archive);
    return status;
}
```

A NULL parent ID selects the archive root. Use `mnx_list` to query the number of entries, allocate metadata storage, and call again to fill it. For `mnx_get_entry` and `mnx_get_info`, initialize `struct_size` to `sizeof` the corresponding struct. See `tests/native/smoke.c` for listing, UTF-8 names, progress subscriptions, extraction, and compaction examples.

## Function list

- `mnx_abi_version`: Return the native interface version, currently 1.
- `mnx_options_init`: Fill creation options with supported defaults and the struct size.
- `mnx_create`: Create a new archive with immutable storage settings and an optional password. Existing archive files are never overwritten.
- `mnx_open`: Open an archive from its front index, authenticating it when encrypted. Return an opaque archive handle.
- `mnx_close`: Release the archive and its notification subscription. Reject a previously closed or unknown handle.
- `mnx_set_progress`: Subscribe or unsubscribe a progress notification function and set the report interval in milliseconds. Notifications contain the current name and completed/total bytes.
- `mnx_set_overwrite`: Choose whether additions can replace an existing entry with the same name and parent.
- `mnx_add_file`: Read a source file from disk and commit its contents internally under the selected parent.
- `mnx_add_directory`: Recursively add a directory tree, including empty directories, under the selected parent.
- `mnx_resume`: Validate an MNQ1 queue and finish its remaining work using the open archive's original compression and encryption. Open encrypted archives with their original password first; after complete source preflight, any matching temporary .mnx-key is authenticated, randomly overwritten, flushed and deleted before payload writes. Success renames the queue as a completed record.
- `mnx_list`: Return complete entry metadata from the front index. A NULL buffer with zero capacity queries the required entry count.
- `mnx_list_directory`: Return metadata for the selected directory's immediate children. NULL parent selects the root.
- `mnx_get_entry`: Return one entry's identity, parent, name, size, and timestamps.
- `mnx_get_info`: Return the archive's settings, identity, disk size, entry counts, and chunk/free-space statistics.
- `mnx_extract_file`: Restore a selected file to the caller's destination path, with explicit overwrite behavior.
- `mnx_extract_directory`: Restore the selected directory's descendants into the caller's destination directory.
- `mnx_delete`: Delete an entry and any descendants, making their chunks reusable.
- `mnx_compact`: Fill holes using whole files where possible, then move tail chunks and truncate the file.
- `mnx_verify`: Check stored checksums/authentication, chains, and decompressed lengths using internal bounded buffers.
- `mnx_last_error`: Copy the last diagnostic on the calling thread, or query its required UTF-8 size. Querying does not consume or change the diagnostic.
- `mnx_last_return_code`: Return the managed MNX flags for the last operation that produced those flags, or zero if that call did not produce flags.

## Errors

Ordinary calls return `MNX_OK` (0) on success and a negative `mnx_status` on failure. Exceptions are caught at the boundary. Retrieve diagnostics immediately, on the same thread, before making another ordinary call. `mnx_last_error(NULL, 0, &required)` returns the required bytes including the final NUL. An undersized buffer returns `MNX_BUFFER_TOO_SMALL` and preserves the diagnostic. Successful ordinary calls clear the diagnostic.

Metadata list capacity is measured in entries; diagnostic capacity is measured in bytes. An undersized metadata list returns the full required count without partially filling the buffer. Output metadata is valid only on success.

## Verified on Linux x64

The plain C integration test passes for uncompressed archives and encrypted Brotli, Snappy, and LZ4 archives. It covers UTF-8 names, callback reentry, metadata sizes, rejected duplicate additions, extraction, recursive deletion, compaction/truncation, and reopening. The managed smoke suite covers the current MNX7 format, all current codecs, cancellation/resume and corruption detection.

`tests/native/cli_smoke.py` exercises archive operations with all four storage choices. `tests/native/list_smoke.py` checks directory selection and recursive path columns; `tests/native/search_smoke.py` checks case-insensitive name matching and proves search does not read data chunks. The archive CLI and C integration tests also passed in a temporary mount namespace with `/usr/lib64/dotnet` hidden and `DOTNET_ROOT` pointing to a nonexistent directory. This does not modify the installed framework. To repeat that check on this machine:

```sh
mnx_work=$(mktemp -d)
bwrap --ro-bind / / --dev-bind /dev /dev --bind /tmp /tmp \
  --tmpfs /usr/lib64/dotnet --setenv DOTNET_ROOT /dotnet-not-installed \
  -- ../mnx_clutter/build/tests/linux-x64/native-smoke "$mnx_work"
bwrap --ro-bind / / --dev-bind /dev /dev --bind /tmp /tmp \
  --tmpfs /usr/lib64/dotnet --setenv DOTNET_ROOT /dotnet-not-installed \
  -- python3 tests/native/cli_smoke.py ../MNX_Builds/mnx
rm -rf -- "$mnx_work"
```

The `/dev` bind keeps the OS random source available for encryption and GUID generation.

The wrapper preserves the managed library's storage limits and its in-place update behavior. Native AOT changes deployment and interoperability, not the MNX archive format or crash-recovery guarantees.

On Linux, `--test` also denies all ICU library loads for the C integration
checks and a real CLI archive-open check. The audit module is confined to those
test processes; it does not change the desktop environment or installed packages.
