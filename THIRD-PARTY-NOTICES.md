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

## Resonite.UnitySDK

Material conversion in MaterialTuner, LilToonConverter, VrchatMaterialBuilder,
ShaderPropertyFallbackConverter and the main-texture baking plan adapts
Resonite.UnitySDK's MToon, lilToon and property-based converters.

Project: https://github.com/Yellow-Dog-Man/Resonite.UnitySDK
Audited source: 5eea4b032d1eb4d42a9d6df522a8888506c93bba
License file: LICENSE

MIT License

Copyright (c) 2026 Yellow Dog Man Studios

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

## lilToon

VrchatMainTextureBaker adapts lilToon color correction, gradation, alpha-mask
and layer-blending behavior for CPU texture baking, including logic from
Assets/lilToon/Shader/Includes/lil_common_functions.hlsl.
The lilToon Unity package and its textures are not bundled.

Project: https://github.com/lilxyzw/lilToon
Audited source: a750ad0dfa01e215cc10e63e59bb51f8db7eaab9
License file: LICENSE

MIT License

Copyright (c) 2020-2024 lilxyzw

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

## UniVRM / MToon

VrmParser's MToon 0.x shading conversion follows MToon10Migrator in
Packages/VRM10/MToon10/Runtime/MToon10Migrator.cs.
UniVRM assemblies and sample models are not bundled.

Project: https://github.com/vrm-c/UniVRM
Audited source: 2b5ebcf3d793f7a853e31412cd1c32b4f79a6962
License file: LICENSE.txt

MIT License

Copyright (c) 2020 VRM Consortium
Copyright (c) 2018 Masataka SUMI for MToon

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

## Modular Avatar

VrchatSkinRetargeter's bind-pose correction follows the approach in
Editor/MeshRetargeter.cs. This notice credits that reference implementation.
Modular Avatar's restricted Editor/images assets are not included.

Project: https://github.com/bdunderscore/modular-avatar
Audited source: d110f247e18f6938a909b6423b70855b28036219
License file: COPYING.md (MIT portion, applicable to source code)

MIT License

Copyright (c) 2022 bd_

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

## Modular Avatar for Resonite

ResoPon adapts the avatar identification, mesh-loading gate, loading stand-in
and dynamic-bone template setup from modular-avatar-resonite.
The relevant upstream files include Puppeteer/conversion/AvatarSetup.cs,
Puppeteer/conversion/Dynamics.cs, Puppeteer/filters/MeshLoadingFilter.cs,
and MA.Gadgets/resources/loading_standin.resonitepackage under
Resonite~/ResoniteHook/.

LoadingFallback.png is the fallback texture extracted from that package.
The reconstructed loading stand-in and other adapted component graphs are
included in generated avatars. This notice accompanies those graphs and
the fallback image; it does not license the user's avatar assets.

Project: https://github.com/bdunderscore/modular-avatar-resonite
Audited source: 1ec58b5637f85149d0fea4cd5785024118290df9
License file: COPYING.md

MIT License

Copyright (c) 2025 bd_

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

## .NET native application host

The published ResoPon.exe includes the .NET native application host supplied
by Microsoft.NETCore.App.Host.win-x64, even in framework-dependent builds.
The .NET and Windows Desktop shared runtimes are installed separately.

Project: https://github.com/dotnet/runtime
License: https://github.com/dotnet/runtime/blob/v10.0.8/LICENSE.TXT

The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

## xxHash

UnityModelFileIdResolver implements the xxHash64 rounds, lane processing and
avalanche for Unity model file IDs, with a signed four-byte tail for Unity
compatibility. This notice credits the xxHash reference implementation.
The upstream C library is not bundled.

Project: https://github.com/Cyan4973/xxHash
Reference: https://github.com/Cyan4973/xxHash/blob/v0.8.3/xxhash.h
The header additionally states: Copyright (C) 2012-2023 Yann Collet.
License: https://github.com/Cyan4973/xxHash/blob/v0.8.3/LICENSE

xxHash Library
Copyright (c) 2012-2021 Yann Collet
All rights reserved.

BSD 2-Clause License (https://www.opensource.org/licenses/bsd-license.php)

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this
  list of conditions and the following disclaimer in the documentation and/or
  other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
