# CLAUDE.md

このファイルは、このリポジトリでコーディングエージェントが作業する際の入口です。
常に必要な規約だけを置き、変換処理の詳細は `docs/` の実装資料へ分離しています。
ユーザー向けの使い方は [README.md](README.md) を参照してください。

## 作業規約

- 調査で判明した再利用可能な知識は、対象に応じてこのファイルまたは `docs/` の実装資料へ反映する。
  `CLAUDE.md` には常時必要なルールだけを残し、モデル固有の回帰事例や数式は詳細資料へ置く。
- GitHub PR のレビュー指摘に対応した場合は、修正を commit・push して PR の head へ反映した後、
  対応済みのスレッドだけを resolve する。未対応・未検証・未 push の指摘は resolve しない。
- PR は Draft ではなく通常の PR として作成する。
- GitHub CLI (`gh`) はサンドボックス内では認証やAPIアクセスが失敗するため、常にサンドボックス外の
  通常環境で実行する。
- リポジトリ内の一時ビルド、展開物、変換結果は `.tmp_verify/` に置く。
  `.vrm`、`.unitypackage`、テスト出力はコミットしない。

## プロジェクト概要

VRM または VRChat アバター入り `.unitypackage` を Resonite の `.resonitepackage` に変換する
Windowsアプリ。ヘッドレス FrooxEngine と Resonite 本体の `ModelImporter` を使い、リグ、視点、
表情、揺れもの、マテリアル、VRM FirstPerson を設定する。

- C# / .NET 10 / WPF / x64
- `SelfContained=false` の単一ファイルEXE
- Resonite DLLは実行環境から読み込み、配布物には含めない

## ビルドとリリース

```powershell
dotnet build src/VrmToResonitePackage -c Release
dotnet publish src/VrmToResonitePackage -c Release -o publish
```

- DLL参照元は `ResonitePath`、環境変数 `RESONITE_PATH`、または既定のSteamパス。
  別パスの場合は `-p:ResonitePath="D:\Games\Resonite"` を指定する。
- ResoPonのバージョンは `.csproj` がビルド日時 `yyyy.MM.dd.HHmm` から生成する。
  `-p:Version=...` で上書きできる。ログ、コンソール、GUIで同じ値を使う。
- `publish.ps1` は配布EXEを生成し、`release.ps1` はタグ作成とGitHub Release公開を行う。
  詳細は [RELEASE.md](RELEASE.md) を参照する。
- `.ps1` は Windows PowerShell 5.1 で実行されるため、スクリプト本文はASCIIだけで記述する。

### Resonite更新後の再ビルド

ResoniteのAPI更新後に `MissingMethodException` や `TypeLoadException` が発生した場合は、更新済みの
Resonite DLLを `ResonitePath` に指定して再ビルドする。配布版も対象DLLに対して作り直す。

ログ冒頭の次の情報を比較する。同じ表示バージョンでもDLL内容が異なる場合がある。

- `ResoPonバージョン`
- `Resoniteバージョン`
- `FrooxEngine.dll` のサイズ、更新日時、SHA-256

詳細は [ヘッドレスエンジン実装](docs/headless-engine.md) を参照する。

## 実行と検証

- 自動実行では `RESOPON_NOPAUSE=1` を設定し、キー入力待ちを無効にする。
- 変換ログはEXEと同じディレクトリの `Logs/convert_*.log` に出力される。
- エンジンを起動せず生成物を確認する場合は `--inspect` / `--inspect-verbose` を使う。
- VRMのブレンドシェイプ診断には `--assimp-dump`、`.unitypackage` の解析には
  `--vrchat-dump` を使う。
- `.unitypackage` に複数アバターがある場合は `--avatar <name>` で対象を指定する。
- 実変換の回帰確認では、ログの「完了」だけでなく生成パッケージを `--inspect` で再読込する。
- Resoniteを起動するテストは `%LOCALAPPDATA%\ResoPon` に実行ごとのLocalDBを作成する。
  サンドボックスでは通常環境での実行許可が必要になる場合がある。

## 主要な処理経路

1. `Program.cs` / `GuiApp.cs` が入力とオプションを受け取る。
2. `Converter.cs` がVRM経路とVRChat経路を拡張子で分岐する。
3. VRMは `VrmParser` と `GlbPreprocessor`、VRChatはUnity/VRChatパーサで中間モデルを作る。
4. Resoniteの `ModelImporter` でモデルを読み込む。
5. `AvatarSetup`、`SpringBoneSetup`、マテリアル変換を適用する。
6. `PackageCreator.BuildPackage` で `.resonitepackage` を出力する。

## リポジトリ構成

- `Program.cs` / `GuiApp.cs`: CLI、WPF GUI、引数、子プロセス制御
- `Converter.cs`: 変換全体の統括
- `AvatarSetup.cs`: BipedRig、VRIK、視点、表情、FirstPerson
- `SpringBoneSetup.cs`: VRM SpringBone / VRChat PhysBoneからDynamicBoneへの変換
- `MaterialTuner.cs`: VRM MToonからXiexeToonへの変換
- `Vrm/`: VRM 0.x / 1.0解析、GLB前処理、中間モデル
- `Unity/`: Unity YAML、unitypackage展開、FBX stable fileID解決
- `Vrchat/`: VRChatアバター解析、prefab合成、マテリアル変換
- `PackageInspector.cs`: 生成パッケージの構造検査
- `ResoniteLocator.cs`: ResoniteパスとDLL解決
- `LocalDbMaintenance.cs`: 実行ごとのDataディレクトリ管理

## 詳細資料の読み分け

作業対象に応じて必要な資料だけを読む。

- FrooxEngine起動、DLL互換性、LocalDB、終了処理:
  [docs/headless-engine.md](docs/headless-engine.md)
- VRM解析、GLB前処理、座標系、アバター設定、FirstPerson、MToon:
  [docs/vrm-conversion.md](docs/vrm-conversion.md)
- Unity YAML、prefab/FBX合成、VRChat設定、lilToon、回帰ケース:
  [docs/vrchat-conversion.md](docs/vrchat-conversion.md)

## 外部参照

- [VRM仕様](https://github.com/vrm-c/vrm-specification)
- [UniVRM](https://github.com/vrm-c/UniVRM)
- [Resonite.UnitySDK](https://github.com/Yellow-Dog-Man/Resonite.UnitySDK)
- [Resonite.UnityShaders](https://github.com/Yellow-Dog-Man/Resonite.UnityShaders)

Resonite APIの最終確認は、実行対象のインストール済みDLLに対して行う。デコンパイル済みソースは
古い場合があるため、`ilspycmd` 等で実DLLを確認する。
