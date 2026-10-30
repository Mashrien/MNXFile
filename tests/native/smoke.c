#if defined(_WIN32)
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "mnxfile.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(_WIN32)
#include <direct.h>
#include <windows.h>
/* Keep the test's disk I/O UTF-8 too, regardless of the Windows code page. */
static FILE *utf8_fopen(const char *path, const char *mode) {
    wchar_t wide_path[4096], wide_mode[16];
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide_path, 4096) ||
        !MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, mode, -1, wide_mode, 16)) return NULL;
    return _wfopen(wide_path, wide_mode);
}
static int utf8_mkdir(const char *path) {
    wchar_t wide_path[4096];
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide_path, 4096)) return -1;
    return _wmkdir(wide_path);
}
#define fopen utf8_fopen
#define MKDIR(p) utf8_mkdir(p)
#else
#include <sys/stat.h>
#define MKDIR(p) mkdir(p, 0700)
#endif

_Static_assert(sizeof(mnx_create_options) == 24, "options ABI size");
_Static_assert(sizeof(mnx_archive_info) == 144, "archive info ABI size");
_Static_assert(sizeof(mnx_entry_info) == 248, "entry info ABI size");
static void check(int condition, const char *message) {
    if (!condition) { fprintf(stderr, "FAIL: %s\n", message); exit(1); }
}
static void ok(int32_t code, const char *message) {
    if (code != MNX_OK) {
        char error[2048]; uint64_t needed = 0;
        mnx_last_error(error, sizeof error, &needed);
        fprintf(stderr, "FAIL: %s: %d %s\n", message, code, error); exit(1);
    }
}
static unsigned char expected(size_t offset, unsigned seed) {
    return (unsigned char)((offset * 37 + (offset >> 13) * seed) & 255);
}
static void write_source(const char *path, size_t size, unsigned seed) {
    FILE *file = fopen(path, "wb"); check(file != NULL, "create source");
    for (size_t i = 0; i < size; ++i) fputc(expected(i, seed), file);
    check(fclose(file) == 0, "close source");
}
static void check_extracted(const char *path, size_t size, unsigned seed) {
    FILE *file = fopen(path, "rb"); check(file != NULL, "open extracted");
    for (size_t i = 0; i < size; ++i) check(fgetc(file) == expected(i, seed), "extracted bytes");
    check(fgetc(file) == EOF, "extracted length"); fclose(file);
}
struct progress { mnx_handle archive; unsigned calls; };
static void MNX_CALL on_progress(void *context, const char *name, int64_t complete, int64_t total) {
    struct progress *progress = context;
    check(name && complete >= 0 && complete <= total, "progress counts");
    ++progress->calls;
    check(mnx_compact(progress->archive) == MNX_BUSY, "callback reentrancy rejected");
    check(mnx_close(progress->archive) == MNX_BUSY, "callback cannot close active archive");
}

int main(int argc, char **argv) {
    check(argc == 2, "pass an existing temporary work directory");
    check(mnx_abi_version() == 1, "ABI version");
    char path[4096], tree[4096], child[4096], input[4096], second[4096], nested[4096], extracted[4096];
    snprintf(tree, sizeof tree, "%s/tree", argv[1]); MKDIR(tree);
    snprintf(child, sizeof child, "%s/tree/empty", argv[1]); MKDIR(child);
    snprintf(input, sizeof input, "%s/source.bin", argv[1]); write_source(input, 300000, 17);
    snprintf(second, sizeof second, "%s/m\xc3\xa9moire.bin", argv[1]); write_source(second, 700000, 29);
    snprintf(nested, sizeof nested, "%s/tree/nested.bin", argv[1]); write_source(nested, 123457, 43);
    const int32_t codecs[] = {MNX_COMPRESSION_NONE, MNX_COMPRESSION_BROTLI, MNX_COMPRESSION_SNAPPY, MNX_COMPRESSION_LZ4};
    for (size_t c = 0; c < sizeof codecs / sizeof codecs[0]; ++c) {
        snprintf(path, sizeof path, "%s/native-%d.mnx", argv[1], codecs[c]);
        const char *password = c == 0 ? NULL : "native test passphrase";
        mnx_create_options options; ok(mnx_options_init(&options), "default options");
        options.compression = codecs[c];
        options.compression_level = codecs[c] == MNX_COMPRESSION_BROTLI ? 1 : 0;
        mnx_handle archive = 999;
        options.compression = 257;
        int32_t invalid_status = mnx_create(path, password, &options, &archive);
        if (invalid_status != MNX_INVALID_ARGUMENT) ok(invalid_status, "invalid enum status");
        check(invalid_status == MNX_INVALID_ARGUMENT && archive == 0, "invalid enum cannot wrap to valid byte enum");
        options.compression = codecs[c];
        ok(mnx_create(path, password, &options, &archive), "native create");
        mnx_handle rejected = 123;
        check(mnx_create(path, password, &options, &rejected) == MNX_IO_ERROR && rejected == 0,
            "create protects existing archive");
        struct progress progress = {archive, 0};
        ok(mnx_set_progress(archive, on_progress, &progress, 1), "progress callback");
        ok(mnx_set_overwrite(archive, 0), "disable duplicate replacements");
        ok(mnx_add_file(archive, input, NULL), "add disk file");
        ok(mnx_add_file(archive, second, NULL), "add UTF-8 disk file");
        check(progress.calls > 0, "progress notifications delivered");
        ok(mnx_add_directory(archive, tree, NULL), "add directory tree");
        check(mnx_add_file(archive, input, NULL) != MNX_OK, "duplicate addition refused");
        check(mnx_add_file(archive, "/no-such-mnx-source.bin", NULL) == MNX_NOT_FOUND, "missing source fails");
        uint64_t count = 0; ok(mnx_list(archive, NULL, 0, &count), "index-only list size");
        check(count == 5, "failed additions left no extra entries");
        mnx_entry_info entries[5];
        check(mnx_list(archive, entries, 1, &count) == MNX_BUFFER_TOO_SMALL && count == 5, "undersized list");
        ok(mnx_list(archive, entries, 5, &count), "list complete index");
        char disk_id[37] = "", second_id[37] = "", tree_id[37] = "";
        for (size_t i = 0; i < count; ++i) {
            check(entries[i].struct_size == sizeof entries[i], "entry layout");
            if (!strcmp(entries[i].name, "source.bin")) strcpy(disk_id, entries[i].id);
            if (!strcmp(entries[i].name, "m\xc3\xa9moire.bin")) strcpy(second_id, entries[i].id);
            if (!strcmp(entries[i].name, "tree")) strcpy(tree_id, entries[i].id);
        }
        check(*disk_id && *second_id && *tree_id, "UTF-8 metadata");
        mnx_entry_info entry = {0}; entry.struct_size = sizeof entry;
        ok(mnx_get_entry(archive, second_id, &entry), "get entry");
        check(entry.original_size == 700000, "original size");
        ok(mnx_list_directory(archive, tree_id, entries, 5, &count), "list children");
        check(count == 2, "directory children include empty directory");
        check(mnx_get_entry(archive, "bad-guid", &entry) == MNX_INVALID_ARGUMENT, "invalid entry id");
        uint64_t needed = 0; ok(mnx_last_error(NULL, 0, &needed), "error size query");
        check(needed > 1, "error retained");
        char error[1000];
        check(mnx_last_error(error, 1, &needed) == MNX_BUFFER_TOO_SMALL, "error buffer size");
        ok(mnx_last_error(error, sizeof error, &needed), "error message");
        snprintf(extracted, sizeof extracted, "%s/extracted-%d.bin", argv[1], codecs[c]);
        ok(mnx_extract_file(archive, disk_id, extracted, 0), "extract file");
        check_extracted(extracted, 300000, 17);
        check(mnx_extract_file(archive, disk_id, extracted, 0) != MNX_OK, "existing destination protected");
        ok(mnx_extract_file(archive, disk_id, extracted, 1), "explicit overwrite");
        snprintf(extracted, sizeof extracted, "%s/extracted-tree-%d", argv[1], codecs[c]);
        ok(mnx_extract_directory(archive, tree_id, extracted, 0), "extract directory tree");
        snprintf(extracted, sizeof extracted, "%s/extracted-tree-%d/nested.bin", argv[1], codecs[c]);
        check_extracted(extracted, 123457, 43);
        ok(mnx_verify(archive), "native verify");
        mnx_archive_info before = {0}, after = {0};
        before.struct_size = sizeof before; after.struct_size = sizeof after;
        ok(mnx_get_info(archive, &before), "archive stats");
        check(before.chunk_size == 32768 && before.file_count == 3 && before.directory_count == 2, "settings and counts");
        ok(mnx_delete(archive, disk_id), "delete middle file");
        ok(mnx_compact(archive), "compact and truncate");
        ok(mnx_get_info(archive, &after), "stats after compact");
        check(after.size_on_disk < before.size_on_disk, "compaction reduced archive");
        snprintf(extracted, sizeof extracted, "%s/surviving-%d.bin", argv[1], codecs[c]);
        ok(mnx_extract_file(archive, second_id, extracted, 0), "extract relocated file");
        check_extracted(extracted, 700000, 29);
        ok(mnx_set_progress(archive, NULL, NULL, 250), "unsubscribe progress");
        ok(mnx_close(archive), "close archive");
        check(mnx_close(archive) == MNX_INVALID_HANDLE, "double archive close");
        archive = 777;
        check(mnx_open(path, "wrong", &archive) != MNX_OK && archive == 0, "wrong password fails with no handle");
        ok(mnx_open(path, password, &archive), "reopen native archive");
        ok(mnx_verify(archive), "verify persisted compaction");
        ok(mnx_delete(archive, tree_id), "recursive delete directory");
        ok(mnx_list(archive, NULL, 0, &count), "list after recursive deletion");
        check(count == 1, "recursive delete removed descendants");
        char long_name[205], long_source[4096];
        const unsigned char utf8_accent[] = { 0xc3, 0xa9 };
        for (int i = 0; i < 100; ++i) {
            memcpy(long_name + i * 2, utf8_accent, sizeof utf8_accent);
        }
        memcpy(long_name + 200, ".txt", 5);
        snprintf(long_source, sizeof long_source, "%s/%s", argv[1], long_name);
        write_source(long_source, 73, 7);
        ok(mnx_add_file(archive, long_source, NULL), "add long UTF-8 filename");
        mnx_entry_info long_entries[2];
        ok(mnx_list(archive, long_entries, 2, &count), "list legacy metadata prefix");
        const char *long_id = NULL;
        for (uint64_t i = 0; i < count; ++i) {
            if (strlen(long_entries[i].name) == 128 && memcmp(long_entries[i].name, long_name, 128) == 0) {
                long_id = long_entries[i].id;
            }
        }
        check(long_id != NULL, "legacy name prefix remains UTF-8 safe");
        needed = 0;
        ok(mnx_get_entry_name(archive, long_id, NULL, 0, &needed), "query complete name size");
        check(needed == sizeof long_name, "complete name size includes NUL");
        char full_name[205];
        check(mnx_get_entry_name(archive, long_id, full_name, 1, &needed) == MNX_BUFFER_TOO_SMALL,
            "short complete-name buffer is rejected");
        ok(mnx_get_entry_name(archive, long_id, full_name, sizeof full_name, &needed), "get complete UTF-8 name");
        check(strcmp(full_name, long_name) == 0, "complete name preserves every byte");
        ok(mnx_verify(archive), "verify long-name native archive");
        ok(mnx_close(archive), "final close");
        printf("PASS: native codec %d, encryption=%s, paths, events, metadata, extract, delete, compact, reopen\n", codecs[c], password ? "yes" : "no");
    }
    return 0;
}
