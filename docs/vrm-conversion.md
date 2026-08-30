# VRM変換の実装資料

この資料は `Vrm/`、`AvatarSetup.cs`、`SpringBoneSetup.cs`、`MaterialTuner.cs` を変更するときに読む。

## 変換パイプライン

1. `VrmParser` がVRM 0.x / 1.0を共通の `VrmModel` へ読み込む。
2. `GlbPreprocessor` がResonite `ModelImporter` 向けの互換修正をGLBへ適用する。
3. 一時的に `.glb` としてResoniteへインポートする。
4. `AvatarSetup` がBipedRig、VRIK、視点、表情、FirstPersonを構築する。
5. `MaterialTuner` と `SpringBoneSetup` がマテリアルと揺れものを設定する。

## GLB前処理

### ブレンドシェイプ名と属性

- 旧UniVRMで `targetNames` がprimitive側にしかない場合はmesh側へ昇格・補完する。
  補完しないと複数の空名シェイプが `MeshX.AddBlendShape` で衝突する。
- 同一mesh内のprimitive間でモーフNORMAL/TANGENTの有無が一致しない場合は、先頭primitiveを基準に
  不一致属性を除去する。Resonite側のマージで配列範囲外になるのを防ぐ。
- インポートコルーチンが例外終了しても完了通知が来ない場合があるため、`--import-timeout` を維持する。

### JoinIdenticalVertices対策

Resoniteのモデルインポータは `JoinIdenticalVertices` を適用するが、モーフデルタを同一点判定に含めない。
静止時に重なる頂点が結合されると、ブレンドシェイプの一部が動かなくなる。

`GlbPreprocessor.AddMorphVertexGuardChannel` はモーフを持つprimitiveへ頂点ごとに一意な追加TEXCOORDを
付与し、結合を防ぐ。値は頂点index、型はVEC2 floatで、BINチャンクと `buffer[0].byteLength` を更新する。
診断には `--assimp-dump` の `movedVerts` を使う。

## 座標系

- Resonite `ModelImporter.PreprocessScene` はシーン全体へXミラーとwinding反転を適用する。
- VRM0はUniVRMのReverseZ、一般的なVRM1はReverseXを前提とする。
- VRM0は条件を満たす階層へY180をベイクし、CenteredRootの不要なY180を防ぐ。
- Blender VRMアドオン製VRM1は通常のglTF配置になる場合がある。
  `rightUpperLeg.x > leftUpperLeg.x` を手掛かりに判定し、
  `MirrorXForProperHandedVrm1` でUniVRM規約へ合わせる。
- X反射ではtranslation、quaternion、IBM、POSITION、NORMAL、TANGENT、三角windingを一貫して変換する。
  VRM0のY180回転と反射を混同しない。
- コライダーオフセットは `VrmModel.OrientationMirroredX` とVRM世代を考慮して変換する。

## アバターセットアップ

- BipedRigはインポータの名前推測結果を信用せず、VRM humanoid mapを正とする。
  既存の分類を消してから割り当て、装飾ボーンの誤分類を残さない。
- BipedRigとVRIKはモデル子ではなくアバタールートへ作成する。
- 手の基準方向は両手とも `+Z=手首から中指`、`+Y=手の甲`。指と親指の位置から計算する。
- 視点は目の中点から眉間側へ補正する。`--view-forward` と `--view-up` で上書きできる。
- `AvatarRenderSettings` はルート直下へ作り、既定 `NearClip=0.075`、`FarClip=null` とする。
- `EyeRotationDriver.MaxSwing=4` を使う。
- ビセームと瞬きはResoniteの名前推測を避け、VRM Expressionだけから構築する。
- `AvatarExpressionDriver` は既定で作らず、`--face-tracking` 指定時だけ有効にする。
- `SimpleAvatarProtection` は既定でrootと各SkinnedMeshRendererへ付与し、
  `--no-protection` で無効にする。

## VRM FirstPerson

- VRM0はmesh index、VRM1はnode indexでannotationを保持する。VRM1はnodeからmeshへ解決する。
- annotationがないrendererは `Both` ではなくUniVRM準拠の `Auto` として補完する。
- `Both` は変更しない。
- `ThirdPersonOnly` / `FirstPersonOnly` はrendererのEnabledではなく `RenderMaterialOverride` で切り替える。
- Modular Avatar互換の `DynamicVariableSpace("modular_avatar")`、`OnlyDirectBinding=true`、
  `modular_avatar/AvatarWornLocal` を維持する。
- 装着者判定は埋め込み `AvatarRootIdentification.resonitepackage` をインポートする。
  手動のLocalUpdate書き込みへ置き換えない。
- Invisible Materialは `Assets/Invisible Material` の透明な `PBS_RimSpecular` とする。
- `Auto` は頭ボーン配下の影響頂点を除いたheadless meshを生成する。
  元rendererを三人称用、headless rendererを一人称用にし、生成meshのblendshapeは除去する。
- `ApplyFirstPersonAutoAsync` は `MaterialTuner.Apply` の後に実行し、最終マテリアルを参照させる。

## SpringBone

VRM SpringBoneは `DynamicBoneChain` へ変換し、テンプレート設定を `modular_avatar` の動的変数として公開する。
コライダーはnodeと形状から共有し、VRM世代とorientationに応じてoffsetを変換する。

## MToonからXiexeToon

- 基本マッピングは `Resonite.UnitySDK` のMToonコンバーターに合わせる。
- alpha mode、ZWrite、render queueはVRMの値を保持し、固定queueへ戻さない。
- shade color、shading shift、shading toonyから256x256のShadowRampを生成する。
- MToonリムはXiexeToonの式に合わせて半値点と傾きを近似する。
- XiexeToonのoutline幅はオブジェクト空間で `値 * 0.01` のため、メートル値を100倍して設定する。
- OutlineMaskはGLB内画像を直接抽出し、VRM1の共有パラメータテクスチャでは緑チャネルを使う。

## 回帰確認

- VRM 0.xと1.0の両方を実変換する。
- Blender製VRM1ではCenteredRootの向きと左右を確認する。
- `--assimp-dump` でモーフの `movedVerts` が生データより大幅に減っていないことを確認する。
- `--inspect-verbose` でBipedRig、VRIK、FirstPerson renderer、DynamicBone、blendshapeを確認する。
