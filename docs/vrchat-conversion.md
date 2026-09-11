# VRChat unitypackage変換の実装資料

この資料は `Unity/`、`Vrchat/`、および `Converter.ConvertVrchat` を変更するときに読む。

## 全体設計

VRChatパーサはUnityアセットを解析し、共通の `VrmModel` とVRChat固有情報を生成する。
リグ、視点、表情、揺れものはVRM経路の `AvatarSetup` / `SpringBoneSetup` を再利用し、
prefab合成、初期状態、マテリアルはVRChat専用処理で補う。

GUIとCLIはいずれも `.unitypackage` を入力として受理する。複数アバターを含む場合はGUIで選択するか、
CLIの `--avatar` を使う。出力名は入力package名ではなく選択したprefab名を使い、子プロセスは
`RESOPON_OUTPUT:` で実出力パスを通知する。

## Unity YAMLとアバター候補

- ブロックシーケンスは親キーと同じindentになるUnity YAML形式を扱う。
- 長いplain scalarの継続行を結合する。`m_ShaderKeywords` の途中で解析を切らない。
- double-quoted scalarの `\uXXXX` をデコードし、日本語GameObject名を正しく照合する。
- `VRCAvatarDescriptor` はscript GUIDだけでなくフィールド署名でも検出する。
- prefab、prefab variant、composition、`.unity` scene内のprefab instanceを候補に含める。
- 候補一覧では重いFBX配置・material解決を遅延し、選択された候補だけを詳細解析する。

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
  Authored mesh templates have a separate model identity from PrefabInstances of the
  same FBX, so an EditorOnly instance cannot exclude visible local geometry. Create
  separate template identities only from authored MeshFilter/SkinnedMeshRenderer mesh
  references; an Animator Avatar reference alone must not duplicate an instantiated model.
  Composed candidates retain the selected root Animator's humanoid model preference,
  including when nested humanoid clothing is discovered before the unpacked body.
  Select the primary model from visible identities after collecting EditorOnly exclusions.
  Local copied skin bones infer their model within the owning prefab occurrence:
  prefer the primary model if referenced locally, otherwise the sole local model.
  With no local mesh source, retain the caller's skeleton context; explicit source
  ancestors still take precedence. Repeated unpacked skins keep separate skeletons.
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
- Capture Animator renderer paths after prefab placement and mesh removal, before Merge Armature.
  Reuse that resolver for blink and visemes so moved renderers retain their identities through
  bone merging and eye-pivot insertion; missing or ambiguous paths still cannot select namesakes.
- Descriptor blink and visemes retain the referenced prefab Transform identity or FBX
  occurrence/path through adaptation. Capture the exact renderer before hierarchy changes;
  missing identities must not fall back to other same-named renderers. An explicit
  `VisemeSkinnedMesh: {fileID: 0}` suppresses descriptor viseme bindings even when inherited
  shape names and `lipSync=3` remain.
- Face inference rejects motions that also animate the target renderer's `m_Enabled`
  or `m_IsActive` on its GameObject or any ancestor (including the empty root path).
  Even constant visibility curves can differ from prefab defaults; face drivers cannot
  reproduce them. Sibling/child activation and ancestor renderer enable curves do not
  control the target renderer and do not disqualify its face binding.
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
  Before accepting a viseme or blink, check its renderer path and blendshape against bindings
  in other nonzero-weight Animator layers, including zero-valued curves and BlendTree
  motions. Reject overlapping bindings conservatively because permanent drivers cannot
  reproduce the combined layer result; unrelated bindings do not block inference.
  Retain zero-valued curves when counting viseme and blink shape requirements. Initial prefab
  and FBX weights are collected after face inference, so a neutral curve cannot be
  assumed redundant. Reject multi-curve face motions conservatively: a single-shape driver
  cannot also clear another authored shape, even when its required value is zero.
- descriptor hierarchyが参照するhumanoid FBXをprimaryとして優先する。
- `humanDescription.human` がない場合は、必須human boneが揃うskeletonからhumanoidを推定する。
  少数の名前一致だけでは推定しない。
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
  Bone Proxyも同じ削除集合を使い、outer variantで削除された操作を再生成しない。
  descendantに配置済みのsourceも統合できる。Revan underwearを含む複数衣装では、
  同名の別衣装に一致する骨が多くても指定された衣装だけを消費することを検証する。
- semanticに同じbone間で子を移すときはglobalではなくlocal transformを保持する。
- primaryを含むimport wrapperは最終階層に残さない。

## FBXの単位と軸

- Unityのscaleは `globalScale * UnitScaleFactor / 100` をroot hierarchyへ適用する。
  `ModelImportSettings.Scale` でmeshだけを拡縮しない。
- `UnitScaleFactor=100` かつtop-level wrapperのuniform 0.01は、Unity生成scaleとの二重適用を避ける。
- `UpAxis` メタデータだけで事前回転せず、import後のHipsからHeadの実方向をY+へ最小回転で合わせる。
- `FBX Import Alignment` は一時スロットであり、global transformを保持して畳む。
- static meshをモデル階層外へ複製するときはimport scaleを保持し、配置先FBXの補正分を除く。
  この補正はそのmeshだけに必要なので、子の位置とscaleへ逆補正して二重適用を防ぐ。

## prefabの状態反映

- `m_RemovedComponents` は共通のinstance別component collectorで継承順に畳む。
  static MeshRendererまたはMeshFilterの削除は描画を除き、authored objectと子の階層を残す。
  元FBXのテンプレートrendererも復活させず、削除componentのmaterial/weight overrideは無視する。
  outer variantのexplicit/omitted stripped aliasを解決し、同じprefabの別instanceへ削除を伝播しない。
- `m_RemovedGameObjects` とprefabに存在しないrendererを除去する。
- `m_IsActive`、material、初期blendshapeはbaseからderivedの順に畳み、外側overrideを最後に適用する。
- overrideはrenderer名だけでなくsource FBX GUIDでscopeする。同名rendererを持つ合成FBXを混同しない。
- standalone `.asset` meshだけはFBX GUIDがないため名前照合を許可する。
  未解決GUIDを `.asset` と同一視してscopeを外してはいけない。
  Descriptor face targets for baked standalone meshes use a unique matching renderer
  path in the selected FBX. Missing or ambiguous matches retain strict prefab identity.
  With no local FBX mesh references, physics placement retains the selected Animator
  skeleton context, reusing bones only after skeleton membership and full-path checks;
  helpers absent from that skeleton remain authored objects.
- authored FBX meshのコピーはprefab GUIDとGameObject fileIDから実Slotを保持し、同名でも
  materialと初期blendshapeを個別に適用する。識別子を持たない従来のrendererは、空のoverrideも
  含めて出現順に1対1で消費する。
- コピーとその親のactive stateもobject単位で保持し、従来の名前照合による非アクティブ化を重ねない。
- 複数materialのskinned FBXはAssimpでsubmeshへ分割され、bone配列も重複・部分化する。
  bone index表はmaterial読込を無効にした別importの未分割geometryから取得する。
  名前による重複除去は同名の別boneを失うため行わない。通常importのmaterial情報は保持する。
- 同名のauthored rendererの一方がEditorOnlyでも、残るobjectのmodel/nameをkeep-listに残す。
  除外objectのmaterialと外側overrideは取り込まず、残るrendererへ流用しない。
- outer variant自身の変更を読むときは、descriptorの親sceneではなく選択候補のsourceを再読込する。

## ブレンドシェイプ

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
瞬きは `eyelidsBlendshapes[0]` だけを使い、LookingUp / LookingDownはblinkとして扱わない。
Blendshape repair selects original tables by imported model identity or the authored
copy's GameObject identity. Same-named copies must not replace the primary model's
table. Descriptor blink indices resolve through the referenced renderer's source mesh,
so an accessory with a different shape order retains its own blink name.

## PhysBone

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

### lilToon

- headless変換ではUnity editor bakerを実行できない。直接表現できるpropertyとtexture transformを保持し、
  必要なmaskやchannel合成だけを画像処理する。
- legacy `VRChat/Mobile/Toon Lit` はvertex colorを使わないため、XiexeToonでも無効にする。
- ShadowRampMaskがない場合は白を使い、生成rampは縦方向に白から本来のrampへ変化させる。
- MatCapはAdd modeかつblend maskなしの場合だけ変換し、color alphaとtexture alphaをRGBへ焼き込む。
- emission fallbackは `_EmissionMap`、`_EmissionBlendMask`、main texture、白の順に選ぶ。
- outline shader variantとoutline property overrideを認識し、`_OutlineWidth` をXiexeToonへ割り当てる。
- lilToon Rimは表現差が大きいため変換せず、`RimIntensity=0` とする。

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
