# Unity・変換前・変換後の階層比較

骨やスロットの増加を調べるときは、UnityとResoPonの**変換前解析が一致しただけで修正完了にしない**。
同じ入力・選択アバターを変換し、保存済み `.resonitepackage` から階層・骨接続を採取して比較する。
以下は開発用ツール。配布GUIの通常変換には影響せず、採取JSONや実アバターを配布物に含めない。

視点位置の調査は [DescriptorとHead Proxy／Targetの比較](avatar-viewpoint-tests.md) を使う。
Unityの設定値と、骨格で座標系を揃えた保存位置を別々に判定する。

## 一括実行

[Unity参照データ](unity-reference-tests.md)で作った解決済みv2の `coverage.json` を使う。
Python 3.11以降、.NET 11 SDK、インストール済みResoniteが必要。Unityの再起動は不要。

```powershell
python -B scripts/check-converted-avatar.py .local/unity-resolved/<collection>/coverage.json `
  --case omaneko-110 --output .tmp_verify/structure-check/run-001
```

出力先は `.tmp_verify/` 以下の新規ディレクトリ。入力・期待値のSHA-256、選択Prefab GUID、
Prefab入力の依存ファイルと設定を検査する。既存の期待値やローカル登録一覧を上書きしない。
coverageの `conversionArguments` は `[]`（本番の既定選択）または `['--avatar', '名前']` が必須。
`check-prefab-unity.py` の新しい採取結果には選択引数が入る。項目のない旧Prefab基準は再採取する。
アーカイブ外の依存を補完したUnity基準は、通常変換と入力が異なるためこのrunnerでは拒否する。
その場合は依存が揃ったプロジェクト内Prefabで再採取する。

実行内容:

1. 現行ソースから解析・採取ツールをビルドする。
2. 本番と同じアバター選択を検証し、変換前解析を再採取してUnity基準と比較する。
3. 同じ入力を変換し、保存パッケージを `--inspect` で再読込する。
4. パッケージの保存値から構造を採取し、三段階の比較レポートを出す。
5. 入力・期待値・監視対象依存のハッシュを再照合する。

| 出力 | 用途 |
| --- | --- |
| `report.json` | コマンド、ログ、コード・DLL・入力のハッシュ、各工程の成否 |
| `parser.json` / `parser-comparison.json` | 変換前のv2解析とUnityとの差分 |
| `package/*.resonitepackage` / `inspect.log` | 実変換結果と保存形式の再読込確認 |
| `converted.json` | 全スロット・親子構造・姿勢・骨参照の保存値 |
| `converted-tree.tsv` | 表計算等で読めるID・親・有効状態・名前パス・コンポーネント型一覧 |
| `comparison.json` | 階層の対応候補、骨接続の差分、未対応スロットとその参照元 |

変換成功・解析一致・採取成功・構造比較は別の状態として記録する。解析不一致でも変換と採取を続け、
入力不整合では停止する。終了コードは、比較対象範囲の一致0、要調査・工程失敗1、事前条件不備2。
`captured` や `inspect=passed` は階層の正しさを保証しない。runnerはUnity基準を再採取しない。

## スキンの巨大化・縮小を検出する

Unityの解決済み解析には各SkinnedMeshRendererの `skinSizes` が含まれる。
Unityと保存パッケージの両方で、元頂点にbind pose・各骨のワールド行列・ウェイトを適用し、
初期骨格で変形させた頂点の外接箱の幅・高さ・奥行きをメートルで記録する。
非アクティブな衣装も対象にする。比較を骨格の影響に限定するため、両側ともモーフ適用前を測る。
保存側はアーカイブ内のStaticMeshをデコードするため、エンジンの起動は不要。

通常の `comparison.json` の `summary.skinScale` と各Rendererの `skinScale` で判定を確認する。
向き補正により外接箱の各辺は変わるが、点群の直径は常に最長辺以上、対角線以下である。
一方の最長辺が他方の対角線の2倍を1 mm超えて上回る場合だけ `expanded`／`shrunk` とし、
`differences` に `skin-scale` を追加する。`no-scale-outlier` はこの閾値での判定で、
小さな形状差、位置だけのずれ、モーフや動作中の破綻は検出対象外。
旧基準の寸法欠落、ウェイト付き骨の欠落、比較先が曖昧なケースは `unverified` にする。
旧Unityデータには `scripts/check-prefab-unity.py` での再採取が必要。

MANUKA DR_05 Variant 1では、修正前の下着2件で巨大化を検出し、修正後はどちらも閾値内となった。
変換前解析を作成できない場合も、同じ入力・選択アバターであることを確認したUnity基準と
保存構造を次のように直接比較できる。この場合の `parserParity` は `unverified` となる。

```powershell
python -B scripts/compare-converted-structure.py unity.json converted.json --report comparison.json
```

## 保存済みパッケージから採取する

Unity の新しい v2 採取には `lipSync` が入る。Descriptor の方式、参照 Renderer、宣言した
shape 配列を記録する。パラメーター方式では独立したプレビュー階層で FX に音素0〜14を入力し、
Voice=1 で1秒評価したとき、その音素だけで100になる shape を測る。既定状態で反応がなく、
音素レイヤーの Any State に単一の真偽スイッチがある場合は、その反対値も測定して条件を残す。
これらは指定条件での割当であり、動作中の音量曲線や表情の全組合せは測定しない。

保存側は `PackageRegressionDump --structure` の `visemeDrivers` に、有効状態、Analyzer の接続先、
各音素のフィールド参照、その所有 Renderer、ウェイト添字、埋め込みメッシュの shape 名を保存する。
上記の一括実行または `compare-converted-structure.py` が `comparison.json` の `visemes` に
一致数・欠落・余分な割当を出す。名前が同じ表情制御用 Slot もあるため、対象 Renderer を
一意に識別できないときや、旧データに採取項目がないときは未検証になる。

口形を全件照合するときは、次の回帰ケースを区別する。

- GameVketChan は FBX インポーターが省略する `Silence` を含む。復元したチャンネル名と
  保存後のドライバーの接続先をセットで確認する。
- アイザラ、Omaneko、Tolass は複数の音素に同じ shape を指定する。期待値は
  Renderer・音素・shape の組で数える。ユニークな shape 数だけを比較すると欠落を見逃す。
  保存データでは `field` が音素の直結先、`weightField` が最大値を合成した後のメッシュの接続先。
  `PrefabSceneSmoke --viseme-package <package> <expectations.json>` はヘッドレスエンジンに
  パッケージを読み戻し、ドライバー出力に個別・同時入力・ゼロを与えて各 shape の応答を確認する。
  期待値は Unity 等から得た音素名と shape 名の JSON オブジェクトで指定する。
- Ricorine は Descriptor の宣言配列が空でも FX を評価すると15音素が得られる。
  Bel Albino は `MouthMode=2`、`CN_MOUTH_MORPH_CANCEL_ENABLE=true`、`Voice=1` で
  5母音の独立した応答を確認できる。接続の比較には FX で観測した対応を優先し、残りの音素は
  Descriptor の指定先 Renderer に存在する宣言名で照合する。Bel Albino の接続期待数は15。
  レポートの `declaredFallbacks` は宣言から補った数で、FX の測定条件と結果は別に記録する。
- VRM 0.x は入力の母音プリセットから対象メッシュとモーフ添字を引き、保存先の Renderer・
  shape 名と比較する。AvatarSample_C は5母音が比較対象になる。

VRMのテストアバターを登録するときは、元の入力と同じSHA-256のVRMを隔離プロジェクトでUniVRMの
該当世代のインポーターに読み込ませる。VRM 0.xでは、生成Prefabの `VRMBlendShapeProxy` から
各母音クリップの `RelativePath` と `Index` を取り、対象の `SkinnedMeshRenderer.sharedMesh` で
shape名を引く。保存側でも実際のメッシュ名・shape名とドライバーの接続先を照合する。
AvatarSample_C.v0ではUniVRM 0.128.3の顔が57 shape、保存メッシュが55 shapeで、5母音の
添字が2ずつずれる。5母音の名前・接続先・発話後のリセットは個別に検査できる。
元のshape列の欠落と、口パクが意図したshapeに届くかを別々に記録する。

FBXのshape名を検証するときは、Unityの選択Rendererと、対応する保存メッシュ（埋め込みMeshX）の
名前を添字ごとに比較する。同名の表情制御用Slotを件数に含めない。

#### Lyphos 1.10 Ex・Fa の Body

Ex・Faは同じFBXの同じメッシュ（fileID `4300044`）を参照する。Unityで採取した111件の名前は
すべて元FBXの `BlendShapeChannel` 名と一致した。一方、このBodyをResonite付属のAssimpで
読み込むと、マテリアル別の3つのメッシュすべてで、111件のattachment名はチャンネルに接続された
Shape Geometryの名前と一致する。たとえば `mouth.smile1` と `sample.smile1` のGeometry名はどちらも `smile1`。

Resoniteの `ModelImporter.ImportMesh` はattachment名でMeshXにshapeを追加し、同名があれば
空くまで末尾に `_` を付ける。従来は重複する3種類のGeometry名に対して元のチャンネルを
選べず、次の8件に短い名前が残った。

| Bodyの添字（0始まり） | Unityと修正後の保存MeshX | 修正前の保存MeshX |
|---|---|---|
| 14, 104 | `mouth.smile1`, `sample.smile1` | `smile1`, `smile1_` |
| 15, 105 | `mouth.smile2`, `sample.smile2` | `smile2`, `smile2_` |
| 57, 85, 89, 97 | `eye.hide`, `brow.hide`, `tongre.hide`, `nose.hide` | `hide`, `hide_`, `hide__`, `hide___` |

現在は、同一Renderer内でGeometry名が重複する場合、FBXの接続から各チャンネルが専有する
Shape GeometryのオブジェクトIDを取得する。全チャンネルが一意の名前と1対1の接続を持つ場合、
解析と変換に使う一時FBXで、該当Geometryに接続先のチャンネル名を付けてからAssimpに渡す。
共有Geometry、複数フレーム、同名チャンネルなどでこの条件を満たさない場合は従来の別名解決を使う。

Ex・Faとも保存メッシュの名前と添字はUnityと111件すべて一致し、各添字のフレームの頂点変位も
修正前の保存パッケージと一致した。Faの `vrc.Dummy3` の初期ウェイトは従来からUnityの0%に対して
保存側が100%で、両手Neutralで選ばれるクリップの先頭キーが1を指定する。
起動時の表情を含む保存値とPrefabの静止値を比較したときは、この違いをウェイト差として記録する。

チャンネル順の回帰検査では、Unity採取結果の各Rendererの `blendShapes` と、保存パッケージの
対応するStaticMeshを復号した名前配列およびRendererの `BlendShapeWeights` を同じ添字で照合する。
名前・順序・要素数は完全一致とし、ウェイトは `abs(Unity値 / 100 - 保存値) > 1e-5` を差とする。
元のチャンネルが保持されたかは、名前で対応させて全frameのウェイトと全頂点の位置変位を
修正前後で比較できる。Rendererの対応や参照先のメッシュを確かめられない場合は未検証として残す。

登録全件の変換を `scripts/convert-all-local-avatars.py` で完了した後は、
その実行の `report.json` と隣接する凍結済み `registry.json` を使って一括採取できる。

```powershell
dotnet build tests/PackageRegressionDump -c Release -o .tmp_verify/structure-tools
python -B scripts/check-converted-batch.py .tmp_verify/all-avatar-conversions/<run>/report.json `
  --dump .tmp_verify/structure-tools/PackageRegressionDump.dll `
  --output .tmp_verify/structure-review/<new-run>
```

全登録ケースの存在、入力・パッケージ・Unity基準・解析JSON・coverageのハッシュを確認し、
Prefab入力は採取時のプロジェクト依存ファイルも照合する。変換・inspect済みの全パッケージから
構造を採取し、Unity基準のないケースも骨参照の `null / unresolved` を記録する。
ケース別の `converted.json`、比較可能なら `comparison.json`、全件の `report.json` を出力する。
既存結果や入力は更新せず、実行DLLと変換時commitの識別情報を引き継ぐ。

Unity/parserが外部プロジェクト依存を補って解析したアーカイブは、通常変換のアーカイブ単独入力と
同一とは扱えない。この場合は `not-compared` にする。古いPrefab基準に依存ハッシュがない場合は、
`scripts/check-prefab-unity.py` で再採取し、出力されたcoverageで `check-converted-avatar.py` を再実行する。
元のバッチ結果は上書きしない。
終了コード0は比較対象範囲の一致、1は要調査・未比較・失敗を含む結果。

全件の判定では、変換成功数・同一入力での解析一致数・保存構造の比較対象数を別々に集計する。
次の差分は修正優先度を決めるため追加確認する。

- `activeSelf` は対象の完全なパスと、再読込後の値・driver有無を確認する。
  Hinaの `FY_Tulip` / `Fyuett_afk_object` のような、Prefabで無効な小物が有効になる事例を
  rendererの読み込み待ちEnabled差と混同しない。元FBX名だけの非アクティブ指定では、
  改名・複数インスタンス・別FBX由来の配置を区別できるか確認が必要。
- `sibling-order` は同じ親の対応済み子同士の順を確認する。Prefabの `m_Children` を
  復元しても、インポートしたFBX内部の兄弟順まで一致するとは限らない。
- `renderers[].boneTargets` は、Unityの `Mesh.GetAllBoneWeights()` と保存したMeshXの頂点から、
  ウェイトが **0より大きい** 骨番号をそれぞれ列挙する。Unity側は同じMeshを所有する元モデルの
  骨名、保存側はMeshXの骨名で、各表内で一意な項目を対応させる。その後、Prefabインスタンスで
  実際に指定されたTransformと保存Rendererの参照先Slotを比較する。Prefabで骨を差し替えた場合も
  元モデルの骨名とインスタンスの参照先を区別する。配列長・同じ添字での不一致数は診断情報に残す。
  使用骨の対応がすべて一致すれば、並べ替え・未使用骨の増減は差分にならない。
- `bone-target` は使用骨の参照先の変更、または保存側で使用する骨参照の欠落・null・未解決を表す。
  `missing-weighted-bone` と `extra-weighted-bone` は、一方で使用される参照先が反対側の使用骨に
  存在しないことを表す。保存側の該当indexが分かる場合は、その骨の頂点ウェイトの最大値も記録する。
  元モデルの骨名が重複・不明、Transformが未対応、旧スナップショットに使用骨の記録がない場合など、
  証明できない対応は `unverified` に残す。全null参照は `unresolvedOrNullBones` で調査できる。
  ここでの一致は骨参照の一致を示す。頂点ごとのウェイト配分とbind pose、実際に動かした際の変形は別途確認する。
- Yuzukiの `tail-dog` はPrefabの骨参照が174本、現行FBXの骨表が183本で、Unityでは使用骨の
  `tail-dog_02` が `tail-dog_11` を指す。ResoPonの `ResolveShortenedBoneArray` は同一FBXの
  オブジェクトID・一意なパス・省略骨が未使用であることを確認する。Prefab内にある添字では
  Unityの参照先を保存する。174以降はUnityに比較できる参照がないため、FBX由来の復元を
  行っても比較結果は未検証とする。
- `Renderer.Enabled` はResoPonのメッシュ読み込み制御が駆動するため、Unity解析と差分比較の対象から除く。
  読み込み中・完了後の表示切替はResoPonの動作試験で確認する。

```powershell
dotnet build tests/PackageRegressionDump -c Release -o .tmp_verify/structure-tools
dotnet .tmp_verify/structure-tools/PackageRegressionDump.dll --structure `
  avatar.resonitepackage .tmp_verify/structure-check/converted.json
python -B scripts/compare-converted-structure.py unity.json parser.json `
  .tmp_verify/structure-check/converted.json --report .tmp_verify/structure-check/comparison.json
```

親ディレクトリは事前に作成し、JSONには新しいファイル名を指定する。
`--structure` なしの既存 `resopon.package-snapshot` v1形式は従来どおり。
新形式は `resopon.converted-structure` v1。エンジンを起動せずRecordPackage/DataTreeを読む。
単独比較は渡されたJSONを比較するだけなので、元入力と変換パッケージの同一性の確認は一括runnerを使う。

採取内容:

- 非アクティブを含む全スロットのID、親ID、兄弟index、名前パス配列、tag、有効状態。
- ローカル位置・回転（xyzw）・スケール。Resoniteの保存座標系をそのまま記録する。
- コンポーネント型とEnabledの保存値、SkinnedMeshRendererの順序付きBones・BlendShapeWeights。
- BipedRigの部位名からスロットへの参照。
- 各コンポーネントの保存フィールドにあるスロット参照候補（フィールドパス付き）。
  DynamicBone等の参照元調査にも使える。シリアライズされた値がスロットIDと一致するものを列挙するため、
  同じID文字列を持つ文字列フィールドと参照型の区別までは行わない。

スロットIDは `r/兄弟index/...`。同名スロットを区別するが、階層挿入で変わる。
`serializedId` は保存データ内の参照解決用で、別変換との照合キーにはしない。
骨参照は `resolved / null / unresolved` を区別し、順序、重複、nullを消さない。
骨かどうかは名前で推定せず、スキンまたはrigの参照で判断する。

## 比較結果から修正する手順

1. `parserParity` が不一致なら、先にUnityと変換前の `parser-comparison.json` を確認する。
   元のUnity基準を保持し、Prefab合成・FBX解析など原因の工程を修正する。
2. `transforms` の対応候補と `unmappedSlots` を確認する。完全なルート相対名前パスを
   変換後パスの末尾と照合する。変換用ラッパーの挿入を許容するが、同名の候補が複数なら
   `ambiguous` とし、自動選択しない。名前中の `/` は区切らず親IDからパスを復元する。
   Avatar Settings の Dynamic Bone Settings に生成する無効なテンプレートチェーンとその子は、
   自動対応の候補から外す。Legnia 4P では、Unity の `EditorOnly` の衣装と物理設定テンプレートが
   同じ `skirt` という名前を持つ。対応先が見つからない衣装は未対応として残す。
3. `unmappedSubtrees` で連続した未対応部分木を数え、`referencedBy` で利用箇所を追う。
   `sameNameSourceIds` は調査用で、対応判定には使わない。表情・装着・素材用の生成スロットも
   含まれる。参照がないことだけを削除理由にしない。
4. 曖昧な対応を入力・変換処理から確認できた場合だけ、明示的な対応表を作る。
   ファイルには `unitySha256`、`packageSha256`、`transforms`（Unity Transform ID→変換後Slot ID）を
   記録し、比較CLIへ `--mapping mapping.json` を渡す。ハッシュが変わった対応表は拒否する。
   多対一指定は認めず、自動候補との衝突も曖昧として残す。ルートは自動対応しない。
   単体runnerにも `--mapping FILE` を指定できる。初回の生成物を見て対応を確認したら、
   同じcoverage・ケースに `--review-run 初回の出力ディレクトリ --mapping FILE` を追加し、
   `--output` に新しいディレクトリを指定する。元の変換・inspect・採取の完了と入力・パッケージ・
   比較用JSONのハッシュを検査し、そのパッケージを再ビルドや再変換なしで照合する。
   全件runnerは `--mapping ケースID FILE` を必要なケースごとに指定する。
5. 対応候補上の有効状態、対応した祖先、同型Renderer、頂点が使用する骨の参照先を比較する。
   対応先ごとにMeshRendererとSkinnedMeshRendererの数を双方向に照合し、増減を
   `renderer-count` として記録する。骨の個別照合は同型Rendererが両側で一つずつの場合に行う。
   `siblingOrders` は元の兄弟同士の相対順序を比較する。補助階層を経由したSlotの位置も考慮し、
   未対応の親・子や親関係の不一致がある群は `unverified` にする。差は `sibling-order` として
   報告する。生成補助Slotを含む絶対indexの一致や、skinのBones配列順とは区別する。
   UnityのRendererに対応するSlotで件数が0なら、その子のFBX階層も調べる。たとえば
   KipfelのFishToyでは同名の子、HinaのFY_TulipではRootNode配下の同名の子にRendererがある。
   元メッシュと配置の対応が確認できるまでは、同名の子を自動的に一致へ変更しない。
   `differences` と `unverified` を分け、未対応骨をnull一致と見なさない。
   bone数・順序の差にはインポータの未使用骨除去やスキン再構成もあり、差分だけで不具合とは断定しない。
6. 原因の変換工程を修正し、自作fixtureまたは明示したパッケージ期待条件を回帰検査に追加する。
   新しい出力ディレクトリで一括runnerを再実行し、修正前後の報告を保持する。
   スキン変更は[保存スキンの追従試験](local-avatar-tests.md)、外観と装着はクライアントで確認する。

完全に一意な名前対応も出典を証明するものではなく `unique-path-candidate` と表記する。
Modular Avatarで移動した子が、削除された別の同名オブジェクトの元pathを占める場合もある。
Legniaの `PBcollider_chest` はこの判別が必要で、元の無効な本体側オブジェクトと、髪側の
有効なcolliderを同じものと見なさない。元Prefab occurrence/Transform ID、除外指定、
配置・Merge Armatureの情報で出典を確認し、候補pathだけで有効状態を戻さない。
変換後のTRSは階層・単位・座標軸が異なるため、そのままUnityと数値一致判定しない。
RootBone相当、bind pose、頂点ウェイト、物理の挙動、実行時のEnabledを駆動するリンクも別検証。
補助階層を含む実アバターでは `needs-review` が通常であり、これはダンプ実行失敗とは別。
`matched-covered-fields` でもこれら未比較範囲が検証済みになるわけではない。

### 子スロットに移ったRendererと三角形の照合

対応スロットでUnityのRendererが1個、保存側が0個なら、子孫にある未対応の同型Rendererと
そのメッシュ参照を調べる。KipfelのFishToy、FyuettのFY_Tulip、EkuのTakt、
Aizaraのベルト部品6個、Nagiya RuriのPokk左右では、同名の子孫に実体のあるメッシュがあり、
階層上の有効状態も一致した。Takt配下には別の描画対象もあるため、子孫の総数だけでは照合できない。

形状を調べるには、入力と同梱のFBX・metaをUnityに読み込み、Rendererが参照するGUIDと
メッシュのlocal fileIDから頂点と三角形を取得する。保存側は該当Rendererのメッシュ参照を
パッケージ内のMeshXまでたどる。軸の順列・符号、一様スケール、平行移動をそろえて、
使用頂点の位置を一対一に対応付ける。上記11件はメッシュの最長軸寸法の `1e-5` 以内で
全使用位置が対応し、三角形数も一致した。面は頂点順を無視した位置3点の組を重複込みで比較する。
この方法ではワールド配置、面の表裏、UV・法線の継ぎ目、マテリアルは別途検証が必要になる。

11件中9件は三角形の組み合わせが異なった。四角形の対角線が異なるときは、4点のうち
面積最大の三角形を取り、残る1点からその平面までの距離を測る。最長軸寸法の `1e-5` を
超えた四角形はFY_Tulipで1,585個、FishToyで96個、Aizaraのmag_beltで277個、
mag_pouchで82個、mag_A (1)で14個あった。これらは頂点位置が同じでも面の形状が変わる。
一方、Taktで異なる44三角形のうち4枚は平面とみなせる四角形2個の対角線違いで、
残り40枚は外周と頂点が一致する平面領域3個（14枚・12枚・14枚）の内側の分割が異なる。
Pokk左右では三角形の組み合わせも一致した。

全Rendererを調べるときは、Unityの各Rendererを1行として、同じメッシュを参照する複数の
インスタンスも個別に残す。Aizaraのナイフのように、同名部品の前にFBXのRootNodeと
モデルルートが挿入されるケースでは、鞘・ナイフを区別する親パスも使って対応付ける。
対応先がない場合はUnity側のメッシュ参照、階層上の有効状態、EditorOnlyタグを確認する。
Yuzukiの2個のmerge用Rendererはメッシュ参照がnull、LilLeoの有効なBody_Base (naked)は
EditorOnlyだった。

三角形数が異なる場合は、面積ゼロの三角形を除いた枚数も記録する。たとえばAlconiaの
gogglesはUnityが3,918枚、保存側が4,120枚で、差の202枚は退化面の枚数差と一致する。
面積がある三角形の枚数がそろっても、対角線や頂点の組み合わせは個別に照合する。
近い頂点が複数ある場合は最近傍の衝突を記録し、許容距離内の候補から一対一対応を
作れるかを調べる。対応が曖昧な位置をまとめても残る面の差は、その頂点の選び方だけでは説明できない。

### Omanekoでの調査上の注意

FBXから読み込んだ骨格下の元メッシュスロット、Prefabから再構築したrendererスロット、
表情制御内の項目に同名が現れる。rendererを取り除いた元スロットが残る場合もあるので、
名前別の件数だけでは重複描画や余分な骨格を判定できない。骨格自体がスキン・rig・物理の
どこから参照されているかと、現在のrendererコンポーネントの有無を合わせて確認する。
実入力パス、採取結果、修正前後の件数は `.local/avatar-tests.json` とローカル結果に記録する。

Omanekoの余分な骨格には二つの原因があった。ウェイトを持たないFBX末端をPrefab由来の
新規helperとして重ねて生成する経路と、Unityで髪・耳の親を変更した後も元FBXの未使用の枝を
残す経路である。前者は完全な元pathを検証して再利用し、後者は実参照とPrefab上の存在を
保護したうえで骨格参照用templateに限って削除する。単なるSlot数削減を期待条件にしない。

眼球PivotやAligned Handが挿入された部分は名前pathだけの自動照合では未対応になる。
EyeRotationDriverのRoot、VRIKのhand参照などから補助Slotの用途を確認し、元Transformへの
対応表を作る。補助Slot自体を元の親へ多対一対応させない。元骨の一意な対応・祖先関係、
順序付きskin bone接続、Unityにある末端の欠落と重複を確認する。
補助階層・外観・物理などの未検証項目は報告に残す。

未使用のラッパーを畳んだ出力では、元Prefabルートの対応先が残る親Slotへ変わり得る。
古い対応表を流用せず新しいパッケージに対して採取し直す。子のワールド基底、スキンの
renderer相対基底・bind pose、視点、Root追従、兄弟順を確認する。元ルートのTRS自体は
子へ合成するため数値一致を求めない。`CenteredRoot` のIK参照とcolliderは保持する。

## ツールの回帰検証

```powershell
dotnet .tmp_verify/structure-tools/PackageRegressionDump.dll --check-structure .tmp_verify/structure-check/synthetic.json
python -B -m unittest discover -s tests/ConvertedStructureComparison -v
python -B -m unittest discover -s tests/PackageRegressionComparison -v
```

自作階層で同名の曖昧性、骨配列の順序、null・未解決参照、入力不整合、対応表の期限、
既存レポートの保護を確認する。実DLLの保存形式は実変換後の採取・再読込でも検証する。
