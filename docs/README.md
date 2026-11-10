# 実装・検証資料

利用方法は[プロジェクトREADME](../README.md)、配布手順は[RELEASE.md](../RELEASE.md)を参照。
このディレクトリは現行コードの仕様・制約・保守に必要な判断をまとめる。
開発途中の差分や未採用設計は残さず、変更経緯はGit履歴で確認する。

## 表情システム

| 資料 | 内容 |
|---|---|
| [表情システム](expression-system.md) | 対応範囲、操作、生成階層、選択保持、ライフサイクル、公開API、編集方法 |
| [変数リファレンス](expression-variables.md) | 空間・名前・型・初期値、出力キー、瞬きと追跡の合成・接続 |
| [VRChat表情の検出と割り当て](vrchat-expression-import.md) | 顔候補、最初のセット、通常・CAC形式、ハンド割り当て解析、制限 |
| [コントローラー入力](controller-gestures.md) | 5機種の判定表、待機と入力破棄、出典・ライセンス |

## 変換処理

| 資料 | 内容 |
|---|---|
| [ヘッドレスエンジン](headless-engine.md) | DLL互換性、LocalDB、ワールド、終了処理、パッケージ出力 |
| [VRM変換](vrm-conversion.md) | GLB前処理、座標系、アバター設定、FirstPerson、SpringBone、MToon |
| [VRChat変換](vrchat-conversion.md) | Unity YAML、Prefab・FBX合成、初期状態、Viseme・Blink推定、PhysBone、lilToon |
| [変換性能の診断](conversion-performance.md) | 工程別計測、メッシュ圧縮保存・FBX再解析の増幅要因、改善時の検証条件 |

## 検証とリリース準備

| 資料 | 内容 |
|---|---|
| [ローカルアバター回帰テスト](local-avatar-tests.md) | 実アバター登録、変換・dump・inspect、利用中EXEへの反映確認 |
| [ExpressionSmoke](../tests/ExpressionSmoke/README.md) | 合成データの表情検証、生成・複製・保存再読込、保存パッケージの64組検証 |
| [PrefabInputSmoke](../tests/PrefabInputSmoke/README.md) | Unity入力・Prefab解析の回帰検証 |
| [PrefabSceneSmoke](../tests/PrefabSceneSmoke/README.md) | 実FBXを用いたシーン生成検証 |
| [外部ソース・素材のライセンス](third-party-licenses.md) | 使用範囲、ソース・配布物の通知、配布検証 |
| [ローカルレビュー](local-review.md) | レビュー修正時の全差分レビュー・検証・コミット手順 |

表情機能のリリースでは、Releaseビルド、ExpressionSmoke、関連する実アバターの変換とinspect、
Resoniteクライアントでの入力・メニュー・同期・再装着を確認する。
実行した環境・アバター・結果はローカル記録やリリースの検証記録へ残す。
仕様文書に日付別の成功ログを積み重ねず、現在の対応範囲と未確認の範囲を区別する。

回帰テストは、修正済みでも現行の入出力や保存後の動作を検証するものを維持する。
旧API・旧変数名の削除だけを確認するテストや、一時的な文言変更の専用テストは残さず、
現行API一覧・構造検証・機能テストへ集約する。実アバターの比較用スナップショットは維持する。

文書を変更するときは既存の該当節を更新し、同じ仕様を複数資料へ重複させない。
数式・API契約・再現条件など再利用できる知識は残し、一時パス・個別の調査出力はコミットしない。
