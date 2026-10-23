import hashlib
import json
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch
import zipfile
import test_build_desktop_release
from build_desktop_release import build_installer_release


class InstallerReleaseTests(unittest.TestCase):
    def setUp(self):
        test_build_desktop_release.ReleaseTests.setUp(self)
        helper = self.source / 'packaging/installer/build_installer.py'
        helper.parent.mkdir(parents=True)
        helper.write_text('# test-only compiler stub')
        compiler = patch.dict(os.environ, {'YOMI_MAKENSIS': 'fixture-makensis.exe'})
        compiler.start()
        self.addCleanup(compiler.stop)

    def compile(self, args, **kwargs):
        runtime = Path(args[args.index('--runtime') + 1])
        self.assertTrue((runtime / 'resources/app/electron/main.js').is_file())
        self.assertFalse((runtime / 'Data').exists())
        output = Path(args[args.index('--output') + 1])
        output.mkdir(parents=True, exist_ok=True)
        (output / 'YoMi_Desktop_Pet_Setup.exe').write_bytes(b'MZ' + b'fixture setup' * 200)

    def test_public_zip_contains_only_setup_and_update_retains_full_runtime(self):
        with patch('build_desktop_release.subprocess.run', side_effect=self.compile):
            release = build_installer_release(self.source, self.output, 'https://site.invalid')
        with zipfile.ZipFile(self.output / release['fileName']) as archive:
            self.assertEqual(archive.namelist(), ['YoMi_Desktop_Pet_Setup.exe'])
            self.assertIsNone(archive.testzip())
        with zipfile.ZipFile(self.output / release['update']['fileName']) as archive:
            self.assertIn('YoMi_Desktop_Pet/resources/app/backend.json', archive.namelist())
            self.assertFalse(any(b'PRIVATE-' in archive.read(n) for n in archive.namelist()))
        for payload in [release, release['update']]:
            file = self.output / payload['fileName']
            self.assertEqual(payload['sizeBytes'], file.stat().st_size)
            self.assertEqual(payload['sha256'], hashlib.sha256(file.read_bytes()).hexdigest())
            self.assertTrue((self.output / (file.name + '.sha256')).is_file())
        self.assertEqual(json.loads((self.output / 'release.json').read_text()), release)
        self.assertEqual((self.source / 'Data/account.json').read_bytes(), b'PRIVATE-ID')

    def test_failed_compilation_keeps_previous_manifest(self):
        self.output.mkdir()
        previous = b'{"version":"0.6.0"}'
        (self.output / 'release.json').write_bytes(previous)
        with patch('build_desktop_release.subprocess.run', side_effect=subprocess.CalledProcessError(1, 'compiler')):
            with self.assertRaises(subprocess.CalledProcessError):
                build_installer_release(self.source, self.output, 'https://site.invalid')
        self.assertEqual((self.output / 'release.json').read_bytes(), previous)
        self.assertEqual([p.name for p in self.output.iterdir()], ['release.json'])

    def test_same_version_rejected_before_compilation(self):
        with patch('build_desktop_release.subprocess.run', side_effect=self.compile) as compiler:
            build_installer_release(self.source, self.output, 'https://site.invalid')
            manifest = (self.output / 'release.json').read_bytes()
            with self.assertRaises(FileExistsError):
                build_installer_release(self.source, self.output, 'https://site.invalid')
            self.assertEqual(compiler.call_count, 1)
            self.assertEqual((self.output / 'release.json').read_bytes(), manifest)

    def test_missing_compiler_fails_before_packaging(self):
        with patch.dict(os.environ, {}, clear=True), patch('build_desktop_release.shutil.which', return_value=None):
            with self.assertRaisesRegex(ValueError, 'YOMI_MAKENSIS'):
                build_installer_release(self.source, self.output, 'https://site.invalid')
        self.assertFalse(self.output.exists())


if __name__ == '__main__':
    unittest.main()
