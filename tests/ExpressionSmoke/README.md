# Expression integration checks

Run from the repository root with .NET 10 and an installed Resonite runtime:

```powershell
$env:RESOPON_NOPAUSE = '1'
dotnet run --project tests/ExpressionSmoke -c Release
```

No avatar assets are required for the default synthetic fixtures. Artifacts go to a
new timestamped directory under `.tmp_verify/expression-smoke/`; the first argument
can override that directory. For a non-default installation, set `RESONITE_PATH`
for runtime loading and pass `-p:ResonitePath="D:\Games\Resonite"` for build references.
Use the same installation for both.

## Coverage

- FaceEmo normal/CAC detection, first-pattern selection, gesture graph resolution,
  mixed clips, curve validation, and all 64 compiled gesture combinations.
- VRM0/1 parser-to-adapter fixtures: preset aliases and weights, all 64 default
  mappings, missing/unresolved presets, custom namesakes, neutral release, real
  mesh outputs, blink mixing, grouped menus and saved-package reload.
- Both-hands neutral as the load/reset default, with explicit and missing reset
  curves in sparse menu poses, untouched shapes, native blink mixing and reload.
- Public API tags and argument types, unrestricted integer inputs and extra table
  rows, invalid/foreign selection Slots, null release, and same-Slot no-op behavior.
- Menu selection hold, permission settings, reset and wear lifecycle, numbered
  flat selection for up to eight expressions, groups for larger catalogs,
  label/color bindings, and direct-child menus shared by VR and desktop modes.
- Keyboard rising-edge behavior, controller sensor tables and Index neutral bands,
  timeout/drop semantics, delayed snapshots, disconnect/VR-exit sentinels, and permissions.
- Static output writes, sparse named pose values, readable binding keys and collisions,
  existing tracking drivers, normal/inverted blink mixing, and actual mesh weights.
  Blink mesh outputs bypass smoothing; other mesh outputs converge through SmoothValue.
- DynamicVariable spaces, independent Flux boards, node layout, local references,
  cloning, package inspection and reload.
- Generated Avatar Root Identification and loading stand-in behavior, including
  wear state, head tracking, mesh visibility, and collected fallback texture assets.
- Hand collider raycasts follow local wear state through equip, dequip, reparenting
  and package reload, with an unresolved-variable fallback and unchanged tool positions.

Current behavior and editing instructions are documented in the
[expression system](../../docs/expression-system.md),
[variable reference](../../docs/expression-variables.md), and
[controller input reference](../../docs/controller-gestures.md).
Tests inspect generated stock components and execute ProtoFlux in a headless world.
They do not establish physical controller behavior, keyboard focus handling,
interactive menu usability, or multi-client synchronization in a running client.

## Locally converted package

The second argument switches runtime validation to an imported avatar package
with the current expression layout and a generated context menu:

```powershell
dotnet run --project tests/ExpressionSmoke -c Release -- .tmp_verify/imported-gestures path/to/avatar.resonitepackage
```

The check sends both hand APIs for every table cell (64 for both VRChat and VRM),
including null/release cells,
then compares actual outputs against fixed pose values and Base after smoothing
settles. External tracking sources are frozen for this comparison; separate blink
fixtures exercise live tracking. It also presses the saved grouped menu buttons and
checks selection hold and permission behavior.

At least eight distinct visible poses are required by default. For an avatar with
fewer poses, set `RESOPON_TEST_EXPECTED_DISTINCT_POSES` to an independently verified
count from 2 through the number of table cells; the test then requires exactly that
count. Do not change
the expected count merely to accept unexpected output.

An optional third argument compares a previous package's semantic snapshot:

```powershell
dotnet run --project tests/ExpressionSmoke -c Release -- .tmp_verify/imported-gestures path/to/new.resonitepackage path/to/baseline.resonitepackage
```

Comparison covers catalog entries, final pose values, output bindings and all
mappings. Snapshot readers support legacy layouts, but intentional semantic changes
can still fail comparison. Inspect `current-expressions/expressions.json` and
`baseline-expressions/expressions.json`; legacy compatibility does not imply that
all old package versions must compare equal to current output.

Keep local avatar paths and results outside version control, using the
[local avatar regression workflow](../../docs/local-avatar-tests.md).
For an independent neutral-pose oracle, set `RESOPON_TEST_INITIAL_SHAPES` to JSON
such as `{"Body":{"Mouth.Open":0,"Face.Adjust":0.5}}`. The saved mesh values are
checked after loading and again after expression/menu playback and reset. Renderer
names must be unique; obtain expected values from the source avatar, not the output.
The headless test enables ForceFullUpdateCycle in its world and exits without
asynchronous engine shutdown callbacks; neither behavior is exported to the avatar.
