# ローカルでレビューと修正を繰り返す

リポジトリのルートで実行する。ログイン済みの Codex CLI と、アプリのビルドに使う
.NET SDK・Resonite が必要。CLI が PATH にない場合は `-CodexPath` に実行ファイルを指定する。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/review-local.ps1 -Base main -Fix -CommitFixes
```

1. Release ビルド、PrefabInputSmoke、`git diff --check` を実行する。
2. 新しい Codex 実行で、基準ブランチとの共通祖先からの全差分を読み取り専用でレビューする。
   コミット済み・未コミット・未追跡のソースを含め、解析結果の下流での利用も確認する。
3. 指摘があれば別の実行で修正し、必要な回帰テストを追加する。
4. 検証後に修正をコミットし、ブランチ全体を新しくレビューする。指摘がなくなったら終了する。

`-Fix` を省略すると指摘を保存して停止する。`-MaxRounds` は既定10回、
`-CleanPasses` は既定1回。上限到達、ビルド失敗、レビュー失敗・未完了はエラーで終了する。
上限到達をレビュー合格として扱わない。結果は `.tmp_verify/local-review/<実行ID>/` に
JSON、修正説明、実行ログとして残る。

レビューと修正はローカルの作業ツリーで実行する。モデルの実行には Codex の接続が必要であり、
オフライン動作ではない。修正には workspace-write と自動承認審査を使う。
`-CommitFixes` は実行前の作業ツリーがクリーンな場合のみ利用できる。修正後の検証を通してから
そのラウンドの変更をコミットする。省略した場合は未コミットのままレビューを繰り返す。
スクリプトは push、GitHubへの投稿、レビュー依頼を行わない。
修正実行が中断された場合、同じコードに対する完了済みレビュー JSON を `-ResumeReport` に指定して
その指摘から再開できる。別のコードに対する古い結果には使わない。

シーン生成に関わる変更では [PrefabSceneSmoke](../tests/PrefabSceneSmoke/README.md) も実行する。
このテストはローカルFBXが必要なため、共通ループには含めていない。
必要なシーン検証も修正実行内で行う。ループ完了後に push し、対応済みのスレッドを resolve する。

Codex の非対話実行と JSON 出力については
[OpenAI 公式ドキュメント](https://learn.chatgpt.com/docs/non-interactive-mode) を参照。
