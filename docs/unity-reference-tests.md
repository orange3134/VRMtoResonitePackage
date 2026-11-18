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

### プロジェクト内Prefabから採取と比較を一括実行する

`scripts/check-prefab-unity.py` に保存済みPrefabを渡すと、そのプロジェクトでUnity参照値を採取し、
同じ `.prefab` をResoPonの本番パーサで読み込んで比較する。Python 3.11以降・.NET 10 SDK・
Resonite、およびプロジェクトと同じ版のUnity Editorが必要。対象プロジェクトのEditorは閉じておく。
依存物と利用条件を確認済みの信頼できるプロジェクトで実行する。

```powershell
python scripts/check-prefab-unity.py "D:\Unity\Project\ResoPonTest\Assets\Unan\12B\12B.prefab" `
  --case-id 12b-100
```

Prefabからプロジェクトを特定し、`ProjectVersion.txt` と同じ版をUnity Hubの標準インストール先から探す。
別の場所にある場合は `--unity "D:\Unity\2022.3.22f1\Editor\Unity.exe"` で同じ版を指定する。
採取コマンドはプロジェクトを開く前に、選択した実行ファイルの製品バージョンを照合する。
`--reference-dump <UnityReferenceDump.dll>` を省略すると比較用ビルドを自動作成する。
`--output <new-directory>` を省略すると `.local/prefab-reference/<日時と識別子>/` に保存する。
出力先は新規ディレクトリに限り、Unityプロジェクト内には作成しない。一時ビルドは `.tmp_verify/` に置く。

採取スクリプトが未導入なら、その実行専用の `Assets/ResoPonReference_<識別子>/Editor/` に一時配置し、
終了後に自分が配置した未変更のファイルだけを除去する。同一内容の導入済みスクリプトは再利用する。
別版や重複がある場合は上書きせず停止するため、診断に示された既存スクリプトを明示的に更新する。
Unityは通常のimportとプロジェクト内のEditorコードを実行する。アセットの再保存は要求しないが、
インポーターによる更新まで禁止する機能ではない。

保存内容:

- `unity/<case-id>/expected.json` と `repeat.json`: Unityで2回採取した解決済みv2データ。
- `unity/coverage.json`: 同じPrefab入力で後から再比較できるmanifest。
- `parity/`: ResoPon解析JSON、項目ごとの差分、比較ログ。
- `report.json`: 採取状態と比較状態、Unity版、Prefabとmeta・プロジェクト設定の前後ハッシュ、
  Unityの採取前後の依存ハッシュ、採取コードのハッシュ。ビルド・Unityログも同じ出力先に保存する。

終了コードは一致0、差分または実行失敗1、引数・事前条件の不備2。
`captureStatus=captured` は採取成功だけを意味し、`parityStatus=matched` とは区別する。
比較中に入力や監視対象の設定ファイルが変わった場合は全体を失敗にする。
Unityの依存ハッシュは実体化の前後を検査するもので、Editor起動前の全依存アセットの不変性を保証しない。
登録一覧や過去の期待値は更新しない。必要なケースへ結果の参照先を別途記録する。
これは開発用の採取・解析比較コマンドで、配布GUIの通常変換や `.resonitepackage` の生成は実行しない。

### 登録済みのPrefabをまとめて検証する

同じコマンドの `--registry` にテストアバター一覧を渡すと、入力の拡張子が `.prefab` のケースを
自動選択する。`--case` で特定の登録IDだけを選択することもできる。

```powershell
python scripts/check-prefab-unity.py --registry .local/avatar-tests.json
python scripts/check-prefab-unity.py --registry .local/avatar-tests.json --case my-prefab
```

比較用ビルドは一度だけ作成し、同じUnityプロジェクトを同時に開かないよう順番に実行する。
一件が採取・解析に失敗しても残りを続行し、各ケースのログと `report.json` を保存する。
最上位の `report.json` は採取状態・比較状態・差分数・エラーを一覧化する。
全選択ケースが一致した場合だけ終了コード0、差分・実行失敗が一件でもあれば1、
未知のIDやPrefab以外の明示選択などの引数不備は2になる。
相対入力パスは登録ファイルのディレクトリを基準にする。登録一覧自体は変更しない。

最上位の `coverage.json` には採取できた期待値への参照と、採取できなかったケースの理由を記録する。
ResoPonの解析処理を修正した後は、このファイルでUnityを起動せず再比較できる。

```powershell
python scripts/test-unity-resolved.py .local/prefab-reference/<run>/coverage.json `
  --reference-dump .tmp_verify/my-build/UnityReferenceDump.dll `
  --output .local/prefab-parity/new-run --work .tmp_verify/prefab-parity/new-run
```

再比較は採取済みケースのみを判定し、未採取は `not-captured` として別集計する。
全登録ケースの検証完了を判断するときは、比較の終了コードだけでなく未採取件数も確認する。

個別Prefabの再比較用coverageは、採取時のPrefab・期待値のSHA-256と、終了時の入力・設定の
不変性を確認してから保存する。Unityが列挙した継承元Prefab、FBX、マテリアルなどの再帰的な依存ファイルと
各 `.meta` をSHA-256で記録し、ProjectVersion、package manifest・lockとともに再比較の前後に照合する。
パッケージ依存は実ファイルのパスで記録する。依存ファイルが変化した場合や、旧形式のPrefab採取に
依存一覧がない場合は `recapture-required` になる。同じPrefabを採取コマンドで再実行し、新しいcoverageを使う。
単一の `.unitypackage` を入力とする既存のcoverageは引き続きアーカイブのSHA-256で照合する。
アーカイブに含まれないPrefab等を採取元プロジェクトから補う診断では、coverageへ
`dependencyFiles`（絶対パス→SHA-256）を明示して保存する。再比較は `--dependencies` で
その一覧だけを渡し、アーカイブにないGUIDを補完する。元のGUIDを置換しない。
一覧の全ファイルを前後に照合し、変更時は `recapture-required` とする。
レポートの `dependencyScope` は補完を明記する。この一致は「アーカイブ＋採取時の依存」の
解析結果であり、アーカイブ単体の依存充足や通常の変換出力まで証明するものではない。
比較CLIの `--report` には新しいファイル名を指定する。

### シーン内アバターの採取

Prefabではなく `.unity` に選択対象がある場合は、バッチmanifestの `prefabPath` にシーンを、
`sceneRootPath` に対象のルートからの名前パスを指定する（例: `yuzuki`）。
個別採取では `RESOPON_REFERENCE_SCENE_ROOT` が同じ指定になる。
`resolved`・batch mode専用で、既に開いているシーン、存在しないパス、同名による曖昧なパスは拒否する。
シーンを加算で開き、指定した部分木だけを採取して保存せず閉じる。
シーン自身のオブジェクトのsource参照は空、ネストPrefab由来のsource参照はPrefabを指す。

再比較の入力は元の `.unitypackage` とし、coverageの `conversionArguments` に登録済みの
`--avatar` 指定を保持する。診断側は `--verify-selection` を必須とし、本番と共通の候補選択・
シーン配置の合成処理を通す。Yuzuki 2.1.1はこの経路で2回採取し、シーンのハッシュ不変と
採取結果の一致を確認した。FaceEmoのテンプレートPrefabはアーカイブ外にあるため、上記の
明示的な依存補完を用いた比較として記録する。

### Unityから個別に採取する

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

v2出力は通常Prefab・Variant・ネストPrefabと、**FBX由来の暗黙のTransform・Renderer**に対応する。
削除、追加、同一Prefabの複数配置、兄弟順、参照の上書きを合成した後に比較する。
FBXの階層・材質スロットは本番のfileID resolverと同じAssimp importから取得し、
姿勢は対応するバイナリFBXのProperties70から元の局所行列を復元する。未対応の形式・幾何変換では
従来のimport行列を使う。材質の外部参照と名前による検索は本番の材質マッピング処理を共有する。
skinの接続先はFBXのcluster接続から完全なパスとして取得する。同名のメッシュやボーンを名前だけで選ばない。
BlendShape名・初期ウェイトも既存の本番メタデータから取得する。ボーンのないmorph専用メッシュも対象とする。
Unityで生成された `.asset` のMeshは、YAML・バイナリともGUIDとfileIDを指定して本番の
`ReadAssetDocument` で取得する。バイナリのシーン一覧はMesh payloadを列挙しないため、
`ReadScene(...).Doc(...)` では代用できない。同一ファイル内の複数Meshと符号付きfileIDを区別し、
BlendShapeの名前・順序・初期ウェイトを比較する。存在しない参照やMesh以外の型はエラーにする。
FBX全体の走査でBlendShapeがないことを証明できれば、メッシュノードのpathが曖昧でも空配列を出せる。
走査失敗や形状を持つメッシュの曖昧なpathを、空配列として成功させない。

モデル内の補助ノードの姿勢を合成し、ModelImporterの単位・globalScale・fileID世代・既存ID表・
`sortHierarchyByName` を反映する。モデル内のroot表現は元Prefabの参照IDと照合し、
合成済みのプロパティ上書き・削除をモデルのレコードへ適用する。
これは解析段階の診断用投影であり、Resoniteへインポートした後の状態や全ModelImporter設定を保証しない。
最適化されたGameObject階層、参照がなくroot表現を決められないモデル、曖昧な同名skin接続などは失敗させる。
未出力の階層を省略して一致扱いにしない。変換後パッケージとの比較は別途必要である。

Quaternionは `q` と `-q` が同じ回転を表すため、v2の `localRotation` では符号反転も比較する。
それ以外の数値差に対する既定の許容差（絶対・相対とも `1e-6`）は維持する。
実アバターの局所的な姿勢の差を一律に丸めたり、許容差を広げて一致に変更したりしない。

### 収集済みケースの一括再検証

登録一覧のPrefab入力は、その元プロジェクトで直接採取できる。別のテストプロジェクトへ未導入でも、
元プロジェクトの依存関係が揃っていれば採取対象から除外しない。
LilLeo・ClonkaのVariantとNDMF Bake済みMANUKAで、Unity 2022.3.22f1による2回採取の一致、
Prefab・meta・監視対象のプロジェクト設定の前後ハッシュ一致を確認した。

この3件の比較結果には次の制約が残る。

- 空名のFBX BlendShapeChannelにAssimpが `SubDeformer::.` を付ける場合は、
  同じRendererで一意に接続されたShapeの名前と照合する。既存名との衝突や複数候補があれば元の名前を保持する。
- Clonkaはv2出力に成功するが、Transformの回転・スケール19成分に数値差がある。
- 生成 `.asset` のMeshで比較用 `ResolvedSnapshot` が `Unsupported native mesh metadata` となる
  問題は、上記のfileID指定による取得に修正した。本番変換の処理は変更しない。

上記の差を一致や無害とは扱わない。採取済み件数と比較成功件数を分け、元の登録入力と
過去の結果を保持したまま `prefabUnityCheck` と統合coverageへ最新の参照先を記録する。

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

網羅テストの `convert-all-local-avatars.py` はこの比較を各ケースの変換前に必ず実行する。
登録coverageのケースを複製して `conversionArguments` を付け、`UnityReferenceDump` の
`--verify-selection` で本番パーサの選択PrefabがUnity参照と一致することも確認する。
指定名がない場合も既定選択を検査し、別Prefabなら `selection-mismatch` で失敗する。
採取済み参照の更新や追加採取は行わず、未採取・再採取必要は網羅テストの未完了として報告する。
詳細は[全アバター変換の検証フロー](local-avatar-tests.md#登録アバターをすべて変換して一か所へ集約する)を参照。

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

FBXを含むfixtureは、既存のAssimpライブラリで自作三角形モデルを書き出してからUnityへ読み込む。

```powershell
dotnet .tmp_verify/unity-reference/bin/UnityReferenceDump.dll --write-fbx-fixture .tmp_verify/unity-reference/source.fbx
$env:RESOPON_FBX_SMOKE_SOURCE = 'C:\work\ResoPon\.tmp_verify\unity-reference\source.fbx'
$env:RESOPON_REFERENCE_SMOKE_OUT = 'C:\work\ResoPon\.tmp_verify\unity-reference\fbx-capture'
```

`tools/unity/Tests/ResoPonFbxReferenceSmoke.cs` を指定プロジェクトの `Assets/Editor/` に置き、
同じEditor起動手順で `-executeMethod ResoPonFbxReferenceSmoke.Run` を実行する。
日本語名・大小文字・アンダースコアを含むモデルを2個配置し、Variant、非アクティブ状態、
FBX RendererとGameObjectの削除、通常GameObjectの追加、別配置へのPhysBone参照を採取する。
出力の `prefab-path.txt` と `first.json` を上記のproducer統合チェックへ渡す。
モデル・採取結果はローカルに保持し、配布しない。

### 再利用する回帰事例

- FBX内の別枝にある同名Rendererが同じMesh subassetを共有する場合、MeshのfileIDから
  Rendererパスへの対応は一対多になる。v2生成では全パスを保持し、表情メタデータは
  Rendererの出典を使って選ぶ。`m_Mesh` が上書きされている場合は、元Rendererのパスを
  無条件で流用しない。Rendererの文脈がない参照は、候補すべての表情チャンネル名・順序が
  一致する場合に限り共有する。異なる候補を先勝ちで選ばない。
  `UnityReferenceDump --check-shared-mesh <新規ディレクトリ>` は自作FBXで、共有ID、
  別々のRenderer識別子、Rendererごとの表情ウェイト上書き、文脈付きパス解決を検証する。
- Variantで `m_AddedGameObjects` の `insertIndex: -1` が複数ある場合、配列に記録された順に
  末尾へ追加する。Prefab graphのscene列挙順で追加すると、継承元・ネスト側が先に現れ、
  追加した通常GameObjectと兄弟順が入れ替わる。
- UnityのPrefabルート名と、YAMLのルート `m_Name` が違う入力がある。
  Omanekoの例では、Prefabファイル名とUnity採取値にない末尾の ` 1` がYAMLとResoPon解析値に残る。
  Unityはアセットを読み込む段階でファイル名をルート名として公開する。v2出力も選択Prefabの
  ファイル名を使う。子オブジェクトの名前や上書きは維持し、名前比較は除外しない。
  自作VariantのYAMLに古いルート名を残す統合テストで確認する。
- Unityプロジェクトで解決できても、元unitypackageに外部Prefabが同梱されない入力がある。
  12Bの例ではFaceEmo側のPrefab依存が不足する。Unity採取済みという理由で元入力単独の
  解決可能性を仮定せず、依存不足として残す。別プロジェクトから黙って依存を補完しない。
  FaceEmoを導入済みのプロジェクト内の12B Prefabを入力にすれば、この依存は解決できる。
  パッケージ単独の結果を保持し、プロジェクト入力・依存パッケージの版とハッシュを別に記録する。
  プロジェクト入力ではFBX階層を含むv2出力が可能。依存解決や通常解析の正常終了を
  Unityとの全項目一致として扱わない。元の登録入力を置換せず、比較用manifestで入力種別を明記する。
- `sortHierarchyByName: 0` ではFBXの元順序を保持する。1では小文字化した名前のordinal順で
  比較する。`OrdinalIgnoreCase` は大文字を基準に比較するため、アンダースコアと英字の相対順が
  Unityと異なる。Marycia・Sanatiaの順序設定、Lina・Milfyのアンダースコアで検出できる。
- Siskaのように現在のMeshより長い `m_BlendShapeWeights` が残る場合、Unityは現在のMeshの
  チャンネル数までを公開する。余剰の保存値を新しいチャンネルとみなさない。
  Linaの `importBlendShapeDeformPercent: 0` は、元FBXの非ゼロ値を初期ウェイトへ取り込まない設定。
  Unityで同じFBXの別コピーの設定を切り替えると、Body・socksの初期値が0/100へ変わる。
  本番のfileID resolverもこの設定を反映し、チャンネル名・順序は維持する。
- 姿勢差は成分差に加えて位置距離・最短回転角・スケール差を集計する。回転角の診断では
  quaternionを正規化するが、一致判定の値や許容差は変えない。
  FyuettのPointesは回転した非均一スケール、SanatiaはQuaternionの一部成分だけの上書き、
  NagiyaはZ-upと負のCoordAxisSignを含むモデルであり、単なる丸め差ではない。
  行列をSystem.Numericsの行ベクトル形式へ転置し、軸・左右手系変換後に分解して投影する。
  q/-qが同じ回転でも、一部成分の上書き前には符号を任意に選べない。
- 12Bでは暗黙のFBX Transformを持つGameObjectへPhysBoneを追加している。
  `rootTransform: 0` の所有GameObjectに対応するTransformのYAMLがない場合も、FBX出典から
  所有Transformを解決する。v2で参照が一致しても本番解析や生成物で欠落し得るため、実変換・inspectも行う。

再採取で残差を調べるときは、旧期待値を上書きせず、新しい採取先で同じPrefabを2回取得する。
元入力・依存ハッシュ・Unity版と、旧採取値に対するバイト一致を確認してからResoPonと比較する。
Fyuett・Pilica・Sanatia・12B・Lina・NagiyaはUnity 2022.3.22f1で再採取しても旧期待値と
バイト一致し、微小な局所Transformの数値差が再現した。採取の揺らぎで消える差ではない。
当初、残差のある13件・181成分について `originalSource` のGUID/fileIDから元FBXのTransformを
Unityで直接読み直すと、すべてPrefab採取値と一致した。差はPrefab合成前のFBXインポート時点に
存在した。追加調査では、元の `PreRotation` の微小角度と `Lcl Scaling` の1付近の値が
Assimpの行列ではidentityへ省略されることを確認した。Plumの約-0.000162度のPreRotationや
Kipfelの約1.00001526のスケールも、バイナリFBXから読むと値が残っている。
`UnityFbxLocalTransform` はFBXの回転順序・pre/post回転・pivot/offsetを含む局所行列を
独立に復元し、既存の座標系変換と分解へ渡す。モデル別の補正値や許容差の拡大は使わない。
修正後、アーカイブの依存が揃った元の不一致12件は既存のUnity期待値と一致した。

AizaraのPAWSはskinのない末端Meshでrotation/scaling pivotを共有し、Unityはpivotを
オブジェクト原点として残す。skinのcluster接続があるMeshへこの原点補正を適用しない。
爪Meshのように共通祖先がskinの骨配列に含まれない場合、UnityのrootBoneは最後のclusterの骨になる。
共通祖先が骨配列に含まれる場合はその祖先を使う。自作FBXをUnityへ読み込む
`ResoPonSkinRootSmoke` で、cluster順序の反転と祖先を含む場合を別々に検証する。
バイナリFBX 7400/7500の微小TRS・static/skinのpivot・全6回転順序・未対応形式のfallbackは
`UnityReferenceDump --check-source-transforms <新規ディレクトリ>` で検証する。
一般条件を含む[生成パッケージの構造検査](local-avatar-tests.md#保存パッケージの構造を自動検査する)と、
Unityとの解析値比較、実クライアントの外観・動作確認は別の結果として保存する。

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
