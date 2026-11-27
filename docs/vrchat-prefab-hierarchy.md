# VRChatのPrefab階層と骨格

[VRChat変換の全体設計](vrchat-conversion.md)で選択したアバターを、Resoniteの階層と骨参照へ変換する際の識別規則をまとめる。

## FBXの形状と組み込みメッシュ

バイナリFBXを実変換する際は、四角形の開始角を一つ巡回させる。Resoniteのインポート時に
巻き順が反転しても、非平面四角形がUnityと同じ対角線で分割される。五角形以上は
Windows GLUで三角形化し、元の境界と巻き順を検査する。頂点ID、角単位のUV・法線、
面単位の属性と既存の辺参照をそれぞれ対応付ける。連続する同位置の角はGLUが選んだ
角の属性を使う。交点の新規生成が必要でGLUが面を返さない輪郭は、Unity同様に省く。
その面を基準に登録されていた共有辺は、隣接する残存面に付け直す。未知の辺属性などは
元のインポート経路で処理する。入力FBXは変更しない。

Unityの組み込みSphereをMeshFilterで参照する場合は、半径0.5 mのResonite `SphereMesh`
を生成し、元のRendererの有効状態、GameObjectの配置とマテリアルを適用する。
これによりEkuのFresnelなど、初期状態では無効なFXの描画対象も残る。球の分割とUVは
Resoniteの生成方式による。FBXのRootNodeが配置名に改名された後も、親を解決するときは
元FBXの識別子を使う。子モデルにある別のRootNodeへ配置するとエフェクトの位置がずれる。

## prefab参照とstable fileID

Unity参照はGUIDとlocal fileIDの組で解決する。stripped objectは
`m_CorrespondingSourceObject` と `m_PrefabInstance` を辿って元アセットへ戻す。

- `fileIdsGeneration=1`: 型とobject名を使うxxHash64候補を生成する。
- `fileIdsGeneration=2`: `//RootNode` からのhierarchy pathとcomponent typeを使う。
- GameObjectのhash pathには `/GameObject` を付けず、componentだけ型名を付ける。
- xxHash64の4バイト末尾は符号なし32bitとして読む。符号拡張するとUTF-8の非ASCII名で
  fileIDが変わる。`Mesh` の `髪`、`頭アクセサリ`、`服` はUnityから取得したIDで回帰検証する。
  Omanekoの `ねこじゃらし` もこの修正でMesh copyの参照を解決できるようになる。
  非アクティブな親 `catToy` はcopyの親Transformに状態を保持するため、名前ベースの
  `InactiveGameObjects` からは除外される。診断件数の減少だけで状態喪失と判断せず、親の `Active` も確認する。
- serialized stripped documentがない参照は、prefab instance fileIDとのXORからsource fileID候補を復元する。
- Assimpの人工root配下に実mesh nodeが1つある場合、Unityのsynthetic `//RootNode/root` pathを実childへ割り当てる。
- A real top-level child named `root` takes precedence over the synthetic lowercase-root
  alias. Its GameObject and Transform IDs retain that branch's path for EditorOnly
  exclusion and physics references; sibling branches remain part of the model.

## FBXの選択と合成

fileID resolverは同じAssimp importから `UnityModelNode` の読み取り専用メタデータも保持する。
補助ノードを畳んだローカル姿勢・完全なpath・材質スロットをv2診断出力で使い、モデルの配置ごとに
元のメタデータを書き換えない。Assimpの列ベクトル行列をSystem.Numericsの行ベクトル形式へ転置し、
Translation→PreRotationの鎖で親の平行移動を回してしまわないようテストする。
モデルの直下へFBXのCoord/Up/Front軸変換を一度適用し、X反転による左右手系変換後に分解する。
回転済みの非均一スケールを行方向から取り出すと軸が入れ替わる。Quaternionも分解後の成分反転だけでは
Unityと符号の選び方が異なり、一部成分だけのPrefab上書きで別の回転になる。
この姿勢メタデータはv2診断用であり、本番のModelImporterによる頂点・skin変換を置換しない。
FBXのskin接続はclusterの接続順で読み、同名Jointでも完全なパスで区別する。
BlendShapeはボーンのあるメッシュに限定せず、morphだけを持つメッシュでも取得する。

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
  Animatorの `m_Avatar` だけから骨格を取得するFBXも、実際のPrefabInstanceがなければ
  元rendererを出力しない。`UninstantiatedFbxGuids` は選択済みモデルの配置IDと
  合成済みPrefabInstanceを照合して保持し、生成コピーの接続後に元rendererのcomponentだけを除く。
  bone・Transform・field参照は保持し、別配置の実FBXやPrefab由来のrendererは対象外とする。
  Omaneko v1.1の本体16メッシュは独立した `.asset` から生成するため、同名のFBXメッシュを
  名前ベースの保持リストだけで残すと二重描画になる。保存パッケージはUnity採取値にある
  各本体名のSkinnedMeshRendererが1個であることを検査する。空の元SlotやFirstPerson用の
  `_headless_` rendererはこの重複判定に含めない。
  置換したrendererの元スロットとimport側の祖先を記録し、アバター設定後に子・componentが
  なくなったものだけを葉から削除する。Prefab由来のobjectやcomponentから参照されるSlot・
  Slotのfieldは保持する。Revan underwearのように骨格統合後に空になるimport階層も対象となる。
  import wrapperに残るRig・MeshRendererMaterialRelayは、登録先のbone・rendererがすべて
  消失していて外部からの参照もない場合に除去する。有効な登録先を持つcomponentは保持する。
  Merge Armatureが統合先へ移した未一致の補助骨は、移動先の祖先にあるRigへ登録を移す。
  primary wrapperとそのRigが既に畳まれている場合はアバターrootにRigを作成して登録を保持する。
  アバター外のboneを含む場合や旧Rigが外部参照されている場合は旧wrapperを保持する。
  名前や空であることだけを条件に、アバター全体のスロットを削除しない。
  unpack済みPrefabが骨を別の親へ移した場合、骨格参照用FBXの元の枝も残り得る。
  `RemoveUnusedSkeletonTemplateNodes` は `UninstantiatedFbxGuids` に属する元importだけを対象に、
  子・component・実参照のないSlotを葉から除く。通常のRigへの登録だけは使用中の証拠とせず、
  削除時に登録も除く。スキン・BipedRig・物理・Slotのfield参照、Prefabで存在を確認した骨と
  その祖先は保持する。別componentがRig自体またはそのfieldを参照する場合は登録骨も保持する。
  実配置されたFBXや新規生成した補助階層は対象外。Omanekoの髪・耳をPhysBone用の親へ移した後の
  旧FBX枝が回帰例で、眼球PivotやIKのAligned Handは使用中の補助階層として残す。
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
  ウェイトを持たない末端や中間helperも、元FBXのskin骨の子孫に完全pathが一意に存在し、
  meshノードでなければ既存Slotを再利用する。名前や `_end` suffixだけでは再利用しない。
  Unityにだけ追加されたhelperは独立して生成する。`AuthoredImportedBones` にはTRSの上書きが
  省略された骨も記録し、後段の不要template削除から保護する。
  `HierarchyOrders` には合成後Transformの `m_Children` 順を記録する。YAML文書の列挙順や
  同名骨の検索順は使わず、Prefab occurrenceとTransform ID、検証済みの元FBX pathで解決する。
  `ApplyHierarchyOrder` はアバター設定・不要階層削除・更新待ち後の保存直前に `Slot.SortChildren`
  を呼び、Unity由来の子を先に、残る生成Slotを元の相対順で後ろへ配置する。眼球Pivotや
  Aligned Handを挟む場合はその枝ごと並べる。親・TRS・SkinnedMeshRenderer.Bonesは変更しない。
  未解決対象と、複数の元兄弟が同一の枝へ統合されて並べられない群はログに記録する。
  FBX内部は `VrchatHierarchyState` が元の階層と `ModelImporter.meshes.sortHierarchyByName`
  （省略時true）から順序を採取する。falseなら元FBXの順序を保持し、明示的なPrefabの
  `m_Children` を後から優先する。改名後の表示名で再ソートせず、元pathを照合キーにする。
  順序指定は既存Slotだけに適用し、除外対象の子を新規生成しない。残っているtemplateの子も、
  元配列にあれば相対順を復元する。
  `HierarchyActiveStates` はFBXの明示的な `m_IsActive` 上書きと、合成済みローカルGameObjectの
  値を保持する。従来の非アクティブ指定の後に適用し、明示的なtrueも復元する。
  値を持たないstripped参照からtrueを捏造せず、FBXの既定trueでtemplateや配置の状態を戻さない。
  配置対象のローカルGameObjectは名前だけの非アクティブ指定から除き、Prefab identityで処理する。
  Nagiya Ruriの無効な `Hair_Odango.L/R` 小物が、同名の有効な髪ボーンを無効化するため。
  除外されたtemplateの従来の非アクティブ抑制は維持する。
  ローカル非meshノードは検証済みの一意な元pathを補助キーにできるが、同名のローカルmeshは
  Prefab identityでのみ解決する。後者を元meshへ誤対応するとLilLeoのCat_Tail等を再有効化してしまう。
  FBXルートは `importedFbxRoots` の配置先を使い、内部のAssimp RootNodeだけを無効にしない。
  HinaのTulipやEkuのTaktのような小物は、この配置ルートと内部ノードの区別が必要。
  `m_Father` のないstripped FBXルートは `m_PrefabInstance` の `m_TransformParent` で親を
  検証して子配列へ含める。Aizaraのベルト下の追加モデルがこの形式。FBX内部のstripped骨を
  インスタンス全体の直下の子と誤認しないよう、ルートidentityであることも確認する。
  順序確定後、unpacked配置でprimary FBXが骨格参照用templateである場合に限り、
  `VrchatHierarchyCleanup` が採取済みのimportルートと選択Prefabルートを葉側から畳む。
  名前検索で任意の `RootNode` を除去せず、元import pathとモデルID、保持したSlot identityで限定する。
  正の均一スケールのラッパーだけを対象に、ローカルTRSを子へ合成して兄弟順を保持する。
  Slot・field・componentへの参照、非アクティブ、tag、動作component、非均一/反転スケール、
  使用中のmaterial relayがあるラッパーは保持理由を記録して残す。参照されていないRig登録は
  アバターrootのRigへ移し、スキンのBones配列と元の骨Slotは変更しない。
  `CenteredRoot` はVRIKのroot基準・身体colliderを持つため対象にしない。Omanekoでは
  `アバター/CenteredRoot/Prefab名/FBX名/RootNode/アーマチュア/Root/Hips` を
  `アバター/CenteredRoot/アーマチュア/Root/Hips` に短縮する。
  絶対パスの変わる処理なので、パス解決・表情構築・並び順適用を終えた保存直前に実行する。
  unpack済み骨格の通常のTransform文書に保存された位置・回転・スケールも保持する。
  PhysBoneやskinから直接参照されないArmatureなどの親を含め、skin骨またはその祖先であることと
  元モデルの完全なpathが一致することを確認して `AuthoredBoneTransforms` に収集する。
  骨格モデルの候補は合成済みシーンごとにroot AnimatorとrendererのFBX参照から一度だけ収集し、
  子のTransformや別Prefabの祖先をたどる際も、そのシーンを所有するビューの候補を使う。
  合成済みPrefabを読むため、Variantの上書きも反映し、省略フィールドはimport値を残す。
  Assimp補助ノードの折り畳みと配置の完了後、primary wrapperの折り畳み・MA統合・IK設定の前に適用する。
  FBXのmesh・bindposeを元の単位で保持するため、位置だけをそのモデルのimport scaleで割り、
  wrapperによるメートル補正を維持する。回転・スケールには単位倍率を掛けない。
  主骨格の最上位ノードまで保存されたunpacked姿勢では、hips→headからの上方向推定を行わない。
  推定補正を重ねるとUnityで設定した胴体の傾きまで変わってしまう。
  MANUKAのNDMFベイク済みPrefabは、Headの1.15倍拡縮や脚・胴体のTRSがFBXから変更された回帰例。
  `PrefabInputSmoke` / `AuthoredBoneTransformChecks` に加え、`PrefabSceneSmoke --native-package` は
  元YAMLから独立に階層行列を組み、保存パッケージの使用骨のrenderer相対基底と比較する。
  生成meshの頂点・shape・bindposeと、FBX由来meshの骨index対応も確認する。
  imported bone を親として再利用するときも model GUID と authored active state を保持し、
  static attachment の import scale を親モデル分で補正して、既存の骨 Slot へ active state を適用する。
  コピーの skin bone は全 renderer と親 Slot の生成後に prefab Transform identity を優先して解決する。
  unpack 後に改名されたローカル骨の path が FBX と一致せず、対応する authored Slot もない場合は、
  元の skin binding を保持して警告する。別名の骨を名前だけで推測しない。
  改名後のPrefab骨が実在する場合は、そのTransformへ接続する。元のbind poseが出力する
  骨ローカル座標に、メッシュの取り込み単位を接続先の単位で割った倍率を3軸とも適用する。
  接続先の単位は最も近いimport済み祖先のモデルから取り、モデル外では1 mとする。
  作者が設定した骨のスケールはこの比に含めない。Merge Armatureによる骨の置換行列を左から
  合成し、各renderer専用のmeshに一度だけ保存する。MANUKA DR_05の追加下着は、
  センチメートル単位のFBXと、Prefabで改名されたメートル単位の`Armature.1`を持つ回帰例。
  unpack時にFBXの単一コンテナがPrefabルートへ置き換わる場合は、モデル側で確認したコンテナ名を
  最初の子のpathへ補う。対象はTRSがidentityで、meshもskin boneとしての使用もない単一の直下ノード。
  既存の完全一致を優先し、任意の祖先名を削るsuffix照合には広げない。子孫も元モデルの完全なpathで照合する。
  再利用したFBX骨の下へ新規作成する親・子ボーンは、各local positionを親モデルのimport scaleで割る。
  scaleは作者の値を保持し、meshコピーの位置も同じ単位へ変換する。
  これにより追加された骨もFBXのbindposeと同じ単位系を保つ。
  Ritori 1.0.5の`Ritori_lil`は、identityコンテナ`md_ritori`の省略と、`Skirt_Root`等の追加親が併存する回帰例。
  ルート省略を扱わないとメートルの複製骨へセンチメートルのbindposeを接続し、100倍の変形になる。
  ルートだけを修正しても、追加親以下の位置換算がないとスカート等が腰付近へ縮む。
  `UnpackedModelRootChecks`で厳密なpath照合と除外条件、`FbxTransformHelperChecks`で
  メートル／センチメートル・追加親の非一様scale・使用頂点の変形を確認する。
  `PrefabSceneSmoke --skin-package`は単一のunpacked Prefabを対象に、保存後の使用骨の基底を元YAMLと比較する。
  unpack済み骨格へ追加FBXを取り付ける場合も、配置収集時に主モデルの骨所属と完全pathを検証し、
  `ImportedBone` と親モデルの単位を保持する。追加FBXの配置はmesh copyより先に行われるため、
  この段階で通常の親Slotとして骨格を複製すると、そのPrefab identityがskinの接続先になってしまう。
  `ResolvePrefabParent`へimport済み参照と単位を渡し、追加した中間親の位置も同じ単位へ換算する。
  Nagmaは手の剣の親階層がこの複製を起こし、胸・腕の骨が100倍の基底となって本体と衣装が破裂した回帰例。
  `UnpackedFbxAttachmentChecks`で配置収集、`FbxTransformHelperChecks`で追加モデルとskinの骨共有・
  中間親の非一様scale・異なるFBX単位・変形後頂点を検証する。
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
  捕捉済みのGUID/pathとモデルルート表を生存する親Slotへ移し、physics・並び順・有効状態の参照を維持する。
  主FBXのルートに無効化指定がある場合は専用Slotを残し、アバター全体や兄弟モデルへの波及を防ぐ。
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
- primaryを含むimport wrapperは、モデル単位の無効化を保持する場合を除いて最終階層から取り除く。

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
- PhysBoneの `rootTransform` 未指定時は所有Transformを使う。12BのようにFBXオブジェクトへ
  コンポーネントを追加すると、stripped GameObjectだけが保存され、TransformのYAMLがない場合がある。
  この場合もGameObjectの出典IDからモデル内の完全なパスを解決し、物理を欠落させない。
- Colliderの `rootTransform` があればそのboneへ、なければ所有GameObjectの親boneへ局所変換を畳む。
  所有Transformが暗黙のFBXノードならそのノードへ直接取り付け、position/rotationはその局所空間のまま使う。
  同名ボーンを持つ別枝との混同や、親変換の二重適用を避ける。
- 12Bの登録ケースにはplane colliderが7個ある。PhysBone 14個を取り込めても、これらは既存の
  plane非対応方針により出力されない。チェーン生成の成功を衝突挙動の完全な再現とみなさない。
- FrooxEngine 2026.11.16.1330の実DLLを反射で確認したところ、`DynamicBoneChain.StaticColliders`
  は `SyncRefList<IDynamicBoneCollider>` で、同DLL内の具象実装は `DynamicBoneSphereCollider` のみ。
  PlaneCollider型は存在しない。標準コンポーネントへの直接変換はできず、独自コンポーネントは
  受け取るクライアント側にも実装を必要とするため、通常パッケージの互換性を満たさない。
  大球による平面近似も境界・裏面・接触方向が変わるので自動適用しない。必要ならUnity側で
  対応するSphere/Capsuleへ調整した別ケースを作り、実クライアントで衝突を確認する。
  APIは更新され得るため、再検討時は実行対象DLLで実装型と保存可能な参照先を再確認する。
- node、offset、tail、radiusの署名でcolliderを共有する。

## FBX衣装の明示的な骨参照の回帰事例

Nagmaの手動結合済み衣装では、FBXのメッシュが通常のPrefab Transformを `m_Bones` で参照する。
この階層の採取を生成メッシュだけに限定すると、参照先が生成されず、テンプレート側の骨へ
フォールバックする。静止時の形状が似ていても、本体のHipsを動かした際に衣装が追従しない。
FBXを使うMeshCopyも参照先の階層を採取し、検証済みのimport骨を親として明示的な子骨を生成する。
削除済みRendererはこの採取対象から除く。名前だけで別枝の骨へ結び直さない。

腰に装着する衣装・アクセサリーの追従は、Hipsの一時親を動かし、Hips自身と各Rendererの使用骨が移動したことを確認する。
静止形状は全Renderer共通の剛体変換で座標を揃えて照合する。骨ごとに位置合わせをすると接続の誤りを見落とす。

Unity 2022.3の標準スキン設定では、FBX内の正規化前の頂点ウェイトに下限0.001がある。
カスタム設定ではモデルの `minBoneWeight` を参照する。メッシュ全体でこの下限に達する頂点がない
骨だけを除外し、影響の変わった頂点は残ったウェイトを正規化する。どの骨も下限に達しない頂点は保護する。
例えばMilfyの包帯の `Elbow_support.R` はFBXでは最大0.001057、保存後は約0.000881になる。
保存後の値で一律に切り捨てるとUnityが使用する肘の骨を失う。判定はFBXのGUID・メッシュの元パスで限定し、
マテリアル分割後のいずれかの面で使われる骨や、Prefabが直接持つ生成メッシュのウェイトを保持する。
