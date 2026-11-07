# Third-party notices

## Avatar Expression Editor

The controller gesture classifiers are adapted from the behavior of
Avatar Expression Editor v1.12.1 and its bundled hand-sign add-ons, inspected in
Resonite. The source tool contains the following MIT license notice.

MIT License

Copyright (c) 2022-2025 rhenium, kazu0617, orange

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## FaceEmo

The VRChat expression importer adapts source-level expression detection and
selection logic from FaceEmo 1.7.0 by suzuryg, commit
`829f9ddf5d75aa28cbfa93293ade8c901f191ea0`.

Project: https://github.com/suzuryg/face-emo
License: https://github.com/suzuryg/face-emo/blob/829f9ddf5d75aa28cbfa93293ade8c901f191ea0/LICENSE.md

The adapted logic covers normal/CAC branch extraction, gesture condition filtering,
branch ordering and first-match selection, first-pattern selection, face motion
and authored-value difference detection, face mesh selection, and viseme exclusions.
The source files, relative to `Packages/jp.suzuryg.face-emo/`, are:

- `Editor/Detail/AV3/Importers/ExpressionImporter.cs`
- `Editor/Detail/AV3/Importers/ImportUtility.cs`
- `Editor/Detail/AV3/AV3Utility.cs`
- `Runtime/Domain/Mode.cs`

These parts are adapted to ResoPon's Unity YAML reader and fixed-pose output,
primarily in `VrchatFaceEmoExpressionImporter`, `VrchatExpressionDetection`,
`VrchatExpressionParser`, and the face-mesh selection in `VrchatAvatarParser`.
FaceEmo's Unity Editor package, UI, assets, and bundled third-party libraries
are not included by this integration. The following notice applies to the
FaceEmo-derived portions, not to the entire converter or to input avatar assets.

The MIT License (MIT)
Copyright (c) 2023 suzuryg

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
