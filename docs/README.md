# 実装・検証資料

利用者向けの操作は[ユーザーガイド](user-guide/README.md)、対応形式と必要環境は[プロジェクトREADME](../README.md)、配布手順は[リリース手順](../RELEASE.md)を参照する。

## アバターを検証する

| 目的 | 手順 |
|---|---|
| テストアバターを登録する・全件変換して集約する | [ローカルアバター回帰テスト](local-avatar-tests.md) |
| Unity Editorで基準値を採取し、変換前の解析と照合する | [Unity参照テスト](unity-reference-tests.md) |
| 保存後の階層・骨順・衣装の大きさ・口形をUnityと照合する | [変換後の階層比較](converted-structure-tests.md) |
| 視点位置のずれを調べる | [DescriptorとHead Proxy／Targetの比較](avatar-viewpoint-tests.md) |
| 合成データや実FBXで回帰テストを実行する | [Prefab入力](../tests/PrefabInputSmoke/README.md)・[シーン生成](../tests/PrefabSceneSmoke/README.md)・[表情の再生と保存](../tests/ExpressionSmoke/README.md) |

同じ入力・選択アバターで比較する。変換の成否、Unity解析との一致、保存後の検査、クライアントでの装着・動作確認はそれぞれ記録する。
登録データは `.local/`、実行ごとのログ・一時ビルド・生成物は `.tmp_verify/` で管理する。

## 変換処理を調べる

| 対象 | 資料 |
|---|---|
| 起動と保存 | [Resoniteの検出とセットアップ](resonite-setup.md)・[ヘッドレスエンジン、DLL互換性とLocalDB](headless-engine.md) |
| リグ・追跡・FirstPerson・揺れもの | [アバター変換の共通実装](avatar-conversion.md) |
| VRM 0.x / 1.0 | [GLB前処理、座標系、表情、SpringBone、MToon](vrm-conversion.md) |
| VRChat入力 | [全体設計、Prefab選択、Unity生成アセット、視点](vrchat-conversion.md) |
| VRChatのモデル | [Prefab階層と骨格](vrchat-prefab-hierarchy.md)・[ブレンドシェイプと顔の追跡](vrchat-blendshapes.md)・[マテリアル変換](vrchat-materials.md) |
| 性能 | [工程別の計測と保存・読み込みの検証](conversion-performance.md) |
| ライセンス | [外部コード・素材の出典、許諾、配布時の通知](third-party-licenses.md) |

## 表情システムを変更する

| 対象 | 資料 |
|---|---|
| 操作、生成階層、装着・解除、公開API | [表情システム](expression-system.md) |
| 変数の名前・型・初期値、追跡との合成 | [変数リファレンス](expression-variables.md) |
| FX・FaceEmoの表情セット、ハンドサインへの割り当て | [VRChat表情の検出と割り当て](vrchat-expression-import.md) |
| コントローラー別の入力判定 | [コントローラー入力](controller-gestures.md) |

表情機能をリリースするときは、ReleaseビルドでExpressionSmokeと実アバターの変換・再読込を実行し、Resoniteクライアントで入力、メニュー、同期、再装着を確認する。
