# ローカルアバター回帰テスト

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

Variantを含むPrefabの解決済み状態は[Unity参照データのv2採取](unity-reference-tests.md)を使い、
`.local/unity-resolved/` に保存する。ケースの `unityResolvedCollection` には採取先・状態・失敗理由を記録する。
`captured` はUnityの期待値を収集できた状態であり、ResoPonとの照合成功ではない。
`test-avatar-analysis.py` と `UnityReferenceDump` は従来のv1照合を継続し、v2は入力に使わない。
v2の再現性・構造検査には `compare-unity-reference.py expected.json repeat.json` を使う。
元入力・依存物・Unity/SDK版・採取コードのハッシュも保存し、期待値は自動上書きしない。
