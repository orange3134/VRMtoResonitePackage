Run from the repository root:

```powershell
dotnet run --project tests/PrefabInputSmoke -c Release
```

Requires the same Resonite installation as the app build. Set `RESONITE_PATH` for a
nonstandard installation. Checks project GUID lookup, package assets, selected
prefab isolation, invalid inputs, preservation of source files, and unitypackage
compatibility without starting the engine. Fixtures remain in the printed temp
directory for inspection. This does not verify an end-to-end avatar conversion.
