# VRChatアバターの変換

この資料は `Unity/`、`Vrchat/`、および `Converter.ConvertVrchat` を変更するときに読む。

リグ・追跡設定、装着者判定、FirstPersonの描画制御、読み込み中の表示、揺れものの共通出力は
[アバター変換の共通実装](avatar-conversion.md)を参照する。
処理時間を調べる場合は[変換性能の診断](conversion-performance.md)を参照する。

| 調べたい処理 | 実装資料 |
|---|---|
| 複数FBXの配置、スロットや骨の増減、PhysBone | [Prefab階層と骨格](vrchat-prefab-hierarchy.md) |
| 表情での頂点・法線の変形、口パク、瞬き、初期ウェイト | [ブレンドシェイプと顔の追跡](vrchat-blendshapes.md) |
| FX・FaceEmoから表情セットやハンドサインを取り込む処理 | [VRChat表情の検出と割り当て](vrchat-expression-import.md) |
| 透明度、テクスチャの合成、lilToon、輪郭線 | [マテリアル変換](vrchat-materials.md) |

## 全体設計

VRChatパーサはUnityアセットを解析し、共通の `VrmModel` とVRChat固有情報を生成する。
リグ、視点、表情、揺れものは両経路共通の `AvatarSetup` / `SpringBoneSetup` と表情システムを使い、
prefab合成、初期状態、マテリアルはVRChat専用処理で補う。

### Prefab解決と変換の責務

Unity Editorを起動せず、プロジェクトの保存済みYAML・meta・FBXから次の順序で変換する。
UnityのLibraryやVRChat SDKの実行環境は不要。

1. `UnityPrefabInstances` が選択範囲を切り出し、各配置に固有の識別子を割り当てる。
   `UnityAsset.SourceGuid` は元アセット、`OccurrencePath` は配置経路を表す。
   既存の `Guid` フィールドは変換中の配置識別子としても使うため、元アセットのGUIDとは区別する。
   `.unity`内でDescriptorの所有ルートがstrippedの場合は、選択したPrefabInstanceを起点に
   所属・親参照を辿って範囲を決める。兄弟アバターを除外し、配下の追加オブジェクト・
   コンポーネント・Prefabは保持する。省略されたstripped親も配置ごとのfileIDで解決する。
   選択ルートはシーン側の親から切り離し、元のPrefabやシーンは変更しない。
2. `UnityObjectResolver` が通常のfileID、明示されたstripped参照、省略されたstripped参照を
   共通の `UnityObjectId`（配置識別子とfileID）へ解決する。名前はオブジェクトの識別に使わない。
3. `UnityPrefabGraph` がソース文書を複製し、内側のPrefabから外側のVariantへ上書きを合成する。
   `UnityPropertyOverrides` はコンポーネント型に依存せず、フィールド、配列サイズ・要素、
   明示的な0・空文字・null参照を適用する。上書きで追加されたローカル参照は宣言元の配置に属する。
   コンポーネント削除とGameObjectの子孫削除もここで処理し、外側に記録された追加コンポーネントへ伝播する。
   元のキャッシュ・ファイルは変更しない。
4. Descriptor、Renderer、PhysBone、Modular Avatarの抽出は同じ合成済みシーンを読む。
   新しいコンポーネントを実装するときに、Variantの走査や削除判定を再実装しない。
   YAML文書を持たないFBX内部への上書きは、正規化した対象参照をモデル変換側へ渡す。
5. 出力側は `InstantiateMeshCopies` でオブジェクトを生成・登録し、物理用の階層も生成してから
   `MeshCopyBuild.Bind` でskinの参照を接続する。生成中のコールバックで参照先を追加しない。

削除されたRendererの元メッシュは、生存するGameObjectと子の配置を復元するためのテンプレートとして
必要になる場合がある。`RendererTemplateScenes` はその用途だけに削除前の文書を提供する。
Renderer自身やマテリアルを復活させてはいけない。

AnimatorからのViseme・Blink推定と他レイヤーとの競合判定は `VrchatAnimatorGraph` の到達可能性を共有する。
未接続のstateに競合するclipがあるだけでは表情を除外しない。到達可能な競合は保守的に除外する。
Prefab・FBXの初期ウェイトを収集した後に表情を推定する。

ハンドジェスチャー・Resoniteのコンテキストメニュー・外部イベントによる表情切り替えの使い方と対応範囲は
[表情システム](expression-system.md)を参照する。Viseme／Blink推定とは別に解析し、共通の出力へ接続する。

表情はFaceEmoの通常形式／CAC形式を `VrchatFaceEmoExpressionImporter` で読み、最初のパターンだけを
Clipsと左右64通りへ変換する。通常形式は後ろのFXレイヤーを優先して条件付き分岐を選び、
CACは番号順の14表情セットの最初を選ぶ。詳細は [表情の検出と割り当て](vrchat-expression-import.md) を参照。
ハンド割り当てはClips確定後、`FaceEmoGestureConditions` がEntry・Any State・通常遷移・入れ子Exitを
辿って検出済みClipを選ぶ。Driverの有無や位置を固定せず、途中のSet・既知値のCopy/Addにも対応する。
未知の条件を既定値で埋めず、任意の実行履歴や時間経過は再現しない。
材質・Transform・イベント等の混在は顔カーブ抽出を妨げず、顔以外の効果は実行しない。
顔のパスと実Rendererの一致、初期ウェイトとの差分を確認してからセットを確定する。

FBXの初期ブレンドシェイプ値は、同名Rendererをまとめず、配置識別子とモデル内の完全なパスで保持する。
`UnityFbxBlendShapeDefaults` はFBXの `Connections` をたどり、channel → blendshape → geometry → modelの
所属を解決する。チャンネル名やAssimpの走査順では割り当てない。同じgeometryを共有するmodelにも値を保持する。
コピーしたRendererには元の `SourcePath` から値を継承し、Prefabの明示的な0を優先する。
出力への適用はauthored object identity、またはimport直後に捕捉したmodel/pathで照合するため、
移動・改名後も対象を区別できる。指定パスが見つからない場合、別の同名Rendererへ適用しない。
ブレンドシェイプ順序の補修とマテリアル適用にも同じパス情報を渡す。

これは保存済みデータと対応機能の合成器であり、Unityの実行結果全体を再現するものではない。
任意スクリプト、NDMFのビルド処理、Animator全機能は実行しない。FBX内部のfileID/path対応には
引き続きモデル解決器の対応範囲がある。これらを追加するときも共通の参照・合成処理を利用する。

GUIとCLIはいずれも `.unitypackage` を入力として受理する。複数アバターを含む場合はGUIで選択するか、
CLIの `--avatar` を使う。出力名は入力package名ではなく選択したprefab名を使い、子プロセスは
`RESOPON_OUTPUT:` で実出力パスを通知する。

## Unity YAMLとアバター候補

- ブロックシーケンスは親キーと同じindentになるUnity YAML形式を扱う。
- 長いplain scalarの継続行を結合する。`m_ShaderKeywords` の途中で解析を切らない。
- double-quoted scalarの `\uXXXX` をデコードし、日本語GameObject名を正しく照合する。
- `VRCAvatarDescriptor` はscript GUIDとfileIDを確認し、SDK差異には固有フィールド署名も使う。
  DLL内Descriptorは542108242、単独MonoScriptは11500000を認める。
  同じDLLのGUIDを持つ音声コンポーネント等をDescriptorと誤認しない。
- prefab、prefab variant、composition、`.unity` scene内のprefab instanceを候補に含める。
- 候補一覧では重いFBX配置・material解決を遅延し、選択された候補だけを詳細解析する。
- `m_RemovedGameObjects` があっても継承した Descriptor の候補を除外しない。
  選択後の合成済みビューで再探索し、Descriptor の所有者自体が削除された場合は変換を拒否する。
- 直下の PrefabInstance が1つでも、その参照先が複数モデルを合成している場合がある。
  composition の候補探索はこの外側の Variant も扱う。Descriptor の元FBX数と全体のFBX数が
  異なることだけで、衣装を追加した Variant を変換不可にしない。

## Unityで生成したネイティブアセット

NDMF Manual Bake等で保存した`.asset`は、YAMLだけでなくUnity SerializedFileのバイナリ形式でも
読み込む。`UnitySerializedAssets`がAssetsTools.NETとファイル内のtype treeを使い、既存の
`YamlDocument`/`YamlNode`へ変換する。Unity Editor、Libraryのインポート結果、外部class databaseは不要。
元のファイルは変更しない。型ツリーを含まないバイナリは対応外として停止する。

- 識別子はGUIDと符号付き64bitのfileID。ファイル先頭のMeshやMaterialを代用しない。
  バイナリPPtrの外部参照はexternal tableのGUIDへ、同じファイル内のClip等は元のGUIDとfileIDへ解決する。
  Animatorのstate・transition・BlendTreeは既存グラフが扱うローカルfileIDを保持する。
  YAMLの`.asset`内でも、同一ファイルのAnimationClip参照には所有アセットのGUIDを補う。
  fileIDだけのClip参照を残すとAnimator顔解析とFaceEmoがClipを取得できないため、
  直接のstate motionとBlendTree内のmotionの両方を正規化する。null・外部参照は保持する。
- 材質・Texture2Dは内部キー`GUID@fileID`で既存のキャッシュへ接続する。material variantの親や
  texture参照も同じ方式で区別する。YAMLサブアセットも同様に扱う。
- ControllerはDescriptorのfileIDで選び、同一ファイルの別Controllerを混ぜない。
  ClipキャッシュもfileIDを含める。対応済みの表情推定・FaceEmo解析へ渡すが、Animator全体を実行するものではない。
- `UnityNativeMesh`はUnity 2019以降の非圧縮Meshを直接MeshXへ変換する。
  複数vertex streamの16byte境界、頂点channelのformat/offset/dimension、16/32bit index、
  submeshのfirstByte/baseVertexを扱う。位置・法線・tangent・頂点色・UV0〜3、bindpose、
  最大4本のbone weight、疎なblendshape deltaと複数frameを保持する。
  1本のbone indexだけを保存するrigid skinでは、省略されたweightを1とする。
- 座標・三角形の順序・bindposeはUnityのローカル値を使い、FBXの単位補正を重ねない。
  `modular-avatar-resonite/Editor/SerializeAvatar/AvatarSerializer.cs`の`TranslateMesh`と、
  `Resonite~/ResoniteHook/Puppeteer/conversion/MeshTranslator.cs`のMeshX変換を参照した。
  frame weightは100で割り、blendshapeのnormal/tangent flagは同実装と同様に統一する。
- `VrchatGeneratedMeshes`で一時templateを生成し、既存のMeshCopy構築・Prefab階層復元・明示的なBindに接続する。
  bone番号は生成MeshのbindposeとRendererの`m_Bones`の順序を保持し、名前による再並べ替えはしない。
  顔Descriptorも元FBXの同名メッシュへ戻さず、生成RendererのPrefab identityを使う。
  root Animatorが参照するhumanoid FBXは引き続きリグ情報の取得に必要。
- Texture2DはRGB24/RGBA32/ARGB32/BGRA32/Alpha8/R8の内蔵画素を一時PNGへ変換する。
  上下反転、straight alpha、color space、wrap/filter設定を保持して既存の材質・ベイク処理へ渡す。
  圧縮／streaming texture、圧縮／streaming Mesh、triangle以外のtopologyは現在対応外。
  対応外形式・壊れたバッファ・欠落したMesh subassetは、元FBXへ置き換えずエラーにする。

回帰ケースはManual Bake済みMANUKAの衣装25Mesh。`PrefabInputSmoke/NativeAssetChecks`は
合成バイナリとYAMLで複数subasset、材質map、同一ファイルの参照、Texture2Dの上下・alpha、
複数stream、indexの基点、rigid/2本skin、複数blendshape frameを検証する。
`PrefabSceneSmoke/NativeMeshSceneChecks`は同名boneの接続、static/skinnedでのmesh共有とpackage再読込を確認する。
実アバターの保存メッシュ比較は同テストの`--native-package <prefab> <resonitepackage>`を使う。

## VRChat FirstPerson

`AvatarSetup.ApplyFirstPersonAutoAsync` は `Source=ModelSource.VrchatFbx` の場合、
annotationがなくてもアバタールート配下のMeshRendererを対象にAuto処理を実行する。
FirstPerson Autoはavatar rootのBipedRigが使うHeadを優先し、リグにHeadがない場合は
解決済みnode参照だけを使う。衣装の同名Headや子階層の別BipedRigをfallbackにしない。
headless meshの生成と描画制御は[共通のFirstPerson処理](avatar-conversion.md#firstpersonの描画制御)を参照する。

## 視点

- Descriptor の `ViewPosition` がある場合は、目ボーンからの推定より優先する。
  主FBXルートのDescriptorでは、元の人体ボーンと最終リグの姿勢が一様な拡縮・回転・移動で
  対応するときに、その変換を視点にも適用する。NagiyaRuriのセンタリング後の位置と
  前後方向を含む比較手順は[視点比較](avatar-viewpoint-tests.md)を参照。
  目ボーンは回転用の支点であり、見えている眼球の中心とは限らない。
- `ViewPosition` は Descriptor 原点からの、Unityワールド軸に沿ったメートル単位のオフセット。
  SDK 3.10.6 の `VRC_AvatarDescriptor.OnDrawGizmosSelected` は
  `transform.position + ViewPosition` で表示し、`TransformPoint` を使わない。
  Descriptorやその親の回転・拡縮、FBXの単位補正をオフセットに掛けない。
  原点の配置は保持するが、`AlignVrchatImportUp` の回転もオフセットには掛けない。
  この補正はFBXの軸をUnityの軸へ揃えるためのもので、すでにUnity軸の視点へ重ねると二重補正になる。
- インポートの向きを揃えた後、Descriptorを含むラッパーを畳む前にワールド座標を保存する。
  `AvatarSetup` はこれをHeadProxyの配置に使い、標準のVRIKAvatarセットアップでTargetを生成する。
- 明示した `--view-forward` / `--view-up` は元の視点へのメートル単位の加算になる。
  元の視点がある場合は目の間隔による自動オフセットを加えない。
  設定またはDescriptorの参照先がない場合、非有限値の場合は
  [共通の視点推定](avatar-conversion.md#アバターセットアップ)へ戻る。
- Z-upのアバターでは視点に直立補正を二重適用すると、Head Proxyの位置とアバターの身長が崩れる。
  `ViewpointChecks` はZ-up・単位補正・親の回転と非一様scaleを含む座標の回帰を確認する。
  `PrefabSceneSmoke --upright-package <package> <viewHeight>` は、Descriptor原点が地面にある
  直立アバターの保存パッケージを実エンジンで再読込し、元Descriptorの視点高さ、骨格の上方向、
  Head Proxyと頭の距離を検証する。変換・inspectの成功だけでは負の身長を検出できない。
- NoraMiareeのようにPrefabルートが拡縮されている場合も、Descriptorの視点オフセットはメートル値のまま扱う。
  unpacked prefabでは主FBXと衣装FBXの再利用骨格を、コピーしたrendererと同じauthored root配下へ配置する。
  骨格をimport root直下に置くと、元のルートのscaleがskinへ反映されず、目と視点の高さがずれる。
  rendererコピーやphysicsの祖先階層がDescriptorを再現していない場合、新たな骨格配置で
  作ったDescriptorを表情の厳密なpath接頭辞にしない。FBXにはUnityにない祖先が残るため、
  明示的なanimation rootがなければ従来の一意なpath末尾照合を維持する。
  standalone Mesh、別FBXのアクセサリー併用、import済み骨格配下の顔コピーもこの対象。
  既存のコピー・physicsがDescriptorを再現している場合は従来の厳密なpath照合を保つ。
  消された名前付きshapeの復元も同じ一意なpath末尾照合を使い、あいまいな候補は復元しない。
  通常のprefab Transformはscale=0.01でも作者の設定として保持する。FBXの単位情報だけでは
  別のprefab Transformが生成された単位補正かは判別できない。既存の生成wrapper用の
  `NormalizeImportedModelRootScale` をこの階層へ広げない。scale=1.1やネストした祖先も保持する。
  目から推定すると視点が下・後方へずれるため、モデル名による補正ではなくDescriptorを使う。
  保存パッケージを再読込し、HeadProxyがSDKの規約から独立に求めた視点と一致し、
  そのTargetが頭ボーンと一致することまで確認する。
  目ボーンや変換関数自身を正解値にせず、顔メッシュの目の領域とも比較する。
- RicorineのAlbifloraのように、親PrefabのFBXルートのscaleを継承し、子Variantで位置・回転だけを
  上書きする場合がある。子の変更一覧だけで配置を作るとscaleが1へ戻り、目と視点の高さがずれる。
  FBX Transformには合成先のYAML文書がないため、
  `PrefabGraph.Modifications` の正規化済み occurrence/参照先で各軸を基底から順に解決する。
  明示した0や1も上書きとして保持し、同じFBXの別インスタンスの変更を混ぜない。
  視点オフセットは従来どおり非拡縮のメートル値を使い、モデル側の縮尺を復元する。

## 代表的な回帰ケース

詳細なアセット自体はリポジトリへコミットしない。次の名前は不具合の再現条件を探す索引として使う。

- Kipfel: prefab variant、outline、FBX単位
- Milltina / Eku: blendshape index修復、prefab継承
- Fyuett: 大量shape、複数FBX、nested prefab、local transform保持
- Siro_HairRibbon: `//RootNode` transformとwrapper collapse
- Legnia: primary humanoid選択、複数Merge Armature
- Listy: fileIdsGeneration 1、outer variant override
- Yuzuki: scene instance、material search fallback
- Nagma PhysBone: YAML継続行
- Platinum: Unicode GameObject名
- Pilica / Kumagaya: `DeformPercent` 既定weight

## 未確定事項

- VRChat collider offsetの最終的な実機位置
- 特殊FBXでのblink index順序
- lilToonの影、outline、近似できない複合表現

未確定事項を変更するときは `--vrchat-dump`、実変換、`--inspect-verbose`、Resonite実機表示を組み合わせる。
