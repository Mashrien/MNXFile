# Native application protocol 1

This supplements the existing native ABI1 exports without changing them. The
protocol version is independent of the on-disk MNX format. CLI/GUI bindings
load libmnxfile.so.1 (Linux) or MNXFile.Native.dll (Windows), check protocol 1
and retain the native library for the entire process lifetime. NativeAOT shared
libraries must not be unloaded, including an incompatible library rejected at
startup. MNXFILE_NATIVE_LIBRARY selects an absolute development/test path.

## Ownership and transport

mnx_client_request receives a length-delimited UTF-8 JSON request, a borrowed
optional callback/user pair and output pointer/length. It executes exactly once.
On transport success, it returns zero and a response allocation which the caller
must release once using mnx_client_release with the returned length. The release
clears response bytes before freeing them. Nonzero transport status means no
usable response. Operation failures are Error/ErrorType/ReturnCode in the JSON
response. The client preserves engine ReturnCode values and maps exception types.

The exact field definitions live in interop/Protocol.cs. Missing additive fields
have defaults. Operation names and existing fields must retain their meaning for
protocol 1; incompatible changes require another protocol. No JSON payload holds
file contents. It describes operations, paths, IDs, queue/index metadata and
explicitly supplied passphrases. Never log requests/responses containing secrets.

## Operations

- new: allocate an unopened session, including root/instance IDs; queueing before
  creation is supported. create/open can reuse that session to preserve queued IDs.
- create/open/info: archive settings, identity and occupancy.
- files/queue: immutable metadata snapshots; the client caches them until mutation.
- queue-file/queue-directory/queue-remove/process: engine scan and batch writing.
- add-file/add-directory/add-stream: immediate addition; stream input is borrowed
  synchronously through a native read callback, never retained or put in temp files.
- extract-file/extract-directory/delete/compact/verify: engine-owned archive IO.
- load-queue/save-queue/consume-key: archive-bound resume operations.
- queue-is/queue-read/queue-locate/queue-save/queue-mark/queue-discard/queue-password:
  recovery sidecar operations owned by the engine, including key validation/deletion.
- archive-is/validate-name: format magic recognition and destination validation.
- stream-open/stream-close: engine-owned sliding-window reader. The binary
  mnx_client_stream_read reads at an explicit offset into the caller's buffer,
  limited to 4 MiB per call; no JSON/base64 file-data transfers.
- archive-close: close archive IO while retaining queued work/session IDs.
- close: dispose the session, its readers, cancellation source and queue.

## Progress and cancellation

Callbacks execute synchronously on the request's worker thread. Kind 0 reports
progress (name/current/total), throttled to the requested interval apart from final
updates. Kind 1 reports file boundaries (phase 0 before writing, 1 after committing).
A nonzero callback return stops the operation. Callback pointers/user contexts are
borrowed only until request return. Exceptions must never cross the native ABI;
C# bindings capture and rethrow them after native processing unwinds safely.

mnx_client_prepare arms a new operation before registering the caller's token.
mnx_client_cancel may run from another thread without waiting for the archive
operation gate; it cooperatively cancels actual IO. The engine rolls back an
unfinished archive file and deletes unfinished extraction output as before.
The caller serializes operations per archive. UI progress polling stays on the UI
thread and archive IO stays on the worker. Whole-file boundary cancellation leaves
completed work valid and available for a recovery sidecar.

## Runtime updates

Replace the installed library atomically, preserve protocol 1 and exported ABI1,
and restart applications to load the new engine. An existing process retains its
loaded version. Engine format/codec fixes require no frontend rebuild when their
application interface remains compatible; exposing new UI functionality can still
require a frontend update. New frontends may raise the minimum required protocol.
