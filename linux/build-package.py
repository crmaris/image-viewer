#!/usr/bin/env python3
"""Build an architecture-independent Linux companion, without installing it."""
import argparse
import gzip
import hashlib
import io
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile


def packed_tree(root):
    """Explicit archive modes work even when staging is on a Windows filesystem."""
    raw = io.BytesIO()
    with gzip.GzipFile(filename='', fileobj=raw, mode='wb', mtime=0) as zipped, tarfile.open(fileobj=zipped, mode='w') as tar:
        for path in sorted(root.rglob('*')):
            info = tar.gettarinfo(str(path), arcname='./' + path.relative_to(root).as_posix())
            info.uid = info.gid = info.mtime = 0
            info.uname = info.gname = 'root'
            info.mode = 0o755 if path.is_dir() or path.parent.name == 'bin' or path.name in ('postinst', 'prerm', 'postrm') else 0o644
            if path.is_dir():
                tar.addfile(info)
            else:
                with path.open('rb') as content:
                    tar.addfile(info, content)
    return raw.getvalue()


def write_deb(path, control, data):
    with path.open('wb') as archive:
        archive.write(b'!<arch>\n')
        for name, content in [('debian-binary', b'2.0\n'), ('control.tar.gz', control), ('data.tar.gz', data)]:
            header = f'{name + "/":<16}{0:<12}{0:<6}{0:<6}{"100644":<8}{len(content):<10}`\n'
            archive.write(header.encode('ascii'))
            archive.write(content)
            if len(content) % 2:
                archive.write(b'\n')


def build(output, version):
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Version must have the form major.minor.patch.')
    source = Path(__file__).resolve().parent
    repo = source.parent
    output.mkdir(parents=True, exist_ok=True)
    scratch = repo / '.codex-tmp'
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='linux-package-', dir=scratch) as temporary:
        stage = Path(temporary)
        portable = stage / f'ImageViewer-{version}-linux'
        portable.mkdir()
        for name in ('viewer.py', 'viewer_core.py', 'viewer_cli.py', 'update_service.py', 'imageviewer-linux', 'README.md', 'VERSION'):
            shutil.copyfile(source / name, portable / name)
        (portable / 'VERSION').write_text(version + '\n')
        (portable / 'INSTALL_KIND').write_text('portable\n')
        shutil.copyfile(repo / 'LICENSE', portable / 'LICENSE')
        (portable / 'imageviewer-linux').chmod(0o755)
        archive = output / f'ImageViewer-{version}-linux.tar.gz'
        with archive.open('wb') as raw, gzip.GzipFile(filename='', fileobj=raw, mode='wb', mtime=0) as zipped, tarfile.open(fileobj=zipped, mode='w') as tar:
            for path in sorted(portable.rglob('*')):
                info = tar.gettarinfo(str(path), arcname=f'{portable.name}/{path.relative_to(portable).as_posix()}')
                info.uid = info.gid = info.mtime = 0
                info.uname = info.gname = 'root'
                info.mode = 0o755 if path.name == 'imageviewer-linux' else 0o644
                with path.open('rb') as content:
                    tar.addfile(info, content)
        package = stage / 'deb'
        app = package / 'usr/share/image-viewer-linux'
        app.mkdir(parents=True)
        for name in ('viewer.py', 'viewer_core.py', 'viewer_cli.py', 'update_service.py', 'VERSION'):
            shutil.copyfile(portable / name, app / name)
        (app / 'INSTALL_KIND').write_text('deb\n')
        units = package / 'usr/lib/systemd/system'
        units.mkdir(parents=True)
        for name in ('image-viewer-linux-update.service', 'image-viewer-linux-update.timer'):
            shutil.copyfile(source / name, units / name)
        launchers = package / 'usr/bin'
        launchers.mkdir(parents=True)
        for name, script in (('imageviewer-linux', 'viewer.py'), ('imageviewer-linux-cli', 'viewer_cli.py')):
            launcher = launchers / name
            launcher.write_text(f'#!/bin/sh\nexec /usr/bin/python3 /usr/share/image-viewer-linux/{script} "$@"\n')
            launcher.chmod(0o755)
        desktop = package / 'usr/share/applications'
        desktop.mkdir(parents=True)
        shutil.copyfile(source / 'image-viewer-linux.desktop', desktop / 'image-viewer-linux.desktop')
        subprocess.run(['desktop-file-validate', str(desktop / 'image-viewer-linux.desktop')], check=True)
        icons = package / 'usr/share/icons/hicolor/256x256/apps'
        icons.mkdir(parents=True)
        shutil.copyfile(repo / 'packaging/icon-preview.png', icons / 'image-viewer-linux.png')
        docs = package / 'usr/share/doc/image-viewer-linux'
        docs.mkdir(parents=True)
        shutil.copyfile(source / 'README.md', docs / 'README.md')
        shutil.copyfile(repo / 'LICENSE', docs / 'copyright')
        control = package / 'DEBIAN'
        control.mkdir()
        (control / 'control').write_text(
            f'Package: image-viewer-linux\nVersion: {version}\nSection: graphics\nPriority: optional\n'
            'Architecture: all\nMaintainer: Image Viewer contributors <noreply@github.com>\n'
            'Depends: python3 (>= 3.10), python3-tk, python3-pil (>= 9.0), python3-pil.imagetk, systemd\n'
            'Recommends: imagemagick, librsvg2-bin, libraw-bin, libjxr-tools, libjxl-tools, libheif-plugin-libde265\n'
            'Homepage: https://github.com/crmaris/image-viewer\n'
            'Description: Image Viewer Linux companion\n'
            ' Browse, zoom, rotate the view, play animations, inspect TIFF pages,\n'
            ' and save rendered PNG copies while preserving image originals.\n')
        for name in ('postinst', 'prerm', 'postrm'):
            shutil.copyfile(source / name, control / name)
            (control / name).chmod(0o755)
        for path in package.rglob('*'):
            if path.is_dir():
                path.chmod(0o755)
            elif path.parent != launchers and path.name not in ('postinst', 'prerm', 'postrm'):
                path.chmod(0o644)
            os.utime(path, (0, 0))
        deb = output / f'image-viewer-linux_{version}_all.deb'
        control_bytes = packed_tree(control)
        # DEBIAN is a control-only directory and must not be installed as data.
        shutil.rmtree(control)
        write_deb(deb, control_bytes, packed_tree(package))
        subprocess.run(['dpkg-deb', '--info', str(deb)], check=True, stdout=subprocess.DEVNULL)
        hashes = output / 'SHA256SUMS'
        hashes.write_text(''.join(f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n'
                                 for path in (deb, archive)))
    return deb, archive, hashes


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parent.parent / 'build/linux')
    parser.add_argument('--version', default=Path(__file__).with_name('VERSION').read_text().strip())
    args = parser.parse_args()
    for artifact in build(args.output.resolve(), args.version):
        print(artifact)
