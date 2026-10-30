#!/usr/bin/env python3
"""Package previously validated Windows x64 outputs; never installs or builds them."""
import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import tempfile
import time
import zipfile
from pathlib import Path

FILES = {
    'engine': ['MNXFile.Native.dll', 'MNXFile.Native.lib', 'mnxfile.h', 'install-runtime.ps1'],
    'cli': ['mnx.exe', 'mnxi.exe'],
    'gui': ['mnx-gui.exe'],
    }


def git_value(root, *arguments):
    return subprocess.check_output(['git', '-C', str(root), *arguments], text=True).strip()


def validate_pe(path):
    with path.open('rb') as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:2] != b'MZ':
            raise ValueError(f'Expected a Windows PE binary: {path}')
        stream.seek(struct.unpack_from('<I', header, 60)[0])
        signature = stream.read(6)
        if signature[:4] != b'PE\0\0' or signature[4:] != b'\x64\x86':
            raise ValueError(f'Expected a Windows x64 binary: {path}')


def installation(component):
    common = ('Windows 10 x64 or later. No installed .NET SDK/runtime required.\n'
              'Keep the supplied LICENSE and THIRD-PARTY notices with redistributed binaries.\n'
              'The engine and applications are separate packages. Install the engine first.\n\n')
    if component == 'engine':
        return common + ('In PowerShell, from this extracted directory:\n'
                         '  .\\install-runtime.ps1\n'
                         'This installs MNXFile.Native.dll under the current user profile.\n'
                         'An elevated PowerShell may use -AllUsers for Common Program Files.\n'
                         'Clients automatically discover either installation.\n'
                         'For a portable deployment, set MNXFILE_NATIVE_LIBRARY to the absolute DLL path.\n'
                         'mnxfile.h and MNXFile.Native.lib are supplied for C/C++ callers.\n'
                         'Native ABI 1; application protocol 1.\n')
    if component == 'cli':
        return common + ('Keep mnx.exe and mnxi.exe together; add their directory to PATH if desired.\n'
                         'Run mnx.exe help or mnxi.exe. mnx.exe --interactive launches mnxi.exe.\n'
                         'Requires the separately installed MNXFile engine, ABI 1/protocol 1.\n'
                         'For an uninstalled engine in PowerShell:\n'
                         '  $env:MNXFILE_NATIVE_LIBRARY = (Resolve-Path C:\\MNXFile\\MNXFile.Native.dll).Path\n'
                         '  .\\mnx.exe list C:\\Backups\\backup.mnx\n')
    return common + ('Run mnx-gui.exe, optionally passing an archive or files/folders to add.\n'
                     'Requires the separately installed MNXFile engine, ABI 1/protocol 1.\n'
                     'This WinForms/MWin build is self-contained single-file, not NativeAOT.\n'
                     'GTK and the Linux Skia/HarfBuzz companions are not needed on Windows.\n'
                     'Windows Explorer shell integration is not installed by this ZIP.\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--epoch', type=int)
    parser.add_argument('--source-commit')
    arguments = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    metadata = json.loads((root / 'packaging/component.json').read_text())
    component = metadata['component']
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+)+(?:-[A-Za-z0-9.-]+)?', arguments.version):
        parser.error('Use a numeric release version with an optional prerelease suffix.')
    source = arguments.input.resolve()
    for notice in metadata.get('required_notices', {}).get('win-x64', []):
        if not any((folder / notice).is_file() for folder in (source / 'THIRD-PARTY', root / 'THIRD-PARTY')):
            parser.error(f'Missing required notice {notice}; stage matching dependency notices under THIRD-PARTY.')
    files = {}
    for name in FILES[component]:
        path = source / name
        if not path.is_file():
            parser.error(f'Missing required release file: {path}')
        if path.suffix.lower() in ('.exe', '.dll'):
            validate_pe(path)
        files[name] = path.read_bytes()
    files['LICENSE'] = (root / 'LICENSE').read_bytes()
    for directory in (source / 'THIRD-PARTY', root / 'THIRD-PARTY'):
        if directory.is_dir():
            for path in sorted(directory.rglob('*')):
                if path.is_file():
                    files['THIRD-PARTY/' + path.relative_to(directory).as_posix()] = path.read_bytes()
    for name in ('DEPENDENCIES.txt', 'MNXFile.Icons.LICENSE.txt'):
        path = source / name
        if path.is_file():
            files[name] = path.read_bytes()
    epoch = arguments.epoch
    if epoch is None:
        epoch = int(os.environ.get('SOURCE_DATE_EPOCH') or git_value(root, 'show', '-s', '--format=%ct', 'HEAD'))
    source_commit = arguments.source_commit or git_value(root, 'rev-parse', 'HEAD')
    info = {
        'name': metadata['name'], 'version': arguments.version,
        'platform': 'win-x64', 'source_url': metadata['url'],
        'source_commit': source_commit, 'native_abi': 1, 'application_protocol': 1,
        'runtime_required': False, 'native_aot': component != 'gui',
        'files_sha256': {name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())},
        }
    files['INSTALL.txt'] = installation(component).encode()
    files['RELEASE-INFO.json'] = (json.dumps(info, indent=2) + '\n').encode()
    arguments.output.mkdir(parents=True, exist_ok=True)
    stem = f"{metadata['name']}-{arguments.version}-win-x64"
    destination = arguments.output / (stem + '.zip')
    timestamp = time.gmtime(max(315532800, epoch))[:6]
    with tempfile.NamedTemporaryFile(dir=arguments.output, delete=False) as temporary:
        pending = Path(temporary.name)
    try:
        with zipfile.ZipFile(pending, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, data in sorted(files.items()):
                entry = zipfile.ZipInfo(stem + '/' + name, timestamp)
                entry.create_system = 3
                entry.external_attr = 0o100644 << 16
                entry.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(entry, data, compresslevel=9)
        pending.replace(destination)
    finally:
        pending.unlink(missing_ok=True)
    checksum = hashlib.sha256(destination.read_bytes()).hexdigest()
    checksum_file = arguments.output / f"{metadata['name']}-{arguments.version}-windows-SHA256SUMS"
    checksum_file.write_text(f'{checksum}  {destination.name}\n')
    print(destination)


if __name__ == '__main__':
    main()
