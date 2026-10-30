# MNQ1 remaining queue sidecar

Choosing **Keep current progress** in the GUI finishes the current file and saves `<archive basename>.mnq` beside the kept archive. The sidecar records queued entries that have not been committed. It is UTF-8 JSON, uses a source-generated serializer compatible with Native AOT, and contains no passwords or encryption keys. File names and source paths are plain text, even when the archive is encrypted; on Unix the file is created with user-only read/write permissions (0600).

Resume is available in the GUI through **File → Resume from queue…** or by supplying a queue file as the program's argument. The CLI accepts `mnx resume QUEUE [ARCHIVE] [--password-env VARIABLE]`; the optional archive path handles relocated/renamed archives. Ctrl+C in CLI resume finishes the current file and writes an updated remaining queue. The GUI provides the same Keep/Delete choices as archive creation.

Resume uses the archive's stored compression codec, effort, layout, chunk size, encryption salt and key derivation settings. Encrypted archives reopen with their original password, either entered by the user or recovered from an explicitly exported matching .mnx-key file. The queue itself never stores that password or the derived archive key.

The reader verifies the exact queue payload checksum, current archive header/front-index fingerprints, destination IDs and folder relationships, and availability/type/size/modification time of every remaining source before restoring any queue items. It refuses existing-name replacements and source links. Moving the queue alongside its archive is supported. Completed queues are renamed with a `.completed-<timestamp>-<unique ID>` suffix.

Abrupt process termination or power loss does not automatically produce this sidecar. This controlled cancel/resume support is not a crash transaction journal.

## Envelope

- `Format`: `MNQ1`.
- `ChecksumAlgorithm`: `SHA256`.
- `PayloadSha256`: uppercase hexadecimal SHA-256 of the exact UTF-8 JSON bytes of the `Payload` object, without the property name or surrounding envelope. The writer emits compact JSON; a reader can hash the raw payload substring before parsing or reformatting it.
- `Payload`: an object containing `CreatedUtc`, `Archive`, and `Entries`.

Checksums detect corruption or a mismatched archive. They are not a keyed authentication mechanism.

## Archive identity

`Archive` includes:

- `Name` and `FullPath`: selected output archive basename and absolute path.
- `ArchiveId`, `RootDirectoryId`: GUIDs from the kept archive header.
- `FormatVersion`, `ChunkSize`, `Length`, `IndexChunks`, `IndexBytes`.
- `HeaderSha256`: SHA-256 of the complete header chunk, including padding.
- `IndexSha256`: SHA-256 of the exact used stored front-index bytes, excluding reserved padding. For an encrypted archive these are ciphertext bytes.

These fingerprints require reads only from the front of the archive. Export does not reread or hash terabytes of file payloads.

## Remaining entries

`Entries` preserves the original parent-before-child queue order. Every entry includes:

- `EntryId`: the ID assigned while queuing. Preserve it when creating an unfinished folder so its queued children can resolve `ParentId`.
- `ParentId`: existing root/folder ID, or `EntryId` of an earlier remaining folder.
- `Name`: exact destination filename component, without substitution or truncation.
- `SourcePath`: absolute input file or folder path.
- `IsDirectory`: folders are included so empty folders and uncreated parents can be resumed.
- `SourceLength`: byte length (zero for folders), or null if the source was unavailable when saving.
- `SourceModifiedUtc`: source modification time in UTC, or null if unavailable.

Completed files and folders are excluded. The source size and timestamp are change-detection hints, not content checksums. The resume reader validates the queue checksum and archive identity/front-index fingerprints, confirms source availability and metadata, preserves destination relationships, then adds only these entries. The current archive may have been moved, so the stored path is a locator rather than its sole identity.

## Saving and collisions

The writer saves a sibling temporary file, flushes it, and renames it into place. If `<archive basename>.mnq` already exists, it retains a copy named `<archive basename>.previous-<UTC timestamp>-<unique ID>.mnq` before replacement. Different archives in one folder have separate queues. The reader still accepts older MNQ1 queues named remaining_queue.mnq.

## Optional temporary password sidecar (MNXKEY1)

The Cancel question includes **Export key-file**, unchecked each time and enabled only for encrypted archives. Choosing Keep with it checked saves backup.mnx, backup.mnq, and backup.mnx-key together. Choosing Delete never exports a key. Saving a replacement queue without export overwrites and deletes a stale key. Key files are never copied into queue backups.

GUI and CLI resume automatically read the matching key when a password was not explicitly supplied. The native mnx_resume call consumes it after the caller opens the archive with its original password. A key can be read only after validating the header's stored SHA-256 checksum, archive identity, full header fingerprint, front-index checksum/fingerprint and queue payload checksum. Password bytes use AES-256-GCM, a new random 12-byte nonce and a 16-byte tag; the entire queue file, full header fingerprint, archive UUID and a format domain are bound as associated data. No archive payload scan is needed; payload integrity is checked by the normal archive verification/extraction paths.

### Key derivation and bytes

This is deliberately a convenience wrapper with **publicly reconstructible key derivation**, not an independent password vault. Anyone possessing the archive, queue and key sidecar can recover the password. Checksums and the wrapping tag detect mismatches/corruption; they do not protect against an editor who reconstructs the wrapping key.

The AES key is HKDF-SHA256 with:

- Input key material: UTF-8 MNX temporary resume password v1.
- Salt: the validated archive header's stored 32-byte SHA-256 checksum (offset 144 in MNX7).
- Info: UTF-8 MNXKEY1 AES-256-GCM.
- Output length: 32 bytes.

Associated data is the domain's UTF-8 bytes, followed by the archive UUID's 16 .NET Guid.ToByteArray() bytes, the decoded 32-byte full-header SHA-256 fingerprint, then SHA-256 of the exact entire .mnq file bytes. It does not contain local filenames, so all three files can move together.

Binary file layout:

| Offset | Length | Contents |
| --- | --- | --- |
| 0 | 8 | ASCII MNXKEY1 followed by a zero byte |
| 8 | 12 | Random GCM nonce |
| 20 | 16 | GCM authentication tag |
| 36 | 4 | Little-endian signed password byte count, positive and at most 1 MiB |
| 40 | Count | AES-256-GCM encrypted UTF-8 password bytes |

### Consumption and deletion

After successfully opening the archive and validating **all remaining sources and destinations**, resume authenticates the key again, checks that its password matches the open archive, overwrites its full length with fresh random bytes in bounded buffers, flushes those writes to disk, closes it, then deletes it. This happens before adding another file. If overwriting/flushing fails, deletion is not attempted and resume reports the error. Header, queue, password and source-validation failures preserve the key for correction/retry.

The live archive retains password bytes in memory for a possible later checked cancellation and clears that byte buffer on Close/Dispose. Another interruption can write a fresh key for the updated queue/header. Unix sidecars are created with 0600 permissions, links are refused, and completed queues cannot be marked completed while an unconsumed key remains. Explicitly discarding recovery files uses the same overwrite/flush/delete sequence.

Overwriting requests actual random writes before unlinking; SSD wear leveling, copy-on-write filesystems and storage snapshots can retain older physical copies. No physical-erasure guarantee is made.
