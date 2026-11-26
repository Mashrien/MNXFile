# MNX 0.1.8 front index and capacity

The package release version and storage format are independent. `LibraryInfo.Version`
reports the loaded engine package, `LibraryInfo.FormatVersion` reports the format it
writes (`0.1.8`), and `Archive.SemanticFormatVersion` reports the opened archive.
Wire version 7 maps to `0.1.7`; wire version 8 maps to `0.1.8`.

## Capacity and widths

The physical archive ceiling is **140,737,488,322,560 bytes**, exactly
`UInt32.MaxValue * 32768`, including the archive header and front index. With
32 KiB chunks, the greatest data/index chunk number is therefore
`UInt32.MaxValue - 1`. With 4 KiB chunks, the UInt32 chunk-address ceiling is
reached first. Allocation and index relocation preflight the complete destination
range before changing archive state or writing relocated data.

| Quantity | Width | Meaning |
|---|---|---|
| Physical archive capacity | UInt64 | Includes header, index and stored data |
| Original file bytes | UInt64 | Public and stored whole-file length |
| Index logical byte length | UInt64 | No 2 GiB array or 4 GiB index-length ceiling |
| Metadata record offset and length | UInt64 each | Relative to the logical front index |
| Physical/sequence chunk number | UInt32 | Individual chunk address or file sequence |
| Per-chunk payload length | UInt32 | One bounded physical/compression payload |
| Entry and directory count | UInt32 | Existing layout allocates one metadata chunk per entry, even with zero payload |
| Stream offset/length | checked Int64 | .NET Stream boundary; arithmetic is widened first |

No archive payload is loaded as one allocation. Compression uses its existing
continuous 64 KiB window; payload AES-GCM remains per physical chunk. A large
contiguous file uses compact extent and logical-length runs. Explicitly requesting
`OrderedChunks` still materializes the legacy per-chunk dictionary view; callers
should use `ChunkExtents` and normal stream/range reads for large files.

## Physical front region

The archive header is chunk zero. Its `IndexChunkCount` identifies the complete
front index reservation in ordinary physical chunks 1 through N. All file chunks
follow this reservation. New archive magic is `MNX8`; existing `MNX7` archives remain
readable. Header offset 60 stores the low 32 index-length bits; offset 176 stores
the high 32 bits. The wire8 header checksum protects both fields.

Each used index chunk has a 64-byte envelope: SHA-256 at bytes 0-31, a 12-byte
nonce at 32-43, a 16-byte AES-GCM tag at 44-59, and four zero reserved bytes.
The remaining 4,032 or 32,704 bytes form a logical index page. SHA-256 covers bytes
32 through the end of that physical chunk. The archive header stores the digest
of the first complete physical index page. Encrypted pages authenticate the
archive ID, wire8 index domain and UInt64 page ordinal; every write uses a fresh
random nonce. Unencrypted pages retain the same framing and per-page checksum.

The logical 128-byte `MNXIDX8` header contains entry/directory/free-range counts,
UInt64 offsets for both tables, metadata and free ranges, the logical length, and
the total free-chunk count. It is followed by:

1. A GUID-sorted table of 56-byte descriptors.
2. An owner-sorted table of the same descriptors, with directories before files
   for each owner and GUID ordering within each group.
3. Variable-length metadata records, physically contiguous in owner-table order.
4. Sorted `(UInt32 first chunk, UInt32 count)` free ranges.

Each descriptor stores entry GUID, owner GUID, UInt64 record offset, UInt64 record
length, directory flag and reserved field. Both tables address the same record.
Records retain compact physical extents and logical-length runs. A GUID lookup
uses binary search; folder listing binary-searches the owner table and reads only
its contiguous records. `HasSubdirectories` checks owner descriptors without
constructing child VFiles. It does not recursively load folder contents.

## Resident working set and rebuilds

Normal wire8 opening reads the header and root index page, without parsing every
record or loading the free-range table. The engine retains at most 64 clean index
pages and 512 clean VFiles, with an additional estimated 8 MiB metadata budget.
A requested VFile larger than that budget is returned without adding it to the
clean cache. Requested directory results belong to the caller; the GUI pins its
current folder, parent and their immediate child lists independently.

Free ranges are page-backed during browsing. The first allocator mutation
materializes and validates the free-range set; writes may also accumulate changed
or deleted entries in a dirty overlay. These write-time structures are released
or rebased after commit. Large edit batches therefore require memory for their
pending changes, while clean browsing does not retain the entire archive index.

Rebuilds stream records through temporary storage and use a bounded external
merge sort: 4,096 descriptors per run and at most 64 readers per merge, with
bounded levels for UInt32 entry counts. The owner-order record placement also
keeps a folder's metadata reads contiguous on a NAS. The rebuilt index is written
one authenticated page at a time. No monolithic index `byte[]`, full clean-entry
dictionary or external database is used for wire8.

All temporary index streams, including sort runs, use per-file ephemeral
AES-256-GCM keys, fresh nonces for every block rewrite, owner-only Unix creation
permissions and delete-on-close files. Archive passphrase keys are not reused
for scratch encryption. No plaintext metadata or names are written to scratch.

Wire7 sources are never changed merely by opening them. Unencrypted wire7 indexes
are converted through a bounded two-pass reader into an encrypted temporary paged
index. **Encrypted wire7 is the compatibility exception:** its old single AES-GCM
tag requires the legacy whole-index authentication buffer and transient legacy
metadata during opening. After conversion, that temporary full index is released
and steady-state browsing uses the same bounded caches. The next write upgrades
the archive to wire8. Wire7's original format cannot provide wire8's independent
page authentication without conversion.

## API and verification

`GetEntry`/`GetFile`, `DirectoryList` and `HasSubdirectories` perform direct lookups.
`EnumerateFiles(offset, count)` supports bounded whole-index enumeration; native
JSON responses for `enumerate-files` contain at most 256 entries. The additive
`mnx_list_page` C ABI offers the same 256-entry maximum. `Files` remains an explicit
materializing compatibility view; the new client obtains it through bounded
native pages. New browser code should use the direct folder and entry methods.

Checksums and metadata bounds are validated as pages and records are requested.
`VerifyArchive` deliberately clears clean page caches and rereads physical bytes,
then checks lookup ordering/coverage, parents/cycles, free-range totals and
occupied overlap, all chunk chains, payload checksums/tags and compression streams.
It does not trust a previous warm-cache verification.

The smoke suite includes a generated sparse index of **4,410,000,128 logical bytes
and 21,000,000 entries**, without creating a giant physical file. A 100-child folder
requires 16 cold physical page reads and zero additional warm reads; scattered GUID
queries leave only 64 pages and 512 clean VFiles resident. Further checks cover
cross-2/4-GiB file offsets, exact physical capacity, failed-growth preflight, malformed
record boundaries/counts, encrypted scratch, warm-cache corruption, wire7 read/upgrade,
and encrypted/unencrypted rebuild/reopen/delete/compaction.
