#!/usr/bin/env python3
"""Read-only comparison against the frozen complete emulator source identity."""
import hashlib
import json
from pathlib import Path
import sys

def main():
    if len(sys.argv) != 2:
        raise SystemExit('Usage: verify-source.py /path/to/reconstructed/source')
    root = Path(sys.argv[1]).resolve(strict=True)
    manifest = json.loads(Path(__file__).with_name('SOURCE-MANIFEST.json').read_text())
    actual = {}
    for p in root.rglob('*'):
        rel = p.relative_to(root)
        if any(part in {'.git', 'bin', 'obj'} for part in rel.parts):
            continue
        if p.is_symlink():
            raise SystemExit('Unexpected source symlink: ' + rel.as_posix())
        if p.is_file():
            actual[rel.as_posix()] = hashlib.sha256(p.read_bytes()).hexdigest()
    expected = manifest['files']
    missing = sorted(set(expected) - set(actual))
    extra = sorted(set(actual) - set(expected))
    changed = sorted(p for p in set(actual) & set(expected) if actual[p] != expected[p])
    if missing or extra or changed:
        print(json.dumps({'missing': missing, 'extra': extra, 'changed': changed}))
        raise SystemExit(1)
    tree = hashlib.sha256()
    for name, digest in sorted(actual.items()):
        tree.update(name.encode() + b'\0' + bytes.fromhex(digest) + b'\n')
    if tree.hexdigest() != manifest['source_tree_sha256']:
        raise SystemExit('Source tree identity mismatch')
    print(json.dumps({'source_match': True, 'files': len(actual), 'tree_sha256': tree.hexdigest()}))

if __name__ == '__main__':
    main()
