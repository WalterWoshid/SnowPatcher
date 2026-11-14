"""Record the files owned by a portable desktop release."""
import argparse
import hashlib
import json
import re
from pathlib import Path


def write_manifest(directory, version, platform):
    if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version):
        raise ValueError('Use a MAJOR.MINOR.PATCH release version')
    if platform not in ('win-x64', 'linux-x64'):
        raise ValueError('Unsupported release platform')
    root = Path(directory).resolve()
    files = []
    names = set()
    for path in sorted(root.rglob('*')):
        if path.is_symlink():
            raise ValueError('Release files cannot be links')
        if not path.is_file() or path.name == 'snowpatcher-update.json':
            continue
        relative = path.relative_to(root).as_posix()
        if relative.casefold() in names:
            raise ValueError('Release paths must be unique on Windows')
        names.add(relative.casefold())
        with path.open('rb') as source:
            digest = hashlib.sha256()
            for chunk in iter(lambda: source.read(1024 * 1024), b''):
                digest.update(chunk)
        files.append({'path': relative, 'size': path.stat().st_size, 'sha256': digest.hexdigest()})
    entry = 'SnowPatcher.exe' if platform == 'win-x64' else 'SnowPatcher'
    required = (entry, 'SnowPatcher.dll', 'SnowPatcher.Core.dll', 'SnowPatcher.runtimeconfig.json', 'SnowPatcher.deps.json')
    if not all(name.casefold() in names for name in required):
        raise ValueError('The published SnowPatcher files are missing')
    manifest = {'formatVersion': 1, 'version': version, 'platform': platform, 'files': files}
    (root / 'snowpatcher-update.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory')
    parser.add_argument('--version', required=True)
    parser.add_argument('--platform', required=True)
    args = parser.parse_args()
    write_manifest(args.directory, args.version, args.platform)
