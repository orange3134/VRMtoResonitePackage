# VRM変換の実装資料

この資料は `Vrm/`、`MaterialTuner.cs` と、アバター設定・揺れもののVRM固有処理を変更するときに読む。
リグ・追跡設定、装着者判定、FirstPersonの描画制御、読み込み中の表示、揺れものの共通出力は
[アバター変換の共通実装](avatar-conversion.md)を参照する。

## 変換パイプライン

1. `VrmParser` がVRM 0.x / 1.0を共通の `VrmModel` へ読み込む。
2. `GlbPreprocessor` がResonite `ModelImporter` 向けの互換修正をGLBへ適用する。
3. 一時的に `.glb` としてResoniteへインポートする。
4. `AvatarSetup` がBipedRig、VRIK、視点、口パク・瞬き・視線の追跡、FirstPersonを構築する。
5. `MaterialTuner` と `SpringBoneSetup` がマテリアルと揺れものを設定する。

共通処理の責務と呼び出し順の制約は[共通モデルと処理の責務](avatar-conversion.md#共通モデルと処理の責務)を参照する。

## GLB前処理

### ブレンドシェイプ名と属性

- 旧UniVRMで `targetNames` がprimitive側にしかない場合はmesh側へ昇格・補完する。
  補完しないと複数の空名シェイプが `MeshX.AddBlendShape` で衝突する。
- 同一mesh内のprimitive間でモーフNORMAL/TANGENTの有無が一致しない場合は、先頭primitiveを基準に
  不一致属性を除去する。Resonite側のマージで配列範囲外になるのを防ぐ。

### JoinIdenticalVertices対策

[ModelImporterの共通制約](avatar-conversion.md#modelimporterの共通制約)に対し、GLBでは追加UVで頂点を区別する。

`GlbPreprocessor.AddMorphVertexGuardChannel` はモーフを持つprimitiveへ頂点ごとに一意な追加TEXCOORDを
付与し、結合を防ぐ。値は頂点index、型はVEC2 floatで、BINチャンクと `buffer[0].byteLength` を更新する。
診断には `--assimp-dump` の `movedVerts` を使う。

## 座標系

- VRM0はUniVRMのReverseZ、一般的なVRM1はReverseXを前提とする。
- VRM0は条件を満たす階層へY180をベイクし、CenteredRootの不要なY180を防ぐ。
- Blender VRMアドオン製VRM1は通常のglTF配置になる場合がある。
  `rightUpperLeg.x > leftUpperLeg.x` を手掛かりに判定し、
  `MirrorXForProperHandedVrm1` でUniVRM規約へ合わせる。
- X反射ではtranslation、quaternion、IBM、POSITION、NORMAL、TANGENT、三角windingを一貫して変換する。
  VRM0のY180回転と反射を混同しない。
- コライダーオフセットは `VrmModel.OrientationMirroredX` とVRM世代を考慮して変換する。

## VRMのアバター設定と表情取り込み

- リグの対応表はVRM humanoid mapから取得する。
- `EyeRotationDriver.MaxSwing=4` を使う。
- ビセームと瞬きはVRM Expressionから構築する。

感情表情は `VrmExpressionAdapter` から共通の[表情システム](expression-system.md)へ渡す。
左右各0〜7の64通りの対応表に標準プリセットを固定番号で割り当て、ハンドジェスチャー・キーボード・
メニュー・外部APIから選択できる。番号と両手の優先順位は
[VRMの標準割り当て](expression-system.md#vrmの標準割り当て)を参照。
`ExpressionModel.GestureTable` はAnimatorを介さない固定Clip IDの表で、
`GesturePairCompiler` は解決済みClipだけを採用し、表の幅をシーン生成とメニュー分類へ渡す。
VRM 0.xの別名はパーサーでVRM 1.0名へ正規化する。`VrmExpression.IsCustom` を保持し、
標準プリセットと同名のカスタム表情が欠落プリセットの代わりに割り当たらないようにする。
VRM0のカスタム表情 `Surprised` は例外として、名前の大文字小文字を区別せず番号6と驚きアイコンに対応させる。

標準番号の根拠はXWear Packager 0.3.4の
`IO/Runtime/Extension/ForVrc/VrcAssetTemplate/Resources/VrcAssetTemplate.asset` と
`VRC_FaceLayer_Template.asset`。前者は各感情をPoint・Peace・RockNRoll・Gun・Thumbs upへ対応させ、
後者は左右とも3〜7に遷移する。0〜2はEmptyクリップのIdleで、Left Handの後にRight Handを
weight=1のOverrideで適用する。生成済みFXの感情クリップは他の顔シェイプのリセット値も持つ。
ResoPonでは同じ優先順位を固定Clip参照として表し、空の右手は左手へフォールバックする。
欠落プリセットには空モーション相当の挙動を採用し、Animatorの履歴依存の状態保持は持ち込まない。

## VRM FirstPerson

- VRM0はmesh index、VRM1はnode indexでannotationを保持する。VRM1はnodeからmeshへ解決する。
- annotationがないrendererは `Both` ではなくUniVRM準拠の `Auto` として補完する。

各flagの描画制御とheadless mesh生成は[共通のFirstPerson処理](avatar-conversion.md#firstpersonの描画制御)を参照する。

## SpringBone

コライダーはnodeと形状から共有し、VRM世代とorientationに応じてoffsetを変換する。
チェーン生成・テンプレート・Grabの設定は[揺れものの共通出力](avatar-conversion.md#揺れものの共通出力)を参照する。

## MToonからXiexeToon

- 基本マッピングは `Resonite.UnitySDK` のMToonコンバーターに合わせる。
- alpha mode、ZWrite、render queueはVRMの値を反映する。ただし、後述の半透明材質のCutout指定では上書きする。
- shade color、shading shift、shading toonyから256x256のShadowRampを生成する。
- MToonリムはXiexeToonの式に合わせて半値点と傾きを近似する。
- XiexeToonのoutline幅はオブジェクト空間で `値 * 0.01` のため、メートル値を100倍して設定する。
- OutlineMaskはGLB内画像を直接抽出し、VRM1の共有パラメータテクスチャでは緑チャネルを使う。

### 半透明MToon材質のAlpha / Cutout選択

VRM 0.x / 1.0のうち、解析後の `IsMToon=true` かつ `AlphaMode="blend"` の材質が対象。
元からOpaque / Cutoutの材質と、MToon以外の材質は対象外。
UnityPackage / Prefabにも同じ選択機能があり、対象の判定は[VRChat変換](vrchat-conversion.md#半透明材質のalpha--cutout選択)を参照する。

GUI設定「半透明マテリアルの変換先を手動で選ぶ」は既定OFF。
ONの場合、VRMごとに対象材質があれば変換前に選択画面を表示する。
名前が空でない対象材質を名前順に並べ、各材質の初期選択はAlphaとする。
設定のON / OFFは保存するが、材質ごとの選択結果は保存せず、変換ごとに指定する。
対象がなければ選択画面を省略し、キャンセルするとその変換ジョブの準備を中止する。

| 選択 | 生成するXiexeToonの設定 | 表示上の意味 |
|---|---|---|
| Alpha（既定） | `BlendMode.Alpha`。ZWriteは元の `TransparentWithZWrite` に従う。RenderQueueは元の指定または通常の算出値 | 半透明の濃淡を維持。UIでは特定のライティングで光って見える場合があると案内 |
| Cutout | `BlendMode.Cutout`、`AlphaClip=AlphaCutoff`、`ZWrite.On`、`RenderQueue=2450` | 透明 / 不透明の二値になり、半透明の濃淡や柔らかい境界は維持しない |

Cutoutのしきい値には、VRMの `alphaCutoff`（VRM 0.xでは `_Cutoff` の値があれば上書き）を使い、
未指定時は0.5。選択画面にしきい値の調整欄はない。
この選択によって元の画像ファイルを書き換えたり、Emissionを無効化したりはしない。

CLIでは `--transparent-cutout <name>` を材質ごとに繰り返し指定できる。
従来の `--mtoon-transparent-cutout <name>` も互換エイリアスとして利用できる。
材質名は大文字・小文字を区別する完全一致で、未指定の対象材質はAlphaになる。
存在しない名前や対象外の材質名を指定しても、このオプションでは変更されない。
`--no-avatar` と併用してもマテリアル変換には適用する。

## 回帰確認

- VRM 0.xと1.0の両方を実変換する。
- Blender製VRM1ではCenteredRootの向きと左右を確認する。
- `--assimp-dump` でモーフの `movedVerts` が生データより大幅に減っていないことを確認する。
- `--inspect-verbose` でBipedRig、VRIK、FirstPerson renderer、DynamicBone、blendshapeを確認する。
