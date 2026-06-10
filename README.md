# VrmToResonitePackage

VRMアバターを **.resonitepackage** に変換するWindows用ツールです。
変換した resonitepackage は、Resonite のファイルブラウザやインベントリへの
ドラッグ＆ドロップでそのままインポートできます。

## 特徴

- **VRMファイルをexeにドラッグ＆ドロップするだけ**で変換完了
- VRM 0.x / VRM 1.0 の両方に対応
- Resonite本体（FrooxEngine）のインポーターをヘッドレスで動かしているので、
  Resonite内で手動インポートしたときと同じ品質のモデルが得られる
- さらにVRMの正確なメタデータを使って、ゲーム内の手動セットアップを超える自動化:
  - **ヒューマノイドリグ**: VRMのhumanoidボーンマップから正確にBipedRigを構築（名前推測に頼らない）
  - **フルアバターセットアップ**: VRIK・アバターアンカー・ツールアンカー・ボイス出力・
    アウェイインジケーター等（ゲーム内のAvatarCreator相当）
  - **視線・まばたき**: VRMのexpression（blink/blinkLeft/blinkRight）から目のセットアップ
    （EyeRotationDriverのMaxSwingはVRM向けに4へ調整）
  - **視点位置**: 目ボーンの中点から眉間（顔表面）へ自動オフセット
  - **アバター保護**: SimpleAvatarProtectionを既定で付与（インポートした人が所有者に）
  - **AvatarRenderSettings**: NearClip=0.075で前髪が一人称視点を遮る問題を回避
  - **手の向き**: ボーン位置から決定論的に計算（+Z=中指方向、+Y=手の甲）
  - **リップシンク**: VRMの母音表情（あいうえお）をResoniteのビセームに直結。
    Resoniteの名前推測が目のシェイプキー等を誤登録した場合は自動解除
  - **揺れもの**: VRMスプリングボーン → DynamicBoneChain変換（コライダー込み）
  - **マテリアル**: MToonのアルファモード・両面描画・アウトライン設定をXiexeToonに反映。
    MToonの見た目に近づけるためShadowRampを除去しShadowSharpnessを0に設定

## 必要環境

- Windows + .NET 10 ランタイム
- **Resonite がインストールされていること**（Steam版の標準パスは自動検出）

> ⚠ このツールはResoniteのDLLを**再配布しません**。実行時にインストール済みの
> Resoniteフォルダから読み込みます。

## 使い方

### ドラッグ＆ドロップ

`VrmToResonitePackage.exe` に `.vrm` ファイルをドロップしてください。
同じフォルダに `<ファイル名>.resonitepackage` が出力されます。

### コマンドライン

```
VrmToResonitePackage.exe <model.vrm> [model2.vrm ...] [オプション]

オプション:
  -o, --output <dir>       出力先フォルダ
  --resonite-path <dir>    Resoniteのインストールフォルダ
  --no-avatar              アバターセットアップを行わずモデルのみ変換
  --face-tracking          フェイストラッキング用のAvatarExpressionDriverを生成
                           （対応アバター以外では誤登録の元になるため既定では無効）
  --no-protection          SimpleAvatarProtection（アバター保護）を付けない
                           （既定で付与。インポートした人が所有者になる）
  --height <m>             アバターの身長をメートル指定でリスケール
  --view-forward <m>       視点（眉間）の前方オフセットをメートルで指定
                           （既定: 目ボーン間距離から自動計算、約0.03〜0.09）
  --view-up <m>            視点の上方オフセットをメートルで指定
                           （既定: 目ボーン間距離から自動計算、約0.005〜0.025）
  --near-clip <m>          AvatarRenderSettingsのNearClip値
                           （既定: 0.075。0を指定するとコンポーネント自体を付けない）
  --keep-working-files     作業用一時ファイルを残す（デバッグ用）
  --inspect                変換せず、.resonitepackageの中身を表示（検証用）
  -h, --help               ヘルプ
```

変換結果の検証（エンジン起動なしでパッケージ内のスロット/コンポーネント構造を表示）:

```
VrmToResonitePackage.exe --inspect MyAvatar.resonitepackage
```

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

## ログ

変換ログは `%LOCALAPPDATA%\VrmToResonitePackage\Logs\` に保存されます。
問題が起きたときはここを確認してください。

## 制限事項

- MToonの完全再現はできません（ResoniteのXiexeToonへの近似マッピング）
- VRMの表情（happy/angry等）のフェイストラッキング連動は`--face-tracking`指定時のみ、
  かつ名前ヒューリスティクスによる割り当てです
- VRM 1.0のコンストレイント（VRMC_node_constraint）は未対応
