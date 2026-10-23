import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from build_desktop_release import build_release, collect_files, ROOT_FILES, REQUIRED, TREES

class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'source'
        self.output = self.root / 'releases'
        files = {
            'YoMi_Desktop_Pet.exe': b'runtime', 'LICENSE.electron.txt': b'license',
            'LICENSES.chromium.html': b'licenses', 'libEGL.dll': b'native',
            'resources/app/package.json': b'{"version":"0.7.0"}',
            'resources/app/electron/main.js': b'entry',
            'resources/app/assets/pet.png': b'image',
            'resources/app/node_modules/native/addon.node': b'addon',
            'resources/app/vendor/google-auth/node_modules/auth/index.js': b'auth',
            'resources/bin/yt-dlp.exe': b'media helper',
            'resources/app/backend.json': b'{"url":"https://old.invalid"}',
            'Data/account.json': b'PRIVATE-ID', 'local-secrets/client_secret.json': b'PRIVATE-KEY',
            'resources/app/seed/yumi-focus-pet.json': b'PRIVATE-PROGRESS',
            'resources/app/tests/private.json': b'PRIVATE-TEST',
            'backups/save.json': b'PRIVATE-BACKUP', 'random.txt': b'PRIVATE-ROOT',
        }
        for name, content in files.items():
            file = self.source / name
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(content)
        for name in ROOT_FILES | set(REQUIRED):
            file = self.source/name
            file.parent.mkdir(parents=True, exist_ok=True)
            if not file.exists(): file.write_bytes(b'runtime fixture')
        for name in TREES:
            folder = self.source/name
            folder.mkdir(parents=True, exist_ok=True)
            if not any(folder.iterdir()): (folder/'fixture.txt').write_text('runtime fixture')

    def test_clean_archive_contains_runtime_and_real_manifest(self):
        release = build_release(self.source, self.output, 'https://localhost:5101')
        archive = self.output / release['fileName']
        with zipfile.ZipFile(archive) as z:
            self.assertIsNone(z.testzip())
            names = z.namelist()
            self.assertTrue(all(n.startswith('YoMi_Desktop_Pet/') for n in names))
            self.assertIn('YoMi_Desktop_Pet/resources/bin/yt-dlp.exe', names)
            self.assertIn('YoMi_Desktop_Pet/resources/app/node_modules/native/addon.node', names)
            self.assertNotIn('YoMi_Desktop_Pet/random.txt', names)
            self.assertFalse(any(b'PRIVATE-' in z.read(n) for n in names))
            self.assertEqual(json.loads(z.read('YoMi_Desktop_Pet/resources/app/backend.json'))['url'], 'https://localhost:5101')
        self.assertEqual(release['sizeBytes'], archive.stat().st_size)
        self.assertEqual(release['sha256'], hashlib.sha256(archive.read_bytes()).hexdigest())
        self.assertEqual(json.loads((self.output/'release.json').read_text()), release)
        self.assertTrue(release['isLocalBuild'])
        self.assertEqual((self.source/'Data/account.json').read_bytes(), b'PRIVATE-ID')

    def test_missing_runtime_does_not_publish(self):
        (self.source/'YoMi_Desktop_Pet.exe').unlink()
        with self.assertRaises(ValueError): build_release(self.source, self.output, 'https://localhost:5101')
        self.assertFalse((self.output/'release.json').exists())

    def test_private_file_inside_selected_runtime_is_rejected(self):
        (self.source/'resources/app/electron/client_secret_live.json').write_text('SECRET')
        with self.assertRaises(ValueError): collect_files(self.source)

    def test_missing_electron_data_is_rejected_before_publish(self):
        (self.source/'icudtl.dat').unlink()
        with self.assertRaises(ValueError): build_release(self.source, self.output, 'https://localhost:5101')
        self.assertFalse((self.output/'release.json').exists())

    def test_existing_release_not_overwritten(self):
        build_release(self.source, self.output, 'https://localhost:5101')
        original = (self.output/'release.json').read_bytes()
        with self.assertRaises(FileExistsError): build_release(self.source, self.output, 'https://localhost:5101')
        self.assertEqual((self.output/'release.json').read_bytes(), original)

    def test_production_release_has_correct_backend_and_instructions(self):
        origin = 'https://yumiclub.duckdns.org:8082'
        release = build_release(self.source, self.output, origin)
        self.assertFalse(release['isLocalBuild'])
        with zipfile.ZipFile(self.output/release['fileName']) as z:
            self.assertEqual(json.loads(z.read('YoMi_Desktop_Pet/resources/app/backend.json'))['url'], origin)
            instructions = z.read('YoMi_Desktop_Pet/START_HERE.txt').decode('utf-8')
            self.assertIn(origin,instructions)
            self.assertNotIn('localhost',instructions)
            self.assertNotIn('測試後端',instructions)
        self.assertEqual(json.loads((self.source/'resources/app/backend.json').read_text())['url'],'https://old.invalid')

    def test_rejects_unsafe_origin_or_output_inside_source(self):
        for origin in ['http://localhost:5100', 'https://host.invalid/path', 'https://name:pass@host.invalid']:
            with self.assertRaises(ValueError): build_release(self.source, self.output, origin)
        with self.assertRaises(ValueError): build_release(self.source, self.source/'output', 'https://localhost:5101')

if __name__ == '__main__': unittest.main()
