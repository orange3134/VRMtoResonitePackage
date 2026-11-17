# Unityの解決結果を使う回帰テスト

既定の `resolved` モードでは、UnityがPrefab Variant・ネストPrefab・FBX由来の階層を
解決したEdit Modeの初期状態を回帰テスト用に採取する。元PrefabのUnpackや再保存は不要。
プレビューシーンへの実体化で取得する。配布版ResoPonや通常の変換にUnityは不要。

**Unityの期待値を採取できたことと、ResoPonの解析が一致したことは別に記録する。**

| モード | 形式と対象 | 比較先 |
| --- | --- | --- |
| `resolved`（既定） | schemaVersion 2 / `unity-resolved-prefab`。継承解決後の対応フィールド | Unityの再採取JSON、またはResoPonのv2ダンプ（下記の制約あり） |
| `authored` | schemaVersion 1 / `selected-prefab-authored-components`。通常Prefab自身の対応コンポーネント | `tests/UnityReferenceDump` のResoPon解析JSON |

## 解決済みデータの採取（v2）

アバターと同梱依存物の利用条件を確認し、VRChat SDK・シェーダー等を揃えた信頼できる
プロジェクトを使う。導入済みの依存物を旧版で上書きしない。
[tools/unity/Editor](../tools/unity/Editor/) の3つの `.cs` を `Assets/Editor/` にコピーする。
`ResoPonReferenceExporter.cs` に加え `ResoPonResolvedReference.cs` が必要で、
一括採取には `ResoPonResolvedReferenceBatch.cs` も必要。

同じプロジェクトを別のEditorで開いていない状態で実行する。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe'
$project = 'C:\work\trusted-avatar-project'
$env:RESOPON_REFERENCE_PREFAB = 'Assets/Example/Avatar Variant.prefab'
$env:RESOPON_REFERENCE_OUT = 'C:\work\private-tests\unity-resolved.json'
$env:RESOPON_REFERENCE_PROJECTION = 'resolved'
$process = Start-Process -FilePath $unity -ArgumentList '-batchmode', '-nographics', '-projectPath', ('"' + $project + '"'), '-executeMethod', 'ResoPonReferenceExporter.Run', '-logFile', '-' -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Unity reference capture failed: $($process.ExitCode)" }
```

同じ環境変数を継承したEditorの **Tools → ResoPon → Export Unity Reference** でも採取できる。
`RESOPON_REFERENCE_PROJECTION` の省略時も `resolved`。既存JSONは上書きしない。
Missing script・階層外への参照・対応コンポーネントがゼロ件の場合は失敗する。
Missing scriptを削除して採取を通さず、依存不足として記録する。
Scene・VRMはこのPrefab採取の入力対象外。Variantというだけで採取対象から除外しない。

### 一括採取と再現性の確認

ローカルmanifestに登録ケースIDとプロジェクト内Prefabパスを書く。

```json
{"cases":[{"id":"example-variant","prefabPath":"Assets/Example/Avatar Variant.prefab"}]}
```

```powershell
$env:RESOPON_RESOLVED_MANIFEST = 'C:\work\private-tests\manifest.json'
$env:RESOPON_RESOLVED_OUT = 'C:\work\ResoPon\.local\unity-resolved\new-run'
$process = Start-Process -FilePath $unity -ArgumentList '-batchmode', '-nographics', '-projectPath', ('"' + $project + '"'), '-executeMethod', 'ResoPonResolvedReferenceBatch.Run', '-logFile', '-' -WindowStyle Hidden -Wait -PassThru
$process.ExitCode
```

出力先は存在しないディレクトリを指定する。各ケースを2回実体化し、`expected.json` と
`repeat.json`、`result.json` を保存する。2回のJSONのSHA-256、および採取前後のPrefab
SHA-256・Unity依存ハッシュが一致した場合だけ `captured` とする。ResoPonとの一致を意味しない。
失敗しても残りのケースを実行し、全体の `index.json` に記録する。全件成功は終了コード0、
1件以上の失敗は1。失敗ケースの未設定ハッシュやfalseフラグは変更の実測結果ではない。

```powershell
python scripts/compare-unity-reference.py expected.json repeat.json --report repeat-comparison.json
```

JSONの検査にはUnityは不要。別のUnity起動で再採取するときも新しい出力先を使い、保存した期待値と比較する。
Unity・SDK版、元入力のハッシュ、Git HEADと採取ツールのソースハッシュも保存する。
元パッケージとプロジェクト内PrefabのGUID・ハッシュを照合し、差し替え・再保存を記録する。
Unity依存ハッシュは採取前後の変化検出用であり、プロジェクトのバックアップではない。

登録一覧の `inputPath` と `arguments` は保持する。継続保存する期待値・ログ・索引は
`.local/unity-resolved/`、一時ビルド・展開物は `.tmp_verify/` に置く。
`.local/avatar-tests.json` の `unityResolvedCollection` に採取先と状態を追加し、
既存の `analysisCollection` の解析結果・v1照合結果とは分ける。未採取ケースには理由を残す。
実アバター・採取JSON・環境固有パス・結果はコミットしない。

追加採取時は前回の期待値を保持し、新しい採取ディレクトリの `coverage.json` に
前回分の参照先と今回分を統合する。`index.json` はその実行で試したケースだけを表すため、
全件の採取状況には使わない。`latest.json` から統合一覧を参照できるようにする。
集計では登録入力の拡張子を使い、unitypackage入力の総数・採取済み・失敗・未対応を分ける。
パッケージ内でSceneを選択しているケースも、unitypackage入力の総数には含める。

インポート直後に材質等のファイルが更新される場合がある。採取前後の依存ハッシュ一致だけでは、
元パッケージとの同一性は保証できない。導入するファイルの元SHA-256とインポート後のSHA-256を
記録し、Prefab本体の一致と、材質等の更新を区別する。既存のResoPon解析基準は元の登録入力で
再検査し、解決済みUnity JSONの再現性検査と別の結果として残す。

### 採取する範囲と識別子

非アクティブな階層を含め、次のフィールドを採取する。

- Transform: 名前・親・兄弟順、ローカル位置・回転・スケール、有効状態。
- MeshRenderer / SkinnedMeshRenderer: 有効状態、メッシュ・材質参照、rootBone、
  順序付きbones、BlendShapeのindex・名前・初期weight。
- VRCPhysBone / VRCPhysBoneCollider: 有効状態、root参照（未指定時は所有Transform）。
  PhysBoneは順序付きコライダー参照と `pull / spring / stiffness / gravity / gravityFalloff / immobile / radius`。
  Colliderの形状・寸法など、他の物理設定は未採取。

IDはルート `r` からの兄弟index列で階層を表し、コンポーネントには型名と同型内の序数を付ける。
同名オブジェクトや同じPrefabの複数インスタンスを区別し、一時的なUnity instance IDは保存しない。
兄弟の挿入・並べ替えはIDも変えるため差分になる。名前パスは表示用で照合キーではない。
直近のsourceとoriginalSourceのGUID・符号付き64bit fileID文字列も記録するが、
同じsourceを持つネストインスタンス同士をこの出典情報だけで同一視しない。

レコードの並びは比較に影響しないが、材質・bones・コライダー等の配列順は比較する。
重複キー・重複ID・不正な数値・未解決の内部参照はエラー。v1とv2を混ぜた比較もエラー。
Play Mode、Animator、VRChat・NDMFのビルド処理で初めて適用される挙動は対象外。
プレビューシーンでも `ExecuteAlways` 等は動作し得るため、信頼できるプロジェクトを使う。
材質の全プロパティや頂点データは採取しない。変換後のskin・物理挙動・外観・装着動作の検証には、
実変換・inspect・クライアント検証を併用する。

## ResoPon解析との直接比較（v2）

`tests/UnityReferenceDump` は期待値のバージョンでv1/v2を切り替える。期待値から読むのは
形式・バージョン・projection・選択Prefab GUIDだけであり、実値の階層・参照・数値は
本番の `UnityPrefabInstances` / `UnityPrefabGraph` とMeshメタデータから組み立てる。
Unity EditorやFrooxEngineを起動せずに実行できる。

```powershell
dotnet build tests/UnityReferenceDump -c Release -o .tmp_verify/unity-reference/bin
dotnet .tmp_verify/unity-reference/bin/UnityReferenceDump.dll input.unitypackage expected.json actual.json
python scripts/compare-unity-reference.py expected.json actual.json --report difference.json
```

現在のv2出力は、**YAMLとして階層とコンポーネントが存在する**通常Prefab・Variant・ネストPrefabに
対応する。削除、追加、同一Prefabの複数配置、兄弟順、参照の上書きを合成した後に比較する。
FBXをメッシュ資産として参照するRendererのBlendShape名は、本番のFBX fileID resolverから取得する。
FBX全体の走査でBlendShapeがないことを証明できれば、メッシュノードのpathが曖昧でも空配列を出せる。
走査失敗や形状を持つメッシュの曖昧なpathを、空配列として成功させない。

**FBXのPrefabInstanceから暗黙に生成されるTransform・Rendererのv2出力は未対応**。
これはUnity採取の制約でも、Variant全体の制約でもない。本番のPrefab graphが保持するYAMLと、
モデルインポート時に生成する階層を結ぶ出力処理が別途必要なため、ダンプは
`UNSUPPORTED_MODEL_HIERARCHY` で失敗する。未出力の階層を省略したJSONで一致判定しない。
この検証基盤の導入と、暗黙のモデル階層の出力・変換後パッケージとの比較は段階を分ける。

### 収集済みケースの一括再検証

```powershell
python scripts/test-unity-resolved.py .local/unity-resolved/collection/coverage.json `
  --reference-dump .tmp_verify/unity-reference/bin/UnityReferenceDump.dll `
  --output .local/unity-parity/new-run --work .tmp_verify/unity-parity/new-run
```

`--output` と `--work` は未使用のディレクトリを指定する。`--case ID` は複数指定できる。
期待値と元入力のSHA-256を確認してから実行し、終了後にも再確認する。unitypackageの展開物と
FBX一時ファイルは `--work` に置く。期待値・登録一覧を更新せず、実値JSON、producerログ、
フィールド単位の差分と `report.json` を `--output` に保存する。

結果は `matched`、`different`、`unsupported`、`missing-dependency`、`producer-failed`、`failed` に分ける。
Unity未採取の登録ケースは `not-captured` として別集計する。選択した採取済みケースがすべて
一致した場合だけ終了コード0、それ以外は1。未採取を含む登録全体の網羅を意味しない。
各結果は入力・期待値・実値のハッシュ、実行したダンプと本番DLL・比較ソースのハッシュを保持する。

### Unity由来の自作Variantによる統合検証

`ResoPonResolvedReferenceSmoke` が作る `variant.prefab` と `first.json` を使う。
fixtureは自作メッシュ・材質のみで、同名のネスト2個、非アクティブ階層、削除・追加、材質順序、
別インスタンスへのPhysBone参照を含む。Unity側でfixtureを作った後は、Unityを起動せず再実行できる。

```powershell
python scripts/test-unity-reference-producer.py path/to/variant.prefab path/to/first.json `
  --reference-dump .tmp_verify/unity-reference/bin/UnityReferenceDump.dll `
  --output .tmp_verify/unity-reference/producer-check
```

全項目一致に加え、期待値の数値を意図的に変えても実値JSONが変わらないこと、変更した期待値は
不一致になること、既存出力・期待値の上書き拒否、入力資産の不変を検査する。

### 再利用する回帰事例

- Variantで `m_AddedGameObjects` の `insertIndex: -1` が複数ある場合、配列に記録された順に
  末尾へ追加する。Prefab graphのscene列挙順で追加すると、継承元・ネスト側が先に現れ、
  追加した通常GameObjectと兄弟順が入れ替わる。
- UnityのPrefabルート名と、YAMLのルート `m_Name` が違う入力がある。
  Omanekoの例では、Prefabファイル名とUnity採取値にない末尾の ` 1` がYAMLとResoPon解析値に残る。
  全体の一致判定から名前だけを除外して隠さず、命名差として記録する。これだけでskinや物理の
  不具合とは判断しない。
- Unityプロジェクトで解決できても、元unitypackageに外部Prefabが同梱されない入力がある。
  12Bの例ではFaceEmo側のPrefab依存が不足する。Unity採取済みという理由で元入力単独の
  解決可能性を仮定せず、依存不足として残す。別プロジェクトから黙って依存を補完しない。
  FaceEmoを導入済みのプロジェクト内の12B Prefabを入力にすれば、この依存は解決できる。
  パッケージ単独の結果を保持し、プロジェクト入力・依存パッケージの版とハッシュを別に記録する。
  その場合もv2出力は12Bの暗黙のFBX階層で未対応になるため、依存解決や通常解析の正常終了を
  Unityとの全項目一致として扱わない。12B自体を検証対象から除外する必要はない。

## 従来のResoPon解析との照合（v1）

Unity EditorがPrefabを実体化したときの状態を期待値として採取し、
ResoPonが同じ入力のUnity YAMLを解決した結果と照合する。Unityが必要なのは
期待値を新規採取・更新するときだけ。配布版ResoPonや通常の変換にUnityは不要で、
診断ツールも配布EXEには含めない。採取後のJSON比較だけならPython 3で実行できる。
ResoPon側のJSONを生成し直すには、通常の開発ビルドと同じWindows、.NET 10、
Resoniteのインストールが必要だが、UnityやFrooxEngineの起動は不要。

1. 依存関係（VRChat SDK、必要なシェーダー・アセット等）を揃えた信頼できる
   Unityプロジェクトに、対象の `.unitypackage` をインポートする。
   `tools/unity/Editor/` の3つの `.cs` を
   プロジェクト内の `Assets/Editor/` にコピーする。
2. Unityを起動するシェルで、対象Prefab（`Assets/` からの相対パス）と出力先を指定する。
   次を実行するか、同じ環境変数を継承してEditorを起動し、Edit Modeで
   **Tools → ResoPon → Export Unity Reference** を選ぶ。出力は上書きしない。
3. 同一の保存済みPrefabと依存アセットからResoPon側のスナップショットを作り、比較する。
   入力にはそのUnityプロジェクトの `.prefab`、または同じGUIDのPrefabを含む
   `.unitypackage` を指定できる。パッケージ入力時も期待値内のGUIDで対象を特定する。

PowerShell例（パスは手元の環境に合わせて変更。Unityプロジェクトを別のEditorで開いていない状態で実行する）:

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe'
$project = 'C:\work\trusted-avatar-project'
$env:RESOPON_REFERENCE_PROJECTION = 'authored'
$env:RESOPON_REFERENCE_PREFAB = 'Assets/Example/Avatar.prefab'
$env:RESOPON_REFERENCE_OUT = 'C:\work\private-tests\unity-expected.json'
$process = Start-Process -FilePath $unity -ArgumentList '-batchmode', '-projectPath', ('"' + $project + '"'), '-executeMethod', 'ResoPonReferenceExporter.Run', '-logFile', '-' -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Unity reference capture failed: $($process.ExitCode)" }

$env:RESONITE_PATH = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite'
$actual = 'C:\work\private-tests\resopon-actual.json'
dotnet run --project tests/UnityReferenceDump -c Release -- 'C:\work\Example.unitypackage' $env:RESOPON_REFERENCE_OUT $actual
if ($LASTEXITCODE -ne 0) { throw "ResoPon snapshot failed: $LASTEXITCODE" }
python scripts/compare-unity-reference.py $env:RESOPON_REFERENCE_OUT $actual
```

入力に `.prefab` を使う場合は、Unityプロジェクト内の保存済みファイルを指定する。
期待値の採取後にUnity上でPrefabの上書きや依存の更新をした場合は、古いパッケージ
と比較せず、期待値と入力が同じ状態になるように揃える。出力JSONはどちらも
新しいファイル名を指定し、市販・非公開アバターのパッケージやJSONはコミットしない。

### 登録済みの実アバターから選んで検証する

`.local/avatar-tests.json` のケースを再利用し、元の `inputPath` と `arguments` を保持する。
通常の変換で成功しているアバターでも、このv1形式の対象範囲に入るとは限らない。
最初は Renderer・PhysBone・Collider をPrefab自身に持つ通常Prefabを選ぶ。
v1でのVariantの採取拒否は別に記録する。Variantの期待値収集には上記のv2を使う。

- Unityプロジェクトへの追加前に、アバターと同梱依存物の利用条件を確認する。
  既に導入済みのシェーダーやSDKを、アバター同梱の旧版で重複導入・上書きしない。
- 保存済みPrefabのGUID・SHA-256を元パッケージ内のPrefabと照合する。
  Unityによる再保存や依存の差し替えがある場合は、その差を記録する。
- 同じ期待値に対し、プロジェクト内 `.prefab` と元 `.unitypackage` の両方から
  別々のactual JSONを生成すると、両入力経路の参照解決を確認できる。
- 終了コードに加えて各sectionの件数と差分レポートを保存する。通常Prefabの中に
  ネストPrefabやFBXのインスタンスがあっても、対象外コンポーネントは採取されないため、
  アバター全体のコンポーネント数との一致は期待しない。
- 作業ツリー内のビルド出力・Unityログ・JSON・差分は `.tmp_verify/` に置く。
  `.local/` のケースには、UnityプロジェクトとPrefabのパス、Unity・SDKの版、
  検証したGit HEAD、各終了コード、結果へのパスを追記する。環境固有の結果はコミットしない。
  回帰テスト用に継続保存する期待値・ログ・索引は `.local/avatar-analysis/` に集約できる。
  再検査は[収集データの再利用](local-avatar-tests.md#収集した解析データを回帰テストで再利用する)を参照。

authoredモードが終了コード1でVariantを拒否した場合は、期待値JSONが作られていないことも確認する。
期待値を得るためだけに元PrefabをUnpackすると別の入力になるため、元アバターの一致検証には使わない。
Missing scriptで失敗した場合もコンポーネントを削除して採取を通さず、依存不足として記録する。
また、Unityやシェーダーのインポーターが材質を更新することがある。既存アセットは上書きせず、
元ファイルとプロジェクト内ファイルのハッシュを分けて残す。採取した参照フィールドが一致しても、
材質の全プロパティや見た目が一致したとは扱わない。

## 比較する範囲（v1）

- 選択した通常Prefab自体に記録された `MeshRenderer` / `SkinnedMeshRenderer`:
  有効状態、GameObjectの有効状態（自分と階層全体）、メッシュと材質スロットの参照。
- 選択したPrefab自体の `VRCPhysBone` / `VRCPhysBoneCollider`: 有効状態、階層の
  有効状態、root参照、PhysBoneのコライダー参照。rootが未指定なら所有Transformを記録する。
  これはUnity側の参照であり、ResoPonが出力時にコライダーを親ボーンへ付け替える処理とは別。
- 選択PrefabのGUIDと、符号付き64bit fileID（文字列）を使うため、同名GameObjectを
  同一視しない。材質とコライダーの配列順も比較する。未対応の階層を参照する場合や、
  比較できるコンポーネントがゼロ件の場合は失敗する。

Prefab Variant、ネストPrefabやFBX由来の暗黙のコンポーネントとそれらへの参照、
Play Mode・Animator・VRChatで初めて適用される挙動は、このv1比較の対象外。FBXを**メッシュ資産として
参照する**ことは対象範囲。Unityのプレビューシーンを使うが、Prefabに含まれる
`ExecuteAlways` 等のコードが動作し得るため、信頼できるプロジェクトに限る。

ResoPon側は本番と同じPrefab合成・YAML読み取りを使う。この比較はその**解析段階**の
差を検出するもので、Resonite向けに変換した後のskin、揺れものの係数、見た目や装着動作
までは保証しない。それらは既存の生成物inspectとクライアント検証を併用する。
比較ツールの終了コード・合成fixtureのテストは
[UnityReferenceComparison](../tests/UnityReferenceComparison/README.md) を参照。

## 出典

UnityからJSONを採取して照合する設計は [vrctom](https://github.com/halby24/vrctom/tree/983995e2372c30b07828b88644b971912ffb68a1/tools/unity) を参考にし、
エクスポーターと照合処理は独立実装した。詳細は
[ライセンス管理](third-party-licenses.md#unity参照データの採取ツール)を参照。
