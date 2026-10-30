#!/usr/bin/env python3
"""Package an existing linux-x64 NativeAOT release without rebuilding it."""

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile


def run(arguments, **options):
    print('+ ' + ' '.join(str(argument) for argument in arguments), flush=True)
    subprocess.run([str(argument) for argument in arguments], check=True, **options)


def copy_file(source, target, executable=False):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    target.chmod(0o755 if executable else 0o644)


def write_file(target, contents, executable=False):
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(contents, encoding='utf-8')
    target.chmod(0o755 if executable else 0o644)


def copy_tree(source, target):
    if not source.is_dir():
        return
    for item in sorted(source.rglob('*')):
        if item.is_symlink():
            raise ValueError(f'Unexpected symlink in release material: {item}')
        if item.is_file():
            copy_file(item, target / item.relative_to(source), bool(item.stat().st_mode & 0o111))


def normalize_times(directory, epoch):
    for item in sorted(directory.rglob('*')):
        if item.is_dir():
            item.chmod(0o755)
        os.utime(item, (epoch, epoch), follow_symlinks=False)
    directory.chmod(0o755)
    os.utime(directory, (epoch, epoch))


def portable_archive(root, output, epoch):
    # Explicit ownership, times and gzip header make identical inputs reproducible.
    with output.open('wb') as archive_file:
        with gzip.GzipFile(filename='', mode='wb', fileobj=archive_file, mtime=epoch) as compressed:
            with tarfile.open(fileobj=compressed, mode='w', format=tarfile.PAX_FORMAT) as archive:
                for path in [root] + sorted(root.rglob('*')):
                    entry = archive.gettarinfo(str(path), str(path.relative_to(root.parent)))
                    entry.uid = entry.gid = 0
                    entry.uname = entry.gname = 'root'
                    entry.mtime = epoch
                    entry.pax_headers = {}
                    if path.is_file():
                        with path.open('rb') as source:
                            archive.addfile(entry, source)
                    else:
                        archive.addfile(entry)


def install_notes(component):
    common = '''Linux x86-64 release (Ubuntu 24.04 build baseline)
Requires glibc 2.39 or newer and OpenSSL 3. No .NET SDK/runtime is required.
The package manager variants install under /usr. Do not run a package installation
on a different architecture or an older glibc baseline.

Portable extraction does not register system packages or desktop integration.
Keep this directory together. Run the executable from a terminal using ./NAME.
Original project code is GPL-3.0-only; see LICENSE and THIRD-PARTY notices.
Corresponding source is available from the repository release/tag for this version.
'''
    if component == 'engine':
        return common + '''
This is the independently installed MNXFile engine, C ABI 1/application protocol 1.
The runtime package owns libmnxfile.so.1. The portable bundle also carries mnxfile.h.
For an uninstalled development engine, set MNXFILE_NATIVE_LIBRARY to the absolute
path of libmnxfile.so.1 before starting mnx, mnxi or mnx-gui.
'''
    notes = common + '''
Requires a separately installed MNXFile engine exposing C ABI 1/application
protocol 1 (libmnxfile.so.1). This bundle does not contain an engine.
For portable use, install the engine package or set MNXFILE_NATIVE_LIBRARY to the
absolute path of the separately extracted engine's libmnxfile.so.1.
'''
    if component == 'gui':
        notes += '''
Requires GTK3 >= 3.24 OR GTK4 >= 4.12, fontconfig, FreeType and installed fonts.
Keep libSkiaSharp.so and libHarfBuzzSharp.so beside mnx-gui.
The RPM/DEB includes the application launcher, icon, MIME definition and Dolphin
service menus under the MNX Archive submenu. Dolphin/KIO is optional.
Optional password storage requires secret-tool and a running Secret Service keyring
(Fedora: libsecret; Debian/Ubuntu: libsecret-tools).
'''
    return notes


def prepare_portable(repository, source, root, config, version, epoch, source_commit):
    component = config['component']
    for filename in config['binaries']:
        copy_file(source / filename, root / filename, executable=True)
    if component == 'engine':
        copy_file(source / 'mnxfile.h', root / 'mnxfile.h')
    for filename in ('DEPENDENCIES.txt', 'Topten.RichTextKit.dll.config'):
        if (source / filename).is_file():
            copy_file(source / filename, root / filename)
    for directory in ('THIRD-PARTY', 'licenses'):
        destination = root / 'THIRD-PARTY' if directory == 'THIRD-PARTY' else root / 'THIRD-PARTY/licenses'
        copy_tree(repository / directory, destination)
        copy_tree(source / directory, destination)
    for filename in ('NOTICE', 'NOTICE.txt', 'THIRD-PARTY-NOTICES.txt', 'THIRD-PARTY-NOTICES.md'):
        for origin in (repository, source):
            if (origin / filename).is_file():
                copy_file(origin / filename, root / 'THIRD-PARTY' / filename)
    copy_file(repository / 'LICENSE', root / 'LICENSE')
    notes = install_notes(component)
    if component == 'engine':
        notes += '\nFrom this extracted engine directory:\n'
        notes += '  export MNXFILE_NATIVE_LIBRARY="$PWD/libmnxfile.so.1"\n'
        notes += 'Then run the separately extracted CLI or GUI from the same terminal.\n'
    else:
        executable = './mnx --help' if component == 'cli' else './mnx-gui --list-backends'
        notes += '\nExample with engine and client bundles extracted beside one another.\n'
        notes += 'From this extracted client directory:\n'
        notes += '  export MNXFILE_NATIVE_LIBRARY="$PWD/../mnxfile-1.2.0-alpha.3-linux-x64/libmnxfile.so.1"\n'
        notes += '  ' + executable + '\n'
        notes += 'Replace the engine directory version if using another compatible release.\n'
    notes += '\nSource: ' + config['url'] + '\nSource commit: ' + source_commit + '\n'
    write_file(root / 'INSTALL.txt', notes)
    write_file(root / 'RELEASE-INFO.json', json.dumps({
        'name': config['name'], 'version': version, 'architecture': 'linux-x64',
        'license': 'GPL-3.0-only', 'minimum_glibc': '2.39',
        'engine_c_abi': 1, 'engine_application_protocol': 1, 'source_date_epoch': epoch,
        'source': config['url'], 'source_commit': source_commit,
    }, indent=2) + '\n')


def stage_package(repository, portable, stage, config, package_format):
    name = config['name']
    component = config['component']
    documentation = stage / 'usr/share/doc' / name
    for item in sorted(portable.iterdir()):
        if item.name in config['binaries'] or item.name in ('mnxfile.h', 'Topten.RichTextKit.dll.config'):
            continue
        if item.is_dir():
            copy_tree(item, documentation / item.name)
        else:
            copy_file(item, documentation / item.name)
    if component == 'engine':
        library_directory = 'usr/lib64' if package_format == 'rpm' else 'usr/lib/x86_64-linux-gnu'
        copy_file(portable / 'libmnxfile.so.1', stage / library_directory / 'libmnxfile.so.1', True)
    elif component == 'cli':
        for binary in config['binaries']:
            copy_file(portable / binary, stage / 'usr/bin' / binary, True)
    else:
        for binary in config['binaries']:
            copy_file(portable / binary, stage / 'usr/libexec/mnxarchiver' / binary, True)
        if (portable / 'Topten.RichTextKit.dll.config').is_file():
            copy_file(portable / 'Topten.RichTextKit.dll.config', stage / 'usr/libexec/mnxarchiver/Topten.RichTextKit.dll.config')
        write_file(stage / 'usr/bin/mnx-gui', '#!/bin/sh\nexec /usr/libexec/mnxarchiver/mnx-gui "$@"\n', True)
        resources = repository / 'packaging'
        copy_file(resources / 'org.mash.MNXFile.desktop', stage / 'usr/share/applications/org.mash.MNXFile.desktop')
        copy_file(resources / 'mnxfile.xml', stage / 'usr/share/mime/packages/mnxfile.xml')
        for service in ('mnxfile-servicemenu.desktop', 'mnxfile-extract-servicemenu.desktop'):
            copy_file(resources / service, stage / 'usr/share/kio/servicemenus' / service, True)
        copy_file(repository / 'src/archive_100x100.png', stage / 'usr/share/pixmaps/org.mash.MNXFile.png')


def deb_dependencies(component):
    dependencies = ['libc6 (>= 2.39)', 'libgcc-s1', 'zlib1g', 'libssl3t64 (>= 3.0) | libssl3 (>= 3.0)']
    if component != 'engine':
        dependencies += ['mnxfile-abi-1', 'mnxfile-protocol-1']
    if component == 'gui':
        dependencies += [
            'libgtk-3-0t64 (>= 3.24) | libgtk-3-0 (>= 3.24) | libgtk-4-1 (>= 4.12)',
            'libfontconfig1', 'libfreetype6', 'desktop-file-utils', 'shared-mime-info',
        ]
    return ', '.join(dependencies)


def refresh_desktop():
    return '''if command -v update-mime-database >/dev/null 2>&1; then
    update-mime-database /usr/share/mime >/dev/null 2>&1 || :
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/share/applications >/dev/null 2>&1 || :
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache --quiet --force /usr/share/icons/hicolor >/dev/null 2>&1 || :
fi
'''


def build_deb(stage, output, config, version, environment):
    component = config['component']
    control = f'''Package: {config['name']}
Version: {version}-1
Architecture: amd64
Maintainer: Mashrien <mashrien@users.noreply.github.com>
Section: {'libs' if component == 'engine' else 'utils'}
Priority: optional
Homepage: {config['url']}
Depends: {deb_dependencies(component)}
'''
    if component == 'engine':
        control += 'Provides: mnxfile-abi-1, mnxfile-protocol-1\n'
    control += f"Description: {config['summary']}\n Native Linux x86-64 release. Project code is GPL-3.0-only.\n"
    write_file(stage / 'DEBIAN/control', control)
    if component == 'engine':
        write_file(stage / 'DEBIAN/triggers', 'activate-noawait ldconfig\n')
        script = '#!/bin/sh\nset -e\ncase "$1" in\n    configure|remove|abort-remove) ldconfig ;;\nesac\nexit 0\n'
        write_file(stage / 'DEBIAN/postinst', script, True)
        write_file(stage / 'DEBIAN/postrm', script, True)
    if component == 'gui':
        write_file(stage / 'DEBIAN/postinst', '#!/bin/sh\nset -e\nif [ "$1" = configure ]; then\n' + refresh_desktop() + 'fi\nexit 0\n', True)
        write_file(stage / 'DEBIAN/postrm', '#!/bin/sh\nset -e\ncase "$1" in\n    remove|purge)\n' + refresh_desktop() + ';;\nesac\nexit 0\n', True)
    normalize_times(stage, int(environment['SOURCE_DATE_EPOCH']))
    run(['dpkg-deb', '--root-owner-group', '-Zxz', '-z9', '--build', stage, output], env=environment)


def build_rpm(stage, output_directory, work, config, version, environment):
    component = config['component']
    top = work / 'rpmbuild'
    for directory in ('BUILD', 'BUILDROOT', 'RPMS', 'SOURCES', 'SPECS', 'SRPMS'):
        (top / directory).mkdir(parents=True)
    requirements = ['glibc >= 2.39', 'openssl-libs >= 3.0', 'libcrypto.so.3()(64bit)', 'libssl.so.3()(64bit)', 'zlib', 'libgcc']
    if component != 'engine':
        requirements += ['libmnxfile.so.1()(64bit)', 'mnxfile-abi = 1', 'mnxfile-protocol = 1']
    if component == 'gui':
        requirements += ['(gtk3 >= 3.24 or gtk4 >= 4.12)', 'fontconfig', 'freetype', 'desktop-file-utils', 'shared-mime-info']
    spec = f'''%global debug_package %{{nil}}
%global __os_install_post %{{nil}}
%global _build_id_links none
%global _binary_payload w9.gzdio
%global _buildhost reproducible.local
%global use_source_date_epoch_as_buildtime 1
%global clamp_mtime_to_source_date_epoch 1
%global __provides_exclude_from ^/usr/libexec/mnxarchiver/.*$
Name: {config['name']}
Version: {version}
Release: 1
Summary: {config['summary']}
License: GPL-3.0-only
URL: {config['url']}
BuildArch: x86_64
'''
    for requirement in requirements:
        spec += 'Requires: ' + requirement + '\n'
    if component == 'engine':
        spec += 'Provides: mnxfile-abi = 1\nProvides: mnxfile-protocol = 1\n'
    spec += f'''
%description
{config['summary']}. Native Linux x86-64 release.

%prep
%build
%install
mkdir -p "%{{buildroot}}"
cp -a "{stage}/." "%{{buildroot}}/"

%files
%defattr(-,root,root,-)
'''
    for item in sorted(stage.rglob('*')):
        if item.is_file():
            prefix = '%license ' if item.name == 'LICENSE' else ''
            spec += prefix + '/' + str(item.relative_to(stage)) + '\n'
    if component == 'engine':
        spec += '\n%post -p /sbin/ldconfig\n\n%postun -p /sbin/ldconfig\n'
    if component == 'gui':
        spec += '\n%post\n' + refresh_desktop()
        spec += '\n%postun\n' + refresh_desktop()
    spec_path = top / 'SPECS' / (config['name'] + '.spec')
    write_file(spec_path, spec)
    run(['rpmbuild', '-bb', '--define', f'_topdir {top}', '--define', '_rpmfilename %{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}.rpm', spec_path], env=environment)
    packages = list((top / 'RPMS').rglob('*.rpm'))
    if len(packages) != 1:
        raise ValueError(f'Expected one RPM; found {packages}')
    copy_file(packages[0], output_directory / packages[0].name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', required=True, type=Path, help='existing linux-x64 native release directory')
    parser.add_argument('--output', required=True, type=Path, help='directory for tar.gz, RPM, DEB and checksums')
    parser.add_argument('--version', required=True, help='release version without a leading v')
    parser.add_argument('--source-commit', help='full source Git commit SHA; defaults to repository HEAD')
    parser.add_argument('--epoch', type=int, help='reproducible timestamp; defaults to SOURCE_DATE_EPOCH or latest commit')
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){2,3}(?:-alpha\.[0-9]+)?', args.version):
        parser.error('version must be numeric dotted version with optional -alpha.N')
    repository = Path(__file__).resolve().parent.parent
    config = json.loads((repository / 'packaging/component.json').read_text())
    source_commit = args.source_commit
    if source_commit is None:
        source_commit = subprocess.check_output(['git', '-C', str(repository), 'rev-parse', 'HEAD'], text=True).strip()
    if not re.fullmatch(r'[0-9a-fA-F]{40}|[0-9a-fA-F]{64}', source_commit):
        parser.error('--source-commit must be a full Git commit SHA')
    source = args.input.resolve(strict=True)
    output = args.output.resolve()
    if output == source or output.is_relative_to(source):
        parser.error('output must be outside the input release directory')
    missing_notices = []
    for notice in config['required_notices']['linux-x64']:
        locations = [source / 'THIRD-PARTY' / notice, repository / 'THIRD-PARTY' / notice]
        if not any(path.is_file() and not path.is_symlink() for path in locations):
            missing_notices.append(notice)
    if missing_notices:
        parser.error('stage required notices in input/THIRD-PARTY or repository/THIRD-PARTY: ' + ', '.join(missing_notices))
    for binary in config['binaries']:
        path = source / binary
        if path.is_symlink() or not path.is_file():
            parser.error(f'missing regular native binary: {path}')
        with path.open('rb') as stream:
            header = stream.read(20)
        if header[:5] != b'\x7fELF\x02' or header[18:20] != b'\x3e\x00':
            parser.error(f'expected an ELF64 x86-64 binary: {path}')
    for binary in config['binaries']:
        versions = subprocess.check_output(['readelf', '--version-info', str(source / binary)], text=True)
        required_glibc = [tuple(int(part) for part in value.split('.')) for value in re.findall(r'GLIBC_([0-9]+(?:\.[0-9]+)+)', versions)]
        if required_glibc and max(required_glibc) > (2, 39):
            parser.error(f'{binary} requires glibc newer than the 2.39 release baseline; rebuild on Ubuntu 24.04')
    if config['component'] == 'engine':
        dynamic = subprocess.check_output(['readelf', '-d', str(source / 'libmnxfile.so.1')], text=True)
        if not re.search(r'\(SONAME\).*\[libmnxfile\.so\.1\]', dynamic):
            parser.error('engine must have SONAME libmnxfile.so.1')
    epoch = args.epoch
    if epoch is None:
        supplied = os.environ.get('SOURCE_DATE_EPOCH')
        epoch = int(supplied) if supplied else int(subprocess.check_output(['git', '-C', str(repository), 'show', '-s', '--format=%ct', 'HEAD'], text=True).strip())
    environment = dict(os.environ, SOURCE_DATE_EPOCH=str(epoch), TZ='UTC', LC_ALL='C', PYTHONHASHSEED='0')
    package_version = args.version.replace('-alpha.', '~alpha.')
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='mnx-packaging-') as temporary:
        work = Path(temporary)
        root = work / f"{config['name']}-{args.version}-linux-x64"
        prepare_portable(repository, source, root, config, args.version, epoch, source_commit)
        normalize_times(root, epoch)
        portable_archive(root, output / (root.name + '.tar.gz'), epoch)
        deb_stage = work / 'deb-stage'
        stage_package(repository, root, deb_stage, config, 'deb')
        build_deb(deb_stage, output / f"{config['name']}_{package_version}-1_amd64.deb", config, package_version, environment)
        rpm_stage = work / 'rpm-stage'
        stage_package(repository, root, rpm_stage, config, 'rpm')
        normalize_times(rpm_stage, epoch)
        build_rpm(rpm_stage, output, work, config, package_version, environment)
    assets = [output / (root.name + '.tar.gz'), output / f"{config['name']}_{package_version}-1_amd64.deb", output / f"{config['name']}-{package_version}-1.x86_64.rpm"]
    checksums = []
    for asset in sorted(assets):
        with asset.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        checksums.append(f'{digest}  {asset.name}\n')
    write_file(output / f"{config['name']}-{args.version}-SHA256SUMS", ''.join(checksums))
    print(f'Created three package assets in {output}')


if __name__ == '__main__':
    main()
