# Prefab scene regression checks

Also checks that EditorOnly removal follows captured model paths after reparenting and preserves a separate branch containing identically named nodes.

Copied-bone checks distinguish same-named bones in two branches of the primary
model and in an additional model, including targets moved after path capture.

Static-renderer checks cover copying an imported MeshRenderer, retaining its
placement and disabled/inactive states, replacing its template, applying prefab
materials after reparenting, and using a skinned FBX mesh as a static renderer.

This integration check boots Resonite's headless engine and imports a local FBX containing a skinned mesh with at least one blendshape. It copies the renderer from an additional model under a primary model, then checks explicit material overrides, FBX default material mappings, initial blendshape weights, inactive state, and isolation from an identically named primary-model renderer.

```powershell
dotnet run --project tests/PrefabSceneSmoke -c Release -- "D:\Models\avatar.fbx" Body
```

The FBX is read-only and is not included in the repository. Use `RESONITE_PATH` and `-p:ResonitePath=...` for a non-default Resonite installation. The test creates an isolated temporary Unity fixture, engine data, cache, and logs under `%TEMP%\ResoPonSceneSmoke`; it does not use the user's Resonite profile. It exits after the assertions without invoking asynchronous engine shutdown callbacks. Success is indicated by `Prefab scene smoke checks passed.` and exit code 0.
