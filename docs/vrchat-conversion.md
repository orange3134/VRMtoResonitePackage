# VRChat unitypackage変換の実装資料

この資料は `Unity/`、`Vrchat/`、および `Converter.ConvertVrchat` を変更するときに読む。

リグ・追跡設定、装着者判定、FirstPersonの描画制御、読み込み中の表示、揺れものの共通出力は
[アバター変換の共通実装](avatar-conversion.md)を参照する。
処理時間を調べる場合は[変換性能の診断](conversion-performance.md)を参照する。

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

## prefab参照とstable fileID

Unity参照はGUIDとlocal fileIDの組で解決する。stripped objectは
`m_CorrespondingSourceObject` と `m_PrefabInstance` を辿って元アセットへ戻す。

- `fileIdsGeneration=1`: 型とobject名を使うxxHash64候補を生成する。
- `fileIdsGeneration=2`: `//RootNode` からのhierarchy pathとcomponent typeを使う。
- GameObjectのhash pathには `/GameObject` を付けず、componentだけ型名を付ける。
- serialized stripped documentがない参照は、prefab instance fileIDとのXORからsource fileID候補を復元する。
- Assimpの人工root配下に実mesh nodeが1つある場合、Unityのsynthetic `//RootNode/root` pathを実childへ割り当てる。
- A real top-level child named `root` takes precedence over the synthetic lowercase-root
  alias. Its GameObject and Transform IDs retain that branch's path for EditorOnly
  exclusion and physics references; sibling branches remain part of the model.

## FBXの選択と合成

### 配置の識別と不要なテンプレートの除去

- Mesh template selection and EditorOnly exclusions compare complete captured model
  paths after removing only the synthetic `RootNode` prefix (and leading slashes).
  A nested `Armature/Body` must never match a top-level `Body` by suffix; the latter
  remains an independent renderer and bone subtree even after reparenting.
- 詳細解析は `UnityPrefabInstances` の非所有ビューで行う。選択した descriptor の subtree に
  含まれる document と PrefabInstance だけを残し、すべての collector に同じ範囲を見せる。
  除外した祖先への root の `m_Father` はビュー内だけで 0 に戻し、ローカル骨の path を
  descriptor 基準にする。元 scene の document は変更しない。
- 同じ prefab/FBX の繰り返し配置は、配置経路から生成した内部 GUID で区別する。
  source file と `.meta` は共有して読み取り、instance の上書きと stripped 参照を同じ内部 GUID へ
  書き換える。元の package のアセット・キャッシュ・Unity プロジェクトは変更しない。
- 循環参照は祖先経路で検出する。兄弟の同一アセットを循環として除外しない。
- unpacked prefabのFBXはメッシュのテンプレートとして扱い、複製後に元rendererを除く。
  置換したrendererの元スロットとimport側の祖先を記録し、アバター設定後に子・componentが
  なくなったものだけを葉から削除する。Prefab由来のobjectやcomponentから参照されるSlot・
  Slotのfieldは保持する。Revan underwearのように骨格統合後に空になるimport階層も対象となる。
  import wrapperに残るRig・MeshRendererMaterialRelayは、登録先のbone・rendererがすべて
  消失していて外部からの参照もない場合に除去する。有効な登録先を持つcomponentは保持する。
  Merge Armatureが統合先へ移した未一致の補助骨は、移動先の祖先にあるRigへ登録を移す。
  primary wrapperとそのRigが既に畳まれている場合はアバターrootにRigを作成して登録を保持する。
  アバター外のboneを含む場合や旧Rigが外部参照されている場合は旧wrapperを保持する。
  名前や空であることだけを条件に、アバター全体のスロットを削除しない。
  外側で追加されたメッシュ用に同じFBXを再importした場合、その骨格が本体とは別に残ることがある。
  `UnityAsset.IsMeshTemplate` を `VrchatFbxAsset` へ引き継ぎ、追加モデルの用途を明示する。
  `RemoveUnusedMeshTemplateModels` はコピーの接続・骨格統合・アバター設定後に、用途がtemplateである
  追加モデルだけを調べる。全子孫が元importに属し、Prefab由来のobject・使用中のrenderer・外部からの
  bone/field参照・任意の動作componentがなく、Rig登録も内部で完結する場合だけモデル全体を削除する。
  空のSlotだけの削除では、Rigが自身の骨を登録している不要な骨格全体を除去できない。
  clonka Variantに追加したBody_Base_pants/nudeのコピー元骨格が具体例。本体のRootNodeと使用中の骨は保持する。
  Authored mesh templates have a separate model identity from PrefabInstances of the
  same FBX, so an EditorOnly instance cannot exclude visible local geometry. Create
  separate template identities only from authored MeshFilter/SkinnedMeshRenderer mesh
  references; an Animator Avatar reference alone must not duplicate an instantiated model.
  Composed candidates retain the selected root Animator's humanoid model preference,
  including when nested humanoid clothing is discovered before the unpacked body.
  Select the primary model from visible identities after collecting EditorOnly exclusions.
  Local copied skin bones infer their model within the owning prefab occurrence:
  prefer the primary model if referenced by a local mesh or the root Animator's
  `m_Avatar`, otherwise the sole local model. With neither source, retain the caller's
  skeleton context; explicit source ancestors still take precedence.
  Repeated unpacked skins keep separate skeletons.
  同じモデルがPrefabInstanceとしても配置されている場合は、そのrendererを残す。
  コピーの親がローカル skeleton の骨で、FBX の skin bone と完全な path が一致するときは、
  prefab Transform identity と imported bone target を保持して既存の骨 Slot を使う。
  骨の下に追加された attachment だけを生成し、別の skeleton chain を作らない。
  imported bone を親として再利用するときも model GUID と authored active state を保持し、
  static attachment の import scale を親モデル分で補正して、既存の骨 Slot へ active state を適用する。
  コピーの skin bone は全 renderer と親 Slot の生成後に prefab Transform identity を優先して解決する。
  unpack 後に改名されたローカル骨の path が FBX と一致せず、対応する authored Slot もない場合は、
  元の skin binding を保持して警告する。別名の骨を名前だけで推測しない。
- prefabの外側にあるattachmentも複製の親階層へ含める。上書きは表示名ではなく、
  explicit/omitted stripped参照を解決したobject identityへ適用する。EditorOnlyタグも同様。
  `m_RemovedGameObjects` also resolves per-occurrence object identities, including
  explicit/omitted stripped aliases. Apply permanent subtree exclusions after collecting
  keep entries and before authored mesh copies or physics placements, so deleted parents
  cannot be recreated by descendants. Repeated sibling instances retain their geometry
  and components; tag overrides cannot restore a deleted subtree.
- authored rendererは全コピーを登録してから親を解決する。descriptor rootにrendererがある場合も、
  子rendererや先に生成したattachmentを同じGameObject相当のSlotへ配置する。
  mesh供給元のFBX instanceがroot placeholder配下にある場合、置換Slotをplaceholderの親へ
  先に退避してから子を移す。複製時の親を保ったまま移すと祖先を自身の子にしてしまう。
- `.unity` のcompositionもprefabと同じcollectorで配置・上書き・ローカル参照を解決する。

### 物理参照と顔Rendererの捕捉

- PhysBoneのroot、ignore、collider参照はモデルのinstance identityとpathを保持し、
  prefab GUID/Transform fileIDに対応する生成済みSlotがあれば先に使用する。
  共通モデルの physics node も prefab GUID/Transform fileID を優先して intern し、
  同じ名前・FBX path の兄弟や FBX GUID のない authored object を混同しない。
  authored rendererのコピーはimport pathを持たないため、FBXテンプレートより優先する。
  import時に捕捉したSlotへ解決してから共通のSpringBoneSetupへ渡す。
  Merge Armature前にSlotを解決し、各mergeのskin用bone mappingでphysics参照も更新する。
  破棄後のsource identity検索や名前fallbackでは、移動先や別instanceを正しく区別できない。
  FBX rootを指す空pathも保持する。primary wrapperとimport alignmentを畳むときは、
  捕捉済みのGUID/pathを順に生存する親Slotへ移し、physics解決時の参照切れを防ぐ。
  additional wrapper を畳む場合も、生存する source root へ GUID/空path と import root 表を移す。
  When a captured `RootNode` survives beneath the primary wrapper, root references
  prefer that node over the wrapper's empty-path identity, including after wrapper
  and alignment collapse. Imports without `RootNode` retain the empty-path fallback.
- Descriptor roots created by FBX placement must resolve through the prefab Transform slot map,
  even without authored mesh copies, so descriptor-relative Merge Armature paths retain their scope.
- FBX GameObject `m_Name` overrides need explicit import-time application because model nodes
  have no YAML documents for `UnityPrefabGraph` to update. Fold them base-to-variant by
  occurrence and original full model path, including stripped aliases. Apply the final names
  after source-name mesh/EditorOnly filtering and before capturing expression paths or merging
  armatures. Retain captured import paths for identity, materials, and initial active states;
  ancestor renames must not redirect children or another occurrence's namesakes.
  Modular Avatar's `ModularAvatarMergeArmature.FindCorrespondingBone` matches the effective
  child name after stripping the configured prefix/suffix, recursively under each matched parent.
  Drawers clothing can rename imported `Hips`/leg nodes to `.1` names in its prefab and configure
  suffix `.1`; ignoring those overrides leaves the clothing skin on its own skeleton.
- Internal FBX Transform `m_LocalScale.x/y/z` overrides are folded per occurrence and
  original path, independently per axis. Keep unspecified axes from import and honor
  explicit `1`, zero, and negative values. Model-root scale remains in the existing
  placement/unit-conversion path and must not be applied a second time.
  Apply internal scales after prefab placement (including additional single-payload resets)
  and Assimp helper collapse, before primary wrapper collapse and before MA captures
  `destination^-1 * source * bindPose`.
  Top-level internal nodes without a retained `RootNode` inherit the wrapper's unit and
  prefab-root scale when it is collapsed. A later raw override would discard those factors
  (for example, explicit `1` would replace `0.01` meter conversion). Keep the factors on
  separate wrappers until authored scales have been applied. The resulting skin retains the authored
  clothing fit after its bones are replaced by body bones; scaling the body instead would
  incorrectly change other clothing. A nested HolidayMessenger bone scaled to `1.05`
  is a regression case. Compare saved skin geometry and bone identities, not just Slot
  scale: a merged source bone is removed and its deformation lives in corrected bind poses.
- Capture Animator renderer paths after prefab placement and mesh removal, before Merge Armature.
  Fold variant `m_Name` overrides into authored renderer and ancestor transforms by object
  identity before creating slots; synchronize renderer material records with the final names.
  The deleted-mesh pass retains authored copies by Slot identity, since variant names
  can differ from the imported inclusion records. Unauthored namesakes remain excluded.
  Reuse that resolver for blink and visemes so moved renderers retain their identities through
  bone merging and eye-pivot insertion; missing or ambiguous paths still cannot select namesakes.
- Descriptor blink and visemes retain the referenced prefab Transform identity or FBX
  occurrence/path through adaptation. Capture the exact renderer before hierarchy changes;
  missing identities must not fall back to other same-named renderers. An explicit
  `VisemeSkinnedMesh: {fileID: 0}` suppresses descriptor viseme bindings even when inherited
  shape names and `lipSync=3` remain.

### Viseme・Blink推定の成立条件

- Face inference rejects motions that also animate the target renderer's `m_Enabled`
  or `m_IsActive` on its GameObject or any ancestor (including the empty root path).
  Even constant visibility curves can differ from prefab defaults; face drivers cannot
  reproduce them. Sibling/child activation and ancestor renderer enable curves do not
  control the target renderer and do not disqualify its face binding.
- Automatic blink inference requires both a fully open (0) key value and a fully closed
  (100) peak. A looping constant closed-eye pose or partially open curve must not install
  a driver that overrides the authored eye state between blinks.
- Animatorのbinding pathはdescriptor root基準で解決する。追加layerの部分weightは、
  full-weightのdriverへ誤変換しないよう推論対象から除く。
  Additive layerも最終的な絶対weightを表さないため、viseme・blinkの推論対象から除く。
  viseme推論はentry/defaultとtransitionの接続を辿り、未接続のchild state machineを含めない。
  Entry が全 Viseme 値を処理する場合、default state を無条件に到達可能としない。
  muted Entry や一部の値だけを処理する Entry では default fallback を残す。
  Nested Entry routing carries the Viseme values allowed by the incoming transition,
  intersecting each Entry condition and its default fallback. Track visited values per
  node so another route can enter the same machine with additional values. State and
  Any State transitions may run later after Viseme changes, so their value domain resets.
  Equality transitions targeting a nested machine resolve its ordered, Solo/Mute-filtered
  Entry route or default state for that same Viseme before checking the constant shape.
  A missing direct destination is not itself an unsupported motion; unresolved or cyclic
  nested routes remain unsupported.
  Solo/Mute適用後の同一transition listではViseme条件と順序を確認し、先行transitionが
  必ず成立する値について後続transitionを辿らない。exit time付きの先行transitionは遮断と見なさない。
  Viseme以外の条件を持つlayerは、toggleの初期値に関係なく保守的に推論対象から除く。
  この判定も実際に到達可能な遷移だけを対象にし、未接続stateの条件では口パクを除外しない。
  永続的なDirectVisemeDriverでは条件付きの有効化・無効化を保持できないため。
  同じ Viseme 値のまま遷移先 state から離脱できる場合も、その phoneme の推論を除外する。
  無条件・exit time 付きの離脱と silence fallback にも適用し、Mute/Solo と Viseme 条件を尊重する。
  所属 state machine と祖先の Any State 遷移も離脱判定に含める。
  Viseme motion must also keep each blendshape curve constant: changing key values or
  nonzero interpolation slopes cannot become a permanent full-weight binding. Blink
  inference still uses the peak of its animated curve.
  Before accepting a viseme or legacy single-shape blink, check its renderer path and blendshape against bindings
  in reachable states of other nonzero-weight Animator layers, including zero-valued curves and BlendTree
  motions. Reject overlapping bindings conservatively because permanent drivers cannot
  reproduce the combined layer result; unrelated bindings do not block inference.
  Retain zero-valued curves when counting viseme and blink shape requirements. Initial prefab
  and FBX weights are collected before face inference, but a neutral curve cannot be
  assumed redundant across Animator states. Reject multi-curve viseme and legacy blink motions conservatively: a single-shape driver
  cannot also clear another authored shape, even when its required value is zero.

### 主モデルと骨格の統合

- descriptor hierarchyが参照するhumanoid FBXをprimaryとして優先する。
- `humanDescription.human` がない場合は、必須human boneが揃うskeletonからhumanoidを推定する。
  モデル選択と実際の割り当ては同じ `HumanoidSkeletonMapper` を使い、必須15部位が
  揃わない推定は採用しない。明示的な対応表は部分的でも優先し、自動補完で変更しない。
  左右の識別は `.L` / `.R` / `_L` / `_R` 接尾辞に加え、`L_` / `R_` と
  `Left` / `Right` 接頭辞に対応する。namespace、Bip接頭辞、番号付き指などを正規化し、
  名前の手掛かりを親子関係とrest poseの位置で検証する。髪・胸などの枝は除外し、
  twistなどの補助骨は階層を辿る際に通過できるが、人体ボーンには割り当てない。
  アイザラ 1.1.1 は `human: []` で、四肢・指が `L_UpperArm` / `R_Index_Proximal`、
  目が `LeftEye` / `RightEye` という混在形式。接尾辞だけの推定では体幹6本しか得られず、
  メッシュ読込後にアバターセットアップが省略される。旧接頭辞対応は54本、階層推定は
  `J_Jaw` を含む55本を解決する。
  ただし `.meta` の `human: []` は、Unityインポート後の `Avatar` にも対応表がないことを
  意味しない。アイザラのPrefabはFBXからAnimatorを継承し、その `m_Avatar` はFBX内の
  `AGS_01Avatar`（fileID `9000000`）を参照する。Unity 2022.3.22f1の生成済みデータでは
  このAvatarの `m_HumanDescription.m_Human` に55本の対応がある。ただしUnity側は
  `Jaw` が `J_F_D_Hair_2` に割り当てられており、本変換器の `J_Jaw` とは異なる。
  同じ本数でもUnityと同一の結果とは扱わない。
  現在の変換器はAnimatorのAvatar参照をモデル選択に使うが、生成済みAvatarの対応表は読まない。
  調査ではUnityの `Library/Artifacts` にある生成済みFBXを付属の `binary2text` で読み、
  Animatorの参照先fileIDとAvatarの対応表を照合できる。これはUnityプロジェクト側の
  インポート結果であり、元の `.unitypackage` 内の `.meta` と区別する。
  UnityなしでAvatar参照から対応表を取得できる割合を調べる場合は、参照の有無だけを数えない。
  元データ内のAvatarオブジェクト（Unity classID `90`）とその対応表の実体を確認する。
  全収録アセットにAvatar実体がなければ、選択Prefabや継承経路によらず取得不可と判定できる。
  FBX内の生成サブアセットへの参照は、FBX `.meta` の明示対応表と区別して集計する。
  バイナリ形式の未解析アセットは取得不可と断定せず未判定にし、Prefab入力の
  `Library/Artifacts` は「Unity生成物を使用しない」集計から除外する。
- Unityの公開実装を調べる際は、2022.3の
  [AvatarSetupTool](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/Inspector/Avatar/AvatarSetupTool.cs) と
  [AvatarAutoMapper](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/Inspector/Avatar/AvatarAutoMapper.cs) を参照する。
  インポート時はbind poseとスキン対象の骨を取得し、Biped専用または一般の自動割り当てへ進む。
  一般の割り当ては階層・名前・左右・方向・長さを評価し、体と手指を分けて処理する。
  Unityの参照用ソースは製品コードへ移植せず、上記の判断材料に基づく独立実装とする。
- 本変換器のフォールバックは `.meta` の `skeleton` にある `parentName` とlocal TRSから
  全体座標を再構成する。名前で一意に識別できるHipsとHeadを基準に中心の鎖を取り、
  腕・脚は祖先関係を満たす3関節の組み合わせを比較する。名前が不足する関節では、
  体に対する左右・伸びる方向・隣接する長さの比を使う。ルートの回転とスケールも反映する。
  顔・指は追加の名前と階層の証拠を必要とし、指は同じ手に属する3関節が揃う場合だけ採用する。
  同名・同点候補、矛盾する左右名、循環、欠落した親、非有限の変換は推測で解消しない。
  親の位置が欠ける場合は幾何判定を使わず、親名のない旧形式では一意の名前対応に限定する。
  Unityのbind pose再標本化、スキン重みの候補選別、T-pose補正は再現していない。
  中心の骨を名前で特定できないモデルや曖昧な姿勢は未対応として扱い、完全互換を保証しない。
- nested animatorはprimary選択に使わず、選択アバタールート直下のAnimatorだけを権威とする。
- 参照される追加FBXを再帰収集し、各FBXのmaterial mapを混ぜずに保持する。
- `PrefabInstance.m_TransformParent` とsource objectを辿り、追加FBXを対応する親boneへ配置する。
- Merge Armatureは同じsource/target名でもcomponentごとに適用する。
  Authored source/target hierarchies are captured even without physics or mesh-parent
  placements. Skin bone references select the owning FBX in multi-model unpacked prefabs;
  imported bones and their armature ancestors are reused only at verified full paths.
  Resolve all merge identities before consuming any armature, then remap subsequent
  source and target references (including descendants) with each skin bone replacement.
  source GameObjectとtargetObjectをprefab instance・Transform・FBX pathの参照として保持し、
  名前や一致する骨の数で別の衣装を選ばない。variantのtargetObject・prefix・suffix上書きと
  component削除を反映する。targetObjectがない旧形式ではdescriptor rootからreferencePathを辿る。
  authored Transformの旧形式pathは、合成済みPrefab上の親子identityを辿って先に参照へ解決する。
  Legnia 4Pでは本体のimport骨格がdescriptor用Slotの外側に残るため、変換後のSlot階層だけで
  `Armature` を探すと衣装・髪の統合が両方スキップされる。解決した統合先も配置収集へ渡し、
  skinと同じimport骨格へ接続する。明示targetObjectを優先し、同名の別branchへ名前検索しない。
  authored親子関係で解決できないimportモデル内のpathは、従来のruntime path解決を継続する。
  `$$AVATAR` もruntimeでdescriptorへ解決する。参照化してdescriptor全体を配置収集すると、
  複数FBXの骨格が混在し、本体のimport骨がauthored Slotへ置き換わるため収集対象にしない。
  複数FBXのskinが同じ統合先を共有する場合は、その骨格を使う本体FBXを優先候補に残し、
  骨の所属と完全pathを検証してimport骨を再利用する。衣装の共有skinを理由に本体骨を複製しない。
  Bone Proxyも同じ削除集合を使い、outer variantで削除された操作を再生成しない。
  descendantに配置済みのsourceも統合できる。Revan underwearを含む複数衣装では、
  同名の別衣装に一致する骨が多くても指定された衣装だけを消費することを検証する。
- Merge Armature の bind pose は Modular Avatar の `Editor/MeshRetargeter.cs` と同じ式
  `新しい骨のworldToLocal * 元の骨のlocalToWorld * 元のbindPose` で補正する。
  骨の参照だけを置換すると、単位が異なる衣装の頂点が骨へ潰れる。
  `VrchatSkinRetargeter` が骨を破棄する前に行列を記録し、連続した統合の補正を順に合成する。
  最後にRendererごとにMeshXを複製して保存し、共有元のmesh/providerや別の配置を変更しない。
  再読込と初期blendshape weightの復元が終わってからアバター設定・FirstPerson生成へ進む。
- 未統合の補助骨・子オブジェクトは、Modular Avatar の `SetParent(..., true)` と同様に
  global transformを保持して移動する。同名骨でもlocalの単位・軸・姿勢は一致するとは限らない。
- Bone Proxyは表示名で重複除外せず、各componentの所有Transformをprefab配置ごとに保持する。
  接続先はdescriptor rootからの完全なsubPath、または主FBXのhumanoid bone参照とその相対subPathで解決する。
  `$$AVATAR` はdescriptor rootを指す。Merge Armatureで消費された参照を更新し、
  全Proxyの接続先を移動前に確定する。明示参照やpathの解決失敗時は同名の別objectへfallbackしない。
  rendererやPhysBoneのない所有objectも配置を生成し、物理と共有する同一配置は重複記録しない。
  boneReferenceの番号は[Unity HumanBodyBones](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/Animation/ScriptBindings/Avatar.bindings.cs)に従い、親指は既存のVRM 1命名へ正規化する。
- primaryを含むimport wrapperは最終階層に残さない。

## FBXの単位と軸

- Unityのscaleは `globalScale * UnitScaleFactor / 100` をroot hierarchyへ適用する。
  `ModelImportSettings.Scale` でmeshだけを拡縮しない。
- 追加FBXを別FBX内へ配置するときは、local scaleに `追加モデルのImportScale / 親モデルのImportScale`、
  Unityで保存されたlocal positionに `1 / 親モデルのImportScale` を掛ける。
  親の実測global scaleでは割らない。ユーザーが設定した親の拡縮は子へ引き継ぐ。
- `UnitScaleFactor=100` かつtop-level wrapperのuniform 0.01は、Unity生成scaleとの二重適用を避ける。
- `UpAxis` メタデータだけで事前回転せず、import後のHipsからHeadの実方向をY+へ最小回転で合わせる。
- `FBX Import Alignment` は一時スロットであり、global transformを保持して畳む。
- static meshをモデル階層外へ複製するときはimport scaleを保持し、配置先FBXの補正分を除く。
  この補正はそのmeshだけに必要なので、子の位置とscaleへ逆補正して二重適用を防ぐ。

## prefabの状態反映

- `m_RemovedComponents` は共通のinstance別component collectorで継承順に畳む。
  static MeshRendererまたはMeshFilterの削除は描画を除き、authored objectと子の階層を残す。
  元FBXのテンプレートrendererも復活させず、削除componentのmaterial/weight overrideは無視する。
  FBX上のcomponent削除はYAML sceneの列挙には現れないため、graphの削除identityを別途取り出す。
  fileID resolverでRenderer／SkinnedMeshRenderer／MeshFilterの型と所有nodeの完全なpathを確認し、
  FBX配置IDとpathの組でインポート後まで保持する。hash生成IDとmetaの型付きID・旧recycle IDを扱う。
  削除するのは該当rendererだけで、同じobjectのTransform・骨参照・子rendererや別配置・authored copyは残す。
  pathが解決できない参照を同名node全体の削除へ置き換えない。
  outer variantのexplicit/omitted stripped aliasを解決し、同じprefabの別instanceへ削除を伝播しない。
- `m_RemovedGameObjects` とprefabに存在しないrendererを除去する。
- `m_IsActive`、material、初期blendshapeはbaseからderivedの順に畳み、外側overrideを最後に適用する。
- overrideはrenderer名だけでなくsource FBX GUIDでscopeする。同名rendererを持つ合成FBXを混同しない。
- standalone `.asset` meshだけはFBX GUIDがないため名前照合を許可する。
  未解決GUIDを `.asset` と同一視してscopeを外してはいけない。
  Descriptor face targets for baked standalone meshes use a unique matching renderer
  path in the selected FBX. Missing or ambiguous matches retain strict prefab identity.
  Physics / eye targets and placement capture include the prefab root Animator's
  `m_Avatar` FBX in their local skeleton candidates, even when only accessories
  reference FBX meshes directly. Reuse bones only after skeleton membership and
  full-path checks; helpers absent from that skeleton remain authored objects.
  Without an explicit Animator, ambiguous physics / eye references retain prefab
  identity rather than guessing one of several models.
  Omaneko v1.1 は本体が standalone mesh、小物が複数FBXの構成。小物だけから骨格を
  推定すると目とPhysBone用にPrefab側の骨が重複生成され、頭は高さ約1.52mなのに
  目は約1.12m、HeadProxyは約1.13mになる。Animatorの参照先を候補に保持すると目が本体の頭の
  子になり、目は約1.60m、視点は約1.62mに揃う。視点オフセットだけで補正しない。
- authored FBX meshのコピーはprefab GUIDとGameObject fileIDから実Slotを保持し、同名でも
  materialと初期blendshapeを個別に適用する。識別子を持たない従来のrendererは、空のoverrideも
  含めて出現順に1対1で消費する。
- コピーとその親のactive stateもobject単位で保持し、従来の名前照合による非アクティブ化を重ねない。
- Assimpが挿入する `_$AssimpFbx$_PreRotation` などの補助nodeはUnityのobjectではないため、
  FBX fileID生成と骨格pathの収集から除き、実import階層との照合でも中間の補助nodeを無視する。
  補助node自身のpathは親と区別し、同名boneの別branchを一意とみなさない。変換行列は保持する。
  GameVketChanではこのpath差によりunpacked prefabの骨格が別途作られ、センチメートルの
  bind poseをメートルの骨へ接続して数十メートルに変形していた。physicsとskinが元の骨格を
  共有すれば単位補正も保持され、8メッシュすべての頂点が元FBXと一致する。
- 複数materialのskinned FBXはAssimpでsubmeshへ分割され、bone配列も重複・部分化する。
  bone index表はmaterial読込を無効にした別importの未分割geometryから取得する。
  名前による重複除去は同名の別boneを失うため行わない。通常importのmaterial情報は保持する。
- コピーへのbone参照適用時は、インポート済みMeshXのbone表から元のFBX indexへ対応付ける。
  `LimitBoneWeights`は未使用boneを除去するため、Prefabの`m_Bones[i]`をインポート後の
  `Bones[i]`へ直接代入してはいけない。服やベールだけbone数が減り、肩・裾が別の骨に
  接続されて形状が反転するケースがある。照合には参照先の名前ではなく元のbone名を使い、
  改名・別骨へのoverride・明示nullも元indexで適用する。配列が同一なら同名boneもindexを
  保持し、配列変更後に元indexが曖昧な場合は誤接続せず変換エラーにする。
- 保存済みPrefabのbone配列が現行FBXより短い場合、各参照が同じソースFBXの一意なboneを
  指すことをobject identityと完全pathで検証して、現行FBXのindexへ対応付け直す。
  FBX更新で未使用boneが増えたり並びが変わったケースに対応するが、省かれたboneに
  頂点weightがある場合、null・ローカル骨・別モデル・重複などで対応を確定できない場合は
  変換を止める。weightの有無もmaterial分割前のgeometryから取得する。
  同じ長さの配列は従来どおりindex指定のoverrideとして扱う。
- 同名のauthored rendererの一方がEditorOnlyでも、残るobjectのmodel/nameをkeep-listに残す。
  除外objectのmaterialと外側overrideは取り込まず、残るrendererへ流用しない。
- outer variant自身の変更を読むときは、descriptorの親sceneではなく選択候補のsourceを再読込する。

## ブレンドシェイプ

### 同位置の頂点で異なる変形を保持する

[ModelImporterの共通制約](avatar-conversion.md#modelimporterの共通制約)に対し、
バイナリFBXでは一時的な頂点カラーでcontrol pointを区別する。

`UnityFbxMorphSeams` は、主FBX・追加FBXをインポートする前にバイナリFBXの
`Shape → BlendShapeChannel → BlendShape → Geometry` 接続と疎な頂点差分を調べる。
同位置のcontrol point間で位置または法線の差分が異なるgeometryだけに、
control point IDを持つ一時的な頂点カラー層を追加し、誤った統合を防ぐ。
既存のカラー層は保持し、空きチャンネルを使う。元FBXは変更せず、作業ディレクトリ内の
コピーだけを書き換える。元のproperty bytesを保持してnode offsetを再計算するため、
圧縮配列やモデル・ボーン・マテリアル等の定義は変更しない。

この一時カラーは統合の識別用であり、VRChat経路では主・追加FBXの両方で
`ImportVertexColors = false` を維持する。ASCII FBXはこの前処理の対象外。
`PrefabInputSmoke` は7400/7500形式、圧縮配列、既存カラー層の有無を使い、
通常の頂点統合での破損再現と、対策後の全ポリゴン頂点の変形一致を検証する。

回帰事例: Shipilka 1.02のL5R0 (`RockNRoll`) で有効になる「汗」。
対策前は210個のポリゴン頂点で最大約0.171mの変形誤差が発生した。
GestureTableのweightではなく、FBXインポート時のメッシュ統合に原因がある。
形状の検証では変換完了ログだけでなく、元FBXの未統合ポリゴン頂点との比較と、
保存パッケージを再読込した後のblendshape差分の比較を行う。

### 変形後の法線

FBXの `ModelImporter.tangentSpace.blendShapeNormalImportMode: 1` はUnityの
Calculate指定。Assimpから取得したshapeの頂点位置が正しくても、折り畳まれた補助面の
通常時の法線がゼロや不適切な向きなら、展開後に黒い面として見える。
Shipilka 1.02のL0R5 (`Cry` motion、出力クリップ名 `RockNRoll`) は、
「照れ」で展開する `Facial` の補助面がこのケースに該当する。

`VrchatBlendShapeNormals` はshape復元後、UnityのCalculate指定があるFBXに対して
変形後の三角形から法線差分を再生成する。面積・角度の重みは `normalCalculationMode`
に従う。移動した頂点に接する面とその頂点を対象にし、通常時の位置・法線・
全shapeの位置差分は保持する。同じ通常時位置・通常時法線・変形先を持つ頂点間では
UV境界を平滑化する。Unityの全smoothing group処理を再現するものではない。

無関係な面まで再計算すると、複数shapeを同時適用したときに通常時の法線補正が
重複して加算されるため、非影響部分と空shapeの法線は保持する。
Import / None / 指定なしにはこの再計算を適用しない。共有mesh/providerは直接変更せず、
補正済みproviderを生成して同じ設定のrendererだけに割り当てる。
検証では保存後の表情を合成し、展開した面にゼロ法線や逆向きの法線がないことも確認する。

参考: [Unity ModelImporterNormals](https://docs.unity.com/en-us/engine/7000.0/script-reference/unityeditor/modelimporternormals)、
[normalCalculationMode](https://docs.unity3d.com/jp/current/ScriptReference/ModelImporter-normalCalculationMode.html)。

### 名前・初期値・表情への接続

Resoniteは空または微小なshapeを除去するため、Unityのindex参照がずれる場合がある。

- FBX内の元shape順を記録する。
- blinkやprefab weightが数値参照する最大indexまで、欠落shapeを空frameとして復元する。
- 名前参照だけのrendererは大量の空frameを復元しない。
- FBX `BlendShapeChannel.DeformPercent` をモデルprefabの既定weightとして読み込む。
- Scope FBX default weights by model occurrence and renderer name, and fill missing
  weights on every corresponding renderer record. Explicit zero overrides remain authoritative.
- 明示的なprefab `m_BlendShapeWeights` は0を含めて既定値より優先する。
- 初期weightはblendshape修復直後と最終scene setupの両方で適用する。
  同じFBX内に同名rendererが複数ある場合、複製元のblendshape名・順序とdescriptorの瞬きindexは
  rendererの完全なFBX pathで解決する。pathが判明している参照を名前だけの表へfallbackしない。

VRChatの15 visemeはResonite enumへ対応させ、Unityの0〜100をResoniteの0〜1へ変換する。
customEyeLookSettingsの左右の目はprefab配置・Transform fileIDまたはFBXの完全なpathを保持する。
通常のhumanoid骨も主FBXの配置IDとnode pathを保持し、向き補正・リグ設定で同じ参照解決を使う。
ローカルの目Transformも配置を生成し、Merge Armature後の参照表をAvatarSetupのリグ割り当てまで渡す。
同名の目があっても指定されたobjectを使用し、欠落・破棄された明示参照を名前検索で置き換えない。
視線には `EyeRotationDriver.MaxSwing=15` を使う。
瞬きは `eyelidsBlendshapes[0]` だけを使い、LookingUp / LookingDownはblinkとして扱わない。
Blendshape repair selects original tables by imported model identity or the authored
copy's GameObject identity. Same-named copies must not replace the primary model's
table. Descriptor blink indices resolve through the referenced renderer's source mesh,
so an accessory with a different shape order retains its own blink name.

Descriptor に有効な Blink 番号と renderer があれば、既存の Parser → ModelAdapter → AvatarSetup の経路で OpenCloseTarget まで設定する。
未割当の -1 は設定情報の欠如として扱い、名前検出で補うかは別の方針になる。

### 瞬きが未設定の場合

Descriptorの `eyelidsBlendshapes` はBlink / LookingUp / LookingDownの順。
先頭の-1は瞬き未割当であり、残りの番号を瞬きの代わりに使わない。
例えばPlum v1.0.1は `[-1, 19, 20]` なので、メッシュにvrc.Blinkが存在しても
Descriptorから瞬きは作られない。

Animatorからの補完はFX layerの初期状態を `VrchatAnimatorDefaults.Resolve` で解決し、
そこで再生されるループClipを `VrchatBlinkLoop` で調べる。Descriptorの明示設定を優先する。

- `blink`（大文字小文字不問）、`まばたき`、`瞬き`、`瞬目` を名前に含むshapeが対象。
  `eye_blink_1` も対象になるが、名前だけでは採用しない。
- キー値が0～100の範囲で、先頭・末尾が0、途中の最大値が100となるカーブを瞬きとして登録する。
  途中で閉じない固定ポーズ、半開き、非ループ、無関係な顔shapeが混在するClipは除外する。
- 同じClip内の瞬きshapeの常時0カーブは初期リセットとして保持し、EyeLinearDriverには登録しない。
  0カーブでも接線が非ゼロならリセットとは見なさない。
- 複数shapeを登録できる。左右の名前（左/右、left/right、区切り文字付きL/R）を判別でき、
  全接続が左右のどちらかに分かれ両側が揃う場合は `blinkLeft` / `blinkRight` に分ける。
  それ以外は `blink` として両目共通の入力につなぐ。
- 部分weight、Additive、対象rendererや祖先の表示切り替えを含む候補は除外する。
  後段の有効layerが初期状態で同じshapeへ書く場合も除外する。
  後段の状態を確定できない場合やBlendTreeは、到達可能motionのbinding重複を保守的に調べる。
  BlendTreeはcontroller内と外部 `.asset` の両方を辿り、`m_Childs` / `m_Children` を読む。
  入れ子の参照元を保持し、循環や欠落参照を「顔カーブなし」と扱わない。
  前段の表情layerとの重複は許可し、取り込み後は既存の表情・瞬き合成で扱う。
- 候補ループが一意の場合だけ採用する。従来の単一 `blink`・他layerとの非競合による検出も残す。

動く瞬きループを検出できない場合は、中立表情の初期ループからの補完も行う。
全blendshapeカーブが定数で、`blink` / `vrc.blink`（大文字小文字不問）または
`まばたき` / `瞬き` / `瞬目` と完全一致する両目用shapeが一意に0へ設定されている場合だけ、
そのshapeをResoniteの自動瞬きへ接続する。これは元の瞬きアニメーションの移植ではなく補完である。
片目用・ウィンク・番号付きshapeから閉眼の意味を推測しない。他の表情shapeの定数値は変更しない。
複数候補、非ゼロ値、動くカーブや対象の表示切り替えを含む場合は補完しない。
Descriptorと動く瞬きループを優先し、後段layerの初期上書きも通常のループ検出と同じように確認する。

元Clipの再生タイミングや、Animatorパラメーターによる瞬きの停止・接触ギミックは再現せず、
初期状態の接続をResoniteのEyeManager / EyeLinearDriverへ置き換える。
初期リセットはBlendshapeResolverが初期値を記録する前と、最終prefab状態復元の後に適用する。
初期値の変更は対象の瞬きshapeだけに限定し、表情選択中の固定ドライブにはしない。

Lina 2.00の `AD01_Lina` はDescriptorの `eyelidType=0`、`eyelidsSkinnedMesh.fileID=0`、
`eyelidsBlendshapes` が空で、FX Animatorの `BLINK` layerが瞬きを担当する。
`VrchatAnimatorDefaults.Resolve` は初期状態を `ENABLE` まで解決するが、
`Lina_Blink_Enable` はBodyの `まばたき右` / `まばたき左` を0～100で動かし、
両目用の `まばたき` を常時0へ戻す3本のカーブを持つ。
従来は単一shape・名前 `blink` の条件を満たさず、他layerとのbinding重複もあり未検出だった。
新しい検出では左右2本をEyeLinearDriverへ登録し、両目用shapeの0固定を初期リセットとして保存する。
SNIFF CONTROLの到達可能motionには同じbindingがあるが、初期状態のIDLEは上書きしない。

LinaのFBXには両目用 `まばたき` の `DeformPercent=100` がある。
従来はこの値が初期weightと表情出力のBaseに残り、保存パッケージのBodyもweight=1となっていた。
ループの0指定を初期値と表情解除時のBaseへ反映することで、左右の瞬きを追加した後も残る閉眼を防ぐ。
衣服の調整shapeなど、瞬きと関係しないFBX既定値は維持する。

Shipilka 1.02はDescriptorの瞬き設定が空で、初期 `Default Shape / ShapeDefault` のループが
Bodyの `まばたき` を0に保つ。動く瞬きループではないため、中立表情からの補完で両目1本を登録する。
`blink.anim` は非ループで `Fist` の `GestureRightWeight` によって時間を制御する握り込み表情なので、
自動瞬きの再生元として扱わない。初期IdleのSDK `proxy_hands_idle.anim`
（GUID `14980fc5fe40191418954549174fe63e`、fileID `7400000`）は手のmuscleカーブだけを持つ。
この参照がunitypackageから欠落している場合だけ、顔カーブを持たない既知のSDK motionとして扱う。
同じGUIDの実体が同梱されていればそちらを解析し、その他の不明な欠落motionは非競合と決めつけない。
保存パッケージをExpressionSmokeで再生するときは `RESOPON_TEST_EXPECTED_BLINK_BINDINGS` を
Shipilkaでは `1`、Linaでは `2` に設定し、瞬き接続が0本でも開閉テストが通ることを防ぐ。

共通の追跡設定ではAvatarCreatorのBlendShape名ヒューリスティクスによる補完を行わないため、
瞬きがないアバターを再変換するだけで接続先が増えるとは限らない。
取り込み後の接続順は[表情システムへの接続](avatar-conversion.md#表情システムへの接続)を参照する。

## FX基底レイヤーの中立表情

PrefabのBlendShape値は、VRChatで実行されるFX基底レイヤーの初期値と異なる場合がある。
`VrchatAnimatorFaceParser` は解決済み起動stateの定数カーブを `VrchatExpressionDefaults` で読み、
取り込む表情Clipに含まれるbindingだけを `BaseExpressionInitialValues` に残す。
先頭レイヤーのweightはUnityの規則に従い、保存値が0でも1として扱う。
後続のジェスチャー・メニュー・衣装レイヤーを中立表情へ固定する処理ではない。
一般的なAnimatorの実行やレイヤー全体の合成は再現しない。

未解決の起動state/motion、パラメーター条件分岐、BlendTree、AvatarMask、同期レイヤー、
motion time parameterは対象外。Expression Parametersや保存値の違いを初期値で推測しない。
値が動くカーブ、非有限値、同一bindingの重複、等しい端点でも区間の接線が非0のカーブは除外する。
対象外のbindingはPrefab/FBXの値を保つ。BlendshapeResolverで実際の顔Rendererを一意に確認し、
Descriptorのvisemeや別Rendererの同名shapeを除いてから基準値を更新する。
最後のPrefab状態復元後にも適用し、瞬きループのopen/reset値があればそちらを優先する。

回帰例: TolassAndWedge 4.7の `Tolass4.5` はPrefabのBody `Mouth.KibaSmile` が100だが、
FXの `DefaultExp / exp_Default` は0にする。Prefab値だけを採用すると、保存された口のweightと
表情出力のBaseが1になり、解除時にも口が開く。修正後は両方0となり、表情の選択中は引き続き
口を開ける。無関係な `Adj.Face.Adjust=50`、Body2や衣装の調整値は維持する。
ただし、これは表情レイヤーの下地であり、両手Neutralの最終表情そのものではない。
Tolassの `Hands` レイヤーは `Idle L / Idle R` のどちらも `exp_Niko` を参照し、
両手Neutralでは `Mouth.Niko=100` と `Sp.Cheek=100` に上書きする。表情出力を生成するときは、コンパイル済みL0R0を
解除用のnullへ変換する前にこのポーズをデフォルトへ保存する。Nikoを名前で決め打ちしない。
検証では保存パッケージの再生に `RESOPON_TEST_INITIAL_SHAPES` を使い、元データから求めた
`Mouth.KibaSmile=0`、`Mouth.Niko=1`、`Sp.Cheek=1` と調整値0.5を、読み込み直後と全表情再生後のResetで確認する。

## VRChat FirstPerson

`AvatarSetup.ApplyFirstPersonAutoAsync` は `Source=ModelSource.VrchatFbx` の場合、
annotationがなくてもアバタールート配下のMeshRendererを対象にAuto処理を実行する。
FirstPerson Autoはavatar rootのBipedRigが使うHeadを優先し、リグにHeadがない場合は
解決済みnode参照だけを使う。衣装の同名Headや子階層の別BipedRigをfallbackにしない。
headless meshの生成と描画制御は[共通のFirstPerson処理](avatar-conversion.md#firstpersonの描画制御)を参照する。

## 視点

- Descriptor の `ViewPosition` がある場合は、目ボーンからの推定より優先する。
  目ボーンは回転用の支点であり、見えている眼球の中心とは限らない。
- `ViewPosition` は Descriptor 原点からの、Unityワールド軸に沿ったメートル単位のオフセット。
  SDK 3.10.6 の `VRC_AvatarDescriptor.OnDrawGizmosSelected` は
  `transform.position + ViewPosition` で表示し、`TransformPoint` を使わない。
  Descriptorやその親の回転・拡縮、FBXの単位補正をオフセットに掛けない。
  原点の配置は保持し、ResoPonがシーン全体に加えた `AlignVrchatImportUp` の変換だけを反映する。
- インポートの向きを揃えた後、Descriptorを含むラッパーを畳む前にワールド座標を保存する。
  `AvatarSetup` はこれをHeadProxyの配置に使い、標準のVRIKAvatarセットアップでTargetを生成する。
- 明示した `--view-forward` / `--view-up` は元の視点へのメートル単位の加算になる。
  元の視点がある場合は目の間隔による自動オフセットを加えない。
  設定またはDescriptorの参照先がない場合、非有限値の場合は
  [共通の視点推定](avatar-conversion.md#アバターセットアップ)へ戻る。
- 回帰例: NoraMiaree 1.0.0の目ボーン間隔は約6.5mmで、支点は顔の後方にある。
  元の視点は `(0, 0.734906, 0.0715642)`、Prefabルートのscaleは1.1。
  このscaleを視点にも掛けると約7.3cm上がり額の位置になる。ルートローカル座標と解釈しない。
  また、通常のunpacked prefabでは主FBXと追加の衣装FBXの再利用骨格を、コピーしたrendererと同じ
  authored root配下へ配置する。骨格をimport root直下に残すとscale=1.1がskinへ反映されず、
  SDK視点だけ直しても目の高さと約6.6cmずれる。FBXインスタンスの配置処理とは分ける。
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
- 回帰例: Ricorine 1.0.1 の Albiflora は、親 Prefab の FBX ルート scale=0.87 を継承し、
  子 Variant では位置・回転だけを上書きしている。子の変更一覧だけで配置を作ると scale が1へ戻り、
  視点は約9.5cm目より低くなる。FBX Transformには合成先のYAML文書がないため、
  `PrefabGraph.Modifications` の正規化済み occurrence/参照先で各軸を基底から順に解決する。
  明示した0や1も上書きとして保持し、同じFBXの別インスタンスの変更を混ぜない。
  視点オフセットは従来どおり非拡縮のメートル値を使い、モデル側の縮尺を復元する。

## PhysBone

取り込み後のチェーン生成・テンプレート・Grabの設定は
[揺れものの共通出力](avatar-conversion.md#揺れものの共通出力)を参照する。

- unpack済みprefabが複数のFBXを参照しても、物理用の親骨は主モデルを候補にして所属とフルパスを照合する。
  renderer数だけで候補を捨てると、物理とskinが複製した骨へ接続される一方、humanoidの名前解決は
  元の骨を選び、着用時の姿勢がメッシュへ伝わらない。明示的なsource祖先はそのFBX identityを優先する。

- Physics-only prefab roots retain their prefab Transform identity without an inferred FBX-root identity.
  Capture their local descendants and enclosing prefab placement even when no mesh needs those slots;
  create them before resolving physics targets. Imported skeleton parents are reused when verified.
- Create physics placements after registering and placing mesh copies, but before resolving
  copied skin bones and compensating static-renderer children. With multiple local FBX
  sources, authored physics bones may have no verified imported match; skins must then
  resolve to the same authored Transform slots as physics. Children created by physics
  placement need the same inverse mesh import-scale correction as existing children.
- Local physics descendants may carry an inferred FBX GUID in unpacked prefabs. Capture
  their authored hierarchy regardless of that GUID; only reuse imported skeleton bones
  when skeleton membership and the full path match. The inferred GUID alone is not proof
  that a helper chain or collider exists in the imported model.
- Merge Armature remaps physics references on the source armature itself to the target armature,
  as well as remapping descendants. Apply this at every merge so later merges retain live references.

- `insideBounds=1` とplane colliderはResoniteで正しく再現できないため変換しない。
- `rootTransform` があればそのboneへ、なければ所有GameObjectの親boneへ局所変換を畳む。
- node、offset、tail、radiusの署名でcolliderを共有する。

## マテリアル

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

### UnitySDKとのlilToon変換差分（2026-11-13調査）

比較基準はResonite.UnitySDKの `5eea4b032d1eb4d42a9d6df522a8888506c93bba` と、
ResoPonの `c565f7907cdd10217cc84e39e4455375133b75da`。
SDKの `Assets/ResoniteSDK/MaterialConverters/Custom/lilToon/LilToonXiexeConverter.cs`
の実行経路を基準とする。SDKの作業メモには現行ソースと異なる記述があるため、
メモにしかない機能を実装済みと判定しない。
以下はソース照合結果であり、Unity/Resonite実機での見た目の一致を検証したものではない。
表は残っている変換差分を管理し、実装済みの項目は削除する。
対応範囲・制約・検証方法は上のlilToon実装説明へ反映する。

「Unityなし」はUnity Editor/Playerを起動せず、既存のC#画像処理とFrooxEngineを使う場合を指す。
可否はソースから判断した実装方針であり、実装・実機検証済みという意味ではない。

| 未実装・部分対応の項目 | UnitySDKの処理 | ResoPonの現状 | Unityなしでの実装可否・方法 |
|---|---|---|---|
| 2nd法線マップへの切り替え | `UpdateNormal` は `_UseBumpMap=0` かつ `_UseBump2ndMap!=0` の場合、`_Bump2ndMap`、ST、`_Bump2ndScale`、`_Bump2ndMap_UVMode` を出力 | `LilToonConverter.Parse` は1stの `_BumpMap` とST・強度のみ読む。2ndのプロパティと `NormalUV` の設定がない | 可能。YAMLから2nd設定を読み、既存の法線画像取り込みと出力フィールドへ割り当てる。選択先UVが取り込み後のメッシュに保持されていることを確認する |
| 2段目・3段目の影 | `GetShadowRamp` はlilToonの `Hidden/ltsother_bakeramp` を使い、`_Shadow2ndColor/Border/Blur` と `_Shadow3rdColor/Border/Blur` を反映 | `GenerateShadowRamp` は1段目の色・境界・ぼかし・強度だけ。2nd/3rdの色alphaによる混合もない | 可能。bakerの境界計算と色混合をC#へ移植し、既存の256×256 ramp生成を拡張する |
| 影の境界色 | 同bakerで `_ShadowBorderColor` / `_ShadowBorderRange` を反映 | プロパティの読み取りもrampへの合成もない | 可能。同じCPU ramp生成へ境界範囲と色の混合式を追加する |
| 2nd/3rdのデカール系設定とアトラスの静止フレーム | `GetMainTexture` はUV0レイヤーの設定を `Hidden/ltsother_baker` に渡す。`IsDecal`、左右制限、copy/flip、MSDF、`TexDecalAnimation` / `TexDecalSubParam` をベイク処理へ引き渡す | `LilToonConverter.Layer` と `LilToonMainTextureBakePlan` はdecal・左右制限・copy/flip・MSDFを含むレイヤー全体を警告して除外。アトラス設定は未読で、フラグがなければ通常画像として扱う | 条件付きで可能。UV変換、範囲外alpha切り捨て、copy/flip、アトラス時刻0の抽出はCPUで実装できる。MSDFの `fwidth` は出力画素間の差分などで近似する。実メッシュの左右・mirrorを再現する場合はtangent情報が必要で、UVが重なる領域は材質分割等も必要になる |
| シェーダー種別・Multi設定からの描画モード判定 | `GetBlendMode` はシェーダー名のCutout/Transparent/Overlay/Refraction/Fur/FurOnly/Gemと、Multiの `_TransparentMode`（0〜6）を判定 | `DetermineAlphaMode` はblend factor、`_AlphaToMask`、custom queue、親の判定結果を使う。シェーダー名と `_TransparentMode` による判定がない。例えばRefractionの `One/Zero` はopaqueになる | 可能。shader名・GUID対応表とYAMLのMulti設定で判定する。shaderが同梱されない場合は既知GUID等による識別が必要。屈折や毛の描画自体の再現ではない |
| シェーダー既定のRenderQueueの解決 | `UpdateRenderSettings` はUnityが解決した `Material.renderQueue` をコピー | `Parse` は `m_CustomRenderQueue` と親の値だけを使う。どちらも未指定なら出力queueを設定せず、元シェーダーの既定queueを解決しない | 条件付きで可能。同梱shaderのQueueタグを解析するか、既知shaderの既定値を表に持つ。既存の `ShaderPropertyFallbackConverter.ReadShaderDefaults` にQueue解析がある。shader・識別情報のない未知variantの既定値は確定できない |

デカールと影のbaker内部は、別途lilToonの
`Assets/lilToon/Shader/ltspass_baker.shader`、`ltspass_bakeramp.shader`、
`Includes/lil_common_functions.hlsl` / `lil_common_macro.hlsl` で照合した。
SDKのチェックアウト自体にはbaker shaderがなく、実際のベイク内容はUnityへ導入したlilToonに依存する。
このlilToonソースではbakerは `lilGetSubTexWithoutAnimation` を呼び、
アトラスは時刻0で評価する。アニメーションの保持ではない。
左右・mirror判定もベイク用頂点のtangentに依存し、アバターメッシュの左右を完全再現する保証はない。

追加の挙動差として、ResoPonはlayer lighting、AudioLink、dissolve、distance fade、
scroll/rotateなどでもレイヤーを除外するが、SDKのレイヤー選別は主にUVModeで行う。
SDKがこれらの動的効果まで再現するという意味ではなく、静的な画像部分を残すかどうかの差である。
また、1段目の影の境界補間もSDK側bakerのclamped linearstep相当と
ResoPonの `SmoothStep` で異なる。影の段数を追加する際はこの式も再確認する。

すでに両方にある処理は、通常のmain色・HSVG・gradation・UV0の2nd/3rd合成・
alpha mask、1st法線、基本outline、加算かつmaskなしのMatCap、
発光色のblend/alpha/fluorescence補正、RGB×texture alpha、
ShadowStrengthMask（欠落時の白を含む）、ShadowBorderMaskによるocclusion、FakeShadowの非表示化。
これらの存在は全設定で同じ出力になることを意味しない。
2枚の法線の合成、2nd法線の強度mask、2nd emission、2nd MatCap、
Rim/RimShade、屈折・背景ぼかしそのもの、stencil、outline textureは現行SDKでも変換しないため、
SDKからの未移植機能には数えない。
反射・specularはResoPonにscalar変換があるが、現行SDKのlilToon変換には対応する更新処理がない。

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
