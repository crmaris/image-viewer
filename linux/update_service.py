#!/usr/bin/env python3
"""Unattended updates from immutable, digest-bearing Linux GitHub releases."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.parse
import urllib.request

REPOSITORY = 'crmaris/image-viewer'
API = f'https://api.github.com/repos/{REPOSITORY}/releases?per_page=100'
MAX_DOWNLOAD = 64 * 1024 * 1024
PORTABLE_FILES = {'viewer.py', 'viewer_core.py', 'viewer_cli.py', 'update_service.py',
                  'imageviewer-linux', 'README.md', 'VERSION', 'INSTALL_KIND', 'LICENSE'}


def version_tuple(value):
    if not re.fullmatch(r'\d+\.\d+\.\d+', value):
        raise ValueError('Invalid release version.')
    return tuple(map(int, value.split('.')))


def select_release(releases, current, kind):
    current = version_tuple(current)
    candidates = []
    for release in releases:
        tag = release.get('tag_name', '')
        if (not re.fullmatch(r'linux-v\d+\.\d+\.\d+', tag) or release.get('draft')
                or release.get('prerelease') or release.get('immutable') is not True):
            continue
        version = tag.removeprefix('linux-v')
        if version_tuple(version) <= current:
            continue
        name = (f'image-viewer-linux_{version}_all.deb' if kind == 'deb'
                else f'ImageViewer-{version}-linux.tar.gz')
        expected_url = f'https://github.com/{REPOSITORY}/releases/download/{tag}/{name}'
        assets = [a for a in release.get('assets', []) if a.get('name') == name]
        if len(assets) != 1:
            continue
        asset = assets[0]
        digest = asset.get('digest') or ''
        size = asset.get('size')
        if (asset.get('browser_download_url') != expected_url
                or not re.fullmatch(r'sha256:[0-9a-fA-F]{64}', digest)
                or type(size) is not int or not 0 < size <= MAX_DOWNLOAD):
            continue
        candidates.append((version_tuple(version), version, asset))
    if not candidates:
        return None
    _, version, asset = max(candidates, key=lambda item: item[0])
    return version, asset


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlparse(newurl)
        if parsed.scheme != 'https' or parsed.hostname not in {
                'github.com', 'api.github.com', 'release-assets.githubusercontent.com'}:
            raise ValueError('Refusing an untrusted download redirect.')
        return super().redirect_request(request, fp, code, msg, headers, newurl)


class ReleaseClient:
    def __init__(self):
        self.opener = urllib.request.build_opener(SafeRedirect())

    def open(self, url):
        return self.opener.open(urllib.request.Request(url, headers={
            'User-Agent': 'ImageViewerLinux-Updater', 'Accept': 'application/vnd.github+json'}), timeout=20)

    def releases(self):
        with self.open(API) as response:
            payload = response.read(4 * 1024 * 1024 + 1)
        if len(payload) > 4 * 1024 * 1024:
            raise ValueError('Release metadata exceeds the limit.')
        result = json.loads(payload)
        if not isinstance(result, list):
            raise ValueError('Invalid release metadata.')
        return result

    def download(self, asset, target):
        digest, size = hashlib.sha256(), 0
        try:
            with self.open(asset['browser_download_url']) as response, target.open('xb') as stream:
                while chunk := response.read(128 * 1024):
                    size += len(chunk)
                    if size > asset['size'] or size > MAX_DOWNLOAD:
                        raise ValueError('Release download exceeds its declared size.')
                    stream.write(chunk)
                    digest.update(chunk)
                stream.flush()
                os.fsync(stream.fileno())
            if size != asset['size'] or digest.hexdigest() != asset['digest'][7:].lower():
                raise ValueError('Release size or SHA-256 verification failed.')
        except Exception:
            target.unlink(missing_ok=True)
            raise
        return target


def replace_portable(root, archive, version):
    if (root / 'INSTALL_KIND').read_text().strip() != 'portable':
        raise ValueError('Only a managed portable installation can update itself.')
    # Unknown content might belong to the user: never move it into an app backup.
    if any(p.name not in PORTABLE_FILES and p.name != '__pycache__' for p in root.iterdir()):
        raise ValueError('The portable folder contains user files; update was deferred.')
    stage = Path(tempfile.mkdtemp(prefix='.imageviewer-update-', dir=root.parent))
    backup = None
    try:
        seen, total = set(), 0
        with tarfile.open(archive, 'r:gz') as tar:
            for entry in tar:
                parts = entry.name.split('/')
                if (len(parts) != 2 or parts[0] != f'ImageViewer-{version}-linux'
                        or parts[1] not in PORTABLE_FILES or not entry.isfile()
                        or parts[1] in seen or not 0 <= entry.size <= 8 * 1024 * 1024):
                    raise ValueError('Unsafe or unexpected portable archive entry.')
                total += entry.size
                if total > 32 * 1024 * 1024:
                    raise ValueError('Portable archive exceeds the extraction limit.')
                seen.add(parts[1])
                source = tar.extractfile(entry)
                with source, (stage / parts[1]).open('xb') as stream:
                    shutil.copyfileobj(source, stream)
                (stage / parts[1]).chmod(0o755 if parts[1] == 'imageviewer-linux' else 0o644)
        if (seen != PORTABLE_FILES or (stage / 'VERSION').read_text().strip() != version
                or (stage / 'INSTALL_KIND').read_text().strip() != 'portable'):
            raise ValueError('Portable release is incomplete or has an inconsistent version.')
        backup = Path(tempfile.mkdtemp(prefix=f'.{root.name}.previous-', dir=root.parent))
        backup.rmdir()
        os.replace(root, backup)
        try:
            os.replace(stage, root)
        except Exception:
            os.replace(backup, root)
            backup = None
            raise
        return backup
    finally:
        if stage.exists():
            shutil.rmtree(stage)


def validate_deb(path, version):
    fields = subprocess.check_output(['/usr/bin/dpkg-deb', '--field', str(path),
                                      'Package', 'Version', 'Architecture'], text=True)
    if fields.splitlines() != ['Package: image-viewer-linux', f'Version: {version}', 'Architecture: all']:
        raise ValueError('The verified archive is not the expected Image Viewer package.')


def snapshot_deb(state, current):
    """Retain a reinstallable package before replacing the current system install."""
    with tempfile.TemporaryDirectory(prefix='rollback-stage-', dir=state) as folder:
        stage = Path(folder)
        files = subprocess.check_output(['/usr/bin/dpkg-query', '-L', 'image-viewer-linux'], text=True).splitlines()
        for value in files:
            source = Path(value)
            if not source.is_absolute() or '..' in source.parts:
                raise ValueError('Invalid installed package path.')
            if source.is_symlink():
                raise ValueError('Unexpected symlink in the installed package.')
            if source.is_file():
                target = stage / value.lstrip('/')
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
        control = stage / 'DEBIAN'
        control.mkdir(mode=0o755)
        info = subprocess.check_output(['/usr/bin/dpkg-query', '-s', 'image-viewer-linux'], text=True)
        (control / 'control').write_text('\n'.join(line for line in info.splitlines()
                                                  if not line.startswith(('Status:', 'Config-Version:'))) + '\n')
        for script in ('postinst', 'prerm', 'postrm'):
            source = Path('/var/lib/dpkg/info') / f'image-viewer-linux.{script}'
            if source.is_file() and not source.is_symlink():
                shutil.copy2(source, control / script)
        # The service uses umask 0077; archive directories must still be normal
        # system directory modes, including /usr and /usr/share during rollback.
        stage.chmod(0o755)
        for directory in stage.rglob('*'):
            if directory.is_dir():
                directory.chmod(0o755)
        target = state / f'rollback-{current}.deb'
        subprocess.run(['/usr/bin/dpkg-deb', '--root-owner-group', '--build', str(stage), str(target)],
                       check=True, stdout=subprocess.DEVNULL)
    validate_deb(target, current)
    return target


def install_deb(download, version, state, current):
    validate_deb(download, version)
    rollback = snapshot_deb(state, current)
    result = subprocess.run(['/usr/bin/dpkg', '--install', str(download)])
    if result.returncode:
        restored = subprocess.run(['/usr/bin/dpkg', '--install', str(rollback)])
        raise RuntimeError(f'Update failed ({result.returncode}); rollback exit code {restored.returncode}. Backup: {rollback}')
    return rollback


def run_update(root=None, client=None):
    root = (root or Path(__file__).resolve().parent).resolve()
    marker = root / 'INSTALL_KIND'
    if not marker.is_file():
        return {'status': 'source-checkout'}
    kind = marker.read_text().strip()
    if kind not in ('portable', 'deb'):
        raise ValueError('Invalid installation kind.')
    if kind == 'deb':
        if root != Path('/usr/share/image-viewer-linux') or os.geteuid() != 0:
            raise PermissionError('System updates run through the installed system timer.')
        state = Path('/var/lib/image-viewer-linux')
    else:
        state = root.parent / f'.{root.name}.update-state'
    if state.is_symlink():
        raise ValueError('Unsafe update state directory.')
    state.mkdir(mode=0o700, exist_ok=True)
    if state.stat().st_uid != os.geteuid() or state.stat().st_mode & 0o077:
        raise ValueError('Update state directory has an unexpected owner.')
    with (state / 'update.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return {'status': 'busy'}
        current = (root / 'VERSION').read_text().strip()
        client = client or ReleaseClient()
        selected = select_release(client.releases(), current, kind)
        if selected is None:
            return {'status': 'current', 'version': current}
        version, asset = selected
        with tempfile.TemporaryDirectory(prefix='download-', dir=state) as folder:
            download = client.download(asset, Path(folder) / asset['name'])
            rollback = (install_deb(download, version, state, current) if kind == 'deb'
                        else replace_portable(root, download, version))
        result = {'status': 'updated', 'version': version, 'rollback': str(rollback)}
        temporary = state / 'last-update.json.tmp'
        temporary.write_text(json.dumps(result, indent=2) + '\n')
        os.replace(temporary, state / 'last-update.json')
        return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    try:
        print(json.dumps(run_update()))
    except Exception as error:
        print(json.dumps({'status': 'failed', 'error': str(error)}))
        raise SystemExit(1)
