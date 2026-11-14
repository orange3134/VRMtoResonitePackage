# アバター変換の共通実装

VRM・VRChatの両経路で使う `AvatarSetup.cs`、`SpringBoneSetup.cs`、`MeshLoadingSetup.cs`、
`LoadingStandInSetup.cs` と、`Converter.cs` からの呼び出し順を扱う。
入力形式ごとの解析・前処理・マテリアル変換は
[VRM変換](vrm-conversion.md)と[VRChat変換](vrchat-conversion.md)を参照する。

## 共通モデルと処理の責務

VRMは `VrmParser`、VRChatはパーサーと `VrchatModelAdapter` が共通の `VrmModel` を生成する。
`VrmModel` はVRM専用の実行経路を意味せず、VRChatでは `Source=ModelSource.VrchatFbx` として
入力形式を区別する。骨・表情・揺れものの取り込み規則と参照解決は各入力経路が担当し、
解決した情報からResoniteのコンポーネントを構築する処理を共有する。

- `AvatarSetup` がリグ、視点、口パク・瞬き・視線の追跡、FirstPersonを構築する。
- マテリアルは各経路で適用し、その後に `AvatarSetup.ApplyFirstPersonAutoAsync` を実行して
  一人称用rendererにも最終マテリアルを参照させる。
- `SpringBoneSetup` が共通モデルの揺れものを構築する。
- `AvatarSetup` は感情表情のメニューや駆動を生成せず、`Converter` が共通の表情システムを
  一度だけ構築する。入力形式ごとの表情取り込みと初期値の反映を終えてから接続する。
- 最終的なrenderer構成に対して `MeshLoadingSetup.Apply` を実行する。

エンジン起動・アセット待機・パッケージ出力は[ヘッドレスエンジン](headless-engine.md)、
工程別の計測は[変換性能の診断](conversion-performance.md)を参照する。

## 入力形式別のオプション対応

`Program.CliOptions`、`GuiApp.SettingsWindow` と `Converter` の両変換経路を基準にする。
○は対応、—は対象外。対応は設定を適用する処理があることを示し、任意のモデルでの完全再現を保証しない。
UnityPackageとUnityプロジェクト内のPrefabは同じVRChat変換経路を使う。
Prefabには所属プロジェクトと関連アセットが必要で、単独ファイルでは変換できない。

| 機能 | VRM | UnityPackage / Prefab | 設定方法・適用条件 |
|---|---|---|---|
| フェイストラッキング用ドライバー生成 | ○ | ○ | GUI / `--face-tracking`。既定OFF。Resoniteが認識できるシェイプキーが必要 |
| アバター保護を付けない | ○ | ○ | GUI / `--no-protection`。既定は保護あり。GUIでは起動ごとに再選択 |
| 表情メニューを生成しない | ○ | ○ | GUI / `--no-expression-menu`。既定は生成。ハンドサイン・キーボード・APIの操作は維持 |
| アバターの元のサイズを維持 | ○ | ○ | GUI / `--default-user-scale`。既定OFF。DefaultUserScaleを付与 |
| 視点の前方・上方オフセット | ○ | ○ | GUI / `--view-forward <m>`、`--view-up <m>` |
| NearClipの指定 | ○ | ○ | GUI / `--near-clip <m>`。既定0.075m、0でAvatarRenderSettingsの生成を省略 |
| 半透明材質のAlpha / Cutout選択 | ○ | ○ | GUIの「半透明マテリアルの変換先を手動で選ぶ」（既定OFF）。CLIは `--transparent-cutout` に材質名・パス・GUIDを指定。VRMはMToon材質名、UnityはAlphaに変換する材質が対象 |
| 作業用一時ファイルを残す | ○ | ○ | GUI / `--keep-working-files`。既定OFF |
| アバターセットアップを省略してモデルを変換 | ○ | ○ | `--no-avatar`。現在の設定画面には表示しない |
| モデルインポートのタイムアウト | ○ | ○ | `--import-timeout <sec>`。既定300秒。現在の設定画面には表示しない |
| 変換対象アバターの選択 | — | ○ | 複数候補がある場合のGUI選択 / `--avatar <name>` |
| 出力先フォルダ | ○ | ○ | GUI / `-o`、`--output <dir>`。既定は入力と同じフォルダ |
| Resoniteのインストール先 | ○ | ○ | GUI / `--resonite-path <dir>`。未指定時は自動検出 |

`--no-avatar` ではフェイストラッキング、保護、表情システム、原寸維持、視点・NearClip、
FirstPersonのセットアップを行わない。マテリアルと揺れものの変換は両形式で引き続き行う。
フェイストラッキングは両経路とも `AvatarCreator.TrySetupFaceTracking` を呼び、
VRChat側のAnimatorや独自フェイストラッキング設定をそのまま移植する機能ではない。

視点オフセットは、VRChatのDescriptor視点がある場合は元の視点へ加算する。
VRMや元の視点がない場合は、自動計算に使うオフセット値を指定値で置き換える。
CLIヘルプの前方オフセット「既定0」は元の視点に対する加算値であり、
自動計算時の前方オフセットは目の間隔などから求める。

診断用CLIは変換オプションと分けて扱う。

| 診断 | 対象 |
|---|---|
| `--assimp-dump` | VRMのメッシュ・モーフ構造（GLB / FBXも直接入力可能）。UnityPackage / Prefabの直接入力は対象外 |
| `--vrchat-dump` | UnityPackage / Prefabの解析。`--avatar` で対象を指定可能 |
| `--inspect` / `--inspect-verbose` | 両形式から生成した `.resonitepackage` の構造検査。変換元ファイルには使用しない |
| `-h` / `--help` | 入力形式に依存しないCLIヘルプ |

GUIの表示言語・起動時の更新確認も入力形式に依存しない。
表情操作や自動変換の詳細は[表情システム](expression-system.md)、
[VRM変換](vrm-conversion.md)、[VRChat変換](vrchat-conversion.md)を参照する。

## アバター保護の設定

GUI の `GuiSettings.NoProtection` は起動中だけ保持し、`JsonIgnore` で設定 JSON の保存・復元から除外する。
以前の設定ファイルに `NoProtection: true` が残っていても読み込まないため、EXE 起動時は毎回保護が有効になる。
設定画面の `Clone` / `CopyFrom` と `ToCliOptions` では値を引き継ぎ、同じ起動中の変換には選択を反映する。
CLI では従来どおり `--no-protection` を指定した実行で保護を外す。

## ModelImporterの共通制約

- `ModelImporter.PreprocessScene` はシーン全体へXミラーとwinding反転を適用する。
  入力側の軸・単位・姿勢の補正は各形式の座標系に従い、この変換との合成を考慮する。
- インポートコルーチンが例外終了しても完了通知が来ない場合があるため、`--import-timeout` を維持する。
- Assimpの `JoinIdenticalVertices` は通常時の頂点属性を比較し、モーフデルタを同一点判定に含めない。
  通常時に重なる頂点でも表情で別々に動く場合、結合されると一部が動かなくなったり、
  別の頂点の変形を参照してポリゴンが引き伸ばされたりする。
  入力側で識別用の頂点属性を付与して誤結合を防ぐが、GLBとFBXでは実装と対象条件が異なる。
  [GLBの対策](vrm-conversion.md#joinidenticalvertices対策)と
  [FBXの対策](vrchat-conversion.md#同位置の頂点で異なる変形を保持する)を参照する。

## アバターセットアップ

- BipedRigは `VrmModel.HumanBones` を正とし、インポータが名前推測で付けた分類を消してから
  割り当てる。装飾ボーンの誤分類を残さない。対応表を得る方法は各入力形式の仕様に従う。
- BipedRigとVRIKはモデル子ではなくアバタールートへ作成する。
- 手の基準方向は両手とも `+Z=手首から中指`、`+Y=手の甲`。
  指方向を骨格から求め、手の甲方向はモデルの上方向から指方向の成分を除いて直交させる。
- 明示的な視点がない場合は、目の中点から眉間側へ補正する。
  `--view-forward` と `--view-up` で推定時のオフセットを上書きできる。
  VRChatのDescriptor視点の優先順位とオプションの加算規則は[視点](vrchat-conversion.md#視点)を参照する。
- `AvatarRenderSettings` はルート直下へ作り、既定 `NearClip=0.075`、`FarClip=null` とする。
- ビセームと瞬きはResoniteの名前推測による自動補完を避け、入力から解決した設定を使う。
  視線の `EyeRotationDriver.MaxSwing` は入力形式ごとの値を使う。
- `AvatarExpressionDriver` は既定で作らず、`--face-tracking` 指定時だけ有効にする。
- `SimpleAvatarProtection` は既定でrootと各SkinnedMeshRendererへ付与し、
  `--no-protection` で無効にする。

## 表情システムへの接続

瞬きの接続は表情システムの構築前に確定し、OpenCloseTargetをOutputs/Baseへ付け替える。
表情構築後の最終ウェイトへEyeLinearDriverを追加すると既存Driveと競合する。
合成・接続は[表情の出力変数](expression-variables.md#出力)を参照する。

メニュー生成は `NoExpressionMenu` オプションで制御する。CLI・GUIの設定と、
メニューを省略したときに生成される操作系は
[表情システムのコンテキストメニュー](expression-system.md#コンテキストメニュー)を参照する。
共通の操作・API・生成階層の詳細は同資料に集約する。

## 装着者判定とアバター識別

- Modular Avatar互換の `DynamicVariableSpace("modular_avatar")`、`OnlyDirectBinding=true`、
  `modular_avatar/AvatarWornLocal` を維持する。
- `AvatarSetup.EnsureAvatarRootIdentification` が標準コンポーネントと ProtoFlux を生成する。
  `AvatarUserReferenceAssigner` の装着者参照が非 null かつ `GetActiveUserSelf` と一致したときだけ
  `modular_avatar/AvatarWorn` を true にする。`AvatarWornLocal` はさらに `IsLocalUser` を要求する。
  `AvatarWorn` は `ValueField<bool>` → `ValueCopy<bool>` → 動的変数、`AvatarWornLocal` は
  `ValueFieldDrive<bool>` からの直接 Drive を維持する。単なる UserRoot 配下への移動では装着扱いにしない。
  `modular_avatar/AvatarRoot` はアバター自身への駆動参照として公開する。

## FirstPersonの描画制御

VRMでは共通モデルへ取り込んだannotation、VRChatでは入力種別に応じたAuto処理で対象を決める。
VRMのannotation解決は[VRM FirstPerson](vrm-conversion.md#vrm-firstperson)、VRChatでの対象とHeadの選択は
[VRChat FirstPerson](vrchat-conversion.md#vrchat-firstperson)を参照する。

- `Both` は変更しない。
- `ThirdPersonOnly` / `FirstPersonOnly` はrendererのEnabledではなく `RenderMaterialOverride` で切り替える。
- Invisible Materialは `Assets/Invisible Material` の透明な `PBS_RimSpecular` とする。
- `Auto` は頭ボーン配下の影響頂点を除いたheadless meshを生成する。
  元rendererを三人称用、headless rendererを一人称用にし、生成meshのblendshapeは除去する。

## メッシュ読み込み中の表示

`MeshLoadingSetup` が対象 renderer の読み込みを監視し、
`LoadingStandInSetup.BuildAsync` が標準コンポーネントと ProtoFlux から代替表示を生成する。
入力用の埋め込み `.resonitepackage`、`LoadObject`、package import callback は不要。
変換結果の `.resonitepackage` 出力と検査は従来どおり。

- `modular_avatar/MeshNotLoaded=false` の公開中は対象 renderer を無効にし、代替表示を有効にする。
  全 mesh の読み込み後は変数名を空にし、各 driver の既定値により表示を切り替える。
  代替表示自身とシステムの音声範囲表示は監視対象に含めない。
- `VirtualParent` は `modular_avatar/AvatarPoseNode.Head` を参照し、位置と回転だけを Drive する。
  オフセットは頭から上に約 0.05236、表示は Y180 度の両面・透過 `UnlitMaterial` の Quad。
  大きさは公開済み `HumanBonePose.head` の原点の Y 座標 × 0.25 を各軸へ設定する。
- `GetActiveUserSelf` → `UserUserID` → `CloudUserInfo.UserId` からプロフィール画像を取得する。
  `IconURL` が null なら `Resources/LoadingFallback.png` を使う。通常の画像リソースとしてLocalDBへインポートする。
  LocalDB は `local:` または `resdb:` URI を返すため、生成時に `resdb:` 固定とは仮定しない。
  画像 URI は texture と fallback 定数の両方に設定し、出力パッケージへのアセット収集対象にする。
- `AvatarWorn` かつ `IsLocalUser` の場合だけ `RenderTransformOverride(UserView).ScaleOverride=0`。
  それ以外は null に戻し、本人以外やミラーでは通常の表示を維持する。
- `tests/ExpressionSmoke/GeneratedAvatarChecks.cs` で装着・解除・親階層の変更、頭追従、
  mesh 読み込み前後、パッケージ保存・inspect・再インポート後の動作を検証する。

## 手のコライダーとレーザー原点

`VRIKAvatar.Setup` はインポートした `Rig` の末端ボーンから手の `BoxCollider` を生成する。
この範囲はヒューマノイドの中指先端だけから求めたツール位置より長くなる場合がある。
シピルカでは手の box が約22.6cmに達し、従来の通常レーザー原点が内部に入っていた。

`AvatarSetup.SetupHandColliderRaycasts` は `avatar.Setup` の後に、手直下の box の
`IgnoreRaycasts` を `DynamicValueVariableDriver<bool>` で
`modular_avatar/AvatarWornLocal` に連動させる。装着者本人のクライアントだけでtrueとなり、
非装着時・他ユーザー・変数未解決時はfalse。ツール位置、コライダー形状、物理衝突は変更しない。
揺れもの用 `DynamicBoneSphereCollider` は対象外。

`tests/ExpressionSmoke/GeneratedAvatarChecks.cs` で装着・解除・親階層の変更と保存・再読込後の
切り替えを確認する。実アバターでも再変換・inspect後に両手のドライバーと動作を検証する。

## 揺れものの共通出力

VRM SpringBoneとVRChat PhysBoneは共通モデルを介して `SpringBoneSetup` で
`DynamicBoneChain` へ変換し、テンプレート設定を `modular_avatar` の動的変数として公開する。
入力の参照解決・対応する形状・オフセット変換は各形式の資料を参照する。

`generic` グループは袖などの揺れものが通常のGrabを妨げないよう `IsGrabbable=false` とする。
他グループはtrueを維持する。テンプレートと各チェーンの `IsGrabbable` は他の設定と同様に
`modular_avatar/dynamic_bone_template.<group>.IsGrabbable` で連動する。
シピルカの袖 `HandTops_L/R` は `generic` に分類されるため、この設定で通常のGrabとの干渉を防ぐ。
