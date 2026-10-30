#!/usr/bin/env bash
# Explicit installation/staging step. Building never invokes this script.
set -euo pipefail
if [[ ${1:-} == --help || $# -lt 1 || $# -gt 3 ]]; then
    printf 'Usage: install-runtime.sh RELEASE_DIRECTORY [PREFIX [/LIBRARY_DIRECTORY]]\n'
    printf 'Example: install-runtime.sh ../MNX_Builds/mnxfile/linux-x64 /usr /usr/lib64\n'
    printf 'Use DESTDIR=/path/to/staging to stage packaging files without installing.\n'
    exit 0
    fi
mnx_source=$(cd -- "$1" && pwd -P)
mnx_prefix=${2:-/usr/local}
mnx_library_directory=${3:-"$mnx_prefix/lib"}
mnx_stage=${DESTDIR:-}
if [[ $mnx_prefix != /* || $mnx_library_directory != /* ]]; then
    printf 'Prefix and library directory must be absolute paths.\n' >&2
    exit 2
    fi
if [[ ! -f "$mnx_source/libmnxfile.so.1" ]]; then
    printf 'Release directory must contain libmnxfile.so.1.\n' >&2
    exit 2
    fi
install -d -- "$mnx_stage$mnx_library_directory"
# Write beside the current engine, then rename so readers never see a partial file.
mnx_pending=$(mktemp "$mnx_stage$mnx_library_directory/.libmnxfile.so.1.XXXXXXXX")
trap 'rm -f -- "$mnx_pending"' EXIT
install -m 0755 -- "$mnx_source/libmnxfile.so.1" "$mnx_pending"
mv -f -- "$mnx_pending" "$mnx_stage$mnx_library_directory/libmnxfile.so.1"
ln -sfn -- libmnxfile.so.1 "$mnx_stage$mnx_library_directory/MNXFile.Native.so"
printf 'Runtime written to %s\n' "$mnx_stage$mnx_library_directory/libmnxfile.so.1"
if [[ -z $mnx_stage ]]; then
    printf 'Refresh the system library cache with ldconfig as appropriate for this prefix.\n'
    fi
