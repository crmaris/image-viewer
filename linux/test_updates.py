import copy
import hashlib
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import urllib.request
from update_service import PORTABLE_FILES, ReleaseClient, SafeRedirect, provision_decoders, replace_portable, run_update, select_release


def release(version='0.2.6', kind='portable', payload=b'payload'):
    name = f'ImageViewer-{version}-linux.tar.gz' if kind == 'portable' else f'image-viewer-linux_{version}_all.deb'
    return {'tag_name': f'linux-v{version}', 'draft': False, 'prerelease': False, 'immutable': True,
            'assets': [{'name': name, 'size': len(payload), 'digest': 'sha256:' + hashlib.sha256(payload).hexdigest(),
                        'browser_download_url': f'https://github.com/crmaris/image-viewer/releases/download/linux-v{version}/{name}'}]}


def archive(path, version='0.2.6', bad=None):
    with tarfile.open(path, 'w:gz') as tar:
        for name in sorted(PORTABLE_FILES):
            data = (version if name == 'VERSION' else 'portable' if name == 'INSTALL_KIND' else 'new application').encode()
            entry = tarfile.TarInfo(f'ImageViewer-{version}-linux/{name}')
            entry.size = len(data)
            tar.addfile(entry, io.BytesIO(data))
        if bad:
            entry = tarfile.TarInfo(bad)
            entry.type = tarfile.SYMTYPE
            entry.linkname = '/etc/passwd'
            tar.addfile(entry)


class UpdateChecks(unittest.TestCase):
    def test_numeric_versions_and_channels(self):
        selected = select_release([release('1.9.0'), release('1.10.0')], '1.8.0', 'portable')
        self.assertEqual(selected[0], '1.10.0')
        self.assertIsNone(select_release([release()], '0.2.6', 'portable'))
        other = release()
        other['tag_name'] = 'v0.2.7'
        self.assertIsNone(select_release([other], '0.2.5', 'portable'))

    def test_untrusted_missing_or_mutable_metadata_is_rejected(self):
        for key, value in [('immutable', False), ('draft', True), ('prerelease', True)]:
            item = release()
            item[key] = value
            self.assertIsNone(select_release([item], '0.2.5', 'portable'))
        for key, value in [('digest', None), ('size', 0), ('size', True), ('browser_download_url', 'https://evil.example/update')]:
            item = release()
            item['assets'][0][key] = value
            self.assertIsNone(select_release([item], '0.2.5', 'portable'))

    def test_download_digest_and_truncation_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            client = ReleaseClient()
            target = Path(directory) / 'download'
            item = release(payload=b'correct')['assets'][0]
            with patch.object(client, 'open', return_value=io.BytesIO(b'corrupt')):
                with self.assertRaises(ValueError):
                    client.download(item, target)
            self.assertFalse(target.exists())
            with patch.object(client, 'open', return_value=io.BytesIO(b'correct')):
                client.download(item, target)
            self.assertEqual(target.read_bytes(), b'correct')

    def test_redirect_to_http_or_another_host_is_rejected(self):
        redirect = SafeRedirect()
        for url in ('http://github.com/file', 'https://evil.example/file'):
            with self.assertRaises(ValueError):
                redirect.redirect_request(urllib.request.Request('https://github.com/file'), None, 302, '', {}, url)

    def test_portable_installs_atomically_and_retains_rollback(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'portable'
            root.mkdir()
            (root / 'INSTALL_KIND').write_text('portable')
            (root / 'VERSION').write_text('0.2.5')
            package = Path(directory) / 'new.tar.gz'
            archive(package)
            backup = replace_portable(root, package, '0.2.6')
            self.assertEqual((root / 'VERSION').read_text(), '0.2.6')
            self.assertEqual((backup / 'VERSION').read_text(), '0.2.5')

    def test_portable_rollback_when_atomic_publication_fails(self):
        import os
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'portable'
            root.mkdir()
            (root / 'INSTALL_KIND').write_text('portable')
            (root / 'VERSION').write_text('0.2.5')
            package = Path(directory) / 'new.tar.gz'
            archive(package)
            real_replace, calls = os.replace, []
            def failing(source, target):
                calls.append((source, target))
                if len(calls) == 2:
                    raise OSError('Simulated interrupted publication')
                return real_replace(source, target)
            with patch('update_service.os.replace', side_effect=failing):
                with self.assertRaises(OSError):
                    replace_portable(root, package, '0.2.6')
            self.assertEqual((root / 'VERSION').read_text(), '0.2.5')

    def test_unsafe_archive_and_user_content_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'portable'
            root.mkdir()
            (root / 'INSTALL_KIND').write_text('portable')
            (root / 'VERSION').write_text('0.2.5')
            package = Path(directory) / 'new.tar.gz'
            archive(package, bad='ImageViewer-0.2.6-linux/../outside')
            with self.assertRaises(ValueError):
                replace_portable(root, package, '0.2.6')
            self.assertEqual((root / 'VERSION').read_text(), '0.2.5')
            (root / 'my-photo.jpg').write_bytes(b'unique original')
            archive(package)
            with self.assertRaises(ValueError):
                replace_portable(root, package, '0.2.6')
            self.assertEqual((root / 'my-photo.jpg').read_bytes(), b'unique original')

    def test_source_checkout_never_updates_itself(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(run_update(Path(directory)), {'status': 'source-checkout'})

    def test_decoder_bootstrap_uses_only_signed_distro_packages_and_no_removals(self):
        import subprocess
        # Simulate an old installation with no helper packages and a distro with
        # no JPEG XL candidate. It must still install the other four codecs.
        with patch('update_service.Path.is_file', return_value=False), patch('update_service.subprocess.run') as run:
            run.side_effect = [subprocess.CompletedProcess([], 100, stdout=b''),
                               subprocess.CompletedProcess([], 100, stdout=b''),
                               subprocess.CompletedProcess([], 100, stdout=b''), subprocess.CompletedProcess([], 0)]
            installed = provision_decoders()
            self.assertEqual(set(installed), {'imagemagick', 'librsvg2-bin', 'libraw-bin', 'libjxr-tools'})
            apt = run.call_args_list[-1]
            self.assertIn('--no-remove', apt.args[0])
            self.assertNotIn('--allow-unauthenticated', apt.args[0])
            self.assertEqual(apt.kwargs['env']['DEBIAN_FRONTEND'], 'noninteractive')
        with patch('update_service.Path.is_file', return_value=False), patch('update_service.subprocess.run') as run:
            run.side_effect = [subprocess.CompletedProcess([], 0, stdout=b'install ok installed'),
                               subprocess.CompletedProcess([], 0, stdout=b'Package: libjxl-tools\n'),
                               subprocess.CalledProcessError(100, ['apt-get'])]
            with self.assertRaises(subprocess.CalledProcessError):
                provision_decoders()

    def test_portable_does_not_provision_privileged_packages(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'portable'
            root.mkdir()
            (root / 'INSTALL_KIND').write_text('portable')
            (root / 'VERSION').write_text('0.2.7')
            client = ReleaseClient()
            with patch.object(client, 'releases', return_value=[]), patch('update_service.provision_decoders') as provision:
                self.assertEqual(run_update(root, client)['status'], 'current')
                provision.assert_not_called()


if __name__ == '__main__':
    unittest.main()
