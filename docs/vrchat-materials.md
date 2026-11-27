# VRChatのマテリアル変換

材質の選択と継承は[VRChat変換の全体設計](vrchat-conversion.md)で解決したPrefabの参照を使う。

## 変換先と材質の選択

動的テクスチャと複数材質の制約:

Ekuの `Takt_Screen` とMilfyの `SmartPhone_Screen` の第2レイヤーは、それぞれの `ScreenRT.renderTexture` を参照する。
RenderTextureはカメラ等の実行時出力であり、通常画像のデコーダーへ渡さない。
ベイク計画で明示的に警告し、該当する追加レイヤー・マスクの処理だけを除外する。
メイン画像自体が動的ならメイン画像のベイクを省略し、材質の色とUV変換を保持してメインテクスチャを未割当にする。
発光でも動的画像を入力から外し、利用可能な静的な発光マップやマスクを使う。
参照先が存在しない場合はUnityのnull/白フォールバックを維持する。動的画面の再現を実装したものではない。
画像デコードがnullを返した場合も、後続のNullReferenceExceptionではなく対象パスとGUIDを報告する。

Ekuの `Hair_Front` はUnityでも1サブメッシュに2材質（髪とfake shadow）が割り当てられている。
輪郭除外で追加するサブメッシュが既存の追加材質を奪わないよう、この場合は処理を保留する。
警告には材質数・サブメッシュ数を含め、入力自体の構成か変換途中の不整合かを切り分けられるようにする。

material variantは `m_Parent` chainをbaseから継承し、子の差分を上書きする。
FBX `externalObjects` がない場合は、埋め込みmaterial名と `.mat` filename、diffuse texture basenameから
一意な対応を推定する。prefab rendererの明示overrideが最優先である。

### 半透明材質のAlpha / Cutout選択

GUIの「半透明マテリアルの変換先を手動で選ぶ」はUnityPackage / Prefabにも適用する。
アバター選択後、そのアバターのrenderer、主FBX、追加FBXのマテリアル参照から対象を列挙する。
材質variantの親を解決した後の `LilToonInfo.AlphaMode="transparent"` が対象で、
通常変換で `XiexeToonMaterial.BlendMode=Alpha` になる材質を選べる。lilToon以外も同じ判定を使う。
FakeShadow、Opaque、既存のCutout、premultiply（ResoniteのTransparent）、Additive、Multiplyは対象外。

同名材質を区別するため、GUIでは `Assets/.../Name.mat` などの論理アセットパスを表示する。
初期選択はすべてAlpha。設定のON / OFFは保存するが、材質の選択は変換ごとに行う。
既存の設定JSONの `PromptMtoonTransparentBlendMode` 名は互換性のため維持する。

CLIは `--transparent-cutout <name|path|guid>` を繰り返して指定する。
論理パス・GUIDは個別の材質を指定し、材質名では一致するすべての対象材質を指定する。
いずれも大文字・小文字を区別する完全一致。従来の `--mtoon-transparent-cutout` は同じ処理の別名。
未指定材質や対象外の材質は通常の変換結果を維持し、`--no-avatar` でも選択を適用する。

Cutoutを選んだ材質は、元の材質・テクスチャの解析とベイクを終えた後に、
`BlendMode.Cutout`、`AlphaClip=0.5`、`ZWrite.On`、`RenderQueue=2450` に設定する。
このオプションでAlphaからCutoutへ変更する場合のしきい値は、元の `_Cutoff` や材質variantの継承値によらず0.5に固定する。
元からCutoutの材質は、継承を含む元のしきい値（未指定時0.5）を引き続き使う。
これはlilToon Inspectorの描画モード切替に合わせた値である。
`Assets/lilToon/Editor/lilInspector/lilPropertyGroupDrawerBaseSetting.cs` の
`DrawRenderingModeSettings` はCutout / FurCutoutへの変更時に `_Cutoff=0.5`、
Transparent / Fur / FurTwoPassへの変更時に0.001を設定する。MultiもCutout系のモード1/5で0.5になる。
毎描画時の固定ではなくモード変更時の代入なので、変更後に手動調整した材質は別の値を持ち得る。
`lilMaterialUtils.SetupMaterialWithRenderingMode` 自体ではこのしきい値の再設定は行わない。
元のテクスチャやEmissionは変更しない。半透明の濃淡は透明 / 不透明の二値になる。

`PrefabSceneSmoke/TransparentMaterialChecks` は両入力形式で対象一覧、同名材質の個別選択、
variantの継承値より優先する0.5固定、未選択・対象外材質の保持、CLI互換・GUI引数の受け渡し、
生成パッケージのinspectと再読込後の材質設定を確認する。

### Unity-Chan Toon Shader 2

UTS2 はメインテクスチャとその UV 変換を `_MainTex` から読む。Lyphos 1.10 のように
同じマテリアルに非表示の `_BaseMap` が保存される場合もある。シェーダーがパッケージ外に
あるときは保存済みの `_utsVersion` (2.x) で識別する。マテリアルバリアントでは省略された
`_MainTex` を親から継承し、明示された null は未設定として扱う。

Lyphos の頬が使う UTS2 の `NoOutline/ToonColor_DoubleShadeWithFeather_Transparent`
は色描画パスでアルファブレンドと ZWrite Off を固定する。保存された Standard 用の
`_Mode` や `_SrcBlend` はこのパスの描画設定に影響しない。シェーダー同梱時は
色描画パスを読み、外部参照の場合は同シェーダーの GUID から Alpha と既定キュー 3000 を復元する。

### lilToon

- headless変換ではUnity editor bakerを実行できない。直接表現できるpropertyとtexture transformを保持し、
  必要なmaskやchannel合成だけを画像処理する。
- `_Main2ndTex` / `_Main3rdTex` はXiexeToonに対応slotがないため、静的UV0レイヤーを
  `VrchatMainTextureBaker` でmain → 2nd → 3rdの順に焼き込む。lilToonの `lilBlendColor` と
  Resonite.UnitySDKのmain texture bakeを参考に、Normal/Add/Screen/Multiply、color、texture alpha、
  blend mask、scale/offset/angle、cutout/transparentのlayer alpha modeを反映する。
  RGBはlinear空間で合成してsRGBへ戻し、alphaはgamma変換しない。
  maskはshaderと同じmain UVを使う。TextureImporterのsRGB・wrap・point/bilinear設定を読む。
  Alpha, blend and color-adjust masks share the main texture's wrap U/V and point/bilinear
  sampler settings (`sampler_MainTex`), while retaining each mask's own sRGB decode.
  Layer textures retain their own samplers; the gradation lookup uses linear clamp.
  出力はUV0の1タイルを表す。MainTextureのSTを焼き込んだ場合、割当先はidentityへ戻す。
  Bake resolution follows transformed texel density, including negative tiling and layer rotation.
  For a W×H layer scaled by (sx, sy) then rotated by angle a, the UV0-axis densities are
  |sx| (W |cos a| + H |sin a|) and |sy| (W |sin a| + H |cos a|).
  Take the maximum density per axis across sampled inputs and round up (minimum 1).
  Blend/color-adjust masks use main ST; alpha masks use main ST composed with mask ST.
  Gradation lookup dimensions do not describe spatial UV density. If required density is
  nonfinite or exceeds 8192 texels on either axis, reject the bake with a warning and retain
  the original main texture, tint and ST via the existing failure path, rather than downsampling.
  元の画像と共有マテリアルを変更しない。無効レイヤーは無視し、variantの差分・明示nullを保持する。
  別UV、decal、view/time依存や個別lightingなど静的画像で再現できないレイヤーは警告して除外する。
  UDIMやUV0の1タイル外へ異なる絵柄を配置する用途は、この画像合成では再現しない。
- ベイクの要否は `LilToonMainTextureBakePlan` で判定する。参照は
  `Resonite.UnitySDK/Assets/ResoniteSDK/MaterialConverters/Custom/lilToon/LilToonXiexeConverter.cs`
  の `GetMainTexture`。処理単位を独立して選び、対象外のレイヤーがあっても他の処理を妨げない。

  | 設定 | ベイク判定とColorの扱い |
  |---|---|
  | 1stの `_Color` のみ | ベイクせずXiexeToon.Colorに保持する |
  | HSVGが既定値以外／gradation強度が非0 | 1stの色補正をベイクする。color-adjust maskも適用する |
  | 2nd／3rd無効 | そのレイヤーの画像・カラーをベイクしない |
  | 2nd／3rd有効、画像あり | UV0のみベイクする。別UVは警告して除外する |
  | 2nd／3rd有効、画像なし | UV指定によらず白画像×レイヤーカラーとしてベイクする |
  | AlphaMaskのmodeが非0、画像あり | 色のベイクとは独立してアルファをベイクする |
  | AlphaMaskのmodeが0／画像なし | アルファマスク処理を行わない |
  | 色ベイクあり | 1st／対象2nd／対象3rdのカラーを一度だけ合成し、XiexeToon.Colorは白へ戻す |
  | アルファのみベイク | Color.aをマスク前に焼き込み、XiexeToon.ColorのRGBを保持してalphaだけ1へ戻す |

  アルファマスクは色処理後の独立した処理として適用する。Replace/Multiply/Add/Subtract、
  `_AlphaMaskScale` / `_AlphaMaskValue`、main UVに対するmask STを反映する。
  Tint alpha must precede the mask even without a color bake: texture alpha 0.8,
  tint alpha 0.5 and an Add mask of 0.3 produce 0.7. Applying tint after the mask
  instead produces 0.5; Replace/Add/Subtract do not commute with tint multiplication.
  Alpha-onlyのときは空の2nd/3rd配列でも処理でき、mask解像度も出力解像度の選択に使う。
  HSVG・gradation・mask・UVModeはmaterial variantで継承し、明示0/nullで無効化できる。
  GUIDが残っていても実ファイルがない参照は、SDKの `Material.GetTexture` が返すnullと同じ扱いにする。
  欠落alpha maskは警告してその処理だけを除外し、有効な色ベイクを止めない。
  欠落したmain/layer画像は警告してshader既定の白として扱う。
  ベイク失敗時は元のtexture・Color・STへ戻し、白への変更だけが残らないようにする。
- legacy `VRChat/Mobile/Toon Lit` はvertex colorを使わないため、XiexeToonでも無効にする。
- ShadowRampMaskがない場合は白を使い、生成rampは縦方向に白から本来のrampへ変化させる。
- MatCapはAdd modeかつblend maskなしの場合だけ変換し、color alphaとtexture alphaをRGBへ焼き込む。
- lilToon emissionは `_EmissionBlendMask` がある、または `_EmissionMainStrength>0` で
  main画像がある場合に `VrchatEmissionTextureBaker` でCPU合成する。
  `_EmissionMap` 単独なら既存画像・ST・UVを保持し、不要な再ベイクをしない。
  main画像は成功したmainベイクの画素を再利用し、HSVG・2nd/3rd・alpha maskの結果を含める。
  その場合のmain STはidentity。mainベイクなし／失敗時は元のmain画像とSTを使う。
  SDK同様、mainの `_Color` はmainベイクに含まれた場合だけ画像側へ反映される。
- 合成はUnitySDKのmultiply layer方式を使う。linear RGBで、発光画像（欠落時は白）へ
  `lerp(1, mask.rgb, mask.a)` と `lerp(1, main.rgb, clamp01(_EmissionMainStrength) * main.a)`
  を乗算する。mask/mainがない場合はその因子を1とし、出力alphaは発光画像のalphaを保持する。
  sRGBへ戻してから既存のRGB×alpha処理を一度だけ適用する。
  `_EmissionColor`・blend・fluorescenceの補正は引き続きmaterial側で行い、画像には重ねて適用しない。
  これはSDKの静的近似であり、lilToon実描画の全発光モードを再現するものではない。
- 発光画像・mask・元のmain画像はそれぞれのST、TextureImporterのsRGB/wrap/filter設定を読む。
  `_EmissionBlendMask` のSTはmaterial variantから継承できる。
  出力解像度は各入力の変換後のtexel密度を満たす大きさにし、8192を超える軸や不正なSTは
  警告してベイクを中止する。成功した場合だけ出力のSTをidentity、UVを0にする。
  出力はUV0の1タイルを繰り返すため、発光画像・mask・未ベイクmainの各軸で周期を検証する。
  Repeatは整数倍率（負も可）、Mirrorは偶数整数倍率だけを許可する。
  Clamp/MirrorOnceの変化する軸は周期を持たないため除外する。倍率0または画像の該当軸が1画素なら
  値が変化しないため許可する。小数倍率などで周期を保持できない場合は警告して合成を省略し、
  下記fallbackに戻す。発光画像があれば元画像・ST・UVを保持する。
  例えばRepeatのX倍率0.5はメッシュUVで2単位ごとに繰り返すため、1タイルの画像への変換はしない。
  これは解像度を増やしても解決しない。成功済みmainベイクは既存の1タイル画像・identity STとして扱い、
  mainベイク自体のタイル外の制約は上記main処理の説明に従う。
  実在する発光画像が非UV0の場合は、UV0のmask/mainとの合成を警告して省略し、元の発光画像・STを保持する。
  合成不要／失敗時のfallbackは `_EmissionMap`、`_EmissionBlendMask`、
  `_EmissionMainStrength>0` のmain texture、白の順で選ぶ。
  `EmissionBakeChecks` で画素値・独立ST・alpha・色空間・強度・欠落参照・variant・
  ベイク失敗時のfallback、正負のタイル外UVにおける発光画像の周期、
  パッケージ保存画像・inspect・再読込を確認する。
- `_EmissionMap_UVMode` はmaterial variantから継承し、明示0でUV0へ戻せる。
  発光mapを使う場合はUV0/UV1を `XiexeToonMaterial.EmissionUV` へ渡し、mapのSTを保持する。
  mask/main/白へのfallbackでは発光mapのUV設定を引き継がずUV0を使う。
  実DLLの `EmissionUV` は範囲0〜1で、参照shaderの `XSHelperFunctions.cginc` も
  0ならUV0、それ以外ならUV1を選ぶ。lilToonのUV2/UV3/Rim（2/3/4）や不正値を
  そのまま渡さず、実際に発光mapがある場合に警告してUV0へ戻す。
  UnitySDKの値の単純転送とはここを意図的に変えている。UV2/UV3/Rimの再現は未対応。
  `EmissionUvChecks` でvariant継承、明示0、共有画像、ST、無効化、画像解除時のfallback、
  未対応値、パッケージ保存・inspect・再読込後のUV設定を確認する。
- outline shader variantとoutline property overrideを認識し、`_OutlineWidth` をXiexeToonへ割り当てる。
- lilToon Rimは表現差が大きいため変換せず、`RimIntensity=0` とする。

### 幅0の輪郭線と目の裏面

lilToonの `_OutlineDeleteMesh=1` は、マスク適用後の幅がほぼ0の頂点を含む輪郭線の面を
描画しない。XiexeToonは幅0でも反転した面を描画するため、目の裏面などが暗い面として
表に出ることがある。幅を0にするだけでは防げない。

`VrchatOutlineExclusions` は明示されたこの設定をmaterial variantから継承し、
main UVのscale/offsetと、lilToonのlinear-repeat・LOD 0サンプリングで判定する。
メッシュのUVは左下原点なので、上から並ぶデコード画像の行を読む際にVを反転する。
`abs(_OutlineWidth * 0.01 * mask.r) < 0.000001` となる頂点を含む三角形を、
同じ表面設定で輪郭線だけを無効にした材質のsubmeshへ移す。面自体は削除しない。
元の頂点、UV、法線、skin、blendshape、既存material indexは保持し、共有mesh/providerや
元材質を変更しない。全三角形が対象なら材質だけを差し替える。
FirstPersonメッシュ生成前に実行し、追加submeshもFirstPersonの材質設定へ引き継ぐ。

設定なし・明示0は対象外。VRChat経路では頂点カラーを取り込まないため、
`_OutlineVertexR2Width` が非0の場合は警告してこの補正を省略する。
材質数とsubmesh数が異なるrendererも、追加submeshへの誤割り当てを避けて省略する。

回帰事例: NoraMiaree 1.0.0の `EyesBackSideSurface_off` が動かす目の裏側の66三角形は
すべてマスク0だった。修正前の保存パッケージでは輪郭線付き材質で描画されていた。
検証では保存後の材質割り当てだけでなく、全頂点と表情差分、面の集合が維持されることも確認する。
