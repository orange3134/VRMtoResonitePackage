# Offline Unity reference comparison

This development-only comparator uses Python 3 standard libraries. It does not
start Unity, install packages, run the conversion app, or read any avatar assets.
Its input is the JSON reference captured in Unity and the matching JSON snapshot
from the ResoPon parser (v1/v2), or a repeated Unity capture (v2). Capture/compare is separate from the production converter.

Use `scripts/check-prefab-unity.py --registry .local/avatar-tests.json` to capture
and compare all registered prefab inputs sequentially. `--case <id>` selects a
subset. The resulting report distinguishes capture, parity and process failures;
its coverage manifest can be replayed without starting Unity.

Native generated-mesh metadata has a separate synthetic regression:

```sh
dotnet build tests/UnityReferenceDump -c Release -o .tmp_verify/reference-build
dotnet .tmp_verify/reference-build/UnityReferenceDump.dll --check-native-metadata .tmp_verify/new-native-metadata-fixture
dotnet .tmp_verify/reference-build/UnityReferenceDump.dll --check-source-transforms .tmp_verify/new-source-transform-fixture
```

It checks binary and YAML subassets, signed fileIDs, channel names/order, stale
weights, empty meshes, missing/wrong-type rejection and unchanged mesh inputs.
Source transform checks cover tiny authored TRS in binary FBX 7400/7500, helper
collapse, static versus skinned pivots, six Euler orders and unsupported fallbacks.

For an independent Unity skin-root oracle, generate a self-authored model with
`--write-skin-root-fixture <new.fbx>`. Copy `ResoPonSkinRootSmoke.cs` from
`tools/unity/Tests` into a trusted project's Editor folder alongside the exporter.
Set `RESOPON_SKIN_ROOT_SOURCE` to that FBX and `RESOPON_REFERENCE_SMOKE_OUT` to a
new output directory, then invoke `ResoPonSkinRootSmoke.Run` in batch mode. It tests
opposite cluster orders and a weighted common ancestor, and writes a prefab path
and Unity expectation for the regular producer/comparator to replay.

```sh
python scripts/compare-unity-reference.py expected.json actual.json
python scripts/compare-unity-reference.py expected.json actual.json --report diff.json
python -m unittest discover -s tests/UnityReferenceComparison -v
```

Exit status: `0` matching captured fields, `1` differences, `2` malformed input or
I/O failure. Output contains individual field paths and expected/actual values.
Missing and unexpected comparable fields or records are failures. Both projections
ignore the legacy renderer `enabled` field because ResoPon drives it during mesh loading.
The report path cannot overwrite either input.

For v1, records in `renderers`, `physBones`, and `colliders` are matched by their exact
`id` (signed 64-bit local fileID string in the selected prefab), not by order or display name. IDs must be unique. Material slots and collider
reference arrays remain ordered. JSON duplicate keys, wrong primitive types and
non-finite numbers and captures with no component records are rejected. Asset `fileId` values are strings, avoiding large
integer precision loss. Float tolerances (absolute and relative, both `1e-6` by
default) apply only to floating-point values; integer values compare exactly.

The v1 schema uses `format: resopon.unity-reference`, `schemaVersion: 1`, and
`projection: selected-prefab-authored-components`. The projection deliberately
covers only the supported authored component subset, not all effective imported
FBX components. A match proves parity only for the captured fields and scope.
It does not prove that physical simulation, expressions, materials, skinning, or
Resonite runtime behavior are equivalent.

`synthetic-reference.json` is invented data, not an export of a paid or third-party
avatar. Tests exercise missing records, material slot order, changed references,
active values, type mismatches, duplicate identities and CLI exit behavior. Private
Unity references and actual snapshots should remain outside tracked directories.

Resolved v2 uses `schemaVersion: 2`, `projection: unity-resolved-prefab` and adds
`transforms`. Hierarchy sibling indices plus component type/ordinal distinguish
same-name nodes and repeated instances with identical source fileIDs. Inserting
or reordering siblings intentionally changes IDs. Source and original-source
asset references remain provenance, not instance identity. Ordered bone bindings,
blendshape weights, local transforms and selected PhysBone parameters are included.
The validator checks internal references and field types; v1/v2 mixing is rejected.
The ResoPon v2 producer resolves YAML-authored and implicit FBX hierarchy/components,
including variants, removals and repeated nested model occurrences. It uses shared
model metadata, imported ID conventions and serialized overrides; missing dependencies
and ambiguous/optimized model hierarchy fail explicitly. Comparing two Unity captures still proves
repeatability of captured fields, not ResoPon parity.

The Python suite includes invented v2 data for repeated instances, missing records,
ordered references, transform/weight changes, invalid identities and dangling refs.
The quaternion sign pair `q` / `-q` compares equal without changing numeric tolerances.
Replay tests also prevent unsupported cases, changed baselines and field differences
from being reported as matches. Existing replay output is never reused.

For the Unity integration smoke test, copy `tools/unity/Editor/*.cs` and
`tools/unity/Tests/ResoPonResolvedReferenceSmoke.cs` into a trusted project's
`Assets/Editor/` (Unity 2022.3 with VRChat SDK3 PhysBones). Set
`RESOPON_REFERENCE_SMOKE_OUT` to a new local output directory, then run Unity in
batch mode with `-executeMethod ResoPonResolvedReferenceSmoke.Run`.
It creates self-authored test assets under a uniquely named
`Assets/ResoPonReferenceSmoke_*` directory. These assets remain available for inspection.
The test checks actual Variant overrides, removed objects/components, additions,
duplicate-name nested instances, inactive state and cross-instance collider refs.
It also checks identical repeated captures, unchanged prefab bytes, refusal to
overwrite output, and the legacy authored projection's Variant rejection.
Exit code 0 and the `PASS` log indicate success. Generated assets/JSON stay local.

After building `tests/UnityReferenceDump`, run the generated fixture through
`scripts/test-unity-reference-producer.py variant.prefab first.json --reference-dump
path/to/UnityReferenceDump.dll --output .tmp_verify/new-producer-check`.
This checks actual parser parity with Unity, independence from expected values,
negative comparison controls, output protection and unchanged source assets.
Use `scripts/test-unity-resolved.py` to replay a private collection's `coverage.json`;
see [the v2 workflow](../../docs/unity-reference-tests.md) for status semantics and limits.

For the FBX variant integration fixture, generate a new self-authored model with
`UnityReferenceDump --write-fbx-fixture output.fbx`. Set `RESOPON_FBX_SMOKE_SOURCE`
to that file, copy `ResoPonFbxReferenceSmoke.cs` to the Unity project's Editor folder,
and run `ResoPonFbxReferenceSmoke.Run` with a new `RESOPON_REFERENCE_SMOKE_OUT`.
The generated `prefab-path.txt` identifies the variant for the same producer check.
It covers repeated model instances, Japanese names, underscore ordering, model
Renderer/GameObject removals, additions, inactive state and physics references.
