#!/usr/bin/env python3
"""Copy only finished native programs and shipped libraries into one release folder."""
import argparse
import fcntl
import os
from pathlib import Path
import shutil
import tempfile

ARTIFACTS = {
    'mnxfile': ('libmnxfile.so.1',),
    'mnx-cli': ('mnx', 'mnxi'),
    'mnx-gui': ('mnx-gui', 'libSkiaSharp.so', 'libHarfBuzzSharp.so'),
}


def stage(component, source, destination):
    files = ARTIFACTS[component]
    # Check every mandatory input before changing any previously working output.
    for name in files:
        candidate = source / name
        if not candidate.is_file():
            raise RuntimeError(f'Missing finished native artifact: {candidate}')
        with candidate.open('rb') as stream:
            if stream.read(4) != b'\x7fELF':
                raise RuntimeError(f'Expected a native Linux ELF artifact: {candidate}')
    destination.parent.mkdir(parents=True, exist_ok=True)
    with (destination.parent / (destination.name + '.lock')).open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        destination.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='mnx-stage-', dir=destination.parent) as pending:
            pending = Path(pending)
            for name in files:
                shutil.copy2(source / name, pending / name)
            # One-component builds preserve the other components' current files.
            for name in files:
                os.replace(pending / name, destination / name)
        notices = source / 'THIRD-PARTY'
        if notices.is_dir():
            shutil.copytree(notices, destination / 'THIRD-PARTY' / component, dirs_exist_ok=True)
        if (source / 'DEPENDENCIES.txt').is_file():
            shutil.copy2(source / 'DEPENDENCIES.txt', destination / (component + '-DEPENDENCIES.txt'))
    print(f'Final native binaries: {destination}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('component', choices=ARTIFACTS)
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    arguments = parser.parse_args()
    stage(arguments.component, arguments.source.resolve(), arguments.destination.resolve())
