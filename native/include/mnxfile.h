#ifndef MNXFILE_H
#define MNXFILE_H
#include <stdint.h>
#if defined(_WIN32)
#define MNX_CALL __cdecl
#if defined(MNX_STATIC_DECLARATIONS)
#define MNX_API
#else
#define MNX_API __declspec(dllimport)
#endif
#else
#define MNX_CALL
#define MNX_API
#endif
#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t mnx_handle;
enum mnx_status {
    MNX_OK = 0,
    MNX_INVALID_ARGUMENT = -1,
    MNX_INVALID_HANDLE = -2,
    MNX_BUFFER_TOO_SMALL = -3,
    MNX_IO_ERROR = -4,
    MNX_CORRUPT_ARCHIVE = -5,
    MNX_AUTH_ERROR = -6,
    MNX_UNSUPPORTED = -7,
    MNX_BUSY = -8,
    MNX_UNEXPECTED_ERROR = -9,
    MNX_OPERATION_FAILED = -10,
    MNX_NOT_FOUND = -11,
    MNX_OUT_OF_MEMORY = -12,
    MNX_ACCESS_DENIED = -13
};
enum mnx_compression { MNX_COMPRESSION_NONE = 0,
    MNX_COMPRESSION_BROTLI = 1,
    MNX_COMPRESSION_SNAPPY = 4,
    MNX_COMPRESSION_LZ4 = 5 };
enum mnx_layout { MNX_LAYOUT_CHUNKED = 0,
    MNX_LAYOUT_CONTINUOUS = 1 };

#pragma pack(push, 8)
typedef struct mnx_create_options {
    uint32_t struct_size;
    int32_t compression;
    int32_t compression_level;
    uint32_t chunk_size;
    int32_t compression_layout;
    int32_t reserved;
} mnx_create_options;

typedef struct mnx_archive_info {
    uint32_t struct_size, format_version, chunk_size;
    int32_t compression, compression_level, compression_layout;
    uint32_t encrypted, reserved;
    int64_t size_on_disk;
    uint32_t chunk_count, index_chunk_count, free_chunk_count, free_range_count;
    uint32_t file_count, directory_count;
    char archive_id[37], root_id[37];
} mnx_archive_info;

typedef struct mnx_entry_info {
    uint32_t struct_size, is_directory;
    uint64_t original_size;
    int64_t created_unix_ms, modified_unix_ms, accessed_unix_ms;
    /* ABI1 name is a UTF-8 prefix. Use mnx_get_entry_name for complete names. */
    char id[37], parent_id[37], name[129];
} mnx_entry_info;

#pragma pack(pop)

/* Notifications only: name is UTF-8, valid until the callback returns.
   Callback runs synchronously on the invoking thread. Reentry on the same
   archive is rejected with MNX_BUSY. Pass NULL to unsubscribe. */
typedef void(MNX_CALL* mnx_progress_callback)(void* context, const char* name,
    int64_t bytes_complete, int64_t bytes_total);

/* Strings are NUL-terminated UTF-8. NULL password means no encryption.
   IDs are canonical GUID strings; NULL parent means archive root.
   Creation settings are immutable after creation. All payload I/O is internal. */
MNX_API uint32_t MNX_CALL mnx_abi_version(void);
MNX_API int32_t MNX_CALL mnx_options_init(mnx_create_options* options);
MNX_API int32_t MNX_CALL mnx_create(const char* path, const char* password,
    const mnx_create_options* options, mnx_handle* archive);
MNX_API int32_t MNX_CALL mnx_open(const char* path, const char* password, mnx_handle* archive);
MNX_API int32_t MNX_CALL mnx_close(mnx_handle archive);
MNX_API int32_t MNX_CALL mnx_set_progress(mnx_handle archive,
    mnx_progress_callback callback, void* context, int32_t interval_ms);
/* Newly opened handles replace duplicate names by default, as the managed API
   does. Disable this explicitly to reject duplicate additions. */
MNX_API int32_t MNX_CALL mnx_set_overwrite(mnx_handle archive, int32_t overwrite);
MNX_API int32_t MNX_CALL mnx_add_file(mnx_handle archive, const char* source, const char* parent);
MNX_API int32_t MNX_CALL mnx_add_directory(mnx_handle archive, const char* source, const char* parent);
/* Validate and resume an MNQ1 queue using the open archive's stored settings.
   On success the queue is renamed to a completed record. */
MNX_API int32_t MNX_CALL mnx_resume(mnx_handle archive, const char* queue_path);
/* Metadata only. NULL entries + zero capacity queries required entry count.
   Capacity/count are entries, not bytes. Short buffers are never partially filled. */
MNX_API int32_t MNX_CALL mnx_list(mnx_handle archive, mnx_entry_info* entries,
    uint64_t capacity, uint64_t* count);
MNX_API int32_t MNX_CALL mnx_list_directory(mnx_handle archive, const char* parent,
    mnx_entry_info* entries, uint64_t capacity, uint64_t* count);
/* Initialize struct_size to sizeof(the output struct) before calling. */
MNX_API int32_t MNX_CALL mnx_get_entry(mnx_handle archive, const char* id, mnx_entry_info* entry);
/* Complete UTF-8 entry name including a terminating NUL in required.
   Pass NULL buffer and zero capacity to query the required size. */
MNX_API int32_t MNX_CALL mnx_get_entry_name(mnx_handle archive, const char* id,
    char* buffer, uint64_t capacity, uint64_t* required);
MNX_API int32_t MNX_CALL mnx_get_info(mnx_handle archive, mnx_archive_info* info);
MNX_API int32_t MNX_CALL mnx_extract_file(mnx_handle archive, const char* id,
    const char* destination, int32_t overwrite);
MNX_API int32_t MNX_CALL mnx_extract_directory(mnx_handle archive, const char* id,
    const char* destination, int32_t overwrite);
MNX_API int32_t MNX_CALL mnx_delete(mnx_handle archive, const char* id);
MNX_API int32_t MNX_CALL mnx_compact(mnx_handle archive);
MNX_API int32_t MNX_CALL mnx_verify(mnx_handle archive);
/* Thread-local diagnostic from the last ordinary call. Queries preserve it.
   required is UTF-8 bytes including NUL. NULL + zero capacity queries size. */
MNX_API int32_t MNX_CALL mnx_last_error(char* buffer, uint64_t capacity, uint64_t* required);
MNX_API uint64_t MNX_CALL mnx_last_return_code(void);
/* Application protocol 1 supplements ABI1 for the CLI/GUI bindings.
   Metadata requests/responses are length-delimited UTF-8 JSON, described in
   docs/client-protocol.md. Each request executes once and always returns its
   owned response allocation, which must be released exactly once.
   Callback kind 0 = progress, 1 = file boundary; return nonzero to stop.
   No exception may escape a callback. The callback/user pointers are borrowed
   only for the duration of mnx_client_request; they are not retained.
   Operation errors are in the JSON response; nonzero C status means transport
   failure. Payload reads use a separate bounded binary operation. */
typedef int32_t(MNX_CALL* mnx_client_callback)(void* user, int32_t kind,
    const char* name, int64_t current, int64_t total, int32_t phase);
MNX_API uint32_t MNX_CALL mnx_client_protocol_version(void);
MNX_API int32_t MNX_CALL mnx_client_request(const uint8_t* request,
    uint64_t request_length, mnx_client_callback callback, void* user,
    uint8_t** response, uint64_t* response_length);
MNX_API void MNX_CALL mnx_client_release(uint8_t* response, uint64_t length);
MNX_API int32_t MNX_CALL mnx_client_prepare(mnx_handle archive);
MNX_API int32_t MNX_CALL mnx_client_cancel(mnx_handle archive);
MNX_API int32_t MNX_CALL mnx_client_stream_read(mnx_handle archive,
    int64_t stream, int64_t offset, uint8_t* buffer, int32_t capacity,
    int32_t* bytes_read);

#ifdef __cplusplus
}
#endif

#endif
