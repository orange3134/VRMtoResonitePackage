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
