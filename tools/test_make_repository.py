import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec=importlib.util.spec_from_file_location('make_repository',Path(__file__).with_name('make_repository.py'))
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class RepositoryTests(unittest.TestCase):
    def test_flat_package_checksum_and_version_history(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);dll=root/'plugin.dll';dll.write_bytes(b'test DLL fixture')
            image=root/'image.png';image.write_bytes(b'test image fixture');project=root/'plugin.csproj'
            for version in ('0.5.0','0.5.1'):
                project.write_text('<Project><PropertyGroup><Version>'+version+'</Version></PropertyGroup></Project>')
                path=module.generate('https://updates.example.invalid/lowfin/',root/'feed',dll,project,image)
            data=json.loads(path.read_text());self.assertEqual(module.GUID,data[0]['guid'])
            versions=data[0]['versions'];self.assertEqual(['0.5.1.0','0.5.0.0'],[entry['version'] for entry in versions])
            package=root/'feed/Lowfin-Companion-0.5.1.zip'
            self.assertEqual(hashlib.md5(package.read_bytes(),usedforsecurity=False).hexdigest(),versions[0]['checksum'])
            with zipfile.ZipFile(package) as archive:self.assertEqual({'Lowfin.Companion.dll','lowfin-companion.png'},set(archive.namelist()))
            self.assertEqual('https://updates.example.invalid/lowfin/Lowfin-Companion-0.5.1.zip',versions[0]['sourceUrl'])
    def test_reject_credentials_and_non_http_urls(self):
        for url in ('file:///tmp/feed','https://token@example.com/feed','https://example.com/feed?token=secret'):
            with self.assertRaises(ValueError):module.generate(url,Path('/unused'))

if __name__=='__main__':unittest.main()
