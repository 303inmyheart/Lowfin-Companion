#!/usr/bin/env python3
"""Prepare a Jellyfin repository feed locally. Never uploads or publishes files."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import re
import shutil
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET
import zipfile

GUID = '68abfd24-882d-4937-a104-44d55181b41e'
ROOT = Path(__file__).resolve().parents[1]


def generate(base_url, output, dll=None, project=None, image=None):
    url = urlsplit(base_url)
    if url.scheme not in ('http', 'https') or not url.netloc or url.username or url.password or url.query or url.fragment:
        raise ValueError('Use a plain HTTP(S) hosting directory URL without credentials, query or fragment')
    project = Path(project or ROOT / 'src/Lowfin.Companion/Lowfin.Companion.csproj')
    version = ET.parse(project).findtext('./PropertyGroup/Version')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version or ''):
        raise ValueError('Expected a three-part plugin version')
    assembly_version = version + '.0'
    dll = Path(dll or ROOT / 'src/Lowfin.Companion/bin/Release/net10.0/Lowfin.Companion.dll')
    image = Path(image or ROOT / 'assets/lowfin-companion.png')
    if not dll.is_file() or not image.is_file():
        raise FileNotFoundError('Build the plugin locally before preparing the feed')
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    package = output / ('Lowfin-Companion-' + version + '.zip')
    with zipfile.ZipFile(package, 'w', zipfile.ZIP_DEFLATED) as archive:
        # Jellyfin extracts into the installed plugin directory: DLL belongs at ZIP root.
        archive.write(dll, 'Lowfin.Companion.dll')
        archive.write(image, 'lowfin-companion.png')
    checksum = hashlib.md5(package.read_bytes(), usedforsecurity=False).hexdigest()
    shutil.copy2(image, output / 'lowfin-companion.png')
    base = base_url.rstrip('/') + '/'
    manifest_file = output / 'manifest.json'
    versions = []
    if manifest_file.exists():
        previous = json.loads(manifest_file.read_text())
        if not isinstance(previous, list) or len(previous) != 1 or previous[0].get('guid') != GUID:
            raise ValueError('Existing manifest does not belong to Lowfin Companion')
        versions = [entry for entry in previous[0].get('versions', []) if entry['version'] != assembly_version]
    versions.append({
        'version': assembly_version,
        'changelog': 'See the source repository CHANGELOG.md for this locally built version.',
        'targetAbi': '12.2.0.0',
        'sourceUrl': base + package.name,
        'checksum': checksum,
        'timestamp': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    })
    versions.sort(key=lambda entry: tuple(int(part) for part in entry['version'].split('.')), reverse=True)
    manifest = [{
        'guid': GUID, 'name': 'Lowfin Companion', 'owner': '303inmyheart', 'category': 'General',
        'overview': 'Smart Completion, Persistent Resume, incremental cache and cover tags for Lowfin.',
        'description': 'Optional server companion for Lowfin on Jellyfin 12.2.x.',
        'imageUrl': base + 'lowfin-companion.png', 'versions': versions,
    }]
    manifest_file.write_text(json.dumps(manifest, indent=2) + '\n')
    (output / (package.name + '.sha256')).write_text(hashlib.sha256(package.read_bytes()).hexdigest() + '  ' + package.name + '\n')
    return manifest_file


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', required=True, help='Future directory URL reachable from Jellyfin')
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/feed')
    args = parser.parse_args()
    print(generate(args.base_url, args.output))
    print('Prepared locally only. No files uploaded; this is not yet a live repository URL.')


if __name__ == '__main__':
    main()
