# CLAUDE.md

このファイルは、このリポジトリでコーディングエージェント（Claude Code 等）が作業する際の
ガイドです。プロジェクト固有の前提・ハマりどころ・実機検証で確定した値をまとめています。
ユーザー向けの使い方は [README.md](README.md) を参照してください。

## プロジェクト概要

VRM アバター（および VRChat アバター入り `.unitypackage`）を Resonite の
`.resonitepackage` に変換する Windows 用ツール。**ヘッドレスで FrooxEngine を起動**し、
Resonite 本体と同じ `ModelImporter` でインポートしたうえで、VRM の正確なメタデータを使って
ゲーム内 AvatarCreator 相当のセットアップ（リグ・視点・リップシンク・揺れもの・マテリアル）を
自動で行う。

- 言語/ランタイム: C# / .NET 10（`net10.0-windows`, WPF GUI 付き, x64）
- 出力: 単一ファイル exe（`SelfContained=false`、Resonite DLL は非同梱）

## ビルドとリリース

```powershell
dotnet build src/VrmToResonitePackage -c Release
dotnet publish src/VrmToResonitePackage -c Release -o publish   # 配布用
```

- **Resonite DLL の参照解決**: csproj が `ResonitePath` プロパティ（既定は Steam の標準パス
  `C:\Program Files (x86)\Steam\steamapps\common\Resonite`）または環境変数 `RESONITE_PATH`
  から FrooxEngine 系 DLL を参照する。これらは**コンパイル時参照のみで再配布しない**
  （`<Private>false</Private>`）。別パスにインストールしている場合は
  `-p:ResonitePath="D:\Games\Resonite"`。
- **バージョン**: csproj の `<Version>` がビルド日時 `yyyy.MM.dd.HHmm` で自動生成される
  （`-p:Version=...` で上書き可）。`AppVersion.Display` が AssemblyInformationalVersion から
  取得し、変換ログ先頭・コンソールヘッダ・GUI タイトルに表示する。
  → **バグ報告のログからビルドを一意に特定**するための仕組みなので壊さないこと。
- **配布スクリプト**: `publish.ps1`（単一ファイル exe）。`release.ps1` は GitHub Release への
  アップロード（`gh` 認証済み前提、ローカル実行）。詳細は [RELEASE.md](RELEASE.md)。
  - **重要**: これらの `.ps1` は Windows PowerShell 5.1 で実行されるため **ASCII のみ**で書くこと。
    BOM なし UTF-8 の日本語コメントはパースエラーになる。

## 実行とテスト

- **テスト実行**: 環境変数 `VRM2RESPKG_NOPAUSE=1` を立てるとキー入力待ちをスキップする
  （CI/自動実行向け）。
- **検証（エンジン起動なし）**: `--inspect <package>` で生成済みパッケージの
  コンポーネント構造を表示（FrooxEngine を起動せずに確認できる）。`--inspect-verbose` で詳細。
- **テスト用モデル**: VRM 0.x / VRM 1.0 の**同一モデルのバージョン違いペア**を用意して
  両系統を確認するのが定石。`.vrm` と `test-input/` `test-output/` は `.gitignore` 済み
  （リポジトリにはコミットしない）。
- **変換ログの場所**: **exe と同じフォルダの `Logs\`**（例:
  `src/VrmToResonitePackage/bin/Debug/Logs/convert_*.log`）。
  ※エンジンのデータ/キャッシュ（`Data\` `Cache\`）は `%LOCALAPPDATA%\VrmToResonitePackage\`
  配下だが、**ログだけは exe 横**。README に `%LOCALAPPDATA%\...\Logs` と書いてある箇所は古い。

### 主な診断・オプションフラグ（`Program.cs`）

| フラグ | 用途 |
|---|---|
| `--inspect` / `--inspect-verbose` | エンジン起動なしでパッケージ構造を表示 |
| `--assimp-dump` | 各ブレンドシェイプの `movedVerts`（実際に動く頂点数）を出力 |
| `--vrchat-dump` | `.unitypackage` の解析結果をエンジン不要で確認 |
| `--avatar <name>` | VRChat パッケージ内の対象アバターを指定（完全一致優先→部分一致） |
| `--face-tracking` | VRM 表情のフェイストラッキング連動をオプトイン（既定は付与しない） |
| `--no-protection` | SimpleAvatarProtection を付けない（既定は付与） |
| `--view-forward` / `--view-up` / `--near-clip` | 視点オフセット・NearClip の上書き |
| `--import-timeout <sec>` | インポートのタイムアウト（既定 300） |
| `--keep-working-files` | 中間ファイルを残す |

## リポジトリ構成

主要なソースは `src/VrmToResonitePackage/`。

- `Program.cs` / `GuiApp.cs` — CLI / WPF GUI エントリポイント、引数パース
- `Converter.cs` — 変換の統括（VRM 経路 / VRChat 経路を拡張子で分岐）
- `AvatarSetup.cs` — ゲーム内 AvatarCreator 相当のセットアップ再現
- `SpringBoneSetup.cs` — VRM スプリングボーン → DynamicBoneChain 変換
- `MaterialTuner.cs` — MToon → XiexeToon マテリアル変換
- `PackageInspector.cs` — `--inspect` のコンポーネント構造表示
- `ResoniteLocator.cs` — Resonite インストールパスの自動検出
- `LocalDbMaintenance.cs` — 使い捨て Data ディレクトリと孤児掃除
- `AppVersion.cs` — ビルドバージョン表示
- `Vrm/` — `VrmParser.cs`（VRM0/1 両対応）, `VrmModel.cs`（中間モデル）, `GlbPreprocessor.cs`（互換ワークアラウンド群）
- `Unity/` — `UnityYaml.cs`, `UnityPackageExtractor.cs`, `UnityScene.cs`（`.unitypackage` 解析基盤）
- `Vrchat/` — VRChat アバター対応一式（パーサ・liltoon 変換・アダプタ）

## ヘッドレス FrooxEngine 起動の知見

`FrooxEngine.dll` を Resonite 外のアプリから `StandaloneFrooxEngineRunner` で動かすときの
確証済みの注意点（ドキュメントがなく症状から原因が見えにくいので明文化）。

- **CWD を Resonite フォルダにする（必須）**: `EngineInitializer` が
  `Directory.EnumerateFiles(".", "ProtoFluxBindings.dll")` でカレントを検索する。
  やらないと ProtoFlux 初期化で `Sequence contains no matching element`。
- **ネイティブ DLL**: `runtimes\win-x64\native` と `runtimes\win10-x64\native` に
  freetype6 等がある。`AssemblyLoadContext.Default.ResolvingUnmanagedDll` でフックして解決。
- **マネージド DLL**: `AppDomain.CurrentDomain.AssemblyResolve` で
  `Assembly.LoadFrom(resoniteDir + name.dll)`。
- **`Userspace.ExitWorld`/`RunAction` はヘッドレスでハングする**: `LeaveWorld` がフォーカス
  移譲先ワールドを無限待機する。`DoNotAutoLoadHome=true` だと親がいないため永久に終わらない。
  → ワールドは閉じずに 1 つを使い回し、終了は `runner.Shutdown()` にタイムアウトを付けて
  最後に `Environment.Exit`。
- **更新ループはフォアグラウンドスレッド**: Shutdown が完了しないとプロセスが終わらないので
  必ず `Environment.Exit`。
- **DataDirectory は専用パスに**: `Instance.lock` が Resonite 本体と競合しないよう
  `%LOCALAPPDATA%\<app>\Data` 等を使う。
- **LocalDB はシングルプロセス前提**: GUI 分離プロセスと CLI の並行実行で共有 Data ディレクトリが
  破損する（Invalid password）。→ **実行ごとに使い捨て `Data-xxxxxxxx` ディレクトリ**を使い、
  終了時削除＋次回起動時に `Instance.lock` で生存確認しつつ孤児掃除。`LocalKey.bin` だけは
  共有保管して MachineID を安定化させる。
- **パッケージ出力**: `Slot.SaveObject(DependencyHandling.CollectAssets)` →
  `RecordHelper.CreateForObject<Record>` → `PackageCreator.BuildPackage`
  （`PackageExportable.cs` が手本）。
- **正確な API が必要なとき**: インストール済み DLL を直接読むのが確実。
  `ilspycmd -t <型名> FrooxEngine.dll`。ローカルにデコンパイル済みソースを置いて grep するのも
  有効だが、デコンパイルは古い場合がある。

## ModelImporter の既知クラッシュと GLB 前処理ワークアラウンド

Resonite の `ModelImporter` 由来のクラッシュを、変換前に GLB を自前修正して回避している
（`Vrm/GlbPreprocessor.cs`）。

- **空名ブレンドシェイプ複数で例外**: 旧 UniVRM は targetNames が primitive extras 側にしかなく
  Assimp が読めず全シェイプ名が空になる。`HasBlendShape("")` が常に false で重複チェックを
  素通しし `MeshX.AddBlendShape` で「already exists」例外。→ mesh.extras.targetNames へ昇格・補完。
- **プリミティブ間のモーフ属性不一致**: 同一メッシュ内でモーフの NORMAL 有無が混在すると
  マージで `Normals[i]` 範囲外。→ 先頭プリミティブの HasNormals を基準に不一致ターゲットから
  NORMAL/TANGENT を除去。
- **インポートのハング**: インポートコルーチンが例外で死ぬと `ImportModelAsync` が永遠に
  完了しない。→ タイムアウト必須（`--import-timeout`、既定 300 秒）。

### JoinIdenticalVertices によるブレンドシェイプ破壊

`ModelImporter` は常に `PostProcessSteps.JoinIdenticalVertices` を適用する
（`ModelImportSettings` で無効化不可・ハードコード）。このステップは位置/法線/全 UV/全頂点カラー/
ボーンウェイトが一致する頂点をマージするが、**モーフターゲット（ブレンドシェイプ）のデルタは
比較対象に含めない**。VRM 顔メッシュは静止時に座標が一致する頂点（歯・舌が一点に潰れていて
シェイプで展開する等）を持つことが多く、これらがマージされて片方のデルタしか残らず
「シェイプキーを 0→1 にしても一部頂点が動かない」症状になる（UniVRM は独自インポータで Join
しないので出ない）。

- **回避策（`GlbPreprocessor.AddMorphVertexGuardChannel`）**: モーフ持ちメッシュの全 primitive に
  **頂点ごとに一意な値を持つ追加 TEXCOORD チャンネル**を付与する。UV が 1 つでも違う頂点は
  マージ候補にならないため別頂点が保持される。値＝頂点 index（整数間隔で join epsilon に非依存）、
  VEC2 float、POSITION accessor 単位で共有。トーンマテリアルは UV0 しか使わないので無害。
  データは BIN チャンクに追記し `buffer[0].byteLength` と BIN チャンク長を拡張する。
- **検証**: `--assimp-dump` が各ブレンドシェイプの `movedVerts` を出力する。生 glTF の非ゼロデルタ数
  （Python で target POSITION accessor を読む）と比較し、大幅に下回ればマージで潰れている。

## 座標系の取り扱い

実測＋デコンパイルで確定した座標系の規約。コライダーオフセット等の符号を間違えると左右/前後が
反転するため、ここが最重要。

- Resonite `ModelImporter.PreprocessScene` が Assimp シーン全体に **scale(-1,1,1) の X ミラー**
  ＋ FlipWindingOrder を適用する（RH→LH）。つまり**エンジンのスロット値＝glTF 値の X 反転**。
- glTF インポートは `MakeLeftHanded` なしで glTF 座標を数値そのまま使う。VRM0 の Unity 座標データ
  （スプリングボーン offset 等）は X 反転が必要、VRM1 はそのまま。
- **UniVRM0 は ReverseZ エクスポート**（モデルは -Z 向き、VRM0 拡張の spring オフセットは Unity 生値）、
  **UniVRM1 は ReverseX**（+Z 向き、オフセットは glTF ノードローカル）。
- -Z 向きのままだと VRIK `CreateCenteredRoot` が CenteredRoot に Y180 を入れるため、
  `GlbPreprocessor` で正規化済み VRM0 に **Y180 ベイク**（全 translation の X,Z 反転＋IBM に
  R=diag(-1,1,-1) を左乗算。回転ゼロ階層でのみ成立）。FlipX∘Y180∘ReverseZ＝恒等となり
  エンジン＝Unity 座標で回転 0 になる。
- **コライダーオフセット変換**: VRM1=`(-x,y,z)` / VRM0 ベイク済=`(x,y,z)` /
  VRM0 非ベイク=`(-x,y,-z)`。（旧実装の「VRM0 は X 反転のみ」は Z 符号が誤り。対称コライダーでは
  発覚しない。）

### Blender VRM アドオン製 VRM1 の前後反転

UniVRM/VRoid は VRM1 を **ReverseX（鏡映エンコード＝解剖学的左が +X）**で出すが、
**Blender VRM アドオンは「正配置」glTF（左が -X、Blender→glTF は反射なしの回転のみ）**を出す。
Resonite インポータは前者前提で FlipX するため、Blender 製は鏡映のまま残り、さらに VRIK
`CreateCenteredRoot` が足から前方向を測って Y180 を CenteredRoot に注入する。結果
Y180∘FlipX＝Z 鏡映で**体・頭が前後逆・腕は左右(X)保持**になる。

- **判定**: glTF で `rightUpperLeg.x > leftUpperLeg.x`（脚で判定。腕は稀に変な静止回転を持つ）。
- **修正（`GlbPreprocessor.MirrorXForProperHandedVrm1`）**: glTF 全体を X 反射 P=diag(-1,1,1) して
  UniVRM 規約に揃える。自前 X 鏡映∘インポータ FlipX＝恒等で忠実・正面向き・全 X そのまま。
- **反射は負スケールなしの共役で実装**: ノード local は translation.x 反転＋quat
  (x,y,z,w)→(x,-y,-z,w)・scale 不変、IBM は P·IBM·P（列優先 index {1,2,3,4,8,12} 反転）、
  POSITION/NORMAL の X 反転、TANGENT は x+w 反転、三角 winding 反転（2,3 番入替）。
  **これは反射(det -1)なので winding 反転が必須**（VRM0 の Y180 ベイクは proper rotation で
  winding 不変。混同しないこと）。
- **検証**: 出力パッケージの `CenteredRoot` 回転が (0,1,0,0)→恒等になれば OK。
  追跡は `VrmModel.OrientationMirroredX`。

## アバターセットアップ規約（実機確認で確定した値）

ヘッドレス変換では実機の見た目・操作感を確認できないため、**ユーザーの実機検証で確定した値が
唯一の正解**。VRIK の軸推測や Resonite の既定値は VRM アバターに合わないことが多い。

- **手のリファレンス向き**: **+Z=手首→中指方向、+Y=手の甲方向**（両手とも同じ規則）。
  VRIK の `WristToPalmAxis` 推測はモデルごとにバラつくので使わず、ボーン位置（中指近位・親指）から
  計算する。
- **BipedRig ボーン割り当て**: `Bones.Clear()` してから **VRM マップのみで構築**する。インポート時の
  名前ヒューリスティック（`BipedRig.ClassifyBiped`）は装飾ボーンを Chest/UpperChest/Neck/Head 等に
  誤分類し、実スケルトンが使わない BodyNode に入ったゴミが残ると VRIK の `AssignFrom` が
  `chest = UpperChest ?? Chest` で誤ったボーンを拾う。ログの `Duplicate Bone type` 警告は
  誤分類発生のサイン。
- **視点位置**: 目ボーン中点だと顔メッシュに埋まる。**眉間（顔表面）へオフセット**する
  （前方＝目間距離×0.7 をクランプ 3〜9cm、上＝×0.2 を 0.5〜2.5cm）。`--view-forward`/`--view-up` で
  上書き可。
- **AvatarRenderSettings**: ルート直下に作り **NearClip=0.075、FarClip=null**（前髪が一人称視点を
  遮る問題の回避）。`--near-clip` で変更可（0 で無効）。
- **EyeRotationDriver.MaxSwing=4**（既定 15 は VRM には大きすぎて目が突き抜ける）。
- **ビセーム/瞬き**: Resonite の名前推測（`EnsureVoiceOutput` 内の `TrySetupVisemes`）を
  **完全バイパス**し、VRM Expression のバインドのみで設定。`DirectVisemeDriver` は Aa/Ih/Ou/Ee/Oh の
  5 つだけ、`EyeLinearDriver` は Left/Right 2 要素に blinkLeft/blinkRight。音声出力部分
  （VolumeMeter/AudioOutput 等）は自前複製。
- **DirectVisemeDriver の誤登録是正**: Resonite の名前推測は VRoid の `Fcl_EYE_Joy_R` を RR に
  誤登録する。目・眉系シェイプキーの誤登録は解除する。
- **AvatarExpressionDriver（TrySetupFaceTracking）は既定で生成しない**。フェイストラッキング非対応
  アバターでは不要かつ `Fcl_EYE_Angry` 等が誤登録される。`--face-tracking` でオプトイン。
- **アンカー位置**: Tool=中指先端の 3cm 先（指先＝末節の子エンドボーン優先、無ければ前セグメント長
  ×0.8 で外挿）/ GrabArea=中指付け根から手のひら向き 1cm / Toolshelf=手首から手の甲向き 5cm。
- **SimpleAvatarProtection は既定で付与**（root＋各 SkinnedMeshRenderer スロット）。ヘッドレスでは
  所有者なしだが `ReassignUserOnPackageImport` 既定 true でインポートした人が所有者になる。
  `--no-protection` でオフ。

## マテリアル変換（MToon → XiexeToon）

- **公式 MToon10XiexeConverter 準拠**
  （[Resonite.UnitySDK](https://github.com/Yellow-Dog-Man/Resonite.UnitySDK) の
  MaterialConverters/Custom/MToon/）。Alpha＝ZWrite Off（transparentWithZWrite なら On）＋MToon 仕様の
  キュー（blend:3000+offset / zwrite 付き:2501+offset / mask:2450 / opaque:2000、VRM0 はファイル内
  renderQueue 値）。※RenderQueue=2000 固定は破綻するため廃止。
- **XiexeToon 化時**: `ShadowRamp.Target=null、ShadowSharpness=0` が MToon の見た目に近い。
- **ShadowRamp は除去ではなく shadeColor/shadingShift/shadingToony から 256x256 生成**
  （VRM0 値は UniVRM MToon10Migrator 式で 1.0 換算: rangeMin=shift, rangeMax=lerp(1,shift,toony),
  toony10=(2-(max-min))/2, shift10=-(max+min)/2）。ShadowRim=白。
- **リム変換**: 公式コンバーターの式（RimRange=1/power, RimThreshold=lift）は**シェーダー実装と不一致で
  巨大リム化する**ため独自導出を使う。MToon=`pow(1-N·V+lift, power)`、
  XSToon=`smoothstep(range±sharp, 1-N·V)`。range=0.5^(1/power)−lift、
  sharpness=0.75/(power×0.5^((power-1)/power))。RimThreshold（N·L 明所ゲート）は MToon に無い概念→0 固定。
- **アウトライン幅**: XiexeToon(XSToon2.0) は**オブジェクト空間で `_OutlineWidth*0.01` 押し出し**
  （[Resonite.UnityShaders](https://github.com/Yellow-Dog-Man/Resonite.UnityShaders) の XSGeom.cginc）、
  MToon World モードはメートル。→ **Xiexe 値＝幅(m)×100**（VRM0 の `_OutlineWidth` は cm なので 1:1、
  VRM1 の `outlineWidthFactor` は ×100）。ShadowRamp 生成時もアウトライン幅だけは公式の生値代入では
  なく**幅(m)×100**を維持する。
- **OutlineMask**: MToon の `_OutlineWidthTexture`/`outlineWidthMultiplyTexture` は XiexeToon の
  **OutlineMask** と同義（メイン UV・赤チャネル・幅乗算）。ただし標準 glTF スロット外なので Assimp は
  取り込まない→GLB バイナリから自前抽出して `LocalDB.ImportLocalAssetAsync` でインポート。

## VRChat `.unitypackage` 対応

VRM に加え、VRChat アバター入りの `.unitypackage` も変換できる（隠し機能扱い。UI 表記は VRM のみ）。

- **設計**: VRChat パーサが `VrmModel` を生成するアダプタ方式で、リグ/視点/ビセーム/瞬き/揺れものの
  下流処理（`AvatarSetup`/`SpringBoneSetup`）を共用する。ボーン/メッシュは
  **GameObject 名＝インポート後スロット名**、ブレンドシェイプは**名前(ビセーム)/index(瞬き)**で
  解決する。VRChat 固有の liltoon マテリアルのみ別経路。`VrmModel` に
  `ModelSource{Vrm0,Vrm1,VrchatFbx}` を追加し、`Converter` は拡張子で分岐
  （`ConvertVrchat` は GLB 前処理せず FBX を `.fbx` にコピーして `ModelImporter`）。
- **Unity YAML パーサ（`Unity/UnityYaml.cs`）の注意**: **ブロックシーケンスは親キーと同インデント**
  （`m_Component:` の次行 `- component:` が同列）。`ParseFlatDocument` は `.meta` 等のヘッダ無し単一
  doc 用。
- **アバター選定**: `VRCAvatarDescriptor` 付きルート prefab で GameObject 数最大を主要アバターとする
  （`--avatar` で上書き、完全一致優先→部分一致）。`.unity` シーンも走査し、`IsAvatarDescriptor` は
  script GUID 一致**または**フィールド署名（`ViewPosition`＋`baseAnimationLayers`/`VisemeBlendShapes`）で
  SDK 版差の GUID 違いに対応。
- **ビセームは全 15 対応**（VRChat の `VisemeBlendShapes` 順＝Resonite `FrooxEngine.Viseme` enum 順で
  完全一致。ただし sil の enum 名は `Silence`）。`BlendShapeWeights` は 0-1 スケール
  （prefab SMR の Unity 0-100 値を /100）。
- **瞬きは `eyelidsBlendshapes[0]`（=Blink）のみ**。`[1]`=LookingUp/`[2]`=LookingDown は無視。
- **PhysBoneCollider**: `insideBounds==1`（内側保持）と `shapeType==2`（plane）は Resonite 再現不可で
  スキップ。`rootTransform` 指定時はそのボーンへ付与、=0 時は自 GO の親ボーンへ局所変換を畳む。
  署名（node+offset+tail+radius）で重複排除し chain 間共有。
- **FBX モデルの Prefab Variant 対応**: 1 つの全部入り FBX から派生 prefab を作る構造（Kipfel 等）に
  対応。`VRCAvatarDescriptor` が FBX インスタンスへの追加コンポーネントで所有 GameObject が
  stripped（名前無し）の場合、descriptor 自身または所有 stripped GameObject の
  `m_PrefabInstance`→`PrefabInstance.m_SourcePrefab` からソース FBX guid を解決してインポートする。
  追加コンポーネント自身の `m_PrefabInstance` が 0 で所有 GameObject 側だけに参照がある場合もある。診断ログの
  `descriptor(guid=1, strippedOwner=1), variant=True, source=[X.fbx ...]` がこの構造のサイン。
  stripped renderer の通常の `m_Materials` は読めないが、FBX `.meta` の
  `ModelImporter.externalObjects` にある「FBX 内マテリアル名→外部 `.mat` GUID」を使い、Resonite が生成した
  `Material: <name>` placeholder を置換する。解決可能な prefab renderer override はその後に優先適用する。
  stripped 参照のため PhysBone/削除メッシュ/瞬きは引き続き制限あり。
- **FBX の単位変換**: VRChat FBX を直接 Resonite `ModelImporter` へ渡すと、Unity の ModelImporter が
  `useFileUnits`/`useFileScale` で行う単位変換は再現されない。FBX `UnitScaleFactor` は cm 単位なので、
  `ModelImportSettings.Scale = globalScale * UnitScaleFactor / 100` を設定する。Rusk は
  `UnitScaleFactor=1` のため 0.01、Kipfel は `100` のため 1。適用しないと cm 制作のFBXが100倍になる。
- **prefab 削除メッシュの除去（`VrchatSceneSetup.RemoveDeletedMeshes`）**: 全部入り FBX から
  メッシュ GameObject を削除して派生 prefab を作る構造では FBX インポートで削除済みメッシュも
  復活する。`avatar.PrefabGameObjectNames` を保持し、インポート直後（セットアップ前）に
  prefab に無い名前のレンダラースロット＋孤立 StaticMesh アセットを削除する。
- **GUI 落とし穴**: `InstallAssemblyResolver` は CLI 経路（`Program.Main`）でしか呼ばれず GUI プロセスには
  無い。複数アバターの列挙（`ListAvatars`）は `Elements.Core.UniLog` を参照するため、リゾルバ未導入だと
  JIT 例外→catch で握り潰し→空リスト→ダイアログが出ずバッチ直行になる。
  `MainWindow.EnsureAssemblyResolver()` で列挙前にリゾルバを導入すること。

### VRChat 経路の実機検証 TODO（未確認の係数・前提）

- 座標: コライダーオフセットは `ConvertVector(VrchatFbx)=(-x,y,z)`（VRM1/glTF と同じ X 反転と仮定）。
  CenteredRoot 回転は恒等になったが、揺れものコライダー位置は要実機確認。
- 瞬き: `eyelidsBlendshapes[0]` の index を Body レンダラの blendshape index 直で解決
  （Assimp が順序を保持する前提）。ズレたら名前解決へ。
- liltoon マテリアル: 影 border/blur・アウトライン幅・リムの数式は近似。実機で要調整。

## 外部参照リソース

- VRM 仕様: https://github.com/vrm-c/vrm-specification
- VRM の Unity 実装（UniVRM）: https://github.com/vrm-c/UniVRM
- Resonite シェーダー: https://github.com/Yellow-Dog-Man/Resonite.UnityShaders
- Resonite Unity SDK（マテリアルコンバーター）: https://github.com/Yellow-Dog-Man/Resonite.UnitySDK
- Resonite 本体の API 調査は、インストール済み DLL を `ilspycmd` で逆コンパイルするか、
  ローカルにデコンパイル済みソースを置いて grep する（デコンパイルは古い場合があるので最終確認は DLL で）。
