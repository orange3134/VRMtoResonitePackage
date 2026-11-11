# ユーザーガイドの原稿管理

使い方の原稿と画像をGitHubで管理し、利用者向けの最終版はNotionで公開する。
実装資料は[docsの索引](../README.md)、開発・CLIの概要は[プロジェクトREADME](../../README.md)を参照。

| 原稿 | 内容 |
|---|---|
| [日本語](ja.md) | 準備、変換、装着・保存、表情操作、Q&A |
| [English](en.md) | 英語版の使い方 |
| `assets/` | 両言語の画像。共通画像は共有し、言語別の設定画面には `-ja` / `-en` を付ける |

## 取り込み元

[Issue #52](https://github.com/orange3134/VRMtoResonitePackage/issues/52)に対応して、
提供されたNotionのMarkdownエクスポート2件を取り込んだ。

- 日本語: `ResoPon アバターを一発でResoniteにアップロード！`（ページID: `3816b3a1e27f80738667f9c661105173`）
- 英語: `ResoPon — Upload Your Avatar to Resonite in One Shot!`（ページID: `39f6b3a1e27f809381e9d15456e46b00`）

初回取り込みでは本文を保持し、ファイル名・画像の相対パス・日本語版から英語版へのリンクのみ整理した。
各12枚の画像のうち10枚は同一内容のため共有し、計14枚をバイト単位で変更せず保存している。
初期原稿はGit履歴から参照する。現行仕様への更新は別コミットで行う。

## 編集方針

原稿はUTF-8の標準Markdownで記述し、日本語・英語を同じ変更で更新する。
画像は `assets/` に置き、相対パスで参照する。Notionでの修正もGitHub側へ反映し、原稿を揃える。
初期版のスクリーンショットを現行版の操作説明に流用するときは、画面との差を本文に明記する。
Notionへのインポート・公開は原稿の更新とは別に行う。

## Notionへ公開する

1. 日本語・英語の本文と画像をレビューしてコミットする。
2. リポジトリのルートから次を実行する。原稿のローカルリンクを検証し、2言語のMarkdownと参照画像だけをZIPへ格納する。この管理用READMEは含まれない。

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-user-guide.ps1
   ```

   出力先は `.tmp_verify/user-guide-notion.zip`。再実行すると同ファイルを上書きする。
   ZIP内の `user-guide/` に原稿と `assets/` の相対構造を保持する。

3. Notionの作業用ページで `/zip` の「Convert Zip to pages」、または設定のインポートからZIPを取り込む。
   Markdown・ZIPの対応範囲は[Notion公式のインポート手順](https://www.notion.com/help/import-data-into-notion)を参照。
4. 日本語・英語の2ページ、各12枚の画像、見出し、箇条書き、外部リンクを確認する。
   相対リンクの自動変換を前提にせず、言語切り替えリンクを取り込んだNotionページへのリンクに直す。
   画像が取り込まれなかった箇所はZIP内の対応画像をアップロードする。
5. 公開前にプレビューし、既存の公開ページへ本文を反映する。新規公開の場合は公開URLを確認する。
   再インポートによる既存ページの自動更新は前提にしない。既存の公開URLを維持する場合はページを置き換えず、本文を更新する。
6. 公開URLと反映したコミットをPRまたはリリース記録へ残す。正式な公開URLが決まったらプロジェクトREADMEに案内を追加する。

ZIP生成はローカルで完結する。Notionへのアップロード・公開は行わない。
Notion側で画像やリンクがどう取り込まれるかは、実際のインポート後に確認する。

## 現行仕様との照合先

本文を更新するときは次を確認する。API・内部構造の詳細は利用者向けページへ転載しない。

- 画面項目: `src/VrmToResonitePackage/Locales/ja.json` / `en.json` と `GuiApp.cs`
- 必要環境: `src/VrmToResonitePackage/VrmToResonitePackage.csproj`
- 入力・Prefabの条件: [プロジェクトREADME](../../README.md)、[VRChat変換](../vrchat-conversion.md)
- 表情の対応範囲・メニュー・入力: [表情システム](../expression-system.md)
- ログの場所: `GuiApp.OpenLogFolder` と `Logs/convert_*.log` の出力処理

初期原稿からは、カスタム表情の対応、`Expressions` メニューの階層・解除方法、
ハンドサイン・キーボード、Prefabの条件、設定名、ログの場所、必要環境・更新手順を更新した。
初期画像は引き続き使用し、表情画面の名称・階層の違いを両言語の本文に明記している。
新しいスクリーンショットを用意したら、画像と差異の説明を一緒に更新する。
