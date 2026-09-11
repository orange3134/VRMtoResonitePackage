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
Duplicate source bone names retain their individual indices through FBX lookup,
authored renderer parsing, and Variant overrides.
The same skin with two materials retains its two distinct, identically named bones
without counting the repeated Assimp submesh bone arrays twice.

Static-renderer checks cover MeshFilter pairing, regular-prefab placement and
materials, Variant enabled/transform overrides, additional FBX discovery, and
EditorOnly exclusion without changing primary skinned-model selection.

Nested-component checks cover regular-prefab PhysBones, colliders, Modular Avatar
operations, selected-subtree isolation, EditorOnly exclusion, external root
references and null overrides. Cache checks include stale versions without a lock
file and continued access to embedded packages.
Physics-only prefab roots keep their authored identity, enclosing placement,
local transforms and chain descendants without aliasing the primary FBX root.
Removed PhysBones and colliders are excluded per instance, including explicit and
omitted stripped aliases in outer variants; sibling instances retain their components.

Animator checks cover Solo/Mute filtering of State, Entry and Any State transition
lists, condition-independent Solo suppression, priority among multiple Solo
transitions, list isolation, and viseme/silence inference from eligible entries.
Viseme reachability checks reject disabled and enabled toggle gates and disconnected
submachines, while retaining an ungated connected submachine.
Ordered transition checks reject shadowed Entry, State and Any State routes,
including overlapping Viseme ranges, while preserving muted/later fallbacks and
an independent route to the same transition.

Descriptor inheritance checks cover FX controller replacement/null, layer type and
default flags, layer-array shrinking, lip-sync mode, explicit/omitted stripped
aliases across multiple variants, unrelated targets, and base-cache preservation.

Descriptor-wrapper checks cover nested authored geometry, direct FBX instances,
and outer renderer/transform overrides. Empty Animator binding paths are checked
for both visemes and blink, including preservation through model adaptation.

Mixed wrappers retain nested body geometry alongside local static/skinned
accessories. Descriptor inheritance also covers viewpoint, viseme strings,
eye/eyelid fields, packed eyelid arrays, null references, and local/stripped/external
object references across multiple variants without mutating the base asset.

Composed-wrapper checks also exclude sibling instances outside the selected
descriptor subtree and preserve repeated direct FBX/nested prefab occurrences,
independent placements and renderer overrides, stable per-occurrence identities,
and unchanged source-scene caches across repeated parsing.

Local-review regressions cover unpacked template replacement while retaining
separately instantiated models, scene-local bone references, enclosing attachment
transforms, explicit/omitted aliases in outer variants, independent model tags,
partial Animator layer rejection, and per-instance physics node identities.
Logging checks exercise captured callbacks and console writers after log disposal.

Renderer-removal checks cover MeshRenderer and MeshFilter removals through direct,
explicit stripped and omitted stripped references across nested instances. They
retain surviving child/sibling renderers, discard removed material records and
verify that the reusable source scenes remain intact.

Review regressions also cover rebasing a selected subtree without changing its
source, imported bone parents for unpacked attachments, distinct same-path physics
targets with and without FBX identities, and defaults bypassed by Entry routing.
Imported bone parents retain model scale identity and authored inactive state.
Viseme stability checks include owning and ancestor Any State departures, with
muted and other-phoneme transitions retained as negative controls.
Curve checks reject rising/falling viseme weights and equal keys with nonzero
interpolation slopes while retaining constant visemes and animated blink inference.
Unpacked physics helpers with inferred FBX identities retain their local roots,
descendants and collider placements and reuse verified imported skeleton parents.
Competing Animator layers reject visemes overridden by constant zero curves at full
or partial weight, while unrelated blendshapes and zero-weight layers remain eligible.
The same competing-layer checks cover blink inference. Viseme clips that reset an
authored Smile weight while activating a mouth shape are rejected; single-shape
visemes remain eligible.
