# ResoPon

VRMアバターを **.resonitepackage** に変換するWindows用ツールです。
変換した resonitepackage は、Resonite のファイルブラウザやインベントリへの
ドラッグ＆ドロップでそのままインポートできます。

## 特徴

- **VRM をexeにドラッグ＆ドロップするだけ**で変換完了
- VRM 0.x / VRM 1.0 の両方に対応
- Resonite本体（FrooxEngine）のインポーターをヘッドレスで動かしているので、
  Resonite内で手動インポートしたときと同じ品質のモデルが得られる
- さらにVRMの正確なメタデータを使って、ゲーム内の手動セットアップを超える自動化:
  - **ヒューマノイドリグ**: VRMのhumanoidボーンマップから正確にBipedRigを構築（名前推測に頼らない）
  - **視線・まばたき**: VRMのexpression（blink/blinkLeft/blinkRight）から目のセットアップ
    EyeRotationDriverのMaxSwingを自動調整
  - **視点位置**: 目ボーンの中点から眉間（顔表面）へ自動オフセット
  - **アバター保護**: SimpleAvatarProtectionを既定で付与
  - **AvatarRenderSettings**: 前髪が一人称視点を遮る問題を回避
  - **手の向き**: ボーン位置から決定論的に計算（+Z=中指方向、+Y=手の甲）
  - **リップシンク**: VRMのExpressionを元に設定。
  - **揺れもの**: VRMスプリングボーン → DynamicBoneChain変換（コライダー込み）
  - **マテリアル**: MToonのマテリアル設定を極力忠実にXiexeToonに反映。

## 必要環境

- Windows + .NET 10 ランタイム
- **Resonite がインストールされていること**

## 使い方

### ドラッグ＆ドロップ

`ResoPon.exe` を起動して `.vrm` をドロップしてください。

### Resoniteのパス指定

自動検出できない場合は、次のいずれかで指定できます。

1. `--resonite-path "D:\Games\Resonite"`
2. 環境変数 `RESONITE_PATH`
3. exeと同じフォルダに `resonite-path.txt` を置いてパスを1行書く

## ビルド方法

```
dotnet build src/VrmToResonitePackage -c Release
```

配布用:

```
dotnet publish src/VrmToResonitePackage -c Release -o publish
```

Resonite DLLの参照解決には、ビルド時に `ResonitePath` プロパティ
（または環境変数 `RESONITE_PATH`）を使います。標準のSteamパス以外に
インストールしている場合:

```
dotnet build src/VrmToResonitePackage -c Release -p:ResonitePath="D:\Games\Resonite"
```

## 互換性ワークアラウンド

Resonite（FrooxEngine）のインポーターには特定のVRMでクラッシュする既知の問題があり、
変換前にGLBを自動修正して回避しています：

- **モーフターゲット名の欠落**（旧UniVRM/UniGLTF-1.28等）: ターゲット名がprimitive側の
  extrasにしかないとAssimpが読めず全シェイプ名が空になり、FrooxEngineの重複名チェックが
  空名を素通しして「BlendShape with given key already exists」でクラッシュ
  → mesh.extras.targetNamesへ昇格・補完
- **プリミティブ間のモーフ属性不一致**（メッシュ結合ツール使用モデル等）: 同一メッシュ内の
  プリミティブ間でモーフターゲットのNORMAL有無が混在するとマージ処理が範囲外アクセスで
  クラッシュ → 不一致ターゲットからNORMAL/TANGENTを除去して整合させる
- **インポートのハング**: インポートコルーチンが例外で死ぬと完了通知が来ないため、
  タイムアウト（`--import-timeout`、既定300秒）で検出して次のファイルへ進む

## 仕組み

1. VRM（= glTFバイナリ）のJSONチャンクを自前でパースし、humanoid/expressions/
   springBones/MToonなどのVRM固有データを抽出
2. FrooxEngineを `StandaloneFrooxEngineRunner` でヘッドレス起動し、一時ワールドを作成
3. `.vrm` を `.glb` としてResonite標準の `ModelImporter`（XiexeToonプリセット）でインポート
4. VRMデータを使ってアバターセットアップ（ゲーム内AvatarCreatorの処理をヘッドレスで再現）
5. `Slot.SaveObject` + `PackageCreator.BuildPackage` で `.resonitepackage` を出力

## データの保存場所

このツールはエンジン起動時に`-DataPath`/`-CachePath`相当の指定を行い、
すべてのデータを専用フォルダ `%LOCALAPPDATA%\ResoPon\` 配下に生成します：

- `Data\` — エンジンのLocalDB（アセットキャッシュ）。破損時は自動でリセットされます
- `Cache\` — エンジンのキャッシュ
- `Logs\` — 変換ログ。問題が起きたときはここを確認してください

**Resonite本体のデータフォルダには一切触れません**（プレイ環境に影響しません）。
自動リセット処理にも、このフォルダ外では動作しないガードが入っています。
ツールの動作がおかしくなった場合は`%LOCALAPPDATA%\ResoPon`を
丸ごと削除しても安全です。

## 制限事項

- MToon/liltoonの完全再現はできません（ResoniteのXiexeToonへの近似マッピング）
- VRMの表情（happy/angry等）のフェイストラッキング連動は`--face-tracking`指定時のみ、
  かつ名前ヒューリスティクスによる割り当てです
- VRM 1.0のコンストレイント（VRMC_node_constraint）は未対応
