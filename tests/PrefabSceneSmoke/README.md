# Prefab scene regression checks

Also verifies static-mesh import scale outside the source hierarchy, empty Animator
bindings below the export root, and separate DynamicBoneChains on same-named bones
in repeated model instances.
Merge Armature checks reproduce the destroyed-source lookup and verify that physics
targets follow successive bone replacements while another instance remains intact.
References on the source armature itself also follow successive replacements.
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
Root-renderer replacement also covers a mesh source nested beneath the placeholder.
Model-root physics references survive both primary-wrapper and alignment collapse,
while another model's root identity remains independent.
Additional RootNode wrapper collapse preserves synthetic-root physics identities.
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
