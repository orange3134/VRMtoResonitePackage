# ローカルアバター回帰テスト

変換後に骨・スロットが増える問題は、[変換後の階層比較](converted-structure-tests.md)の
一括runnerでUnity・変換前解析・保存パッケージを照合する。解析一致と変換後の一致は別に記録する。

Unityで継承を解決して採取したv2基準値は、[Unity参照データの直接比較](unity-reference-tests.md#resopon解析との直接比較v2)
で再検証する。`.local/avatar-tests.json` の `unityResolvedParity` に比較レポートへのパス、状態、
入力・期待値・実値のハッシュを追記できる。採取状態の `unityResolvedCollection` や既存の
`analysisCollection` は保持し、比較成功と採取成功を区別する。

Prefabの解析結果をUnity Editorの値と比較する場合は、[Unity参照データとの比較](unity-reference-tests.md)を参照。

ユーザーからアバターのパスを渡されたら、調査の最初に `.local/avatar-tests.json` に追記する。
既存ケースは残し、同じ入力は更新する。`.local/` は Git の除外対象で、環境固有の情報を保存する。
アバター本体はコピーせず、保存済みの Unity プロジェクトまたは VRM / unitypackage を参照する。

```json
{
  "version": 1,
  "cases": [
    {
      "id": "example-variant",
      "inputPath": "D:\\ExampleProject\\Assets\\Avatar.prefab",
      "reportedLog": "D:\\ExampleLogs\\convert.log",
      "tags": ["prefab", "variant", "removed-gameobjects"],
      "issue": "継承した Descriptor が見つからず変換に失敗する",
      "expected": "変換と inspect が成功し、削除した衣装が含まれない",
      "arguments": [],
      "notes": "追加の目視確認項目や調査結果を記録する"
    }
  ]
}
```

PowerShell 5.1 からリポジトリのルートで実行する。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-local-avatar.ps1 -List
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-local-avatar.ps1 -CaseId example-variant -Mode Dump
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-local-avatar.ps1 -CaseId example-variant
```

`-List` はパスの存在とタグを表示する。変更箇所に関係するケースを選び、必要なら `notes` の
追加確認も行う。パスが存在しない場合は実行せず、その環境のパスに登録を更新する。
`arguments` は変換オプション（例: `--avatar`, アバター名）だけを配列で指定する。
`--output` や解析モードのフラグはスクリプト側で管理するため登録しない。
非標準の Resonite インストールには実行前に `RESONITE_PATH` を設定する。

スクリプトは現在のソースを実行ごとの専用ディレクトリへ Release ビルドしてから実行する。既にビルド済みの場合に限り
`-SkipBuild` を使える。既定の Convert は通常のアバター変換後、出力パッケージを `--inspect` で
再読込する。Dump は Prefab / unitypackage を `--vrchat-dump`、VRM を `--assimp-dump` で解析する。
入力と同じ場所には出力しない。

実行ごとのログ、生成パッケージ、`result.json` は `.tmp_verify/local-avatar-tests/<id>/<実行ID>/`、
各ケース・モードの直近結果は `.local/avatar-test-results/<id>.<Convert|Dump>.json` に保存する。
結果には入力と実行DLLの SHA-256、Git HEAD、未コミット変更の有無、コマンド引数、終了コードを残す。
終了コード 0 は変換・パッケージ構造の確認までを示す。外観や動作の目視確認は別途記録する。

コミット前に `git check-ignore .local/avatar-tests.json` と `git diff --cached` で、
パス・ログ・アバター本体がステージされていないことを確認する。

### 収集した解析データを回帰テストで再利用する

継続利用するデータセットは `.local/avatar-analysis/<採取ID>/` に保存する。
一時ビルド・展開したアバターは引き続き `.tmp_verify/` に置く。
`.local/avatar-analysis/latest.json` の `path` が直近の `index.json` を指す。
登録一覧のコピー、入力SHA-256、採取したGit HEAD、実行DLLのSHA-256、元ログ、
比較用の `parser.expected.json`、取得できたケースの `unity.expected.json` を保持する。
各ケースの `analysisCollection` からも採取先を参照できる。

Python 3.11以降と現在の開発ビルドで、Unityを起動せず再検査する例:

```powershell
dotnet build tests/UnityReferenceDump -c Release -o .tmp_verify/avatar-analysis-build
$index = (Get-Content -Raw .local/avatar-analysis/latest.json | ConvertFrom-Json).path
python -B scripts/test-avatar-analysis.py $index `
  --converter .tmp_verify/avatar-analysis-build/ResoPon.dll `
  --reference-dump .tmp_verify/avatar-analysis-build/UnityReferenceDump.dll `
  --output .tmp_verify/avatar-analysis-replay
```

出力先は毎回新しいディレクトリを指定する。`--case <ケースID>` は複数指定できる。
入力・記録済み依存ファイル・期待値のハッシュが変わった場合は失敗し、期待値を自動更新しない。
Prefab入力の依存記録はAssets/Packages内のserialized GUID参照とパッケージmanifestであり、
ネイティブアセット内部の外部参照やPackageCache全体を網羅するプロジェクトバックアップではない。

終了コード0は、選択した全ケースの解析結果と、`verified` のUnity参照が一致したことを示す。
1は実行失敗・ハッシュ変更・差分、2はコマンド引数の誤りを示す。
`report.json` の `unityMatched` を別に確認する。未対応・採取失敗・保留中のUnityケースは
`not-verified` として理由を残し、Unityの成功件数には含めない。

解析スナップショットは、VRChatでは選択アバターの診断セクション、VRMではメッシュ診断を
行単位で保持する。起動時のバージョン・環境表示・取り込み途中の診断は元ログ側に保存する。
これは現在の解析動作を固定する比較基準であり、正しさを独立に保証する期待値ではない。
Unity参照との一致も[採取対象のフィールド](unity-reference-tests.md#比較する範囲v1)に限られ、
実変換・inspect・クライアントでの外観と動作は別途検証する。

`-SkipBuild` で同じDLLを使う複数の変換を同秒に起動すると、DLL横の `Logs/convert_yyyyMMdd_HHmmss.log`
が衝突する。並行検証には既定の実行別ビルドを使う。ただし `-o` で出力先を分けても
`obj/Release` は共有されるため、ビルド工程は順番に実行して中間DLLのロック競合を避ける。
また、実行中のDLLへビルド出力を上書きしない。

### 登録アバターをすべて変換して一か所へ集約する

Python 3.11以降で、登録済みの `arguments` を保った通常変換を全件実行する。
相対 `inputPath` は登録ファイルの親ディレクトリを基準にする。Prefab採取、全件変換、
PowerShellの単体検証は同じ絶対パスでハッシュ計算と実行を行う。
以前の全件変換でリポジトリ直下を基準に登録した相対パスは、`.local/avatar-tests.json` から
見たパス（例 `../Assets/A.prefab`）または絶対パスへ更新する。

```powershell
python -B scripts/convert-all-local-avatars.py --jobs 3
```

現在の `UnityReferenceDump` と本番変換DLLを一度のReleaseビルドで生成し、
ケースごとにビルド出力と一時ディレクトリを分離する。
並行数の既定値は2。`--timeout` は変換・inspectそれぞれの制限秒数で、既定値は1800。
入力がないケースや変換に失敗したケースも結果に残し、残りのケースを続行する。

実行ごとに `.tmp_verify/all-avatar-conversions/<実行ID>/` を作成する。
`packages/` には変換と `--inspect` が成功したパッケージを
`<caseId>__<元の出力名>.resonitepackage` として集約し、コピー前後のSHA-256も照合する。
`report.json` に全件の成否、入力・実行DLL・出力のハッシュ、Git HEAD、実行引数を記録する。
登録一覧のコピーは `registry.json`、個別ログは `cases/<caseId>/` に残る。
既存の単体テストの直近結果は更新しない。

網羅的テストには次の解析比較を必ず含める。このバッチでは各ケースの変換前に自動実行する。

1. 登録ケースの `unityResolvedCollection.coveragePath`（未登録なら
   `prefabUnityCheck.coveragePath`）から同じID・入力のUnity v2参照を選ぶ。
2. `test-unity-resolved.py` で入力・期待値・Prefab依存のハッシュを検証し、今回ビルドした
   ResoPonの解析結果と比較する。通常変換の `--avatar` または既定選択が同じPrefabを
   指すことも本番パーサで検証する。過去の一致記録を今回の結果として流用しない。
3. `unityComparison` に一致・不一致・未採取・未対応・再採取必要・失敗とレポートを保存する。
   差分があっても残りの比較・変換・inspectは続行し、手動確認用のパッケージも集約する。
4. `conversionStatus` と `unityComparisonSummary` を分け、全体の `status` は不一致・失敗が
   あれば `failed`、未採取・未対応・再採取必要なら `incomplete` とする。アーカイブ外の依存を
   補って解析が一致した場合も、`unityComparison.status=matched` と `dependencyScope` を記録し、
   アーカイブ単独で実行した変換の `validationStatus` は `incomplete` とする。どちらも終了コード1。
   VRMはUnity Prefab投影の対象外として `not-applicable` を理由付きで記録し、一致件数に含めない。
   対象ケースの比較と全変換が成功した場合だけ `passed`・終了コード0になる。

Unity参照がない、または依存が変わった場合は[Unity参照の採取手順](unity-reference-tests.md)で
再採取し、登録先を更新して再実行する。期待値は比較結果から自動更新しない。
解析比較はTransform・Renderer・骨接続・PhysBone等のv2投影の範囲を検証するものであり、
生成パッケージの検査やResoniteクライアントでの外観・動作確認は引き続き別途行う。
外観・装着・表情・揺れものの動作は、集約したパッケージをResoniteクライアントで確認する。

この処理は利用条件を確認済みのローカル素材を使う。素材と結果はコミット・配布しない。
スクリプトはPython標準ライブラリと既存の変換CLIを使う独立実装で、外部コードや依存の追加はない。

### 登録アバターをすべてAlphaからCutoutへ変換する

「全材質をCutout」は、既存の不透明・加算などを変更せず、選択オプションの対象となる
Alpha材質をすべて指定する。対象がないケースも通常どおり変換する。

- `.local/avatar-tests.json` の全ケースを対象にし、登録済みの `arguments`（特に `--avatar`）を保持する。
- VRMは `VrmParser.Parse` の `IsMToon && AlphaMode == "blend"` の材質名を列挙する。
  UnityPackage / Prefabは選択したアバターを解析し、
  `VrchatMaterialBuilder.ListTransparentMaterials` が返す論理アセットパスを列挙する。
- 各対象を `--transparent-cutout <指定>` として繰り返し渡す。ワイルドカード指定は使わない。
- 各ケースの出力先は分離し、変換成功後に `--inspect` で再読込する。
  Unity系は保存データの `XiexeToonMaterial` に `BlendMode=Alpha` が残っていないことも確認する。
  VRMはMToon以外のAlphaが対象外であるため、対象材質と区別して確認する。
- Alphaから変更した材質は保存後の `AlphaClip=0.5` を確認する。
  元からCutoutの材質は別に列挙し、元データのしきい値（材質variantの継承を含む）が維持されることを確認する。
- 集約先には検証済みパッケージをコピーし、`<caseId>__<元の出力名>.resonitepackage` のように
  ケースIDを付けて同名Prefabの上書きを防ぐ。ログ・対象材質一覧・検証結果は集約先と分けて保持する。
- 並行実行する場合、ケースごとに実行DLLのディレクトリと一時ディレクトリを分ける。
  同じビルドをコピーして使い、実行中のDLLを再ビルドで上書きしない。

生成物・一覧・環境固有パス・実行結果は `.tmp_verify/` と `.local/` に置き、コミットしない。
構造検査の成功は外観の確認を意味しない。Cutout化では半透明の濃淡が失われるため、
実際のライティングや輪郭の見え方はResoniteクライアントで確認する。

### 利用中の EXE まで修正が反映されているか確認する

専用ディレクトリでの回帰テストが成功しても、通常利用する `publish/ResoPon.exe` は更新されない。
修正後も同じ症状が報告された場合は、まず実際の変換ログ冒頭の ResoPon バージョンと実行場所を
確認する。別フォルダーの検証用 EXE だけを生成して完了としない。
通常利用する publish 出力を更新する場合は、EXE と依存 DLL をまとめて publish し、
その場所の EXE を使って実入力の変換・生成パッケージの inspect を実行する。
Windows の GUI EXE を PowerShell から検証するときは `Start-Process -Wait -PassThru` と
`-WindowStyle Hidden` を使い、プロセスの終了コードとログを確認する。
表情の修正ではログの割り当て数と保存済みパッケージの再生も確認する。
アバターと検証結果は引き続き `.tmp_verify/` に置く。

単一ファイル publish は、出力先に以前からある `ResoPon.dll` を更新しない。
DLL を併置して直接実行・参照する環境では、同じ publish ビルドの
`bin/Release/win-x64/ResoPon.dll` も揃え、古い併置 DLL を検証対象にしない。
EXE の実変換ログのバージョンと、参照する DLL のバージョン・SHA-256を確認する。

### Unityで継承を解決した期待値の保存

プロジェクト内 `.prefab` を直接指定する場合は `scripts/check-prefab-unity.py` で採取・解析・比較を
一括実行できる。[実行手順](unity-reference-tests.md#プロジェクト内prefabから採取と比較を一括実行する)を参照。
出力先の `report.json` と `unity/coverage.json` をケースから参照し、元の `inputPath` は保持する。

Variantを含むPrefabの解決済み状態は[Unity参照データのv2採取](unity-reference-tests.md)を使い、
`.local/unity-resolved/` に保存する。ケースの `unityResolvedCollection` には採取先・状態・失敗理由を記録する。
`captured` はUnityの期待値を収集できた状態であり、ResoPonとの照合成功ではない。
`test-avatar-analysis.py` と `UnityReferenceDump` は従来のv1照合を継続し、v2は入力に使わない。
v2の再現性・構造検査には `compare-unity-reference.py expected.json repeat.json` を使う。
元入力・依存物・Unity/SDK版・採取コードのハッシュも保存し、期待値は自動上書きしない。

### 保存パッケージの構造を自動検査する

実変換後の `--inspect` に加え、開発用 `PackageRegressionDump` で保存値をJSONへ取り出し、
`check-package-snapshot.py` で明示した条件を検査できる。エンジンは起動せず、RecordPackageの
main assetを読む。メッシュの外観、表情の再生、装着・衝突の動作は検査しない。

```powershell
dotnet build tests/PackageRegressionDump -c Release -o .tmp_verify/package-check-build
dotnet .tmp_verify/package-check-build/PackageRegressionDump.dll `
  .tmp_verify/avatar-output/Avatar.resonitepackage .local/package-checks/run-001/snapshot.json
python -B scripts/check-package-snapshot.py .local/package-checks/run-001/snapshot.json `
  .local/package-checks/expectations.json --report .local/package-checks/run-001/report.json
python -B -m unittest discover -s tests/PackageRegressionComparison -v
```

出力先の親ディレクトリは先に作る。ダンプとレポートは既存ファイルを上書きしない。
比較の終了コードは一致0、条件違反1、入力・出力エラー2。ダンプは成功0、実行失敗1、引数誤り2。
スナップショットには元パッケージのSHA-256、全Slotの構造ID・名前の配列・タグ・active状態、
コンポーネントの完全な型名、SkinnedMeshRendererのBlendShapeWeightsを保存する。
未設定のnullタグは空文字列へ統一する。パスは `/` を含む名前も区別するため配列で保持する。

期待値の例（対象に応じて独立に条件を選ぶ）:

```json
{
  "format": "resopon.package-expectations",
  "schemaVersion": 1,
  "componentCounts": {
    "[FrooxEngine]FrooxEngine.BipedRig": { "min": 1, "max": 1 },
    "[FrooxEngine]FrooxEngine.FinalIK.VRIK": { "min": 1, "max": 1 },
    "[FrooxEngine]FrooxEngine.SkinnedMeshRenderer": { "min": 1 }
  },
  "slotRules": [
    {
      "selector": { "pathSuffix": ["socks"] },
      "count": 1,
      "weights": [{ "componentIndex": 0, "index": 0, "value": 0 }]
    }
  ]
}
```

`selector` は `pathSuffix`、`tag`、`componentType` のAND条件。選択件数を必須にし、
欠落や同名Slotの重複を見逃さない。`activeSelf` / `activeInHierarchy` も条件に指定できる。
weightの `componentIndex` は選択Slot内のSkinnedMeshRendererの順序、`index` はshapeの順序。
weight比較は既定で完全一致し、必要なら明示的な `absoluteTolerance` を指定する。
存在しないrenderer・shapeを0扱いしない。型名・項目名の誤記、不正数値、重複JSONキーはエラー。

回帰条件はUnity参照や仕様から決め、現在の出力をそのまま期待値へ自動更新しない。
Linaはsocksの初期weight=0、OmanekoはcatToyのactiveSelf=false、12Bは
`tag=modular_avatar/dynamic_bone_controller` かつ
`componentType=[FrooxEngine]FrooxEngine.DynamicBoneChain` のSlotが14個、を確認する。
12Bの全DynamicBoneChain数にはテンプレートも含まれるため、型の総数だけで14と比較しない。
Unityのshape weightは0–100、Resoniteの保存値は0–1であることにも注意する。

再現用には、入力・選択引数・実行DLL・生成パッケージ・期待条件・スナップショットのハッシュと、
変換およびinspectの終了コードを `.local/` に保存する。生成パッケージは `.tmp_verify/` に置く。
一般条件の成功は、登録した全rendererや物理設定がUnityと一致したという意味ではない。

### 保存スキンの追従を検査する

`PrefabSceneSmoke --skin-motion-package <package> <expectations.json>` はパッケージを
ヘッドレスエンジンへ再読込し、指定した骨を動かしたときに対象Rendererの全使用骨が追従するかを検査する。
期待条件はUnityの骨階層などから選び、実アバターの条件ファイルは `.local/` に置く。

```json
{
  "rootBonePathSuffix": ["RootNode", "Armature", "Hips"],
  "renderers": [
    { "pathSuffix": ["Avatar", "Outfit", "Shirt"], "minimumWeightedBones": 1 }
  ]
}
```

```powershell
dotnet build tests/PrefabSceneSmoke -c Release -o .tmp_verify/skin-motion-build
dotnet .tmp_verify/skin-motion-build/PrefabSceneSmoke.dll --skin-motion-package `
  .tmp_verify/avatar-output/Avatar.resonitepackage .local/skin-motion-expectations.json
```

パスはSlot名の配列で、末尾が一致する対象を一意に選ぶ。欠落・曖昧な対象・空の使用骨は失敗する。
IK等が対象骨自身を駆動していても試験できるよう、一時的な親を `(0.1, 0.05, -0.03)` m動かす。
対照となる骨の移動を確認してから、使用骨の移動誤差を `1e-5` m以内で検査する。
試験後は親と位置を復元し、パッケージファイルは変更しない。成功0、検査失敗1。
この許容差は移動試験専用で、Unity v2比較の許容差を変更するものではない。
元からその骨に追従しない仕様のアクセサリーは対象へ含めない。クライアントでの装着・外観確認は別途必要。
