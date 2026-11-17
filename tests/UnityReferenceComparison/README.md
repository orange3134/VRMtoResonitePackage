# Offline Unity reference comparison

This development-only comparator uses Python 3 standard libraries. It does not
start Unity, install packages, run the conversion app, or read any avatar assets.
Its input is the JSON reference captured in Unity and the matching JSON snapshot
from the ResoPon parser. Capture/compare is separate from the production converter.

```sh
python scripts/compare-unity-reference.py expected.json actual.json
python scripts/compare-unity-reference.py expected.json actual.json --report diff.json
python -m unittest discover -s tests/UnityReferenceComparison -v
```

Exit status: `0` matching captured fields, `1` differences, `2` malformed input or
I/O failure. Output contains individual field paths and expected/actual values.
Missing and unexpected fields or records are failures, never a silent skip.
The report path cannot overwrite either input.

Records in `renderers`, `physBones`, and `colliders` are matched by their exact
`id` (signed 64-bit local fileID string in the selected prefab), not by order or display name. IDs must be unique. Material slots and collider
reference arrays remain ordered. JSON duplicate keys, wrong primitive types and
non-finite numbers and captures with no component records are rejected. Asset `fileId` values are strings, avoiding large
integer precision loss. Float tolerances (absolute and relative, both `1e-6` by
default) apply only to floating-point values; integer values compare exactly.

The schema uses `format: resopon.unity-reference`, `schemaVersion: 1`, and
`projection: selected-prefab-authored-components`. The projection deliberately
covers only the supported authored component subset, not all effective imported
FBX components. A match proves parity only for the captured fields and scope.
It does not prove that physical simulation, expressions, materials, skinning, or
Resonite runtime behavior are equivalent.

`synthetic-reference.json` is invented data, not an export of a paid or third-party
avatar. Tests exercise missing records, material slot order, changed references,
active values, type mismatches, duplicate identities and CLI exit behavior. Private
Unity references and actual snapshots should remain outside tracked directories.
