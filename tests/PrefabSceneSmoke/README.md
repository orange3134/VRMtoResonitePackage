# Prefab scene regression checks

The final checks build a temporary Unity project containing the supplied FBX and a
Prefab Variant, invoke the production converter, and decode the saved package.
They verify embedded assets and the overridden hierarchy name. This uses the test's
isolated engine data/cache directories and `NoAvatar` mode; no Unity Editor or
Library folder is needed. Full humanoid setup is outside this end-to-end fixture.

A second production conversion removes a renderer component directly from the FBX;
the saved package must omit that renderer while retaining its GameObject. Scene
checks also retain child renderers, bone/field references, other same-named branches
and model instances, authored copies and objects with unknown imported paths.

Default-weight checks distinguish same-named imported branches using captured
paths after reparenting and renaming, include authored copies, preserve explicit
zero overrides, and leave namesakes unchanged when a source path is missing.

Mesh-template cleanup checks remove a whole unused imported skeleton, while
preserving models with live skin/field references, authored objects, foreign
attachments, renderers, behavior components, or Rig registrations for moved bones.
Ordinary model instances remain outside this cleanup's candidate set.

Custom-eye rig assignment distinguishes same-named transforms and rejects missing
or destroyed explicit references. Bone Proxy checks cover repeated accessories,
descriptor-relative paths, avatar-root targets, humanoid targets after armature
merging, missing references, and target paths changed by an earlier proxy move.

Humanoid head checks put clothing Head/Hips namesakes first, then verify primary
model selection for alignment, rig assignment and first-person erase-bone indices.
First-person fallback rejects clothing rigs and missing/destroyed explicit heads;
the VRM node-name resolution behavior remains unchanged.

Also verifies static-mesh import scale outside the source hierarchy, empty Animator
bindings below the export root, and separate DynamicBoneChains on same-named bones
in repeated model instances.
Merge Armature checks reproduce the destroyed-source lookup and verify that physics
targets follow successive bone replacements while another instance remains intact.
References on the source armature itself also follow successive replacements.
Descriptor wrappers created by intact FBX placement resolve without authored mesh
copies, so descriptor-relative Merge Armature paths connect clothing to body bones.
Physics-only hierarchies are created without mesh copies and resolve independently
of the exported avatar root.

Also checks that EditorOnly removal follows captured model paths after reparenting and preserves a separate branch containing identically named nodes.
Top-level namesakes also remain intact, including their child bones. Mesh template
selection distinguishes a nested renderer from a top-level renderer with the same
name, using the captured path even after reparenting.

Copied-bone checks distinguish same-named bones in two branches of the primary
model and in an additional model, including targets moved after path capture.
Index-based overrides also distinguish identical source bone names and restore a
binding whose imported source entry is null.
Renamed unpacked bones retain the original binding when unresolved, or use their
prefab transform identity once authored parent slots have been created.

Static-renderer checks cover copying an imported MeshRenderer, retaining its
placement and disabled/inactive states, replacing its template, applying prefab
materials after reparenting, and using a skinned FBX mesh as a static renderer.

Animator binding checks include empty paths resolving the root renderer without
falling back to child renderers.
Descriptor visemes and index-based blink select the second same-named renderer by
object identity, retain it after reparenting, and reject missing identities.
Captured empty and nonempty binding paths also retain the original renderer after
reparenting and eye-pivot insertion, while a late capture reproduces the missing binding.
Root-renderer replacement also covers a mesh source nested beneath the placeholder.
Model-root physics references survive both primary-wrapper and alignment collapse,
while another model's root identity remains independent.
Additional RootNode wrapper collapse preserves synthetic-root physics identities.
Primary imports retaining a RootNode resolve empty, RootNode and //RootNode paths
to that node before and after wrapper and alignment collapse.
Unpacked attachments reuse the imported skin bone, and same-path authored physics
targets install independent chains on their respective slots.
Reused imported bones apply authored inactive state to their attachment hierarchy.
Physics on authored static renderer copies resolves through prefab transform
identity before imported template paths, including repeated local file IDs in
different prefab instances and targets without an FBX identity.
Converter-order regressions verify that physics placements created after mesh copies
share their authored bones with copied skins when multiple FBX sources are present,
and retain unit position and scale under a static renderer with a 0.01 import correction.

This integration check boots Resonite's headless engine and imports a local FBX containing a skinned mesh with at least one blendshape. It copies the renderer from an additional model under a primary model, then checks explicit material overrides, FBX default material mappings, initial blendshape weights, inactive state, and isolation from an identically named primary-model renderer.
It also verifies that copied skins, physics placements, and humanoid name lookup
share the same primary imported skeleton.

```powershell
dotnet run --project tests/PrefabSceneSmoke -c Release -- "D:\Models\avatar.fbx" Body
```

The FBX is read-only and is not included in the repository. Use `RESONITE_PATH` and `-p:ResonitePath=...` for a non-default Resonite installation. The test creates an isolated temporary Unity fixture, engine data, cache, and logs under `%TEMP%\ResoPonSceneSmoke`; it does not use the user's Resonite profile. It exits after the assertions without invoking asynchronous engine shutdown callbacks. Success is indicated by `Prefab scene smoke checks passed.` and exit code 0.
