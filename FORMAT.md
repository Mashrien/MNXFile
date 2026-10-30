# MNX7 disk format

This document describes the format emitted by this project. It is not a compatibility claim for older prototypes. All integers are little endian. GUID fields occupy two little-endian 64-bit parts, exactly 16 bytes in .NET GUID byte order. Text is UTF-8. Chunk numbers are one based after the header, and physical offset is `chunkNumber * chunkSize`. The current format permits 4096-byte or 32768-byte chunks; the default is 32768 bytes. Chunk size is fixed when the archive is created.

Archive files use the `.mnx` extension. The signatures are `MNX7` for archive headers, `MNXI` for the front index, and `MNXC` for data chunks. Index encryption uses the `MNXI7` authentication domain. Earlier prototype formats are not supported.

## Physical layout

| Chunk | Content |
| --- | --- |
| 0 | Archive header, padded to `chunkSize` bytes |
| 1 through N | Complete front index, followed by zero padding |
| N+1 through chunkCount | Data or reusable free chunks |

The archive length is exactly `(chunkCount + 1) * chunkSize`. `N` is `indexChunks` in the header and starts at four for 4096-byte chunks or one for 32768-byte chunks. It can grow. A file is represented by ordered physical extents; each extent maps consecutive logical chunk sequence numbers to consecutive physical chunk numbers. The index also stores sorted, merged free ranges. Every data chunk is assigned to exactly one entry or free range.

## Header, chunk 0

| Offset | Bytes | Meaning |
| ---: | ---: | --- |
| 0 | 4 | `MNX7` |
| 4 | 2 | Format version, 7 |
| 6 | 2 | Chunk size, 4096 or 32768 |
| 8 | 4 | Chunk size again, 4096 or 32768 |
| 12 | 16 | Archive GUID |
| 28 | 16 | Root directory GUID |
| 44 | 2 | Storage flags in low byte: bit 0 compression, bit 1 encryption, bit 2 continuous layout; high byte reserved, zero |
| 46 | 2 | Codec ID then effort byte |
| 48 | 4 | `chunkCount`, excluding header |
| 52 | 4 | Entry count, excluding synthetic root |
| 56 | 4 | `indexChunks` |
| 60 | 4 | Used index byte count |
| 64 | 32 | SHA-256 of stored index bytes |

A 16-byte PBKDF2 salt is stored at offset 96, a 4-byte iteration count at 112, a 12-byte index nonce at 116, and a 16-byte index authentication tag at 128. SHA-256 of header bytes 0 through 143 is at offset 144. The salt, iterations, nonce, and tag are zero when encryption is disabled. Codec IDs are 0 none, 1 Brotli, 4 Snappy, and 5 LZ4. Brotli effort is 1 through 9; Snappy and LZ4 use fixed effort 0. Archive flag bit 2 selects continuous file compression. LZ4 requires that layout.

## Front index

The used index bytes start at offset `chunkSize`. Encrypted archives store the entire index as AES-256-GCM ciphertext, including names and folder relationships. The header's index checksum is calculated over the stored ciphertext. The plaintext fields are:

| Order | Type | Meaning |
| --- | --- | --- |
| 1 | 4 bytes | ASCII `MNXI` |
| 2 | `uint16` | Index version, equal to archive version |
| 3 | `uint16` | Reserved, zero |
| 4 | `uint32` | Entry count |
| 5 | `uint32` | Free range count |
| 6 | Repeated entries | Sorted by file GUID |
| 7 | Repeated free ranges | Sorted by physical start |

Each entry has file GUID (16), parent GUID (16), first physical chunk (`uint32`), original byte size (`uint64`), UTC creation/modification/access ticks (three `int64`), attributes (`uint16`), directory flag (`uint8`), name byte count (`uint16`), name bytes, physical extent count (`uint32`), and that many extents. Each extent has physical first chunk (`uint32`) and count (`uint32`); logical sequence positions are implicit from prior extents. Names are one path segment, 1 to 65535 UTF-8 bytes. Paths are represented by parent IDs rather than concatenated names, so deep directory trees do not consume the per-component name allowance. Names are preserved without sanitizing or truncating; extraction rejects a name that the destination operating system cannot represent.

Each entry then stores a chunk-length group count (`uint32`) and `(count, bytesPerChunk)` pairs, both `uint32`. Groups follow file sequence order and cover every chunk. Independent layouts index uncompressed bytes represented by each chunk, up to 65536 bytes. Continuous layouts index the compressed stream bytes stored in each physical chunk. Empty files and directories have one chunk and one zero-length group. A free range is `(physicalStart, count)`, both `uint32`.

## Data chunks

Each used data chunk begins with a 128-byte packed fixed header. The implementation reads/writes its unmanaged struct directly from/to the chunk buffer; inline UTF-8 names follow separately. `Streamed` is set on file/continuation chunks in a continuous-compression archive, and is forbidden on directories and alongside independent `Compressed`.

The shared storage flag definition is used by both archive and chunk headers. Schema 7 adds the chunk-level `Streamed` bit and moves the name bits to positions 3 and 4. Only this schema is accepted.

Fixed header fields:

| Offset | Bytes | Meaning |
| ---: | ---: | --- |
| 0 | 4 | ASCII `MNXC` |
| 4 | 1 | Kind: file 1, directory 2, continuation 3 |
| 5 | 1 | Stored flags: bit 0 compressed, bit 1 encrypted, bit 2 `Streamed`, bit 3 `DoubleNameLength`, bit 4 `IndexName` |
| 6 | 2 | Reserved, zero |
| 8 | 16 | File GUID |
| 24 | 16 | Parent GUID |
| 40 | 4 | Next physical chunk, or zero |
| 44 | 4 | Logical chunk sequence, starting at zero |
| 48 | 4 | Stored payload byte count |
| 52 | 8 | Original file size on first chunk, otherwise zero |
| 60 | 24 | UTC creation, modification, access ticks |
| 84 | 2 | Attributes |
| 86 | 2 | Name byte count |
| 88 | 32 | SHA-256 of stored payload, or nonce/tag for encryption |
| 120 | 8 | First eight bytes of the header SHA-256 seal |

First file and directory chunks use a 512 byte header. Their default inline name field occupies bytes 128 through 255, up to 128 bytes. `DoubleNameLength` expands that field to bytes 128 through 383, up to 256 bytes, without moving the payload at offset 512. Longer names use `IndexName`: inline name length is zero and the complete name is read from the front index. Encrypted archives also set `IndexName` to keep names hidden. These name flags are mutually exclusive and forbidden on continuation chunks. Continuations use a 128 byte header and payload begins at 128. The header seal hashes bytes 0 through 119; for first chunks it additionally hashes the selected 128- or 256-byte inline field. Unused bytes are zero padded. Header seals detect accidental damage, while encrypted payloads use the authentication tag for cryptographic integrity.

For an encrypted archive, descriptive data chunk metadata is blank: name, parent GUID, timestamps, attributes, and size. It remains in the encrypted front index. File GUID, sequence, next pointer, kind, stored flags, and stored length remain visible for physical navigation and compaction.

Independent layouts compress each logical chunk separately using Brotli or raw Snappy blocks. Brotli levels 1 through 9 map to quality 1 through 11. A chunk stores compressed data only when smaller than plaintext and within the physical payload limit; otherwise it stores raw bytes. The writer uses a bounded 64 KiB sliding source window and reduces the logical chunk length until its stored data fits. Indexed chunk-length groups allow range lookup without decoding earlier chunks.

Continuous layouts pass the whole file through one compressor before splitting its output into physical chunks. Brotli remains continuous; Snappy and LZ4 use framed streams. LZ4 uses linked 64 KiB blocks. A 4096-byte physical chunk carries up to 3584 bytes in its first chunk and 3968 in continuations; a 32768-byte chunk carries 32256 and 32640 bytes respectively. Data flag bit 0 stays clear: the archive header identifies stream compression, while individual physical slices can be independently encrypted. Distant reads decode from that file's beginning with bounded memory. Header and front index remain uncompressed.

## Encryption

Encrypted archives derive a 32 byte key with PBKDF2-HMAC-SHA256 using the header salt and 600000 iterations. They encrypt the index and each data payload independently with AES-256-GCM, a fresh random 12 byte nonce, and a 16 byte tag. For an encrypted data chunk, header bytes 88 through 99 hold the nonce, 100 through 115 hold the tag, and 116 through 119 are zero. The authenticated data is archive GUID (16), file GUID (16), sequence (`uint32`), kind (`uint8`), stored flags (`uint8`), and stored length (`uint32`). The index authenticated data is ASCII `MNXI7` followed by the archive GUID. Physical position and next pointer are intentionally outside authenticated data so compaction can move ciphertext while updating links.

Without encryption, each chunk stores SHA-256 of the stored payload. An encrypted archive requires the passphrase before its index can be listed. Counts, flags, and total length remain visible in the plaintext archive header. There is no key recovery, key rotation, or crash transaction protocol in this version.

## Mutation and compaction

Adding a file reuses free chunks before extending the archive. Deletion records its extents as free ranges. Compaction first moves a whole contiguous file when its chunk count fits a hole closely; it then moves individual tail chunks into remaining holes, updates the predecessor link and extents, and truncates free chunks at the tail. Front index growth moves only chunks that occupy the newly reserved index region. These are in-place operations: an interrupted mutation may leave the archive inconsistent. Maintain an independent backup of valuable archives before modifying them.

## Cooperative cancellation

`Options.CancellationToken` is checked during recursive queue scans, between queued entries, and between bounded input reads during file writes. Cancelling an in-progress file removes its newly allocated chunks and index entry; files already committed remain available. This is cooperative cancellation, not protection against power loss or process termination. The GUI can instead stop at a completed-file boundary and save the completed subset. New GUI archives are built in a sibling temporary file and replace their selected output only after success or an explicit keep-progress choice.
