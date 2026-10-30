"""Exercise application protocol through a real installed native library."""
import ctypes
import json
import pathlib
import sys
import tempfile

library = ctypes.CDLL(sys.argv[1])
try:
    version = library.mnx_client_protocol_version
except AttributeError:
    raise AssertionError('Native library is missing the application protocol')
version.restype = ctypes.c_uint32
assert version() == 1
request = library.mnx_client_request
request.argtypes = [ctypes.c_char_p, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_uint64)]
request.restype = ctypes.c_int32
release = library.mnx_client_release
release.argtypes = [ctypes.c_void_p, ctypes.c_uint64]

def call(operation, **values):
    response = ctypes.c_void_p()
    length = ctypes.c_uint64()
    data = json.dumps(dict(Operation=operation, **values)).encode()
    status = request(data, len(data), None, None, ctypes.byref(response), ctypes.byref(length))
    assert status == 0
    try:
        result = json.loads(ctypes.string_at(response, length.value))
    finally:
        release(response, length)
    assert not result.get('Error'), result
    return result

with tempfile.TemporaryDirectory(prefix='mnx-protocol-') as directory:
    base = pathlib.Path(directory)
    archive = base/'ひらがな.mnx'
    source = base/'notes.with spaces.あ.txt'
    source.write_bytes(bytes(range(256))*4097)
    result = call('create', Path=str(archive), Password='あいうえお', Compression=5, Level=0, ChunkSize=32768, Layout=1)
    handle = result['Handle']
    call('queue-file', Handle=handle, Path=str(source))
    queued = call('queue', Handle=handle)['Queue']
    assert len(queued) == 1
    call('process', Handle=handle)
    entries = call('files', Handle=handle)['Entries']
    assert len(entries) == 1 and entries[0]['Name'] == source.name
    output = base/'extracted.txt'
    call('extract-file', Handle=handle, Id=entries[0]['Id'], Path=str(output))
    assert output.read_bytes() == source.read_bytes()
    call('verify', Handle=handle)
    call('close', Handle=handle)
    handle = call('open', Path=str(archive), Password='あいうえお')['Handle']
    assert call('info', Handle=handle)['Compression'] == 5
    call('close', Handle=handle)
print('PASS: application protocol create/queue/process/list/extract/verify/reopen, Unicode + LZ4 + encryption')
