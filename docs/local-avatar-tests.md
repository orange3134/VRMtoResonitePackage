# ローカルアバター回帰テスト

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

`-SkipBuild` で同じDLLを使う複数の変換を同秒に起動すると、DLL横の `Logs/convert_yyyyMMdd_HHmmss.log`
が衝突する。並行検証には既定の実行別ビルドを使う。また、実行中のDLLへビルド出力を上書きしない。

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
