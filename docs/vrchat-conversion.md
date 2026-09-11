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

## FBXの選択と合成

- 詳細解析は `UnityPrefabInstances` の非所有ビューで行う。選択した descriptor の subtree に
  含まれる document と PrefabInstance だけを残し、すべての collector に同じ範囲を見せる。
- 同じ prefab/FBX の繰り返し配置は、配置経路から生成した内部 GUID で区別する。
  source file と `.meta` は共有して読み取り、instance の上書きと stripped 参照を同じ内部 GUID へ
  書き換える。元の package のアセット・キャッシュ・Unity プロジェクトは変更しない。
- 循環参照は祖先経路で検出する。兄弟の同一アセットを循環として除外しない。
- unpacked prefabのFBXはメッシュのテンプレートとして扱い、複製後に元rendererを除く。
  同じモデルがPrefabInstanceとしても配置されている場合は、そのrendererを残す。
- prefabの外側にあるattachmentも複製の親階層へ含める。上書きは表示名ではなく、
  explicit/omitted stripped参照を解決したobject identityへ適用する。EditorOnlyタグも同様。
- authored rendererは全コピーを登録してから親を解決する。descriptor rootにrendererがある場合も、
  子rendererや先に生成したattachmentを同じGameObject相当のSlotへ配置する。
- `.unity` のcompositionもprefabと同じcollectorで配置・上書き・ローカル参照を解決する。
- PhysBoneのroot、ignore、collider参照はモデルのinstance identityとpathを保持し、
  import時に捕捉したSlotへ解決してから共通のSpringBoneSetupへ渡す。
  Merge Armature前にSlotを解決し、各mergeのskin用bone mappingでphysics参照も更新する。
  破棄後のsource identity検索や名前fallbackでは、移動先や別instanceを正しく区別できない。
- Animatorのbinding pathはdescriptor root基準で解決する。追加layerの部分weightは、
  full-weightのdriverへ誤変換しないよう推論対象から除く。
  viseme推論はentry/defaultとtransitionの接続を辿り、未接続のchild state machineを含めない。
  Viseme以外の条件を持つlayerは、toggleの初期値に関係なく保守的に推論対象から除く。
  永続的なDirectVisemeDriverでは条件付きの有効化・無効化を保持できないため。
- descriptor hierarchyが参照するhumanoid FBXをprimaryとして優先する。
- `humanDescription.human` がない場合は、必須human boneが揃うskeletonからhumanoidを推定する。
  少数の名前一致だけでは推定しない。
- nested animatorはprimary選択に使わず、選択アバタールート直下のAnimatorだけを権威とする。
- 参照される追加FBXを再帰収集し、各FBXのmaterial mapを混ぜずに保持する。
- `PrefabInstance.m_TransformParent` とsource objectを辿り、追加FBXを対応する親boneへ配置する。
- Merge Armatureは同じsource/target名でもcomponentごとに適用する。
  descendantに配置済みのsourceも候補から除外しない。
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
- authored FBX meshのコピーはprefab GUIDとGameObject fileIDから実Slotを保持し、同名でも
  materialと初期blendshapeを個別に適用する。識別子を持たない従来のrendererは、空のoverrideも
  含めて出現順に1対1で消費する。
- コピーとその親のactive stateもobject単位で保持し、従来の名前照合による非アクティブ化を重ねない。
- 同名のauthored rendererの一方がEditorOnlyでも、残るobjectのmodel/nameをkeep-listに残す。
  除外objectのmaterialと外側overrideは取り込まず、残るrendererへ流用しない。
- outer variant自身の変更を読むときは、descriptorの親sceneではなく選択候補のsourceを再読込する。

## ブレンドシェイプ

Resoniteは空または微小なshapeを除去するため、Unityのindex参照がずれる場合がある。

- FBX内の元shape順を記録する。
- blinkやprefab weightが数値参照する最大indexまで、欠落shapeを空frameとして復元する。
- 名前参照だけのrendererは大量の空frameを復元しない。
- FBX `BlendShapeChannel.DeformPercent` をモデルprefabの既定weightとして読み込む。
- 明示的なprefab `m_BlendShapeWeights` は0を含めて既定値より優先する。
- 初期weightはblendshape修復直後と最終scene setupの両方で適用する。

VRChatの15 visemeはResonite enumへ対応させ、Unityの0〜100をResoniteの0〜1へ変換する。
瞬きは `eyelidsBlendshapes[0]` だけを使い、LookingUp / LookingDownはblinkとして扱わない。

## PhysBone

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
