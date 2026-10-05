"""Read the explicit pinned mechanism matrix; the fixture library is not a release queue."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MATRIX = ROOT / 'docs/release-matrix.json'


def load_matrix():
    matrix = json.loads(MATRIX.read_text())
    family_path = ROOT / 'docs/test-families.json'
    families = json.loads(family_path.read_text())['families']
    if matrix.get('schemaVersion') != 1:
        raise RuntimeError('Unknown release matrix schema')
    if matrix.get('familiesSha256') != hashlib.sha256(family_path.read_bytes()).hexdigest():
        raise RuntimeError('Mechanism review changed; review the release matrix before running or packaging')
    if matrix.get('dependencies') != json.loads((ROOT / 'versions.lock.json').read_text())['sha256']:
        raise RuntimeError('Release matrix was reviewed for different dependencies')
    if set(matrix['families']) != set(families):
        raise RuntimeError('Every reviewed mechanism family requires a primary witness')
    names = []
    for name, review in matrix['families'].items():
        witnesses = review.get('witnesses', [])
        if not witnesses or not review.get('reason') or set(witnesses) - set(families[name]['fixtures']):
            raise RuntimeError('Missing or unrelated primary mechanism witness: ' + name)
        names.extend(witnesses)
    for boundary, fixtures in matrix['boundaries'].items():
        if not fixtures:
            raise RuntimeError('Empty semantic boundary: ' + boundary)
        names.extend(fixtures)
    if not {'unknown-rune', 'unknown-enemy', 'banned-globe', 'banned-solid'}.issubset(names):
        raise RuntimeError('Release matrix omitted unknown-effect or authorized-ban guards')
    names = list(dict.fromkeys(names))
    for name in names:
        if Path(name).name != name or not (ROOT / 'tests/fixtures' / (name + '.json')).is_file():
            raise RuntimeError('Invalid or missing matrix fixture: ' + name)
    return matrix, names


if __name__ == '__main__':
    matrix, names = load_matrix()
    cold = [name for name in names if json.loads((ROOT / 'tests/fixtures' / (name + '.json')).read_text()).get('hextechExpectedRejection')]
    native = [name for name in names if name not in cold]
    print(json.dumps({'families': len(matrix['families']), 'fixtures': len(names),
                      'reuseBatches': [native[i:i + 20] for i in range(0, len(native), 20)],
                      'coldGuards': cold}, ensure_ascii=False, indent=2))
