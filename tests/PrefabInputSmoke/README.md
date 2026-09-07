Run from the repository root:

```powershell
dotnet run --project tests/PrefabInputSmoke -c Release
```

Requires the same Resonite installation as the app build. Set `RESONITE_PATH` for a
nonstandard installation. Checks project GUID lookup, package assets, selected
prefab isolation, invalid inputs, preservation of source files, unitypackage
compatibility, model-scoped exclusion of same-named meshes, EditorOnly ancestors,
nested prefabs, tag overrides, regular-prefab component exclusion, same-named FBX
branches indexed by file ID/path, and exclusion of whole model/object hierarchies
without starting the engine. Fixtures remain in the printed temp directory for
inspection. This does not verify an end-to-end avatar conversion.

Copied-bone checks cover local transform paths and primary skeleton scope,
explicit/omitted stripped references, FBX paths with duplicate node names, and
Variant bone overrides (local references, external references, and null).

Static-renderer checks cover MeshFilter pairing, regular-prefab placement and
materials, Variant enabled/transform overrides, additional FBX discovery, and
EditorOnly exclusion without changing primary skinned-model selection.
